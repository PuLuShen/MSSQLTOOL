using MSSQLTool.Analysis;
using MSSQLTool.Editor;
using MSSQLTool.Model;

namespace MSSQLTool.Rendering
{
    /// <summary>
    /// Bridges the analysis quick fixes to the editor.  Plain text rewrites live in
    /// <see cref="QuickFixService"/>; the star expansion needs the editor command, so it is
    /// dispatched through <see cref="ActiveEditorLocator"/> here and never from Analysis.
    /// </summary>
    internal static class QuickFixActions
    {
        public static bool TryApply(IEditorHost host, string text, AnalysisDiagnostic diagnostic)
        {
            if (host == null || diagnostic == null) return false;

            if (string.Equals(diagnostic.RuleId, "select-star", System.StringComparison.OrdinalIgnoreCase))
            {
                TextPositions.ToLineColumn(text, diagnostic.Start, out int line, out int column);
                if (!host.TryNavigateTo(line, column)) return false;
                return ActiveEditorLocator.ExpandAsterisk();
            }

            return QuickFixService.TryApply(host, text, diagnostic);
        }
    }
}
