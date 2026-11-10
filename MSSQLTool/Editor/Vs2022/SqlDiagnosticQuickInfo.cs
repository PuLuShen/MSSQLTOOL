using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using MSSQLTool.Analysis;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>
    /// Native quick info: hovering a squiggle shows the rule title, the message and the rule's
    /// explanation.  VS owns the popup, its placement and its lifetime, so nothing is positioned
    /// here and nothing can flicker.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    [Name("MSSQLTool diagnostic quick info")]
    [Order(Before = "default")]
    public sealed class SqlDiagnosticQuickInfoProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer buffer)
        {
            try
            {
                return new SqlDiagnosticQuickInfoSource(buffer);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Quick Info", "The quick info source could not be created", ex);
                return null;
            }
        }
    }

    internal sealed class SqlDiagnosticQuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer buffer;

        public SqlDiagnosticQuickInfoSource(ITextBuffer buffer)
        {
            this.buffer = buffer;
        }

        public Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            try
            {
                if (session == null || buffer == null || cancellationToken.IsCancellationRequested)
                    return Task.FromResult<QuickInfoItem>(null);

                ITextSnapshot snapshot = buffer.CurrentSnapshot;
                SnapshotPoint? trigger = session.GetTriggerPoint(snapshot);
                if (trigger == null) return Task.FromResult<QuickInfoItem>(null);

                DiagnosticSnapshot latest;
                if (!buffer.Properties.TryGetProperty(SqlDiagnosticsTagger.SnapshotKey, out latest) || latest == null)
                    return Task.FromResult<QuickInfoItem>(null);
                if (latest.Snapshot != snapshot || latest.Diagnostics == null || latest.Diagnostics.Count == 0)
                    return Task.FromResult<QuickInfoItem>(null);

                AnalysisSettings settings = latest.Settings;
                if (settings == null) return Task.FromResult<QuickInfoItem>(null);
                if (!settings.showHover) return Task.FromResult<QuickInfoItem>(null);

                AnalysisDiagnostic hit = null;
                foreach (AnalysisDiagnostic diagnostic in latest.Diagnostics)
                {
                    if (diagnostic.Severity == AnalysisSeverity.Info && !settings.showInfoSquiggles) continue;
                    if (trigger.Value.Position >= diagnostic.Start && trigger.Value.Position <= diagnostic.End)
                    {
                        hit = diagnostic;
                        break;
                    }
                }
                if (hit == null) return Task.FromResult<QuickInfoItem>(null);

                int start = Math.Max(0, Math.Min(hit.Start, snapshot.Length));
                int length = Math.Max(1, Math.Min(hit.End - hit.Start, snapshot.Length - start));
                ITrackingSpan span = snapshot.CreateTrackingSpan(start, length, SpanTrackingMode.EdgeInclusive);
                return Task.FromResult(new QuickInfoItem(span, BuildText(hit)));
            }
            catch (Exception)
            {
                // Quick info is cosmetic: never surface an editor error for it.
                return Task.FromResult<QuickInfoItem>(null);
            }
        }

        private static string BuildText(AnalysisDiagnostic diagnostic)
        {
            string severity = diagnostic.Severity == AnalysisSeverity.Error
                ? LocalizationManager.T("Error")
                : diagnostic.Severity == AnalysisSeverity.Warning ? LocalizationManager.T("Warning") : LocalizationManager.T("Info");

            RuleDefinition rule = RuleCatalog.Find(diagnostic.RuleId);
            string description = rule == null ? null
                : (LocalizationManager.IsChinese ? rule.DescriptionZh : rule.DescriptionEn);

            string text = severity + " · " + AnalysisResultsBroker.RuleTitle(diagnostic.RuleId)
                + Environment.NewLine + diagnostic.Message;
            if (!string.IsNullOrWhiteSpace(description))
                text += Environment.NewLine + Environment.NewLine + description;
            if (QuickFixService.CanFix(diagnostic) && !string.Equals(diagnostic.RuleId, "select-star", StringComparison.OrdinalIgnoreCase))
                text += Environment.NewLine + Environment.NewLine + LocalizationManager.T("Ctrl+. applies this fix.");
            return text;
        }

        public void Dispose()
        {
        }
    }
}
