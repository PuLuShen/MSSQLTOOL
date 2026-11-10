using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using MSSQLTool.Analysis;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>
    /// The editor light bulb.  It offers exactly the fixes that can be expressed as a plain text
    /// edit, so the suggestion never depends on metadata or on a command being available; the
    /// metadata-aware SELECT * expansion stays in the Code Analysis tool window.
    ///
    /// The MEF export is intentionally narrow: no imports, no service lookups, so a composition
    /// problem can only cost the bulb, never the editor or the rest of the package.
    /// </summary>
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("MSSQL Tool quick fixes")]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    public sealed class SqlSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            if (textView == null || textBuffer == null) return null;
            return new SqlSuggestedActionsSource(textView, textBuffer);
        }
    }

    internal sealed class SqlSuggestedActionsSource : ISuggestedActionsSource, IDisposable
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private readonly DispatcherTimer refresh;
        private bool disposed;

        /// <summary>
        /// The editor asks for a light bulb after every change it is told about.  Running the rule
        /// engine per keystroke cost the whole document (measured ~11 ms at 10 KB and ~300 ms at
        /// 50 KB), so the event is coalesced and fired once the typing pauses.
        /// </summary>
        private const int RefreshDelayMilliseconds = 400;

        public SqlSuggestedActionsSource(ITextView view, ITextBuffer buffer)
        {
            this.view = view;
            this.buffer = buffer;
            Dispatcher dispatcher = null;
            try
            {
                var wpfView = view as IWpfTextView;
                if (wpfView != null) dispatcher = wpfView.VisualElement?.Dispatcher;
            }
            catch (Exception) { }
            refresh = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(RefreshDelayMilliseconds)
            };
            refresh.Tick += OnRefreshTick;
            buffer.Changed += OnBufferChanged;
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged;

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancel)
        {
            int caret = CurrentCaretOffset();
            if (caret < 0 || cancel.IsCancellationRequested) return Task.FromResult(false);
            // The engine parses the whole document; keep it off the UI thread so a large script
            // cannot block the keystroke that asked for the light bulb.
            return Task.Run(() => FindFix(caret, cancel) != null, cancel);
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancel)
        {
            Fix? fix = FindFix(CurrentCaretOffset(), cancel);
            if (fix == null) return Enumerable.Empty<SuggestedActionSet>();
            return new[] { new SuggestedActionSet(null, new ISuggestedAction[] { new QuickFixAction(fix.Value.Display, fix.Value.Apply) }, null, SuggestedActionSetPriority.Medium, null) };
        }

        private int CurrentCaretOffset()
        {
            try { return view.Caret.Position.BufferPosition.Position; }
            catch (Exception) { return -1; }
        }

        private Fix? FindFix(int caret, CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested || disposed || caret < 0) return null;
            try
            {
                AnalysisSettings settings = SettingsManager.GetAnalysisSettings();
                if (!settings.enabled) return null;

                string text = buffer.CurrentSnapshot.GetText();
                if (caret > text.Length) return null;
                AnalysisDiagnostic diagnostic = QuickFixService.FindFixableAtCaret(text, caret, settings);
                if (diagnostic == null) return null;
                if (!QuickFixService.TryBuildEdit(text, diagnostic, out int start, out int length, out string replacement)) return null;

                string title = AnalysisResultsBroker.RuleTitle(diagnostic.RuleId);
                string display = LocalizationManager.T("Fix") + ": " + title;
                return new Fix
                {
                    Display = display,
                    Apply = () =>
                    {
                        try
                        {
                            using (ITextEdit edit = buffer.CreateEdit())
                            {
                                if (length == 0) edit.Insert(start, replacement);
                                else edit.Replace(new Span(start, length), replacement);
                                edit.Apply();
                            }
                        }
                        catch (Exception ex)
                        {
                            FeatureDiagnostics.Report("Code Analysis", "Applying a light bulb fix failed", ex);
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Code Analysis", "Building light bulb suggestions failed", ex);
                return null;
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (disposed) return;
            try
            {
                // Buffer events normally arrive on the UI thread, but a background edit must not
                // throw out of a text change handler: fall back to the old immediate notification.
                if (refresh.Dispatcher.CheckAccess())
                {
                    refresh.Stop();
                    refresh.Start();
                    return;
                }
                refresh.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed) return;
                    refresh.Stop();
                    refresh.Start();
                }));
            }
            catch (Exception)
            {
                EventHandler<EventArgs> handler = SuggestedActionsChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            refresh.Stop();
            if (disposed) return;
            EventHandler<EventArgs> handler = SuggestedActionsChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { refresh.Stop(); refresh.Tick -= OnRefreshTick; } catch (Exception) { }
            buffer.Changed -= OnBufferChanged;
        }

        private struct Fix
        {
            public string Display;
            public Action Apply;
        }

        private sealed class QuickFixAction : ISuggestedAction
        {
            private readonly string display;
            private readonly Action apply;

            public QuickFixAction(string displayText, Action apply)
            {
                display = displayText;
                this.apply = apply;
            }

            public string DisplayText => display;

            public string IconAutomationText => null;

            public string InputGestureText => null;

            public ImageMoniker IconMoniker => default(ImageMoniker);

            public bool HasActionSets => false;

            public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancel)
                => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);

            public bool HasPreview => false;

            public Task<object> GetPreviewAsync(CancellationToken cancel)
                => Task.FromResult<object>(null);

            public void Invoke(CancellationToken cancel)
            {
                if (!cancel.IsCancellationRequested) apply();
            }

            public bool TryGetTelemetryId(out Guid telemetryId)
            {
                telemetryId = Guid.Empty;
                return false;
            }

            public void Dispose() { }
        }
    }
}
