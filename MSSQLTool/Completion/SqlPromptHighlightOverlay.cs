using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.TextManager.Interop;
using MSSQLTool.Editor;
using MSSQLTool.Editor.Vs2022;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// SQL Prompt-style related-token highlighting.  A click-through transparent window is laid
    /// over the SQL editor, so the editor itself (and its undo buffer) is never modified.
    /// Rendering is driven by editor geometry (viewport line, line height, editor rectangle,
    /// caret position), not by "did the logical line change": scrolling, resizing and split
    /// views all move the rectangles even though the text stays identical.
    /// </summary>
    internal sealed class SqlPromptHighlightOverlay : IDisposable
    {
        private readonly IVsTextView textView;
        private readonly Func<bool> completionVisible;
        private readonly IEditorHost host;
        private readonly DispatcherTimer timer;
        private Window window;
        private Canvas canvas;
        private string lastText;
        private int lastCaret = -1;
        private List<HighlightSpan> spans = new List<HighlightSpan>();
        private bool shown;
        private bool loggedRects;
        private string lastGeometry;
        private int lastFirstVisible = int.MinValue;
        private DateTime lastScrollUtc = DateTime.MinValue;
        private bool disposed;

        public SqlPromptHighlightOverlay(IVsTextView textView, Func<bool> completionVisible, IEditorHost host = null)
        {
            this.textView = textView;
            this.completionVisible = completionVisible ?? (() => false);
            this.host = host;

            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            timer.Tick += (_, __) => Update(false);
            timer.Start();
        }

        /// <summary>Forces a redraw; used after scrolling, resizing or a split-view change.</summary>
        public void Refresh() => Update(true);

        private void Update(bool forceRender)
        {
            if (disposed) return;
            // The editor-native adornment is authoritative when it is composed; this window is only
            // the fallback for environments where MEF could not create it.
            if (RelatedHighlightActivation.IsAdornmentActive)
            {
                Hide();
                return;
            }
            try
            {
                if (textView == null)
                {
                    Hide();
                    return;
                }

                if (completionVisible())
                {
                    Hide();
                    return;
                }

                IntPtr editor = textView.GetWindowHandle();
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero || editor == IntPtr.Zero)
                {
                    Hide();
                    return;
                }

                GetWindowThreadProcessId(foreground, out uint foregroundProcess);
                if (foregroundProcess != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    Hide();
                    return;
                }

                if (textView.GetCaretPos(out int line, out int column) != 0)
                {
                    Hide();
                    return;
                }

                string text = ReadText();
                if (string.IsNullOrEmpty(text))
                {
                    Hide();
                    return;
                }

                int caret = OffsetOf(text, line, column);
                if (!string.Equals(text, lastText, StringComparison.Ordinal) || caret != lastCaret)
                {
                    lastText = text;
                    lastCaret = caret;
                    spans = SqlPromptHighlightAnalyzer.Analyze(text, caret);
                    loggedRects = false;
                    forceRender = true;
                    MSSQLToolPackage._logger?.Debug($"SQL-HIGHLIGHT scan line={line} column={column} caret={caret} spans={spans.Count}");
                    if (spans.Count == 0)
                    {
                        Hide();
                        return;
                    }
                }

                if (spans.Count == 0)
                {
                    Hide();
                    return;
                }

                if (host != null && host.TryGetViewport(out int firstVisible, out int visibleLines, out int lineHeight))
                {
                    if (lastFirstVisible != int.MinValue && firstVisible != lastFirstVisible)
                        lastScrollUtc = DateTime.UtcNow;
                    lastFirstVisible = firstVisible;
                    if ((DateTime.UtcNow - lastScrollUtc).TotalMilliseconds < 200)
                    {
                        // The painted boxes would trail the text during a scrollbar drag.
                        Hide();
                        return;
                    }
                }

                string geometry = BuildGeometrySignature(editor, line, column);
                if (!forceRender && shown && string.Equals(geometry, lastGeometry, StringComparison.Ordinal))
                    return;

                Render(text, line);
                lastGeometry = geometry;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL Prompt highlight overlay failed.");
                Hide();
            }
        }

        /// <summary>
        /// A stable description of everything that moves the painted rectangles: the editor's
        /// client rectangle (window resize / split), the first visible line (scroll), the line
        /// height (zoom / font) and the caret cell (cursor movement).
        /// </summary>
        private string BuildGeometrySignature(IntPtr editor, int caretLine, int caretColumn)
        {
            if (!GetClientRect(editor, out NativeRect client)) return string.Empty;
            int firstVisible = 0;
            int visibleLines = 0;
            int lineHeight = 0;
            try
            {
                if (host == null || !host.TryGetViewport(out firstVisible, out visibleLines, out lineHeight))
                {
                    textView.GetScrollInfo(1, out _, out _, out visibleLines, out firstVisible);
                    textView.GetLineHeight(out lineHeight);
                }
            }
            catch (Exception) { }

            return string.Concat(
                client.Left, ",", client.Top, ",", client.Right, ",", client.Bottom, "|",
                firstVisible, "|", visibleLines, "|", lineHeight, "|", caretLine, "|", caretColumn, "|",
                shown ? "1" : "0");
        }

        private string ReadText()
        {
            if (host != null) return host.TryReadAllText(out string hosted) ? hosted : string.Empty;
            if (textView.GetBuffer(out IVsTextLines lines) != 0 || lines == null) return string.Empty;
            if (lines.GetLastLineIndex(out int lastLine, out int lastColumn) != 0) return string.Empty;
            return lines.GetLineText(0, 0, lastLine, lastColumn, out string text) == 0 ? text : string.Empty;
        }

        private void Render(string text, int caretLine)
        {
            EnsureWindow();
            if (window == null) return;

            IntPtr editor = textView.GetWindowHandle();
            if (!GetClientRect(editor, out NativeRect client)) return;
            NativePoint origin = new NativePoint();
            if (!ClientToScreen(editor, ref origin)) return;

            int width = Math.Max(1, client.Right - client.Left);
            int height = Math.Max(1, client.Bottom - client.Top);
            if (width <= 1 || height <= 1)
            {
                Hide();
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            window.Left = origin.X / dpi.DpiScaleX;
            window.Top = origin.Y / dpi.DpiScaleY;
            window.Width = width / dpi.DpiScaleX;
            window.Height = height / dpi.DpiScaleY;

            canvas.Children.Clear();
            canvas.Width = width / dpi.DpiScaleX;
            canvas.Height = height / dpi.DpiScaleY;

            if (textView.GetLineHeight(out int lineHeight) != 0 || lineHeight <= 0)
                lineHeight = Math.Max(16, (int)(18 * dpi.DpiScaleY));

            int[] lineStarts = BuildLineStarts(text);
            foreach (HighlightSpan span in spans)
            {
                if (!ToLineColumn(lineStarts, text.Length, span.Start, out int startLine, out int startColumn)) continue;
                if (!ToLineColumn(lineStarts, text.Length, span.Start + span.Length, out int endLine, out int endColumn)) continue;

                // A word can cross a wrapped visual row; render each logical line part separately.
                for (int line = startLine; line <= endLine; line++)
                {
                    int from = line == startLine ? startColumn : 0;
                    int to = line == endLine ? endColumn : LineLength(text, lineStarts, line);
                    if (to <= from) continue;

                    var start = new Microsoft.VisualStudio.OLE.Interop.POINT[1];
                    var end = new Microsoft.VisualStudio.OLE.Interop.POINT[1];
                    if (textView.GetPointOfLineColumn(line, from, start) != 0) continue;
                    if (textView.GetPointOfLineColumn(line, to, end) != 0) continue;
                    // The view reports a clamped point for a line it is not painting (scrolled away or
                    // past the last visible row), so those rects would land on the first visible line.
                    if (start[0].y < client.Top || start[0].y >= client.Bottom) continue;

                    double left = start[0].x / dpi.DpiScaleX;
                    double top = start[0].y / dpi.DpiScaleY;
                    double right = end[0].x / dpi.DpiScaleX;
                    if (right <= left) continue;

                    var rectangle = new Rectangle
                    {
                        Width = right - left,
                        Height = lineHeight / dpi.DpiScaleY,
                        Fill = CreateFill(),
                        IsHitTestVisible = false,
                        RadiusX = 2,
                        RadiusY = 2
                    };
                    Canvas.SetLeft(rectangle, left);
                    Canvas.SetTop(rectangle, top);
                    canvas.Children.Add(rectangle);
                    if (!loggedRects)
                        MSSQLToolPackage._logger?.Debug(
                            $"SQL-HIGHLIGHT rect span={span.Start}:{span.Length} line={line} cols={from}-{to} " +
                            $"client=({start[0].x},{start[0].y})-({end[0].x},{end[0].y}) dip=({left},{top})-({right},{top + (lineHeight / dpi.DpiScaleY)})");
                }
            }

            if (canvas.Children.Count == 0)
            {
                Hide();
                return;
            }

            if (!shown)
            {
                window.Show();
                shown = true;
            }

            IntPtr handle = new WindowInteropHelper(window).Handle;
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);

            MSSQLToolPackage._logger?.Debug($"SQL-HIGHLIGHT render rects={canvas.Children.Count} bounds=({window.Left},{window.Top},{window.Width},{window.Height})");
            loggedRects = true;
        }

        private static SolidColorBrush CreateFill()
        {
            // This window honours alpha (unlike the classifier pipeline), so the wash stays
            // translucent but takes its hue from the same theme-aware palette instead of a
            // fixed light-theme grey.
            Color block = RelatedHighlightPalette.Block;
            var brush = new SolidColorBrush(Color.FromArgb(100, block.R, block.G, block.B));
            brush.Freeze();
            return brush;
        }

        private void EnsureWindow()
        {
            if (window != null) return;

            canvas = new Canvas
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                ClipToBounds = true
            };
            window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                IsHitTestVisible = false,
                Content = canvas
            };
            window.Show();
            window.Hide();

            IntPtr handle = new WindowInteropHelper(window).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        }

        private void Hide()
        {
            if (window != null && shown)
            {
                window.Hide();
                shown = false;
            }
            lastGeometry = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            if (window != null)
            {
                try { window.Close(); } catch (Exception) { }
                window = null;
            }
        }

        private static int OffsetOf(string text, int line, int column)
        {
            int offset = 0;
            int currentLine = 0;
            while (currentLine < line && offset < text.Length)
            {
                int newline = text.IndexOf('\n', offset);
                if (newline < 0) break;
                offset = newline + 1;
                currentLine++;
            }

            return Math.Min(text.Length, offset + Math.Max(0, column));
        }

        private static int[] BuildLineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        private static bool ToLineColumn(int[] lineStarts, int textLength, int offset, out int line, out int column)
        {
            line = 0;
            column = 0;
            if (offset < 0 || offset > textLength || lineStarts.Length == 0) return false;
            line = Array.BinarySearch(lineStarts, offset);
            if (line < 0) line = ~line - 1;
            if (line < 0) line = 0;
            column = offset - lineStarts[line];
            return true;
        }

        private static int LineLength(string text, int[] lineStarts, int line)
        {
            int start = lineStarts[line];
            int end = line + 1 < lineStarts.Length ? lineStarts[line + 1] - 1 : text.Length;
            return Math.Max(0, end - start);
        }

        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);
    }
}
