using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using MSSQLTool.Completion;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>Classification type of the grey related-token marker.</summary>
    internal static class RelatedHighlightClassification
    {
        public const string Name = "MSSQLTool.RelatedHighlight";

        /// <summary>
        /// MEF needs a *value* of type <see cref="ClassificationTypeDefinition"/>, and that type is
        /// sealed, so the export has to be a member returning an instance (the shape Roslyn uses).
        /// </summary>
        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Name)]
        internal static readonly ClassificationTypeDefinition Definition = new ClassificationTypeDefinition();
    }

    /// <summary>
    /// Solid marker colour for the shell theme that is active right now.
    ///
    /// The classifier pipeline paints classification backgrounds with the RGB values only (a
    /// translucent brush showed up as a solid block), so a single swatch cannot serve both
    /// themes: #C0C0C0 is readable on a light editor and turns into an unreadable light block
    /// over a dark one.  Light and dark themes therefore get different RGB values, and the dark
    /// one is derived from the themed background so the marker follows custom dark themes too.
    /// </summary>
    internal static class RelatedHighlightPalette
    {
        private static readonly Color LightThemeBlock = Color.FromRgb(0xC0, 0xC0, 0xC0);

        /// <summary>How far the dark marker is lifted from the editor background.</summary>
        private const double DarkBlockBlend = 0.16;

        /// <summary>Luminance below which a theme counts as dark.</summary>
        private const double DarkThemeLuminance = 0.45;

        private static bool cached;
        private static Color cachedColor;

        /// <summary>Marker colour for the current theme.  Safe to call from the UI thread only.</summary>
        public static Color Block
        {
            get
            {
                if (!cached)
                {
                    cachedColor = Build();
                    cached = true;
                }
                return cachedColor;
            }
        }

        /// <summary>Forgets the memoised colour; the shell theme changed or just finished loading.</summary>
        public static void Invalidate()
        {
            cached = false;
        }

        public static SolidColorBrush CreateBrush()
        {
            var brush = new SolidColorBrush(Block);
            brush.Freeze();
            return brush;
        }

        private static Color Build()
        {
            Color background;
            if (!TryGetThemeBackground(out background)) return LightThemeBlock;
            if (RelativeLuminance(background) >= DarkThemeLuminance) return LightThemeBlock;
            return Blend(background, Colors.White, DarkBlockBlend);
        }

        private static bool TryGetThemeBackground(out Color color)
        {
            color = default(Color);
            try
            {
                System.Drawing.Color themed = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundBrushKey);
                if (themed.A == 0) return false;
                color = Color.FromRgb(themed.R, themed.G, themed.B);
                return true;
            }
            catch (Exception)
            {
                // No shell (unit tests) or the theme service is not up yet.
                return false;
            }
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        private static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }
    }

    /// <summary>
    /// Background for related tokens.  A classification format is the same mechanism the editor
    /// uses for its own highlighted references, so the editor owns geometry and theming.
    /// </summary>
    [Export(typeof(EditorFormatDefinition))]
    [Name(RelatedHighlightClassification.Name)]
    [ClassificationType(ClassificationTypeNames = RelatedHighlightClassification.Name)]
    [UserVisible(true)]
    internal sealed class RelatedHighlightFormat : ClassificationFormatDefinition
    {
        public RelatedHighlightFormat()
        {
            DisplayName = "MSSQLTool related highlight";
            // The format is composed once per package load, so the classifier re-applies the
            // colour through the view's format map when the shell theme changes.
            BackgroundBrush = RelatedHighlightPalette.CreateBrush();
        }
    }

    /// <summary>
    /// Related-token highlighting through the classifier pipeline.  This is the path the editor
    /// always runs (it is how syntax colouring is produced), unlike view taggers, which this host
    /// never asks for.
    /// </summary>
    [Export(typeof(IClassifierProvider))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    internal sealed class RelatedHighlightClassifierProvider : IClassifierProvider
    {
        [Import(AllowDefault = true)]
        public IClassificationTypeRegistryService Registry { get; set; }

        [Import(AllowDefault = true)]
        public IClassificationFormatMapService FormatMapService { get; set; }

        public IClassifier GetClassifier(ITextBuffer buffer)
        {
            if (buffer == null) return null;

            try
            {
                if (Registry == null)
                {
                    MSSQLToolPackage._logger?.Info("MSSQLTool: related highlight skipped, no classification registry");
                    return null;
                }

                IClassificationType classification = Registry.GetClassificationType(RelatedHighlightClassification.Name);
                if (classification == null)
                {
                    MSSQLToolPackage._logger?.Info("MSSQLTool: related highlight skipped, classification type is not registered");
                    return null;
                }

                return buffer.Properties.GetOrCreateSingletonProperty(
                    () => new RelatedHighlightClassifier(buffer, classification, FormatMapService));
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Highlight", "The related-highlight classifier could not be created", ex);
                return null;
            }
        }
    }

    internal sealed class RelatedHighlightClassifier : IClassifier, IDisposable
    {
        private const int DebounceMilliseconds = 120;

        /// <summary>How long the view search keeps polling before it backs off.</summary>
        private const int AttachIntervalMilliseconds = 250;

        /// <summary>40 ticks = 10 seconds; a buffer without a view is usually a closed document.</summary>
        private const int AttachAttemptsBeforeBackoff = 40;

        private static readonly TimeSpan AttachBackoff = TimeSpan.FromSeconds(5);

        private readonly ITextBuffer buffer;
        private readonly IClassificationType classification;
        private readonly IClassificationFormatMapService formatMapService;
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer debounce;
        private readonly DispatcherTimer attach;
        private ITextView view;
        private IClassificationFormatMap formatMap;
        private bool themeSubscribed;
        private List<ClassificationSpan> spans = new List<ClassificationSpan>();
        private ITextSnapshot spanSnapshot;
        private int analysisVersion;
        private int analysisRunning;
        private int analysisQueued;
        private int lastAnalyzedVersion = -1;
        private int lastAnalyzedCaret = -1;
        private int attachAttempts;
        private DateTime attachRetryAfterUtc = DateTime.MinValue;
        private CancellationTokenSource analysisCancellation;
        private bool disposed;

        public RelatedHighlightClassifier(ITextBuffer buffer, IClassificationType classification, IClassificationFormatMapService formatMapService = null)
        {
            this.buffer = buffer;
            this.classification = classification;
            this.formatMapService = formatMapService;
            dispatcher = Dispatcher.CurrentDispatcher;
            // The classifier is the editor-native renderer: while it exists the window overlay must
            // stay hidden.  Register() used to have no caller, so the overlay painted the same spans a
            // second time at clamped coordinates, which is what put grey blocks on the first line.
            RelatedHighlightActivation.Register();

            debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds)
            };
            debounce.Tick += OnDebounceTick;

            // The view is what knows the caret, and it may not exist yet when the classifier is
            // first requested.  The search is bounded: polling was previously unbounded, so every
            // classifier of a closed tab kept waking the UI thread four times a second forever.
            attach = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(AttachIntervalMilliseconds)
            };
            attach.Tick += OnAttachTick;
            attach.Start();

            buffer.Changed += OnBufferChanged;
            MSSQLToolPackage._logger?.Info("MSSQLTool: related highlight classifier created");
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            if (disposed) return new List<ClassificationSpan>();

            // The editor asking for spans means a view exists again after a close and reopen.
            EnsureViewAttach();

            List<ClassificationSpan> current = spans;
            ITextSnapshot snapshot = spanSnapshot;
            if (current == null || current.Count == 0 || snapshot == null) return new List<ClassificationSpan>();
            if (snapshot != span.Snapshot) return new List<ClassificationSpan>();

            var result = new List<ClassificationSpan>();
            foreach (ClassificationSpan candidate in current)
            {
                if (candidate.Span.IntersectsWith(span)) result.Add(candidate);
            }
            return result;
        }

        private void OnAttachTick(object sender, EventArgs e)
        {
            if (disposed || view != null)
            {
                attach.Stop();
                return;
            }

            if (++attachAttempts > AttachAttemptsBeforeBackoff)
            {
                // This buffer has no view (usually a closed tab).  Stop waking the UI thread and
                // wait for an edit, a classification request or a caret move to try again.
                attach.Stop();
                attachRetryAfterUtc = DateTime.UtcNow + AttachBackoff;
                return;
            }

            ITextView found;
            if (!SqlEditorServices.TryGetView(buffer, out found) || found == null) return;

            view = found;
            attachAttempts = 0;
            attach.Stop();
            view.Caret.PositionChanged += OnCaretPositionChanged;
            view.Closed += OnViewClosed;
            // The format definition is composed before the shell finishes loading, so the colour
            // it captured may be the fallback.  Re-resolve it now that the view exists.
            RelatedHighlightPalette.Invalidate();
            SubscribeTheme();
            ApplyThemeColor();
            Schedule();
        }

        /// <summary>Restarts the (bounded) view search when the buffer is used again.</summary>
        private void EnsureViewAttach()
        {
            if (disposed || view != null || attach.IsEnabled) return;
            if (DateTime.UtcNow < attachRetryAfterUtc) return;

            attachAttempts = 0;
            attach.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            debounce.Stop();
            Analyze();
        }

        private void Schedule()
        {
            if (disposed) return;
            EnsureViewAttach();
            debounce.Stop();
            debounce.Start();
        }

        private void Analyze()
        {
            if (disposed) return;

            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            ITextView currentView = view;
            if (snapshot == null || currentView == null)
            {
                EnsureViewAttach();
                return;
            }

            int caret;
            try
            {
                caret = currentView.Caret.Position.BufferPosition.Position;
            }
            catch (Exception)
            {
                return;
            }

            // Moving inside the same identifier, scrolling or a selection change produces the same
            // spans.  Re-analysing the whole buffer for those used to be the normal case.
            if (snapshot.Version.VersionNumber == lastAnalyzedVersion && caret == lastAnalyzedCaret) return;

            lastAnalyzedVersion = snapshot.Version.VersionNumber;
            lastAnalyzedCaret = caret;

            int version = Interlocked.Increment(ref analysisVersion);
            if (Interlocked.CompareExchange(ref analysisRunning, 1, 0) != 0)
            {
                // One pass per classifier at a time; a newer request only marks it dirty so the
                // running pass re-schedules itself when it finishes.
                Interlocked.Exchange(ref analysisQueued, 1);
                return;
            }

            var cancellation = new CancellationTokenSource();
            CancellationTokenSource previous = Interlocked.Exchange(ref analysisCancellation, cancellation);
            if (previous != null)
            {
                try { previous.Cancel(); previous.Dispose(); } catch (Exception) { }
            }

            CancellationToken token = cancellation.Token;
            Task.Run(() => AnalyzeCore(version, snapshot, caret, token));
        }

        private void AnalyzeCore(int version, ITextSnapshot snapshot, int caret, CancellationToken token)
        {
            List<HighlightSpan> found = null;
            try
            {
                if (disposed || token.IsCancellationRequested) return;

                // Copying the buffer is the most expensive part of a related-highlight pass;
                // keep it off the UI thread so a 900 KB script cannot stall a keystroke.
                string text = snapshot.GetText();
                found = SqlPromptHighlightAnalyzer.Analyze(text, caret);
            }
            catch (Exception)
            {
                found = new List<HighlightSpan>();
            }
            finally
            {
                Volatile.Write(ref analysisRunning, 0);
                if (Interlocked.Exchange(ref analysisQueued, 0) != 0 && !disposed) Schedule();
            }

            if (found == null || token.IsCancellationRequested) return;

            try
            {
                dispatcher.BeginInvoke(new Action(() => Apply(version, snapshot, found)));
            }
            catch (Exception)
            {
                // The document was closed while the analysis was running.
            }
        }

        private void Apply(int version, ITextSnapshot snapshot, List<HighlightSpan> found)
        {
            if (disposed || version != Volatile.Read(ref analysisVersion)) return;
            if (snapshot != buffer.CurrentSnapshot) return;

            var next = new List<ClassificationSpan>();
            foreach (HighlightSpan item in found)
            {
                if (item.Length <= 0 || item.Start < 0) continue;
                if (item.Start + item.Length > snapshot.Length) continue;
                next.Add(new ClassificationSpan(new SnapshotSpan(snapshot, item.Start, item.Length), classification));
            }

            if (spanSnapshot == snapshot && SameSpans(spans, next)) return;
            bool firstDraw = spans == null || spans.Count == 0;

            // Only the tokens that appeared or disappeared have to be reclassified.  Invalidating
            // the whole document made the editor reclassify the entire script on every caret move
            // (SSMS's own SQL classifier included), which is what made a large script feel heavy
            // while typing even though the spans themselves are a handful of words.
            SnapshotSpan changed = ChangedSpan(spanSnapshot, spans, snapshot, next);

            spans = next;
            spanSnapshot = snapshot;

            if (firstDraw && next.Count > 0)
                MSSQLToolPackage._logger?.Info("MSSQLTool: related highlight spans=" + next.Count);

            ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(changed));
        }

        /// <summary>
        /// Region covered by the old and the new related spans.  Old spans are tracked forward so a
        /// token that stopped being related is repainted as well; if anything cannot be translated
        /// the whole document is invalidated, which is the pre-existing, always-correct fallback.
        /// </summary>
        private static SnapshotSpan ChangedSpan(ITextSnapshot previousSnapshot, List<ClassificationSpan> previous,
            ITextSnapshot snapshot, List<ClassificationSpan> next)
        {
            int start = int.MaxValue;
            int end = -1;
            if (previousSnapshot != null && previous != null)
            {
                foreach (ClassificationSpan item in previous)
                {
                    SnapshotSpan translated;
                    try { translated = item.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive); }
                    catch (Exception) { return new SnapshotSpan(snapshot, 0, snapshot.Length); }
                    if (translated.Start.Position < start) start = translated.Start.Position;
                    if (translated.End.Position > end) end = translated.End.Position;
                }
            }
            foreach (ClassificationSpan item in next)
            {
                if (item.Span.Start.Position < start) start = item.Span.Start.Position;
                if (item.Span.End.Position > end) end = item.Span.End.Position;
            }

            if (end <= start)
                return new SnapshotSpan(snapshot, Math.Min(Math.Max(start, 0), snapshot.Length), 0);
            return new SnapshotSpan(snapshot, start, Math.Min(end, snapshot.Length) - start);
        }

        private static bool SameSpans(List<ClassificationSpan> left, List<ClassificationSpan> right)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i].Span.Start != right[i].Span.Start) return false;
                if (left[i].Span.Length != right[i].Span.Length) return false;
            }
            return true;
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e) => Schedule();

        private void SubscribeTheme()
        {
            if (themeSubscribed) return;
            themeSubscribed = true;
            try
            {
                VSColorTheme.ThemeChanged += OnThemeChanged;
            }
            catch (Exception)
            {
                // Regression tests construct the classifier without a shell.
            }
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            RelatedHighlightPalette.Invalidate();
            ApplyThemeColor();
        }

        /// <summary>
        /// Pushes the current theme colour into the view's classification format map.  Changing
        /// the exported format definition is not enough: the map snapshots formats when the
        /// editor loads, so an already open view would keep the old swatch until restart.
        /// </summary>
        private void ApplyThemeColor()
        {
            if (disposed) return;

            try
            {
                if (formatMap == null)
                {
                    if (formatMapService == null || view == null) return;
                    formatMap = formatMapService.GetClassificationFormatMap(view);
                }

                if (formatMap == null) return;
                TextFormattingRunProperties properties = formatMap.GetTextProperties(classification);
                formatMap.SetTextProperties(classification, properties.SetBackgroundBrush(RelatedHighlightPalette.CreateBrush()));
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Highlight", "The related-highlight colour could not be applied", ex);
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e) => Schedule();

        private void OnViewClosed(object sender, EventArgs e)
        {
            if (view == null) return;
            view.Caret.PositionChanged -= OnCaretPositionChanged;
            view.Closed -= OnViewClosed;
            view = null;

            // Nothing to paint while the tab is closed: stop both timers instead of going back to
            // polling for a view every 250 ms.  An edit, a caret move or a classification request
            // restarts the (bounded) search after a pause.
            debounce.Stop();
            attach.Stop();
            attachRetryAfterUtc = DateTime.UtcNow + AttachBackoff;
            lastAnalyzedVersion = -1;
            lastAnalyzedCaret = -1;
            CancelPendingAnalysis();
        }

        private void CancelPendingAnalysis()
        {
            CancellationTokenSource cancellation = Interlocked.Exchange(ref analysisCancellation, null);
            if (cancellation == null) return;

            try { cancellation.Cancel(); cancellation.Dispose(); } catch (Exception) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Increment(ref analysisVersion);
            if (themeSubscribed)
            {
                try { VSColorTheme.ThemeChanged -= OnThemeChanged; } catch (Exception) { }
                themeSubscribed = false;
            }
            formatMap = null;
            RelatedHighlightActivation.Unregister();
            debounce.Stop();
            debounce.Tick -= OnDebounceTick;
            attach.Stop();
            attach.Tick -= OnAttachTick;
            buffer.Changed -= OnBufferChanged;
            if (view != null)
            {
                view.Caret.PositionChanged -= OnCaretPositionChanged;
                view.Closed -= OnViewClosed;
                view = null;
            }
            CancelPendingAnalysis();
            spans = new List<ClassificationSpan>();
        }
    }
}
