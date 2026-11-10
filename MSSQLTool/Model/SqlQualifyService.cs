using System;
using System.Collections.Generic;

namespace MSSQLTool.Model
{
    /// <summary>Result of planning "qualify object names".</summary>
    internal sealed class SqlQualifyPlan
    {
        public bool Success;
        public string Error = string.Empty;
        public string Schema = string.Empty;
        public List<SqlTextChange> Changes = new List<SqlTextChange>();

        public int Count => Changes == null ? 0 : Changes.Count;
    }

    /// <summary>
    /// Adds the schema prefix to table references that do not have one.  Only the places where a
    /// table can appear are touched (FROM/JOIN/UPDATE/INTO), so a column called "orders" is never
    /// rewritten, and CTEs, temp tables, table variables and table-valued functions are skipped.
    /// </summary>
    internal static class SqlQualifyService
    {
        private static bool SkipsQualification(string name, HashSet<string> cteNames)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.StartsWith("#", StringComparison.Ordinal)) return true;
            if (name.StartsWith("@", StringComparison.Ordinal)) return true;
            if (string.Equals(name, "SELECT", StringComparison.OrdinalIgnoreCase)) return true;
            return cteNames.Contains(name);
        }

        public static SqlQualifyPlan Build(string text, string schema)
        {
            var plan = new SqlQualifyPlan();
            if (string.IsNullOrEmpty(text))
            {
                plan.Error = "There is no SQL text to qualify.";
                return plan;
            }

            string prefix = SqlRenameService.CleanName(schema);
            if (string.IsNullOrEmpty(prefix))
            {
                plan.Error = "Enter the schema name.";
                return plan;
            }

            plan.Schema = prefix;
            SqlDocumentModel model = SqlDocumentModel.Get(text);
            List<SqlToken> tokens = model.Tokens;
            HashSet<string> cteNames = CollectCteNames(tokens);

            for (int index = 0; index < tokens.Count; index++)
            {
                SqlToken token = tokens[index];
                if (token.Kind != SqlTokenKind.Keyword) continue;

                bool startsTableList = token.Is("FROM") || token.Is("JOIN") || token.Is("UPDATE") || token.Is("INTO");
                if (!startsTableList) continue;

                int cursor = index + 1;
                while (cursor < tokens.Count)
                {
                    SqlToken current = tokens[cursor];
                    if (current.Kind == SqlTokenKind.Comment) { cursor++; continue; }

                    // A derived table or a table-valued function: nothing to qualify here.
                    if (current.Text == "(") break;
                    if (current.Kind != SqlTokenKind.Identifier) break;

                    string name = SqlTokenizer.Unquote(current);
                    SqlToken nextToken = NextSignificant(tokens, cursor);
                    bool alreadyQualified = nextToken != null && nextToken.Text == ".";

                    // A table-valued function is called, not qualified.
                    if (nextToken != null && nextToken.Text == "(") break;

                    if (!alreadyQualified && !SkipsQualification(name, cteNames))
                        plan.Changes.Add(new SqlTextChange(current.Start, 0, prefix + "."));

                    cursor++;

                    // Consume "schema.table" (and the occasional database.schema.table).
                    while (cursor + 1 < tokens.Count && tokens[cursor].Text == ".")
                        cursor += 2;

                    // Optional alias: "AS x" or a bare identifier.
                    if (cursor < tokens.Count && tokens[cursor].Is("AS")) cursor++;
                    if (cursor < tokens.Count && tokens[cursor].Kind == SqlTokenKind.Identifier) cursor++;

                    // Column list / table hints: skip the balanced parentheses.
                    if (cursor < tokens.Count && tokens[cursor].Text == "(")
                    {
                        int depth = 0;
                        while (cursor < tokens.Count)
                        {
                            if (tokens[cursor].Text == "(") depth++;
                            else if (tokens[cursor].Text == ")")
                            {
                                depth--;
                                if (depth == 0) { cursor++; break; }
                            }
                            cursor++;
                        }
                    }

                    // "FROM A a, B b": the comma starts the next table reference.
                    if (cursor < tokens.Count && tokens[cursor].Text == ",") { cursor++; continue; }
                    break;
                }
            }

            plan.Success = plan.Changes.Count > 0;
            if (!plan.Success) plan.Error = "No unqualified table reference was found.";
            return plan;
        }

        /// <summary>Names declared by WITH at the start of a statement (used to skip qualifying them).</summary>
        private static HashSet<string> CollectCteNames(List<SqlToken> tokens)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < tokens.Count; index++)
            {
                if (!tokens[index].Is("WITH")) continue;

                int cursor = index + 1;
                while (cursor < tokens.Count)
                {
                    SqlToken current = tokens[cursor];
                    if (current.Kind != SqlTokenKind.Identifier) break;

                    names.Add(SqlTokenizer.Unquote(current));

                    // Optional column list, then AS.
                    cursor++;
                    if (cursor < tokens.Count && tokens[cursor].Text == "(")
                    {
                        int depth = 0;
                        while (cursor < tokens.Count)
                        {
                            if (tokens[cursor].Text == "(") depth++;
                            else if (tokens[cursor].Text == ")")
                            {
                                depth--;
                                if (depth == 0) { cursor++; break; }
                            }
                            cursor++;
                        }
                    }

                    while (cursor < tokens.Count && !tokens[cursor].Is("AS")) cursor++;
                    if (cursor >= tokens.Count) break;
                    cursor++;

                    // Skip the CTE body, then continue only when another CTE is separated by a comma.
                    if (cursor < tokens.Count && tokens[cursor].Text == "(")
                    {
                        int depth = 0;
                        while (cursor < tokens.Count)
                        {
                            if (tokens[cursor].Text == "(") depth++;
                            else if (tokens[cursor].Text == ")")
                            {
                                depth--;
                                if (depth == 0) { cursor++; break; }
                            }
                            cursor++;
                        }
                    }

                    if (cursor < tokens.Count && tokens[cursor].Text == ",") { cursor++; continue; }
                    break;
                }
            }

            return names;
        }

        private static SqlToken NextSignificant(List<SqlToken> tokens, int index)
        {
            for (int cursor = index + 1; cursor < tokens.Count; cursor++)
            {
                if (tokens[cursor].Kind == SqlTokenKind.Comment) continue;
                return tokens[cursor];
            }

            return null;
        }
    }
}
