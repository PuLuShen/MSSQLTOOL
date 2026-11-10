using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace MSSQLTool.Editor.TextManager
{
    /// <summary>
    /// Editor host built only on the stable TextManager COM interfaces
    /// (<see cref="IVsTextView"/> / <see cref="IVsTextLines"/>).  Those interfaces have been
    /// shipped unchanged since VS 2002, so this path keeps working when a future SSMS
    /// changes or replaces the WPF editor stack.
    /// <para>
    /// The view exposes no scroll event in this interop assembly (IVsTextViewEvents has no
    /// Advise method), so scrolling is detected by polling GetScrollInfo from the overlay
    /// timer.  Reading the scroll position is a few field reads, and the timer only does real
    /// work when the signature actually changed.
    /// </para>
    /// </summary>
    internal sealed class TextManagerEditorHost : IEditorHost
    {
        private readonly IVsTextView view;
        private readonly EditorCapabilities capabilities;
        private bool disposed;

        public TextManagerEditorHost(IVsTextView view, EditorCapabilities capabilities)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.capabilities = capabilities ?? new EditorCapabilities();
        }

        public EditorCapabilities Capabilities => capabilities;

        public IntPtr WindowHandle
        {
            get
            {
                try { return view.GetWindowHandle(); }
                catch (Exception) { return IntPtr.Zero; }
            }
        }

        public bool TryGetCaret(out int line, out int column)
        {
            line = 0;
            column = 0;
            if (disposed) return false;
            try { return view != null && view.GetCaretPos(out line, out column) == VSConstants.S_OK; }
            catch (Exception) { return false; }
        }

        public bool TryGetSelection(out EditorSelection selection)
        {
            selection = default;
            try
            {
                if (view == null) return false;
                if (view.GetSelection(out int anchorLine, out int anchorColumn, out int endLine, out int endColumn) != VSConstants.S_OK)
                    return false;
                selection = new EditorSelection(anchorLine, anchorColumn, endLine, endColumn);
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool TryReadAllText(out string text)
        {
            text = string.Empty;
            try
            {
                if (view.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null) return false;
                if (lines.GetLastLineIndex(out int lastLine, out int lastColumn) != VSConstants.S_OK) return false;
                return lines.GetLineText(0, 0, lastLine, lastColumn, out text) == VSConstants.S_OK;
            }
            catch (Exception) { text = string.Empty; return false; }
        }

        public bool TryGetLineText(int line, out string text)
        {
            text = string.Empty;
            try
            {
                if (view.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null) return false;
                if (lines.GetLengthOfLine(line, out int length) != VSConstants.S_OK) return false;
                return lines.GetLineText(line, 0, line, length, out text) == VSConstants.S_OK;
            }
            catch (Exception) { return false; }
        }

        public bool TryReplace(int startLine, int startColumn, int endLine, int endColumn, string text, string description)
        {
            if (view == null) return false;
            if (view.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null) return false;

            IntPtr buffer = IntPtr.Zero;
            try
            {
                string value = text ?? string.Empty;
                buffer = Marshal.StringToCoTaskMemUni(value);
                var changed = new TextSpan[1];
                return lines.ReplaceLines(startLine, startColumn, endLine, endColumn, buffer, value.Length, changed) == VSConstants.S_OK;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Editor host", "The edit could not be applied: " + description, ex);
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            }
        }

        public bool TryGetCaretScreenPoint(out int x, out int y, out int lineHeight)
        {
            x = 0;
            y = 0;
            lineHeight = 0;
            if (disposed) return false;
            try
            {
                if (view == null) return false;
                if (!TryGetCaret(out int line, out int column)) return false;
                var point = new Microsoft.VisualStudio.OLE.Interop.POINT[1];
                if (view.GetPointOfLineColumn(line, column, point) != VSConstants.S_OK) return false;
                if (view.GetLineHeight(out lineHeight) != VSConstants.S_OK || lineHeight <= 0) lineHeight = 18;

                IntPtr editor = view.GetWindowHandle();
                var origin = new NativePoint { X = point[0].x, Y = point[0].y };
                if (!ClientToScreen(editor, ref origin)) return false;
                x = origin.X;
                y = origin.Y;
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool TryGetPointOfLineColumn(int line, int column, out int x, out int y)
        {
            x = 0;
            y = 0;
            try
            {
                if (view == null) return false;
                var point = new Microsoft.VisualStudio.OLE.Interop.POINT[1];
                if (view.GetPointOfLineColumn(line, column, point) != VSConstants.S_OK) return false;
                x = point[0].x;
                y = point[0].y;
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool TryGetViewport(out int firstVisibleLine, out int visibleLines, out int lineHeight)
        {
            firstVisibleLine = 0;
            visibleLines = 0;
            lineHeight = 0;
            try
            {
                if (view == null) return false;
                if (view.GetLineHeight(out lineHeight) != VSConstants.S_OK || lineHeight <= 0) lineHeight = 18;
                if (view.GetScrollInfo(1, out _, out _, out int visibleUnits, out int firstVisibleUnit) == VSConstants.S_OK)
                {
                    firstVisibleLine = firstVisibleUnit;
                    visibleLines = Math.Max(1, visibleUnits);
                    return true;
                }

                IntPtr editor = view.GetWindowHandle();
                if (!GetClientRect(editor, out NativeRect rect)) return false;
                visibleLines = Math.Max(1, (rect.Bottom - rect.Top) / lineHeight);
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool TryNavigateTo(int line, int column)
        {
            try
            {
                if (view == null) return false;
                if (view.SetCaretPos(line, column) != VSConstants.S_OK) return false;
                var span = new TextSpan { iStartLine = line, iStartIndex = column, iEndLine = line, iEndIndex = column };
                view.EnsureSpanVisible(span);
                view.SendExplicitFocus();
                return true;
            }
            catch (Exception) { return false; }
        }

        public void Dispose()
        {
            disposed = true;
            GC.SuppressFinalize(this);
        }

        private struct NativeRect { public int Left, Top, Right, Bottom; }
        private struct NativePoint { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    }
}
