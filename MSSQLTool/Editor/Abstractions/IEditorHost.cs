using System;

namespace MSSQLTool.Editor
{
    /// <summary>
    /// What the current shell/editor offers.  The values are probed once per attached view
    /// and written to the log, so a future SSMS that changes the editor can be diagnosed
    /// without a debugger.
    /// </summary>
    internal sealed class EditorCapabilities
    {
        public string ShellVersion = string.Empty;
        public string EditorAssemblyVersion = string.Empty;
        public bool HasVsEditorAdapter;
        public bool HasWpfTextView;
        public bool HasTextAdornmentLayer;
        public bool HasTrackingSpans;

        public bool SupportsDecorations => HasWpfTextView && HasTextAdornmentLayer;

        public string Summary =>
            "shell=" + (string.IsNullOrEmpty(ShellVersion) ? "?" : ShellVersion) +
            " editor=" + (string.IsNullOrEmpty(EditorAssemblyVersion) ? "?" : EditorAssemblyVersion) +
            " adapter=" + (HasVsEditorAdapter ? "yes" : "no") +
            " wpfView=" + (HasWpfTextView ? "yes" : "no") +
            " adornments=" + (HasTextAdornmentLayer ? "yes" : "no") +
            " trackingSpans=" + (HasTrackingSpans ? "yes" : "no") +
            " path=" + EditorPath;
        /// <summary>Which editor implementation is actually serving this view.</summary>
        public string EditorPath = "textmanager";
    }

    internal readonly struct EditorSelection
    {
        public EditorSelection(int startLine, int startColumn, int endLine, int endColumn)
        {
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
        }

        public int StartLine { get; }
        public int StartColumn { get; }
        public int EndLine { get; }
        public int EndColumn { get; }
    }

    /// <summary>
    /// The only editor surface the analysis/completion pipeline is allowed to touch.
    /// Implementations live under <c>Editor/TextManager</c> and <c>Editor/Vs2022</c>;
    /// nothing else in the new pipeline references TextManager or the VS text model.
    /// </summary>
    internal interface IEditorHost : IDisposable
    {
        EditorCapabilities Capabilities { get; }

        IntPtr WindowHandle { get; }

        bool TryGetCaret(out int line, out int column);

        bool TryGetSelection(out EditorSelection selection);

        bool TryReadAllText(out string text);

        bool TryGetLineText(int line, out string text);

        bool TryReplace(int startLine, int startColumn, int endLine, int endColumn, string text, string description);

        bool TryGetCaretScreenPoint(out int x, out int y, out int lineHeight);

        bool TryGetViewport(out int firstVisibleLine, out int visibleLines, out int lineHeight);

        /// <summary>Maps a 1-based line/column to client coordinates of the text view.</summary>
        bool TryGetPointOfLineColumn(int line, int column, out int x, out int y);

        /// <summary>Brings the caret to a 1-based line/column and scrolls it into view.</summary>
        bool TryNavigateTo(int line, int column);
    }
}
