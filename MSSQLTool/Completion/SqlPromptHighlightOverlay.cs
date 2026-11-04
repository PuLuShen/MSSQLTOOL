using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.TextManager.Interop;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// SQL Prompt-style related-token highlighting.  A click-through transparent window is laid
    /// over the SQL editor, so the editor itself (and its undo buffer) is never modified.
    /// </summary>
    internal sealed class SqlPromptHighlightOverlay : IDisposable
    {
        private readonly IVsTextView textView;
        private readonly Func<bool> completionVisible;
        private readonly DispatcherTimer timer;
        private Window window;
        private Canvas canvas;
        private string lastText;
        private int lastCaret = -1;
        private List<HighlightSpan> spans = new List<HighlightSpan>();
        private bool shown;

        public SqlPromptHighlightOverlay(IVsTextView textView, Func<bool> completionVisible)
        {
            this.textView = textView;
            this.completionVisible = completionVisible ?? (() => false);
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            timer.Tick += (_, __) => Update();
            timer.Start();
        }

        private void Update()
        {
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
                IntPtr focus = GetFocus();
                if (focus == IntPtr.Zero || (focus != editor && !IsChild(editor, focus)))
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
                    if (spans.Count == 0)
                    {
                        Hide();
                        return;
                    }

                    MSSQLToolPackage._logger?.Info($"SQL-HIGHLIGHT showing {spans.Count} span(s) at line={line} column={column}");
                }

                if (spans.Count == 0) return;
                Render(text, line);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL Prompt highlight overlay failed.");
                Hide();
            }
        }

        private string ReadText()
        {
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
                int startLine, startColumn, endLine, endColumn;
                if (!ToLineColumn(lineStarts, text.Length, span.Start, out startLine, out startColumn)) continue;
                if (!ToLineColumn(lineStarts, text.Length, span.Start + span.Length, out endLine, out endColumn)) continue;

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

                    double left = start[0].x / dpi.DpiScaleX;
                    double top = start[0].y / dpi.DpiScaleY;
                    double right = end[0].x / dpi.DpiScaleX;
                    if (right <= left) continue;

                    var rectangle = new Rectangle
                    {
                        Width = right - left,
                        Height = lineHeight / dpi.DpiScaleY,
                        Fill = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
                        IsHitTestVisible = false,
                        RadiusX = 2,
                        RadiusY = 2
                    };
                    Canvas.SetLeft(rectangle, left);
                    Canvas.SetTop(rectangle, top);
                    canvas.Children.Add(rectangle);
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
        }

        private void EnsureWindow()
        {
            if (window != null) return;

            canvas = new Canvas
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false
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
        }

        public void Dispose()
        {
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

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
