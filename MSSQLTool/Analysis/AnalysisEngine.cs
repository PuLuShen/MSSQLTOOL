using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MSSQLTool.Model;

namespace MSSQLTool.Analysis
{
    /// <summary>
    /// Runs the enabled rules per statement with a hard per-rule timeout and a per-document
    /// budget.  Results are cached by text hash + settings, so scrolling or re-rendering never
    /// re-runs the rules.  A rule that throws is logged and skipped; it can never take the
    /// editor down.
    /// </summary>
    internal sealed class AnalysisEngine
    {
        private const int PerRuleMilliseconds = 50;
        private const int DocumentBudgetMilliseconds = 300;
        private const int MaximumDocumentLength = 500 * 1024;
        private const int CacheCapacity = 6;
        private static readonly object Gate = new object();
        private static readonly List<CacheEntry> Cache = new List<CacheEntry>();

        public List<AnalysisDiagnostic> Analyze(string text, AnalysisSettings settings, int caretOffset = -1)
        {
            return Analyze(text, settings, caretOffset, CancellationToken.None);
        }

        /// <summary>
        /// Same analysis with a cancellation token.  The editor schedules one pass per edit and
        /// only the newest result is used, so a pass whose text was already superseded has to
        /// stop instead of holding a pool thread until the document budget runs out.
        /// </summary>
        public List<AnalysisDiagnostic> Analyze(string text, AnalysisSettings settings, int caretOffset, CancellationToken cancellationToken)
        {
            if (settings == null || !settings.enabled) return new List<AnalysisDiagnostic>();

            cancellationToken.ThrowIfCancellationRequested();

            string value = text ?? string.Empty;
            if (value.Length > MaximumDocumentLength)
            {
                MSSQLToolPackage._logger?.Debug("ANALYSIS skipped: document exceeds " + MaximumDocumentLength + " characters.");
                return new List<AnalysisDiagnostic>();
            }

            int hash = SqlDocumentModel.Hash(value);
            string signature = hash + "|" + settings.Signature();
            lock (Gate)
            {
                for (int i = Cache.Count - 1; i >= 0; i--)
                {
                    CacheEntry entry = Cache[i];
                    if (!string.Equals(entry.Signature, signature, StringComparison.Ordinal)) continue;
                    if (!string.Equals(entry.Text, value, StringComparison.Ordinal)) continue;
                    if (i != Cache.Count - 1) { Cache.RemoveAt(i); Cache.Add(entry); }
                    return new List<AnalysisDiagnostic>(entry.Results);
                }
            }

            SqlDocumentModel model = SqlDocumentModel.Get(value);
            List<SqlStatementRange> statements = model.Statements.Statements;
            if (caretOffset >= 0)
            {
                SqlStatementRange current = model.StatementAt(caretOffset);
                if (current != null)
                    statements = statements.OrderByDescending(s => ReferenceEquals(s, current)).ToList();
            }

            List<RuleDefinition> enabledRules = RuleCatalog.Enabled(settings).ToList();
            var collected = new List<AnalysisDiagnostic>();
            var stopwatch = Stopwatch.StartNew();

            foreach (SqlStatementRange statement in statements)
            {
                if (stopwatch.ElapsedMilliseconds > DocumentBudgetMilliseconds)
                {
                    MSSQLToolPackage._logger?.Debug($"ANALYSIS budget reached after {stopwatch.ElapsedMilliseconds} ms; remaining statements skipped.");
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var context = new RuleContext
                {
                    Text = value,
                    Tokens = model.Tokens,
                    Statement = statement,
                    Model = model
                };
                if (IsSuppressed(context)) continue;

                foreach (RuleDefinition rule in enabledRules)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ruleWatch = Stopwatch.StartNew();
                    try
                    {
                        AnalysisRules.Run(rule.Id, context);
                    }
                    catch (Exception ex)
                    {
                        FeatureDiagnostics.Report("Code Analysis", "Rule " + rule.Id + " failed", ex);
                    }
                    if (ruleWatch.ElapsedMilliseconds > PerRuleMilliseconds)
                        MSSQLToolPackage._logger?.Debug($"ANALYSIS rule {rule.Id} took {ruleWatch.ElapsedMilliseconds} ms (limit {PerRuleMilliseconds}).");
                }

                foreach (AnalysisDiagnostic diagnostic in context.Diagnostics)
                {
                    RuleDefinition rule = RuleCatalog.Find(diagnostic.RuleId);
                    if (rule == null) continue;
                    if (settings.IsRuleDisabled(rule.Id)) continue;
                    diagnostic.Severity = RuleCatalog.SeverityOf(rule, settings);
                    if (diagnostic.Length <= 0) continue;
                    diagnostic.Start = Math.Max(0, Math.Min(diagnostic.Start, value.Length));
                    diagnostic.Length = Math.Min(diagnostic.Length, value.Length - diagnostic.Start);
                    collected.Add(diagnostic);
                }
            }

            var result = new List<AnalysisDiagnostic>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (AnalysisDiagnostic diagnostic in collected.OrderBy(d => d.Start))
            {
                string key = diagnostic.RuleId + "@" + diagnostic.Start + ":" + diagnostic.Length;
                if (!seen.Add(key)) continue;
                result.Add(diagnostic);
            }

            lock (Gate)
            {
                Cache.Add(new CacheEntry { Signature = signature, Text = value, Results = result });
                while (Cache.Count > CacheCapacity) Cache.RemoveAt(0);
            }

            MSSQLToolPackage._logger?.Debug($"ANALYSIS {value.Length} chars, {statements.Count} statement(s), {enabledRules.Count} rule(s), {result.Count} diagnostic(s) in {stopwatch.ElapsedMilliseconds} ms.");
            return new List<AnalysisDiagnostic>(result);
        }

        private static bool IsSuppressed(RuleContext context)
        {
            for (int i = context.First; i <= context.Last && i < context.Tokens.Count; i++)
            {
                SqlToken token = context.Tokens[i];
                if (token.Kind != SqlTokenKind.Comment) continue;
                if (token.Text.IndexOf("mssqltool:disable", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            // A directive line directly above the statement suppresses it too, which is how
            // people usually mark a whole block:  -- mssqltool:disable  <newline>  DELETE ...
            string text = context.Model.Text;
            int lineEnd = context.Statement.Start;
            for (int depth = 0; depth < 2 && lineEnd > 0; depth++)
            {
                int newline = lineEnd > 0 ? text.LastIndexOf('\n', lineEnd - 1) : -1;
                if (newline < 0) break;
                int previousNewline = newline > 0 ? text.LastIndexOf('\n', newline - 1) : -1;
                string line = text.Substring(previousNewline + 1, newline - previousNewline - 1);
                if (line.IndexOf("mssqltool:disable", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                lineEnd = newline;
            }

            return false;
        }

        private sealed class CacheEntry
        {
            public string Signature;
            public string Text;
            public List<AnalysisDiagnostic> Results;
        }
    }
}
