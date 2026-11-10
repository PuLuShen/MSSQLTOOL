using System;
using System.Collections.Generic;
using System.Text;

namespace MSSQLTool.Model
{
    internal enum SqlTokenKind
    {
        Identifier,
        Keyword,
        Number,
        String,
        Comment,
        Operator,
        Punctuation
    }

    /// <summary>
    /// One lexical token with its absolute offset in the document.  Offsets are never
    /// normalised, so a token can be mapped back to an editor span without a second pass.
    /// </summary>
    internal sealed class SqlToken
    {
        public SqlTokenKind Kind;
        public int Start;
        public int Length;
        public string Text;

        public int End => Start + Length;

        public bool IsComment => Kind == SqlTokenKind.Comment;

        public bool IsWordLike => Kind == SqlTokenKind.Identifier || Kind == SqlTokenKind.Keyword;

        public bool Is(string value) => string.Equals(Text, value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Allocation-light T-SQL tokenizer used by the document model, the analysis rules and
    /// the highlight analyser.  It never throws on incomplete input: an unterminated string
    /// or comment simply becomes the final token, which is exactly what a tolerant editor
    /// feature needs while the user is still typing.
    /// </summary>
    internal static class SqlTokenizer
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN",
            "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CHECK", "CHECKPOINT", "CLOSE", "CLUSTERED",
            "COALESCE", "COLLATE", "COLUMN", "COMMIT", "COMPUTE", "CONSTRAINT", "CONTAINS", "CONTINUE",
            "CONVERT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP",
            "CURRENT_USER", "CURSOR", "DATABASE", "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DENY",
            "DESC", "DISK", "DISTINCT", "DISTRIBUTED", "DOUBLE", "DROP", "DUMP", "ELSE", "END", "ERRLVL",
            "ESCAPE", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR",
            "FOR", "FOREIGN", "FREETEXT", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT", "GROUP", "HAVING",
            "HOLDLOCK", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN",
            "KEY", "KILL", "LEFT", "LIKE", "LINENO", "LOAD", "MERGE", "NATIONAL", "NOCHECK", "NONCLUSTERED",
            "NOT", "NULL", "NULLIF", "OF", "OFF", "OFFSET", "OFFSETS", "ON", "OPEN", "OPTION", "OR", "ORDER",
            "OUTER", "OUTPUT", "OVER", "PARTITION", "PERCENT", "PIVOT", "PLAN", "PRECISION", "PRIMARY", "PRINT",
            "PROC", "PROCEDURE", "PUBLIC", "RAISERROR", "READ", "READTEXT", "RECONFIGURE", "REFERENCES",
            "REPLICATION", "RESTORE", "RESTRICT", "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT",
            "ROWGUIDCOL", "RULE", "SAVE", "SCHEMA", "SELECT", "SESSION_USER", "SET", "SETUSER", "SHUTDOWN",
            "SOME", "STATISTICS", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "TO", "TOP", "TRAN",
            "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY_CONVERT", "TSEQUAL", "UNION", "UNIQUE", "UNPIVOT",
            "UPDATE", "UPDATETEXT", "USE", "USER", "VALUES", "VARYING", "VIEW", "WAITFOR", "WHEN", "WHERE",
            "WHILE", "WITH", "WITHIN", "WRITETEXT", "THROW", "TRY", "CATCH", "APPLY", "MATCHED", "TARGET",
            "SOURCE", "STRING_AGG"
        };

        public static bool IsKeyword(string value)
            => !string.IsNullOrEmpty(value) && Keywords.Contains(value);

        public static bool IsIdentifierStart(char value)
            => char.IsLetter(value) || value == '_' || value == '@' || value == '#' || value == '$';

        public static bool IsIdentifierPart(char value)
            => char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$';

        public static List<SqlToken> Tokenize(string sql)
        {
            var tokens = new List<SqlToken>();
            string text = sql ?? string.Empty;
            int index = 0;
            while (index < text.Length)
            {
                char current = text[index];
                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }

                if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
                {
                    int start = index;
                    index += 2;
                    while (index < text.Length && text[index] != '\r' && text[index] != '\n') index++;
                    tokens.Add(Create(SqlTokenKind.Comment, text, start, index - start));
                    continue;
                }

                if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
                {
                    int start = index;
                    index += 2;
                    while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                    index = Math.Min(text.Length, index + 2);
                    tokens.Add(Create(SqlTokenKind.Comment, text, start, index - start));
                    continue;
                }

                if (current == '\'')
                {
                    int start = index;
                    index++;
                    while (index < text.Length)
                    {
                        if (text[index] == '\'')
                        {
                            if (index + 1 < text.Length && text[index + 1] == '\'') { index += 2; continue; }
                            index++;
                            break;
                        }
                        index++;
                    }
                    tokens.Add(Create(SqlTokenKind.String, text, start, index - start));
                    continue;
                }

                if (current == '[')
                {
                    int start = index;
                    index++;
                    while (index < text.Length)
                    {
                        if (text[index] == ']')
                        {
                            if (index + 1 < text.Length && text[index + 1] == ']') { index += 2; continue; }
                            index++;
                            break;
                        }
                        index++;
                    }
                    tokens.Add(Create(SqlTokenKind.Identifier, text, start, index - start));
                    continue;
                }

                if (current == '"')
                {
                    int start = index;
                    index++;
                    while (index < text.Length)
                    {
                        if (text[index] == '"')
                        {
                            if (index + 1 < text.Length && text[index + 1] == '"') { index += 2; continue; }
                            index++;
                            break;
                        }
                        index++;
                    }
                    tokens.Add(Create(SqlTokenKind.Identifier, text, start, index - start));
                    continue;
                }

                if (char.IsDigit(current))
                {
                    int start = index;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' || text[index] == 'e' || text[index] == 'E')) index++;
                    tokens.Add(Create(SqlTokenKind.Number, text, start, index - start));
                    continue;
                }

                if (IsIdentifierStart(current))
                {
                    int start = index;
                    index++;
                    while (index < text.Length && IsIdentifierPart(text[index])) index++;
                    string value = text.Substring(start, index - start);
                    tokens.Add(Create(IsKeyword(value) ? SqlTokenKind.Keyword : SqlTokenKind.Identifier, text, start, index - start));
                    continue;
                }

                if ("+-*/%=<>!&|^~:".IndexOf(current) >= 0)
                {
                    int start = index;
                    index++;
                    if (index < text.Length && "=>|&".IndexOf(text[index]) >= 0) index++;
                    tokens.Add(Create(SqlTokenKind.Operator, text, start, index - start));
                    continue;
                }

                tokens.Add(Create(SqlTokenKind.Punctuation, text, index, 1));
                index++;
            }

            return tokens;
        }

        /// <summary>Identifier value without brackets or quotes; safe for comparisons.</summary>
        public static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                return trimmed.Substring(1, trimmed.Length - 2).Replace("]]", "]");
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                return trimmed.Substring(1, trimmed.Length - 2).Replace("\"\"", "\"");
            return trimmed;
        }

        public static string Unquote(SqlToken token)
            => token == null ? string.Empty : Unquote(token.Text);

        /// <summary>Token index of the first token at or after the offset (binary search).</summary>
        public static int IndexAt(IList<SqlToken> tokens, int offset)
        {
            int low = 0, high = tokens.Count - 1, result = tokens.Count;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                if (tokens[mid].End > offset) { result = mid; high = mid - 1; }
                else low = mid + 1;
            }
            return result;
        }

        /// <summary>Token that contains the offset, or the token immediately before it.</summary>
        public static SqlToken TokenAt(IList<SqlToken> tokens, int offset, bool includeComments = false)
        {
            if (tokens == null || tokens.Count == 0) return null;
            int index = IndexAt(tokens, offset);
            if (index < tokens.Count && tokens[index].Start <= offset && offset < tokens[index].End)
                return includeComments || !tokens[index].IsComment ? tokens[index] : null;
            for (int i = Math.Min(index, tokens.Count) - 1; i >= 0; i--)
            {
                if (!includeComments && tokens[i].IsComment) continue;
                return tokens[i];
            }
            return null;
        }

        private static SqlToken Create(SqlTokenKind kind, string text, int start, int length)
            => new SqlToken { Kind = kind, Start = start, Length = length, Text = text.Substring(start, length) };
    }
}
