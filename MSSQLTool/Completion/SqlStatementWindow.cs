using System;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// The statement around the caret.  Completion only needs this slice: parsing and masking the
    /// whole document made every space, comma and dot cost O(document).  The scanner is a single
    /// allocation-free pass and stops at top-level semicolons, GO lines and line-leading statement
    /// starters, so a 600 KB multi-statement script analyzes a window of a few hundred bytes.
    /// </summary>
    internal sealed class SqlStatementWindow
    {
        public int Start { get; private set; }
        public int End { get; private set; }
        public int CaretOffset { get; private set; }
        public string BeforeCaret { get; private set; }
        public string AfterCaret { get; private set; }
        /// <summary>Line-relative column of the caret; Analyze has always taken a column, not an offset.</summary>
        public int CaretColumn { get; private set; }

        public static SqlStatementWindow Find(string text, int caretOffset)
        {
            string value = text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(caretOffset, value.Length));
            int start = 0;
            int end = value.Length;
            Scan(value, caret, ref start, ref end);
            if (start > caret) start = 0;
            if (end < caret) end = value.Length;
            if (start > end) start = end;

            int lineStart = value.LastIndexOf('\n', Math.Max(0, caret - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int column = Math.Max(0, caret - lineStart);
            return new SqlStatementWindow
            {
                Start = start,
                End = end,
                CaretOffset = caret,
                BeforeCaret = value.Substring(start, caret - start),
                AfterCaret = value.Substring(caret, end - caret),
                CaretColumn = column
            };
        }

        /// <summary>FNV-1a over a document range; the session-local cache uses it to notice prefix edits.</summary>
        public static int HashRange(string text, int start, int length)
        {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            unchecked
            {
                int hash = (int)2166136261;
                int end = Math.Min(text.Length, start + length);
                for (int i = Math.Max(0, start); i < end; i++)
                    hash = (hash ^ text[i]) * 16777619;
                return hash;
            }
        }

        private static void Scan(string text, int caret, ref int start, ref int end)
        {
            int depth = 0;
            int state = 0; // 0 normal, 1 line comment, 2 block comment, 3 single quote, 4 double quote, 5 bracket
            int lineStart = 0;
            int goLineStart = 0;
            bool endFound = false;
            bool moduleBody = false;
            string firstKeyword = null;
            int statementStart = 0;

            for (int i = 0; i <= text.Length; i++)
            {
                // Once the first boundary after the caret is known, the rest of the document
                // cannot change the window.  Stopping here keeps a 600 KB script from being
                // walked twice (once to find the start and once to find the end).
                if (endFound && i > end) break;
                char c = i < text.Length ? text[i] : '\0';
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (state == 1)
                {
                    if (c == '\r' || c == '\n')
                    {
                        state = 0;
                        if (c == '\n') lineStart = i + 1;
                    }
                    continue;
                }
                if (state == 2)
                {
                    if (c == '*' && next == '/') { state = 0; lineStart = -1; i++; }
                    continue;
                }
                if (state == 3)
                {
                    if (c == '\'')
                    {
                        if (next == '\'') i++;
                        else { state = 0; lineStart = -1; }
                    }
                    continue;
                }
                if (state == 4)
                {
                    if (c == '"')
                    {
                        if (next == '"') i++;
                        else { state = 0; lineStart = -1; }
                    }
                    continue;
                }
                if (state == 5)
                {
                    if (c == ']')
                    {
                        if (next == ']') i++;
                        else { state = 0; lineStart = -1; }
                    }
                    continue;
                }

                if (i >= text.Length) break;

                if (c == '-' && next == '-') { state = 1; i++; continue; }
                if (c == '/' && next == '*') { state = 2; lineStart = -1; i++; continue; }
                if (c == '\'') { state = 3; lineStart = -1; continue; }
                if (c == '"') { state = 4; lineStart = -1; continue; }
                if (c == '[') { state = 5; lineStart = -1; continue; }

                if (c == '(') { depth++; if (lineStart >= 0) lineStart = -1; continue; }
                if (c == ')') { depth = Math.Max(0, depth - 1); if (lineStart >= 0) lineStart = -1; continue; }

                if (c == ';' && depth == 0)
                {
                    Boundary(i + 1, i + 1, caret, ref start, ref end, ref endFound);
                    if (i + 1 <= caret) { firstKeyword = null; moduleBody = false; statementStart = i + 1; }
                    if (lineStart >= 0) lineStart = -1;
                    continue;
                }

                if (c == '\n')
                {
                    if (depth == 0 && IsGoLine(text, goLineStart, i))
                    {
                        Boundary(i + 1, goLineStart, caret, ref start, ref end, ref endFound);
                        if (i + 1 <= caret) { firstKeyword = null; moduleBody = false; statementStart = i + 1; }
                    }
                    lineStart = i + 1;
                    goLineStart = i + 1;
                    continue;
                }

                if (lineStart >= 0 && depth == 0 && c != ' ' && c != '\t' && c != '\r')
                {
                    // A statement starter is a boundary only at the first token of a line;
                    // checking at the newline would mistake the line the caret is on for the
                    // start of the next statement.
                    string word = ReadWord(text, i);
                    if (IsStatementStarter(text, i, text.Length))
                    {
                        if (IsContinuation(firstKeyword, word, text, i, moduleBody))
                        {
                            lineStart = -1;
                            continue;
                        }
                        Boundary(i, i, caret, ref start, ref end, ref endFound);
                        if (i <= caret) { firstKeyword = word; statementStart = i; }
                    }
                    else if (firstKeyword == null && word.Length > 0)
                    {
                        firstKeyword = word;
                    }
                    lineStart = -1;
                }
                else if (firstKeyword == null && depth == 0 && char.IsLetter(c))
                {
                    // A statement can begin on the same line as the preceding semicolon, so its
                    // first keyword is not necessarily at the start of a line.
                    firstKeyword = ReadWord(text, i);
                }
                if (depth == 0 && (c == 'A' || c == 'a') && (i == 0 || !IsWordChar(text[i - 1]))
                    && (string.Equals(firstKeyword, "CREATE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(firstKeyword, "ALTER", StringComparison.OrdinalIgnoreCase))
                    && Matches(text, i, text.Length, "AS"))
                {
                    // Only a view's AS body is a single query.  Procedure, function and
                    // trigger bodies are statement lists: a line-leading DML keyword is a
                    // new statement there, not a continuation of the previous one.
                    moduleBody = IsViewDefinition(text, statementStart, i);
                }
            }
        }

        private static bool IsViewDefinition(string text, int start, int end)
        {
            int index = Math.Max(0, start);
            while (index < end)
            {
                char c = text[index];
                char next = index + 1 < end ? text[index + 1] : '\0';
                if (c == '-' && next == '-')
                {
                    index += 2;
                    while (index < end && text[index] != '\n') index++;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    index += 2;
                    while (index + 1 < end && !(text[index] == '*' && text[index + 1] == '/')) index++;
                    index = Math.Min(end, index + 2);
                    continue;
                }
                if (c == '\'' || c == '"' || c == '[')
                {
                    char close = c == '[' ? ']' : c;
                    index++;
                    while (index < end)
                    {
                        if (text[index] == close)
                        {
                            if (index + 1 < end && text[index + 1] == close) { index += 2; continue; }
                            index++;
                            break;
                        }
                        index++;
                    }
                    continue;
                }
                if (!IsWordChar(c)) { index++; continue; }
                int wordStart = index;
                while (index < end && IsWordChar(text[index])) index++;
                string word = text.Substring(wordStart, index - wordStart);
                if (string.Equals(word, "VIEW", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(word, "PROC", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(word, "PROCEDURE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(word, "FUNCTION", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(word, "TRIGGER", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return false;
        }

        private static bool IsContinuation(string firstKeyword, string word, string text, int start, bool moduleBody)
        {
            if (string.IsNullOrEmpty(word)) return false;
            // The body of CREATE/ALTER VIEW is one query; its line-leading SELECT/SET/BEGIN
            // belongs to that query.  Other module bodies are statement lists.
            if (moduleBody) return true;
            // SET is a clause of UPDATE, not a new statement, when written on its own line.
            if (string.Equals(word, "SET", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(firstKeyword, "UPDATE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(firstKeyword, "MERGE", StringComparison.OrdinalIgnoreCase))) return true;
            if (string.Equals(word, "SELECT", StringComparison.OrdinalIgnoreCase))
            {
                // INSERT ... SELECT and a UNION branch both put SELECT at the start of a line
                // without starting a new statement.
                if (string.Equals(firstKeyword, "INSERT", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(firstKeyword, "WITH", StringComparison.OrdinalIgnoreCase)) return true;
                string previous = PreviousWord(text, start);
                if (string.Equals(previous, "UNION", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(previous, "ALL", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(previous, "EXCEPT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(previous, "INTERSECT", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if ((string.Equals(word, "UPDATE", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(word, "DELETE", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(word, "INSERT", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(word, "MERGE", StringComparison.OrdinalIgnoreCase))
                && string.Equals(firstKeyword, "WITH", StringComparison.OrdinalIgnoreCase)) return true;
            if ((string.Equals(word, "UPDATE", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(word, "DELETE", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(word, "INSERT", StringComparison.OrdinalIgnoreCase))
                && string.Equals(firstKeyword, "MERGE", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string PreviousWord(string text, int start)
        {
            int index = start - 1;
            while (index >= 0 && char.IsWhiteSpace(text[index])) index--;
            int end = index + 1;
            while (index >= 0 && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index--;
            return index + 1 < end ? text.Substring(index + 1, end - index - 1) : string.Empty;
        }

        private static string ReadWord(string text, int start)
        {
            int end = start;
            while (end < text.Length && IsWordChar(text[end])) end++;
            return end > start ? text.Substring(start, end - start) : string.Empty;
        }

        private static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$';

        private static void Boundary(int nextStart, int previousEnd, int caret, ref int start, ref int end, ref bool endFound)
        {
            if (nextStart <= caret)
            {
                if (nextStart > start) start = nextStart;
                return;
            }
            if (!endFound)
            {
                end = Math.Max(caret, Math.Min(previousEnd, nextStart));
                endFound = true;
            }
        }

        private static bool IsGoLine(string text, int start, int end)
        {
            int index = start;
            while (index < end && IsGoWhitespace(text[index])) index++;
            if (index >= end) return false;
            if (!Matches(text, index, end, "GO")) return false;
            index += 2;
            while (index < end && IsGoWhitespace(text[index])) index++;
            if (index >= end) return true;
            while (index < end && text[index] >= '0' && text[index] <= '9') index++;
            while (index < end && IsGoWhitespace(text[index])) index++;
            return index >= end;
        }

        private static bool IsGoWhitespace(char value) => value == ' ' || value == '\t' || value == '\r';

        private static bool IsStatementStarter(string text, int start, int end)
        {
            // Words are compared in place; a line-leading starter is exactly what the old text
            // scan split on, so existing completion contexts keep their boundaries.
            for (int i = 0; i < Starters.Length; i++)
            {
                if (Matches(text, start, end, Starters[i])) return true;
            }
            return false;
        }

        private static bool Matches(string text, int start, int end, string word)
        {
            if (start + word.Length > end) return false;
            for (int i = 0; i < word.Length; i++)
                if (char.ToUpperInvariant(text[start + i]) != char.ToUpperInvariant(word[i])) return false;
            if (start + word.Length < end)
            {
                char after = text[start + word.Length];
                if (char.IsLetterOrDigit(after) || after == '_' || after == '#' || after == '@') return false;
            }
            return true;
        }

        private static readonly string[] Starters =
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "SET", "CREATE", "ALTER",
            "DROP", "TRUNCATE", "USE", "PRINT", "EXEC", "EXECUTE", "IF", "WHILE", "BEGIN", "DBCC",
            "GRANT", "DENY", "REVOKE", "RETURN", "WAITFOR", "RAISERROR", "THROW", "GOTO", "BREAK",
            "CONTINUE", "COMMIT", "ROLLBACK", "SAVE"
        };
    }
}
