using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MSSQLTool.Completion
{
    internal sealed class SqlTextContext
    {
        public string BatchText { get; private set; }
        public string StatementText { get; private set; }
        public string MaskedBatch { get; private set; }
        public string MaskedStatement { get; private set; }
        /// <summary>Offset of <see cref="StatementText"/> inside the text the caller passed in.</summary>
        public int StatementStart { get; private set; }

        public static SqlTextContext Create(string textBeforeCaret)
        {
            string source = textBeforeCaret ?? string.Empty;
            return Create(source, MaskCommentsAndStrings(source));
        }

        /// <summary>
        /// Caller-supplied mask overload. Masking is a left-to-right state
        /// machine, so the mask of a longer text is valid for its prefix — the
        /// analyze pipeline masks the full session text once and reuses it here
        /// instead of masking the same characters repeatedly.
        /// </summary>
        public static SqlTextContext Create(string textBeforeCaret, string maskedSource)
        {
            string source = textBeforeCaret ?? string.Empty;
            string masked = maskedSource ?? MaskCommentsAndStrings(source);
            int batchStart = 0;
            foreach (Match match in Regex.Matches(masked, @"(?im)^\s*GO(?:\s+\d+)?\s*(?:\r?\n|$)"))
                batchStart = match.Index + match.Length;

            string batch = source.Substring(Math.Min(batchStart, source.Length));
            string maskedBatch = masked.Substring(Math.Min(batchStart, masked.Length));
            int statementStart = FindStatementStart(maskedBatch);
            string statement = batch.Substring(Math.Min(statementStart, batch.Length));
            return new SqlTextContext
            {
                BatchText = batch,
                StatementText = statement,
                StatementStart = Math.Min(statementStart, batch.Length) + batchStart,
                MaskedBatch = maskedBatch,
                MaskedStatement = maskedBatch.Substring(Math.Min(statementStart, maskedBatch.Length))
            };
        }

        /// <summary>
        /// Moves the statement window forward when the parse found a boundary the scan cannot see
        /// (two statements written on one line, with no semicolon between them).
        /// </summary>
        internal void AdvanceStatement(int offset)
        {
            int shift = Math.Max(0, Math.Min(offset, StatementText.Length));
            if (shift == 0) return;
            StatementText = StatementText.Substring(shift);
            MaskedStatement = MaskedStatement.Substring(Math.Min(shift, MaskedStatement.Length));
            StatementStart += shift;
        }

        private static int FindStatementStart(string masked)
        {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < masked.Length; i++)
            {
                if (masked[i] == '(') depth++;
                else if (masked[i] == ')') depth = Math.Max(0, depth - 1);
                else if (masked[i] == ';' && depth == 0) start = i + 1;
            }

            // ScriptDom used to answer this here with its own parse of the batch, which doubled the
            // parse cost of every keystroke.  The semantic model parses the same text a few lines
            // later and reports the statement start from that tree, so the answer is applied there
            // instead (see SqlSemanticModel.StatementStart / AdvanceStatement).

            // EXEC accepts a free-form argument tail, so an incomplete parse can
            // otherwise absorb a following snippet when both start on one line:
            // "EXEC dbo.p @x=NULL  SELECT * ...". Strings/comments are already
            // masked, and procedure argument expressions cannot contain a
            // top-level statement, so a later starter safely begins a new scope.
            int executeStart = start;
            string executeTail = (masked ?? string.Empty).Substring(Math.Min(executeStart, (masked ?? string.Empty).Length));
            if (Regex.IsMatch(executeTail, @"^\s*EXEC(?:UTE)?\b", RegexOptions.IgnoreCase))
            {
                foreach (Match match in Regex.Matches(executeTail,
                    @"(?i)(?<=[\s;,])(?<keyword>EXEC(?:UTE)?|SELECT|INSERT|UPDATE|DELETE|MERGE|DECLARE|SET|CREATE|ALTER|DROP|TRUNCATE|USE|PRINT|IF|WHILE|BEGIN|DBCC)\b"))
                {
                    int candidate = executeStart + match.Groups["keyword"].Index;
                    if (candidate <= executeStart) continue;
                    int currentDepth = 0;
                    for (int i = executeStart; i < candidate; i++)
                    {
                        if (masked[i] == '(') currentDepth++;
                        else if (masked[i] == ')') currentDepth = Math.Max(0, currentDepth - 1);
                    }
                    if (currentDepth == 0) start = candidate;
                }
            }

            // An incomplete trailing statement may be omitted from ScriptDom's AST.
            // Recover only top-level, line-leading starters after the last parsed
            // statement; parentheses prevent subqueries from being mistaken for a
            // new batch statement.
            foreach (Match match in Regex.Matches(masked ?? string.Empty,
                @"(?im)^[ \t]*(?<keyword>EXEC(?:UTE)?|SELECT|INSERT|UPDATE|DELETE|MERGE|DECLARE|SET|CREATE|ALTER|DROP|TRUNCATE|USE|PRINT|IF|WHILE|BEGIN|DBCC)\b"))
            {
                int currentDepth = 0;
                for (int i = start; i < match.Index; i++)
                {
                    if (masked[i] == '(') currentDepth++;
                    else if (masked[i] == ')') currentDepth--;
                    if (currentDepth < 0) currentDepth = 0;
                }
                if (match.Index <= start || currentDepth != 0) continue;
                string keyword = match.Groups["keyword"].Value;
                string first = FirstWord(masked, start, match.Index);
                if (IsContinuation(first, keyword, masked, match.Index, start)) continue;
                start = match.Index;
            }
            return start;
        }

        private static string FirstWord(string text, int start, int end)
        {
            int index = start;
            while (index < end && !char.IsLetter(text[index])) index++;
            int wordStart = index;
            while (index < end && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
            return index > wordStart ? text.Substring(wordStart, index - wordStart) : string.Empty;
        }

        private static string PreviousWord(string text, int start)
        {
            int index = start - 1;
            while (index >= 0 && char.IsWhiteSpace(text[index])) index--;
            int end = index + 1;
            while (index >= 0 && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index--;
            return index + 1 < end ? text.Substring(index + 1, end - index - 1) : string.Empty;
        }

        private static bool IsContinuation(string firstKeyword, string word, string text, int start, int statementStart)
        {
            if (string.IsNullOrEmpty(word)) return false;
            if ((string.Equals(firstKeyword, "CREATE", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(firstKeyword, "ALTER", StringComparison.OrdinalIgnoreCase))
                && HasTopLevelAs(text, statementStart, start)) return true;
            if (string.Equals(word, "SET", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(firstKeyword, "UPDATE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(firstKeyword, "MERGE", StringComparison.OrdinalIgnoreCase))) return true;
            if (string.Equals(word, "SELECT", StringComparison.OrdinalIgnoreCase))
            {
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

        private static bool HasTopLevelAs(string text, int start, int end)
        {
            int depth = 0;
            for (int i = Math.Max(0, start); i + 1 < end; i++)
            {
                if (text[i] == '(') { depth++; continue; }
                if (text[i] == ')') { depth = Math.Max(0, depth - 1); continue; }
                if (depth != 0 || (text[i] != 'A' && text[i] != 'a')) continue;
                if (i > start && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) continue;
                if (text[i + 1] != 'S' && text[i + 1] != 's') continue;
                if (i + 2 < end && (char.IsLetterOrDigit(text[i + 2]) || text[i + 2] == '_')) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// One state machine, two answers: the masked text (comment and string content blanked) and the
        /// state at a limit (inside a comment / string / quoted identifier, and whether the last
        /// character was blanked).  Both used to be written out separately, which is how they drift.
        /// </summary>
        private static string Scan(string sql, int limit, out bool inside, out bool lastMasked)
        {
            inside = false;
            lastMasked = false;
            string value = sql ?? string.Empty;
            int end = Math.Max(0, Math.Min(limit, value.Length));
            var output = new StringBuilder(value);
            bool lineComment = false, blockComment = false, quoted = false, doubleQuoted = false, bracketed = false;
            bool masked = false;
            for (int i = 0; i < end; i++)
            {
                char c = value[i], next = i + 1 < end ? value[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') { lineComment = false; masked = false; }
                    else { output[i] = ' '; masked = true; }
                }
                else if (blockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        output[i] = ' ';
                        output[i + 1 < output.Length ? i + 1 : i] = ' ';
                        blockComment = false;
                        i++;
                        masked = true;
                    }
                    else
                    {
                        output[i] = c == '\r' || c == '\n' ? c : ' ';
                        masked = true;
                    }
                }
                else if (quoted)
                {
                    if (c == '\'' && next == '\'')
                    {
                        output[i] = ' ';
                        output[i + 1 < output.Length ? i + 1 : i] = ' ';
                        i++;
                        masked = true;
                    }
                    else if (c == '\'') { output[i] = ' '; quoted = false; masked = true; }
                    else { output[i] = c == '\r' || c == '\n' ? c : ' '; masked = true; }
                }
                else if (doubleQuoted)
                {
                    if (c == '"' && next == '"') i++;
                    else if (c == '"') doubleQuoted = false;
                    masked = false;
                }
                else if (bracketed)
                {
                    if (c == ']' && next == ']') i++;
                    else if (c == ']') bracketed = false;
                    masked = false;
                }
                else if (c == '-' && next == '-') { output[i] = ' '; if (i + 1 < output.Length) output[i + 1] = ' '; lineComment = true; i++; masked = true; }
                else if (c == '/' && next == '*') { output[i] = ' '; if (i + 1 < output.Length) output[i + 1] = ' '; blockComment = true; i++; masked = true; }
                else if (c == '\'') { output[i] = ' '; quoted = true; masked = true; }
                else if (c == '"') { doubleQuoted = true; masked = false; }
                else if (c == '[') { bracketed = true; masked = false; }
                else masked = false;
                lastMasked = masked;
            }
            inside = lineComment || blockComment || quoted || doubleQuoted || bracketed;
            return output.ToString();
        }

        public static string MaskCommentsAndStrings(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return string.Empty;
            return Scan(sql, sql.Length, out bool _, out bool __);
        }

        public static bool IsInsideCommentOrString(string sql, int offset)
        {
            bool inside, lastMasked;
            ScanState(sql, offset, out inside, out lastMasked);
            return inside;
        }

        /// <summary>
        /// Single scan answering both questions the analyze pipeline asks about
        /// the text before the caret: whether the caret sits inside a comment,
        /// string or bracketed identifier, and whether the final character would
        /// have been blanked by <see cref="MaskCommentsAndStrings"/> (i.e. it was
        /// comment/string content or an opening quote/dash/slash).
        /// </summary>
        public static void ScanState(string sql, int offset, out bool inside, out bool lastMasked)
        {
            Scan(sql, offset, out inside, out lastMasked);
        }

        /// <summary>Replaces the per-line GO regex in suffix scanning.</summary>
        public static bool IsGoLine(string line)
        {
            string value = (line ?? string.Empty).Trim();
            if (!value.StartsWith("GO", StringComparison.OrdinalIgnoreCase)) return false;
            if (value.Length == 2) return true;
            for (int i = 2; i < value.Length; i++)
                if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }
    }
}
