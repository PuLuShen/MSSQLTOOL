using System;
using System.Collections.Generic;

namespace MSSQLTool.Model
{
    internal enum SqlTargetKind
    {
        None,
        Insert,
        Update,
        Delete,
        Merge,
        Exec,
        CreateTable,
        AlterTable,
        Index
    }

    internal sealed class SqlTargetReference
    {
        public SqlTargetKind Kind;
        public string Name;
        public int Start;
        public int Length;
    }

    /// <summary>
    /// Finds the table a statement writes to (INSERT/UPDATE/DELETE/MERGE) with a linear token
    /// scan.  The completion analyzer used to infer this from regular expressions, which missed
    /// bracketed names and statements preceded by a CTE; the token scan only needs the statement
    /// range the document model already produced.
    /// </summary>
    internal static class SqlTargetScanner
    {
        public static SqlTargetReference Find(string text, int start, int length)
        {
            if (string.IsNullOrEmpty(text) || length <= 0) return null;
            int from = Math.Max(0, start);
            int to = Math.Min(text.Length, start + length);

            List<SqlToken> tokens = SqlTokenizer.Tokenize(text);
            var reference = new SqlTargetReference();
            for (int i = 0; i < tokens.Count; i++)
            {
                SqlToken token = tokens[i];
                if (token.Start < from || token.Start >= to) continue;
                if (token.Kind != SqlTokenKind.Keyword) continue;
                SqlTargetKind kind = KindOf(token.Text);
                if (kind == SqlTargetKind.None) continue;

                int next = i + 1;
                if (kind == SqlTargetKind.CreateTable)
                {
                    if (!(next < tokens.Count && IsKeyword(tokens[next], "TABLE"))) continue;
                    next++;
                }
                else if (kind == SqlTargetKind.AlterTable)
                {
                    if (!(next < tokens.Count && IsKeyword(tokens[next], "TABLE"))) continue;
                    next++;
                }
                else if (kind == SqlTargetKind.Index)
                {
                    // CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX <name> ON <table>
                    int on = -1;
                    for (int j = next; j < tokens.Count && j < next + 6; j++)
                    {
                        if (IsKeyword(tokens[j], "ON")) { on = j; break; }
                        if (IsKeyword(tokens[j], "TABLE") || IsKeyword(tokens[j], "VIEW")) break;
                    }
                    if (on < 0) continue;
                    next = on + 1;
                }
                else if (kind == SqlTargetKind.Insert)
                {
                    if (next < tokens.Count && IsKeyword(tokens[next], "INTO")) next++;
                }
                else if (kind == SqlTargetKind.Delete)
                {
                    if (next < tokens.Count && IsKeyword(tokens[next], "FROM")) next++;
                    else continue;
                }
                else if (kind == SqlTargetKind.Merge)
                {
                    if (next < tokens.Count && IsKeyword(tokens[next], "INTO")) next++;
                }
                else if (kind == SqlTargetKind.Exec)
                {
                    // EXEC @rc = dbo.Proc ...: skip the return-code assignment.
                    if (next + 1 < tokens.Count
                        && tokens[next].Kind == SqlTokenKind.Identifier
                        && tokens[next].Text.StartsWith("@", StringComparison.Ordinal)
                        && tokens[next + 1].Text == "=")
                        next += 2;
                }

                // UPDATE TOP (n) / UPDATE TOP n: skip the row limit before the target name.
                if (kind == SqlTargetKind.Update && next < tokens.Count && IsKeyword(tokens[next], "TOP"))
                {
                    next++;
                    if (next < tokens.Count && tokens[next].Text == "(")
                    {
                        int depth = 0;
                        for (; next < tokens.Count; next++)
                        {
                            if (tokens[next].Text == "(") depth++;
                            else if (tokens[next].Text == ")") { depth--; next++; break; }
                        }
                    }
                    else if (next < tokens.Count) next++;
                }

                SqlTargetReference parsed = ReadName(tokens, next, to);
                if (parsed == null) continue;
                parsed.Kind = kind;
                return parsed;
            }
            return null;
        }

        private static SqlTargetReference ReadName(List<SqlToken> tokens, int index, int limit)
        {
            var parts = new List<string>();
            int start = -1;
            int end = -1;
            bool expectName = true;
            for (int i = index; i < tokens.Count; i++)
            {
                SqlToken token = tokens[i];
                if (token.Start >= limit) break;
                if (token.Kind == SqlTokenKind.Comment) continue;
                if (expectName)
                {
                    if (token.Kind != SqlTokenKind.Identifier) break;
                    string name = SqlTokenizer.Unquote(token);
                    if (string.IsNullOrEmpty(name)) break;
                    if (start < 0) start = token.Start;
                    end = token.Start + token.Length;
                    parts.Add(name);
                    expectName = false;
                    continue;
                }

                if (token.Text == ".") { expectName = true; continue; }
                break;
            }

            if (parts.Count == 0) return null;
            return new SqlTargetReference { Name = string.Join(".", parts), Start = start, Length = end - start };
        }

        private static bool IsKeyword(SqlToken token, string value)
            => token.Kind == SqlTokenKind.Keyword && string.Equals(token.Text, value, StringComparison.OrdinalIgnoreCase);

        private static SqlTargetKind KindOf(string keyword)
        {
            if (string.Equals(keyword, "INSERT", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.Insert;
            if (string.Equals(keyword, "UPDATE", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.Update;
            if (string.Equals(keyword, "DELETE", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.Delete;
            if (string.Equals(keyword, "MERGE", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.Merge;
            if (string.Equals(keyword, "EXEC", StringComparison.OrdinalIgnoreCase)
                || string.Equals(keyword, "EXECUTE", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.Exec;
            if (string.Equals(keyword, "CREATE", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.CreateTable;
            if (string.Equals(keyword, "ALTER", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.AlterTable;
            if (string.Equals(keyword, "INDEX", StringComparison.OrdinalIgnoreCase)) return SqlTargetKind.Index;
            return SqlTargetKind.None;
        }
    }
}
