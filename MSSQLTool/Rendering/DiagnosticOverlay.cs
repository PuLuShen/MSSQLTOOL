using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MSSQLTool.Analysis;
using MSSQLTool.Editor;
using MSSQLTool.Model;

namespace MSSQLTool.Rendering
{
    /// <summary>
    /// Draws code-analysis squiggles in a click-through layer over the SQL editor and shows the
    /// explanation for the diagnostic under the caret.  Analysis is debounced: it only runs once
    /// the text has been stable for a moment, so typing never pays for it.
    /// </summary>
    internal sealed class DiagnosticOverlay : IDisposable
    {
        private const int AnalysisDebounceMilliseconds = 650;
        private const int HoverDelayMilliseconds = 600;
        private const int ScrollSettleMilliseconds = 300;

        private readonly IEditorHost host;
        private readonly Func<bool> suppressed;
        private readonly DispatcherTimer timer;
        private readonly AnalysisEngine engine = new AnalysisEngine();

        private Window surface;
        private Canvas canvas;
        private Window tip;
        private TextBlock tipText;
        private Border tipBorder;

        private string lastText;
        private string lastAnalyzedText;
        private List<AnalysisDiagnostic> diagnostics = new List<AnalysisDiagnostic>();
        private AnalysisSettings settings = SettingsManager.GetAnalysisSettings();
        private int settingsVersion = SettingsManager.AnalysisSettingsVersion;
        private DateTime lastTextChangeUtc = DateTime.MinValue;
        private string lastDrawSignature;
        private int hoverDiagnostic = -1;
        private bool hoverFromMouse;
        private readonly List<SquiggleHit> hits = new List<SquiggleHit>();

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        private sealed class SquiggleHit
        {
            public int DiagnosticIndex;
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        private DateTime hoverSinceUtc = DateTime.MinValue;
        private int lastViewportTop = int.MinValue;
        private DateTime lastScrollUtc = DateTime.MinValue;
        private string lastTipSignature;
        private bool disposed;

        public DiagnosticOverlay(IEditorHost host, Func<bool> suppressed)
        {
            this.host = host;
            this.suppressed = suppressed ?? (() => false);
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, __) => Update();
            timer.Start();
        }

        private void Update()
        {
            if (disposed) return;
            try
            {
                if (suppressed() || host == null)
                {
                    HideAll();
                    return;
                }

                if (settingsVersion != SettingsManager.AnalysisSettingsVersion)
                {
                    settingsVersion = SettingsManager.AnalysisSettingsVersion;
                    settings = SettingsManager.GetAnalysisSettings();
                    lastAnalyzedText = null;   // force one re-run with the new rules
                }

                if (!host.TryReadAllText(out string text) || string.IsNullOrEmpty(text))
                {
                    HideAll();
                    return;
                }

                if (!string.Equals(text, lastText, StringComparison.Ordinal))
                {
                    lastText = text;
                    lastTextChangeUtc = DateTime.UtcNow;
                }

                // Reading the whole buffer is the only change detector the TextManager interop
                // exposes cheaply, so slow the poll down for very large scripts instead of
                // copying a megabyte four times a second.
                int desiredInterval = text.Length > 200000 ? 1200 : 400;
                if (Math.Abs(timer.Interval.TotalMilliseconds - desiredInterval) > 1)
                    timer.Interval = TimeSpan.FromMilliseconds(desiredInterval);

                if (!string.Equals(text, lastAnalyzedText, StringComparison.Ordinal)
                    && (DateTime.UtcNow - lastTextChangeUtc).TotalMilliseconds >= AnalysisDebounceMilliseconds)
                    Analyze(text);

                // While the scrollbar is being dragged the geometry below is already stale by the
                // time the window repaints, which made the squiggles trail behind the text.  Hide
                // them for a moment and draw again once the viewport settles.
                if (host.TryGetViewport(out int viewportTop, out int viewportLines, out int viewportHeight))
                {
                    if (viewportTop != lastViewportTop)
                    {
                        if (lastViewportTop != int.MinValue) lastScrollUtc = DateTime.UtcNow;
                        lastViewportTop = viewportTop;
                    }
                    if ((DateTime.UtcNow - lastScrollUtc).TotalMilliseconds < ScrollSettleMilliseconds)
                    {
                        HideSurface();
                        HideTooltip();
                        return;
                    }
                }

                if (diagnostics.Count == 0)
                {
                    HideSurface();
                    HideTooltip();
                    return;
                }

                DrawIfNeeded(text);
                UpdateHover(text);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Code analysis overlay failed.");
                HideAll();
            }
        }

        private void Analyze(string text)
        {
            settings = SettingsManager.GetAnalysisSettings();
            int caret = -1;
            if (host.TryGetCaret(out int line, out int column))
                caret = TextPositions.ToOffset(text, line, column);

            diagnostics = engine.Analyze(text, settings, caret);
            lastAnalyzedText = text;
            lastDrawSignature = null;
            AnalysisResultsBroker.Publish("active", text, diagnostics, settings);
        }

        private void DrawIfNeeded(string text)
        {
            IntPtr editor = host.WindowHandle;
            if (editor == IntPtr.Zero) return;
            if (!GetClientRect(editor, out NativeRect client)) return;
            int width = Math.Max(1, client.Right - client.Left);
            int height = Math.Max(1, client.Bottom - client.Top);
            if (width <= 1 || height <= 1) { HideSurface(); return; }

            host.TryGetViewport(out int firstVisible, out int visibleLines, out int lineHeight);
            if (lineHeight <= 0) lineHeight = 18;

            string signature = string.Concat(client.Left, ",", client.Top, ",", client.Right, ",", client.Bottom, "|",
                firstVisible, "|", lineHeight, "|", diagnostics.Count, "|", lastAnalyzedText == null ? 0 : lastAnalyzedText.Length,
                "|", settings.showInfoSquiggles ? "1" : "0");
            if (string.Equals(signature, lastDrawSignature, StringComparison.Ordinal) && surface != null) return;

            EnsureSurface();
            NativePoint origin = new NativePoint();
            if (!ClientToScreen(editor, ref origin)) return;

            DpiScale dpi = VisualTreeHelper.GetDpi(surface);
            surface.Left = origin.X / dpi.DpiScaleX;
            surface.Top = origin.Y / dpi.DpiScaleY;
            surface.Width = width / dpi.DpiScaleX;
            surface.Height = height / dpi.DpiScaleY;
            canvas.Width = width / dpi.DpiScaleX;
            canvas.Height = height / dpi.DpiScaleY;
            canvas.Children.Clear();
            hits.Clear();

            for (int index = 0; index < diagnostics.Count; index++)
            {
                AnalysisDiagnostic diagnostic = diagnostics[index];
                if (diagnostic.Severity == AnalysisSeverity.Info && !settings.showInfoSquiggles) continue;
                if (!TryGetOffsets(text, diagnostic, out int startLine, out int startColumn, out int endLine, out int endColumn)) continue;
                for (int line = startLine; line <= endLine; line++)
                {
                    int from = line == startLine ? startColumn : 0;
                    int to = line == endLine ? endColumn : Math.Max(startColumn + 1, endColumn);
                    if (!host.TryGetPointOfLineColumn(line, from, out int startX, out int startY)) continue;
                    if (!host.TryGetPointOfLineColumn(line, to, out int endX, out int endY)) continue;
                    double left = startX / dpi.DpiScaleX;
                    double right = endX / dpi.DpiScaleX;
                    if (right <= left) right = left + 6;
                    double baseline = (startY + lineHeight - 3) / dpi.DpiScaleY;
                    canvas.Children.Add(BuildSquiggle(left, right, baseline, ColorFor(diagnostic.Severity)));
                    hits.Add(new SquiggleHit
                    {
                        DiagnosticIndex = index,
                        Left = origin.X + (int)Math.Round(left * dpi.DpiScaleX),
                        Right = origin.X + (int)Math.Round(right * dpi.DpiScaleX),
                        Top = origin.Y + startY + (lineHeight / 2),
                        Bottom = origin.Y + startY + lineHeight + 2
                    });
                }
            }

            if (canvas.Children.Count == 0)
            {
                HideSurface();
                lastDrawSignature = signature;
                return;
            }

            if (surface != null)
            {
                if (!shown) { surface.Show(); shown = true; }
                IntPtr handle = new WindowInteropHelper(surface).Handle;
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
            }
            lastDrawSignature = signature;
        }

        private bool shown;

        private static bool TryGetOffsets(string text, AnalysisDiagnostic diagnostic,
            out int startLine, out int startColumn, out int endLine, out int endColumn)
        {
            TextPositions.ToLineColumn(text, diagnostic.Start, out startLine, out startColumn);
            TextPositions.ToLineColumn(text, diagnostic.End, out endLine, out endColumn);
            return endLine >= startLine;
        }

        private static Polyline BuildSquiggle(double left, double right, double baseline, Color color)
        {
            var points = new PointCollection();
            const double step = 4.0;
            const double amplitude = 2.2;
            bool up = true;
            for (double x = left; x <= right; x += step)
            {
                points.Add(new Point(x, up ? baseline - amplitude : baseline));
                up = !up;
            }
            return new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
        }

        private static Color ColorFor(AnalysisSeverity severity)
        {
            switch (severity)
            {
                case AnalysisSeverity.Error: return Color.FromRgb(0xE5, 0x14, 0x00);
                case AnalysisSeverity.Warning: return Color.FromRgb(0xE9, 0xA7, 0x00);
                default: return Color.FromRgb(0x1E, 0x90, 0xFF);
            }
        }

        private void UpdateHover(string text)
        {
            if (!settings.showHover) { HideTooltip(); return; }

            // Prefer the pointer: hovering a squiggle should explain it without moving the caret.
            bool fromMouse = TryGetMouseHit(out int index, out int x, out int y);
            if (!fromMouse)
            {
                if (!host.TryGetCaret(out int line, out int column)) { HideTooltip(); return; }
                index = FindDiagnosticAt(TextPositions.ToOffset(text, line, column));
                if (index >= 0)
                {
                    int anchorHeight;
                    if (!host.TryGetCaretScreenPoint(out x, out y, out anchorHeight)) return;
                }
            }

            if (index < 0 || index >= diagnostics.Count)
            {
                HideTooltip();
                hoverDiagnostic = -1;
                hoverFromMouse = fromMouse;
                hoverSinceUtc = DateTime.MinValue;
                return;
            }

            // Moving inside the same diagnostic must not restart the delay, otherwise the tip can
            // never appear while the pointer is still settling.
            if (index != hoverDiagnostic || fromMouse != hoverFromMouse)
            {
                hoverDiagnostic = index;
                hoverFromMouse = fromMouse;
                hoverSinceUtc = DateTime.UtcNow;
                HideTooltip();
            }

            if ((DateTime.UtcNow - hoverSinceUtc).TotalMilliseconds < HoverDelayMilliseconds) return;
            ShowTooltip(diagnostics[index], x, y);
        }

        private int FindDiagnosticAt(int offset)
        {
            for (int i = 0; i < diagnostics.Count; i++)
            {
                AnalysisDiagnostic diagnostic = diagnostics[i];
                if (diagnostic.Severity == AnalysisSeverity.Info && !settings.showInfoSquiggles) continue;
                if (offset >= diagnostic.Start && offset <= diagnostic.End) return i;
            }
            return -1;
        }

        /// <summary>
        /// Maps the pointer to a squiggle using the rectangles the drawing pass just produced.
        /// The overlay is pure WPF, so the drawn geometry is the authoritative hit target and no
        /// VS text API is needed.
        /// </summary>
        private bool TryGetMouseHit(out int index, out int x, out int y)
        {
            index = -1;
            x = 0;
            y = 0;
            if (!GetCursorPos(out NativePoint cursor)) return false;

            IntPtr editor = host.WindowHandle;
            if (editor == IntPtr.Zero || !GetClientRect(editor, out NativeRect client)) return false;
            var origin = new NativePoint();
            if (!ClientToScreen(editor, ref origin)) return false;

            int width = Math.Max(1, client.Right - client.Left);
            int height = Math.Max(1, client.Bottom - client.Top);
            if (cursor.X < origin.X || cursor.Y < origin.Y
                || cursor.X > origin.X + width || cursor.Y > origin.Y + height) return false;

            for (int i = 0; i < hits.Count; i++)
            {
                SquiggleHit hit = hits[i];
                if (cursor.X < hit.Left || cursor.X > hit.Right || cursor.Y < hit.Top || cursor.Y > hit.Bottom) continue;
                index = hit.DiagnosticIndex;
                x = hit.Left;
                y = hit.Top;
                return true;
            }

            // Inside the editor but not on a squiggle: suppress the caret fallback, otherwise the
            // tip would pop up somewhere else while the pointer is parked on plain text.
            x = cursor.X;
            y = cursor.Y;
            return true;
        }

        private void ShowTooltip(AnalysisDiagnostic diagnostic, int x, int y)
        {
            if (diagnostic == null) return;
            EnsureTooltip();

            string severity = diagnostic.Severity == AnalysisSeverity.Error
                ? LocalizationManager.T("Error")
                : diagnostic.Severity == AnalysisSeverity.Warning ? LocalizationManager.T("Warning") : LocalizationManager.T("Info");

            // Include the rule explanation, not just the one-line message:  the catalog carries a
            // description per rule and leaving it out made the tip look incomplete.
            RuleDefinition rule = RuleCatalog.Find(diagnostic.RuleId);
            string description = rule == null ? null
                : (LocalizationManager.IsChinese ? rule.DescriptionZh : rule.DescriptionEn);
            string text = severity + " · " + AnalysisResultsBroker.RuleTitle(diagnostic.RuleId) + Environment.NewLine + diagnostic.Message;
            if (!string.IsNullOrWhiteSpace(description))
                text += Environment.NewLine + Environment.NewLine + description;
            if (QuickFixService.CanFix(diagnostic) && !string.Equals(diagnostic.RuleId, "select-star", StringComparison.OrdinalIgnoreCase))
                text += Environment.NewLine + Environment.NewLine + LocalizationManager.T("Ctrl+. applies this fix.");

            const double maxWidth = 460;
            tipText.Text = text;
            tipBorder.Measure(new Size(maxWidth, double.PositiveInfinity));
            double width = Math.Min(maxWidth, Math.Max(180, tipBorder.DesiredSize.Width));
            double height = tipBorder.DesiredSize.Height;

            DpiScale dpi = VisualTreeHelper.GetDpi(tip);
            double left = x / dpi.DpiScaleX;
            double top = Math.Max(0, (y - height - 10) / dpi.DpiScaleY);

            // The overlay ticks every 400 ms; re-applying the same geometry each tick makes the
            // layered window repaint and the tip visibly blinks.  Only touch the window when the
            // content or the anchor actually changed.
            string signature = diagnostic.Start + ":" + diagnostic.Length + "|" + text.GetHashCode() + "|" + left + ":" + top + ":" + width + ":" + height;
            if (string.Equals(signature, lastTipSignature, StringComparison.Ordinal) && tip.IsVisible) return;
            lastTipSignature = signature;

            tip.Left = left;
            tip.Top = top;
            tip.Width = width;
            tip.Height = height;
            if (!tip.IsVisible) tip.Show();
            IntPtr handle = new WindowInteropHelper(tip).Handle;
            // SWP_SHOWWINDOW only.  The previous call also passed 0x0080, which is
            // SWP_HIDEWINDOW, so every tip was shown and hidden in the same call.
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        private void EnsureSurface()
        {
            if (surface != null) return;
            canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false, ClipToBounds = true };
            surface = new Window
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
            surface.Show();
            surface.Hide();
            IntPtr handle = new WindowInteropHelper(surface).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        }

        private void EnsureTooltip()
        {
            if (tip != null) return;
            tipText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                FontSize = 12,
                MaxWidth = 400
            };
            tipBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 6, 8, 6),
                Child = tipText
            };
            tip = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                IsHitTestVisible = false,
                Topmost = true,
                Content = tipBorder
            };
            tip.Show();
            tip.Hide();
            IntPtr handle = new WindowInteropHelper(tip).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        }

        private void HideTooltip()
        {
            lastTipSignature = null;
            if (tip != null && tip.IsVisible) tip.Hide();
        }

        private void HideSurface()
        {
            if (surface != null && shown)
            {
                surface.Hide();
                shown = false;
            }
            lastDrawSignature = null;
        }

        private void HideAll()
        {
            HideSurface();
            HideTooltip();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            try { surface?.Close(); } catch (Exception) { }
            try { tip?.Close(); } catch (Exception) { }
            surface = null;
            tip = null;
        }

        private struct NativeRect { public int Left, Top, Right, Bottom; }
        private struct NativePoint { public int X, Y; }

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    }
}
