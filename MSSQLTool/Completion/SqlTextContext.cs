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
            int statementStart = FindStatementStart(batch, maskedBatch);
            string statement = batch.Substring(Math.Min(statementStart, batch.Length));
            return new SqlTextContext
            {
                BatchText = batch,
                StatementText = statement,
                MaskedBatch = maskedBatch,
                MaskedStatement = maskedBatch.Substring(Math.Min(statementStart, maskedBatch.Length))
            };
        }

        private static int FindStatementStart(string source, string masked)
        {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < masked.Length; i++)
            {
                if (masked[i] == '(') depth++;
                else if (masked[i] == ')') depth = Math.Max(0, depth - 1);
                else if (masked[i] == ';' && depth == 0) start = i + 1;
            }

            // ScriptDom provides the authoritative top-level statement boundary.
            // It can separate normal T-SQL statements even when semicolons are
            // omitted, which is essential after expanding a multi-line snippet.
            try
            {
                var parser = new TSql170Parser(true);
                TSqlFragment fragment = parser.Parse(new StringReader(source ?? string.Empty), out IList<ParseError> _);
                var script = fragment as TSqlScript;
                TSqlStatement parsed = script?.Batches.SelectMany(b => b.Statements)
                    .Where(s => s.StartOffset <= (source ?? string.Empty).Length)
                    .OrderBy(s => s.StartOffset)
                    .LastOrDefault();
                if (parsed != null) start = Math.Max(start, parsed.StartOffset);
            }
            catch { }

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
                @"(?im)^[ \t]*(?:EXEC(?:UTE)?|SELECT|INSERT|UPDATE|DELETE|MERGE|DECLARE|SET|CREATE|ALTER|DROP|TRUNCATE|USE|PRINT|IF|WHILE|BEGIN|DBCC)\b"))
            {
                int currentDepth = 0;
                for (int i = start; i < match.Index; i++)
                {
                    if (masked[i] == '(') currentDepth++;
                    else if (masked[i] == ')') currentDepth = Math.Max(0, currentDepth - 1);
                }
                if (match.Index > start && currentDepth == 0) start = match.Index;
            }
            return start;
        }

        public static string MaskCommentsAndStrings(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return string.Empty;
            var output = new StringBuilder(sql);
            bool lineComment = false, blockComment = false, quoted = false, doubleQuoted = false, bracketed = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') lineComment = false; else output[i] = ' ';
                }
                else if (blockComment)
                {
                    output[i] = c == '\r' || c == '\n' ? c : ' ';
                    if (c == '*' && next == '/') { output[i + 1] = ' '; blockComment = false; i++; }
                }
                else if (quoted)
                {
                    output[i] = c == '\r' || c == '\n' ? c : ' ';
                    if (c == '\'' && next == '\'') { output[i + 1] = ' '; i++; }
                    else if (c == '\'') quoted = false;
                }
                else if (doubleQuoted)
                {
                    if (c == '"' && next == '"') i++;
                    else if (c == '"') doubleQuoted = false;
                }
                else if (bracketed)
                {
                    if (c == ']' && next == ']') i++;
                    else if (c == ']') bracketed = false;
                }
                else if (c == '-' && next == '-') { output[i] = output[i + 1] = ' '; lineComment = true; i++; }
                else if (c == '/' && next == '*') { output[i] = output[i + 1] = ' '; blockComment = true; i++; }
                else if (c == '\'') { output[i] = ' '; quoted = true; }
                else if (c == '"') doubleQuoted = true;
                else if (c == '[') bracketed = true;
            }
            return output.ToString();
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
            string value = sql ?? string.Empty;
            int limit = Math.Max(0, Math.Min(offset, value.Length));
            bool lineComment = false, blockComment = false, quoted = false, doubleQuoted = false, bracketed = false;
            bool masked = false;
            for (int i = 0; i < limit; i++)
            {
                char c = value[i], next = i + 1 < limit ? value[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') { lineComment = false; masked = false; }
                    else masked = true;
                }
                else if (blockComment)
                {
                    if (c == '*' && next == '/') { blockComment = false; i++; masked = true; }
                    else masked = true;
                }
                else if (quoted)
                {
                    if (c == '\'' && next == '\'') { i++; masked = true; }
                    else if (c == '\'') { quoted = false; masked = true; }
                    else masked = true;
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
                else if (c == '-' && next == '-') { lineComment = true; i++; masked = true; }
                else if (c == '/' && next == '*') { blockComment = true; i++; masked = true; }
                else if (c == '\'') { quoted = true; masked = true; }
                else if (c == '"') { doubleQuoted = true; masked = false; }
                else if (c == '[') { bracketed = true; masked = false; }
                else masked = false;
            }
            inside = lineComment || blockComment || quoted || doubleQuoted || bracketed;
            lastMasked = limit > 0 && masked;
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
