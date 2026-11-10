using System;
using System.Text.RegularExpressions;
using MSSQLTool.Editor;
using MSSQLTool.Model;

namespace MSSQLTool.Analysis
{
    /// <summary>
    /// The three zero-risk rewrites shipped with the first release.  Each one produces a single
    /// replace operation through <see cref="IEditorHost"/>, so the edit lands in one undo step.
    /// SELECT * expansion is delegated to the editor bridge, because it drives the existing
    /// metadata-aware asterisk expansion command instead of rewriting text here.
    /// </summary>
    internal static class QuickFixService
    {
        private static readonly Regex EqualsNull = new Regex(@"^\s*(?<op><>|!=|=)\s*NULL\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The fixable diagnostic under the caret, or null.  Used by the "Fix problem at caret"
        /// command so a fix does not require the Code Analysis window to be open.
        /// </summary>
        public static AnalysisDiagnostic FindFixableAtCaret(string text, int caret, AnalysisSettings settings)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (caret < 0 || caret > text.Length) caret = text.Length;
            foreach (AnalysisDiagnostic diagnostic in new AnalysisEngine().Analyze(text, settings ?? new AnalysisSettings(), caret))
            {
                if (!CanFix(diagnostic)) continue;
                int end = diagnostic.Start + Math.Max(1, diagnostic.Length);
                if (diagnostic.Start <= caret && caret <= end) return diagnostic;
            }
            return null;
        }

        public static bool CanFix(AnalysisDiagnostic diagnostic)
        {
            if (diagnostic == null) return false;
            switch (diagnostic.RuleId)
            {
                case "equals-null":
                case "select-star":
                case "missing-as-alias":
                    return true;
                default:
                    return false;
            }
        }

        public static string Title(AnalysisDiagnostic diagnostic)
        {
            if (diagnostic == null) return string.Empty;
            switch (diagnostic.RuleId)
            {
                case "equals-null": return LocalizationManager.IsChinese ? "改为 IS NULL" : "Rewrite to IS NULL";
                case "select-star": return LocalizationManager.IsChinese ? "展开为列清单" : "Expand to column list";
                case "missing-as-alias": return LocalizationManager.IsChinese ? "补上 AS" : "Add AS";
                default: return string.Empty;
            }
        }

        public static bool TryApply(IEditorHost host, string text, AnalysisDiagnostic diagnostic)
        {
            if (diagnostic == null || host == null || string.IsNullOrEmpty(text)) return false;
            if (diagnostic.Start < 0 || diagnostic.Start >= text.Length) return false;

            switch (diagnostic.RuleId)
            {
                case "equals-null": return RewriteEqualsNull(host, text, diagnostic);
                case "missing-as-alias": return InsertAsAlias(host, text, diagnostic);
                default:
                    return false;
            }
        }

        /// <summary>
        /// The text edit a fix would perform, independent of any editor.  The light bulb applies it
        /// through ITextBuffer and the tool window through IEditorHost, so both paths share the
        /// same replacement logic.
        /// </summary>
        public static bool TryBuildEdit(string text, AnalysisDiagnostic diagnostic, out int start, out int length, out string replacement)
        {
            start = 0;
            length = 0;
            replacement = null;
            if (diagnostic == null || string.IsNullOrEmpty(text)) return false;
            if (diagnostic.Start < 0 || diagnostic.Start >= text.Length) return false;

            switch (diagnostic.RuleId)
            {
                case "equals-null":
                {
                    int spanLength = Math.Min(diagnostic.Length, text.Length - diagnostic.Start);
                    Match match = EqualsNull.Match(text.Substring(diagnostic.Start, spanLength));
                    if (!match.Success) return false;
                    start = diagnostic.Start;
                    length = spanLength;
                    replacement = match.Groups["op"].Value != "=" ? "IS NOT NULL" : "IS NULL";
                    return true;
                }
                case "missing-as-alias":
                {
                    if (diagnostic.Start <= 0) return false;
                    string before = text.Substring(Math.Max(0, diagnostic.Start - 4), Math.Min(4, diagnostic.Start)).TrimEnd();
                    if (before.EndsWith("AS", StringComparison.OrdinalIgnoreCase)) return false;
                    start = diagnostic.Start;
                    length = 0;
                    replacement = "AS ";
                    return true;
                }
                default:
                    return false;
            }
        }

        private static bool RewriteEqualsNull(IEditorHost host, string text, AnalysisDiagnostic diagnostic)
        {
            if (!TryBuildEdit(text, diagnostic, out int start, out int length, out string replacement)) return false;

            TextPositions.ToLineColumn(text, start, out int startLine, out int startColumn);
            TextPositions.ToLineColumn(text, start + length, out int endLine, out int endColumn);
            return host.TryReplace(startLine, startColumn, endLine, endColumn, replacement, "Rewrite to " + replacement);
        }

        private static bool InsertAsAlias(IEditorHost host, string text, AnalysisDiagnostic diagnostic)
        {
            if (!TryBuildEdit(text, diagnostic, out int start, out int length, out string replacement)) return false;

            TextPositions.ToLineColumn(text, start, out int line, out int column);
            return host.TryReplace(line, column, line, column, replacement, "Add AS to the column alias");
        }
    }
}
