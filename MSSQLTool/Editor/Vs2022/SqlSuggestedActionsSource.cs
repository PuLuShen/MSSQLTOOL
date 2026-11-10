using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private bool disposed;

        public SqlSuggestedActionsSource(ITextView view, ITextBuffer buffer)
        {
            this.view = view;
            this.buffer = buffer;
            buffer.Changed += OnBufferChanged;
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged;

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancel)
            => Task.FromResult(FindFix(cancel) != null);

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancel)
        {
            Fix? fix = FindFix(cancel);
            if (fix == null) return Enumerable.Empty<SuggestedActionSet>();
            return new[] { new SuggestedActionSet(null, new ISuggestedAction[] { new QuickFixAction(fix.Value.Display, fix.Value.Apply) }, null, SuggestedActionSetPriority.Medium, null) };
        }

        private Fix? FindFix(CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested || disposed) return null;
            try
            {
                AnalysisSettings settings = SettingsManager.GetAnalysisSettings();
                if (!settings.enabled) return null;

                string text = buffer.CurrentSnapshot.GetText();
                int caret = view.Caret.Position.BufferPosition.Position;
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
            EventHandler<EventArgs> handler = SuggestedActionsChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
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
