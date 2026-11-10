using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using MSSQLTool.Analysis;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>
    /// Native squiggles: the editor draws and positions them, so scrolling, zooming, word wrap and
    /// split views need no geometry code in the extension.  The tagger also publishes the result so
    /// the Code Analysis tool window and the quick info source use the same data.
    /// </summary>
    [Export(typeof(ITaggerProvider))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    [TagType(typeof(IErrorTag))]
    public sealed class SqlDiagnosticsTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (buffer == null || typeof(T) != typeof(IErrorTag)) return null;
            try
            {
                var tagger = buffer.Properties.GetOrCreateSingletonProperty(() => new SqlDiagnosticsTagger(buffer));
                return tagger as ITagger<T>;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Diagnostics", "The diagnostic tagger could not be created", ex);
                return null;
            }
        }
    }

    /// <summary>Latest analysis for a buffer, shared with the quick info source.</summary>
    internal sealed class DiagnosticSnapshot
    {
        public ITextSnapshot Snapshot;
        public List<AnalysisDiagnostic> Diagnostics;
        public AnalysisSettings Settings;
    }

    internal sealed class SqlDiagnosticsTagger : ITagger<IErrorTag>, IDisposable
    {
        internal const string SnapshotKey = "MSSQLTool.DiagnosticSnapshot";

        private const int DebounceMilliseconds = 650;

        private readonly ITextBuffer buffer;
        private readonly AnalysisEngine engine = new AnalysisEngine();
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer settingsTimer;
        private List<TagSpan<IErrorTag>> tags = new List<TagSpan<IErrorTag>>();
        private CancellationTokenSource pending;
        private int analysisVersion;
        private int settingsVersion;
        private bool disposed;

        public SqlDiagnosticsTagger(ITextBuffer buffer)
        {
            this.buffer = buffer;
            dispatcher = Dispatcher.CurrentDispatcher;
            settingsVersion = SettingsManager.AnalysisSettingsVersion;

            settingsTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            settingsTimer.Tick += OnSettingsTick;
            settingsTimer.Start();

            buffer.Changed += OnBufferChanged;
            MSSQLToolPackage._logger?.Info("MSSQLTool: diagnostics tagger created (native IErrorTag)");
            Schedule();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (disposed || spans.Count == 0) return Empty;

            List<TagSpan<IErrorTag>> current = tags;
            if (current == null || current.Count == 0) return Empty;

            ITextSnapshot snapshot = current[0].Span.Snapshot;
            if (snapshot != spans[0].Snapshot) return Empty;

            var result = new List<ITagSpan<IErrorTag>>();
            foreach (TagSpan<IErrorTag> candidate in current)
            {
                if (candidate.Span.Snapshot != snapshot) continue;
                foreach (SnapshotSpan requested in spans)
                {
                    if (!requested.IntersectsWith(candidate.Span)) continue;
                    result.Add(candidate);
                    break;
                }
            }
            return result;
        }

        private static readonly IEnumerable<ITagSpan<IErrorTag>> Empty = new ITagSpan<IErrorTag>[0];

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e) => Schedule();

        private void OnSettingsTick(object sender, EventArgs e)
        {
            if (disposed) return;
            if (settingsVersion == SettingsManager.AnalysisSettingsVersion) return;
            settingsVersion = SettingsManager.AnalysisSettingsVersion;
            Schedule();
        }

        private void Schedule()
        {
            if (disposed) return;

            CancellationTokenSource next = new CancellationTokenSource();
            CancellationTokenSource previous = Interlocked.Exchange(ref pending, next);
            if (previous != null)
            {
                try { previous.Cancel(); previous.Dispose(); } catch (Exception) { }
            }

            int version = Interlocked.Increment(ref analysisVersion);
            CancellationToken token = next.Token;
            Task.Delay(DebounceMilliseconds, token).ContinueWith(task =>
            {
                if (task.IsCanceled || token.IsCancellationRequested) return;
                Analyze(version);
            }, TaskScheduler.Default);
        }

        private void Analyze(int version)
        {
            if (disposed) return;

            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            string text = snapshot.GetText();
            AnalysisSettings settings;
            List<AnalysisDiagnostic> results;
            try
            {
                settings = SettingsManager.GetAnalysisSettings();
                results = engine.Analyze(text, settings, -1);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Diagnostics", "Analysis failed", ex);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(new Action(() => Apply(version, snapshot, settings, results, text)));
            }
            catch (Exception)
            {
                // The buffer was torn down while the analysis was running.
            }
        }

        private void Apply(int version, ITextSnapshot snapshot, AnalysisSettings settings, List<AnalysisDiagnostic> results, string text)
        {
            if (disposed || version != Volatile.Read(ref analysisVersion)) return;
            if (snapshot != buffer.CurrentSnapshot) return;

            var next = new List<TagSpan<IErrorTag>>();
            foreach (AnalysisDiagnostic diagnostic in results)
            {
                if (!settings.showSquiggles) break;
                if (diagnostic.Severity == AnalysisSeverity.Info && !settings.showInfoSquiggles) continue;
                if (diagnostic.Start < 0 || diagnostic.End <= diagnostic.Start) continue;
                if (diagnostic.End > snapshot.Length) continue;

                string errorType = diagnostic.Severity == AnalysisSeverity.Error
                    ? PredefinedErrorTypeNames.SyntaxError
                    : PredefinedErrorTypeNames.Warning;
                next.Add(new TagSpan<IErrorTag>(
                    new SnapshotSpan(snapshot, diagnostic.Start, diagnostic.End - diagnostic.Start),
                    new ErrorTag(errorType, diagnostic.Message)));
            }

            buffer.Properties[SnapshotKey] = new DiagnosticSnapshot
            {
                Snapshot = snapshot,
                Diagnostics = results,
                Settings = settings
            };

            tags = next;
            AnalysisResultsBroker.Publish("active", text, results, settings);
            MSSQLToolPackage._logger?.Info(
                "MSSQLTool: diagnostics analyzed findings=" + results.Count + " squiggles=" + next.Count);

            var changed = new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length));
            try
            {
                if (dispatcher.CheckAccess()) TagsChanged?.Invoke(this, changed);
                else dispatcher.BeginInvoke(new Action(() => TagsChanged?.Invoke(this, changed)));
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Increment(ref analysisVersion);
            try { settingsTimer.Stop(); settingsTimer.Tick -= OnSettingsTick; } catch (Exception) { }
            buffer.Changed -= OnBufferChanged;
            CancellationTokenSource current = Interlocked.Exchange(ref pending, null);
            if (current != null)
            {
                try { current.Cancel(); current.Dispose(); } catch (Exception) { }
            }
            tags = new List<TagSpan<IErrorTag>>();
        }
    }
}
