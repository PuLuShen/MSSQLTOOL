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
using MSSQLTool.Model;

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

    internal sealed class SqlDiagnosticsTagger : ITagger<IErrorTag>, IDisposable, IAnalysisSettingsListener
    {
        internal const string SnapshotKey = "MSSQLTool.DiagnosticSnapshot";

        private const int DebounceMilliseconds = 650;

        /// <summary>
        /// At most one analysis per buffer and at most two documents at a time in the whole shell.
        /// Every edit used to start another pool task, and a task that had already started could
        /// not be cancelled, so fast typing kept several cores busy with results that were then
        /// thrown away by the version check.
        /// </summary>
        private static readonly SemaphoreSlim ConcurrencyGate = new SemaphoreSlim(MaxConcurrentAnalyses, MaxConcurrentAnalyses);

        private static int MaxConcurrentAnalyses => Math.Max(1, Math.Min(2, Environment.ProcessorCount / 4));

        private readonly ITextBuffer buffer;
        private readonly AnalysisEngine engine = new AnalysisEngine();
        private readonly Dispatcher dispatcher;
        private List<TagSpan<IErrorTag>> tags = new List<TagSpan<IErrorTag>>();
        private CancellationTokenSource pending;
        private int analysisVersion;
        private int analysisRunning;
        private int analysisQueued;
        private string lastAppliedSignature;
        private bool disposed;

        public SqlDiagnosticsTagger(ITextBuffer buffer)
        {
            this.buffer = buffer;
            dispatcher = Dispatcher.CurrentDispatcher;

            buffer.Changed += OnBufferChanged;
            AnalysisSettingsWatcher.Register(this);
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

        /// <summary>A setting changed somewhere in the shell; this buffer has to be analysed again.</summary>
        public void OnAnalysisSettingsChanged() => Schedule();

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
                Pump(version, token);
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// Starts the analysis for <paramref name="version"/> unless this buffer already has one in
        /// flight.  A newer request only marks the buffer dirty; the running pass schedules itself
        /// again when it finishes, so the pool never holds more than one pass per buffer.
        /// </summary>
        private void Pump(int version, CancellationToken token)
        {
            if (disposed || token.IsCancellationRequested) return;
            if (version != Volatile.Read(ref analysisVersion)) return;

            if (Interlocked.CompareExchange(ref analysisRunning, 1, 0) != 0)
            {
                Interlocked.Exchange(ref analysisQueued, 1);
                return;
            }

            _ = Task.Run(() => AnalyzeAsync(version, token));
        }

        private async Task AnalyzeAsync(int version, CancellationToken token)
        {
            try
            {
                await ConcurrencyGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (disposed || version != Volatile.Read(ref analysisVersion)) return;

                    ITextSnapshot snapshot = buffer.CurrentSnapshot;
                    string text = snapshot.GetText();
                    AnalysisSettings settings = SettingsManager.GetAnalysisSettings();

                    // The engine caches by text hash too, but stopping here saves the copy and the
                    // hash for the common case where only the caret or the settings stamp moved.
                    string signature = text.Length + "|" + SqlDocumentModel.Hash(text) + "|" + settings.Signature();
                    if (string.Equals(signature, lastAppliedSignature, StringComparison.Ordinal)) return;

                    List<AnalysisDiagnostic> results = engine.Analyze(text, settings, -1, token);
                    token.ThrowIfCancellationRequested();

                    _ = dispatcher.BeginInvoke(new Action(() => Apply(version, snapshot, settings, results, text, signature)));
                }
                finally
                {
                    ConcurrencyGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer edit; the newest pass publishes the result.
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Diagnostics", "Analysis failed", ex);
            }
            finally
            {
                Volatile.Write(ref analysisRunning, 0);
                if (Interlocked.Exchange(ref analysisQueued, 0) != 0 && !disposed) Schedule();
            }
        }

        private void Apply(int version, ITextSnapshot snapshot, AnalysisSettings settings, List<AnalysisDiagnostic> results, string text, string signature)
        {
            if (disposed) return;

            if (version != Volatile.Read(ref analysisVersion) || snapshot != buffer.CurrentSnapshot)
            {
                MSSQLToolPackage._logger?.Debug("MSSQLTool: stale diagnostics result discarded");
                return;
            }

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
            lastAppliedSignature = signature;
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
            AnalysisSettingsWatcher.Unregister(this);
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
