using System;
using System.Collections.Generic;
using System.Linq;
using MSSQLTool.Model;

namespace MSSQLTool.Analysis
{
    internal enum AnalysisSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    internal sealed class AnalysisDiagnostic
    {
        public string RuleId;
        public AnalysisSeverity Severity;
        public string Message;
        public int Start;
        public int Length;

        public int End => Start + Length;
    }

    internal sealed class AnalysisSettings
    {
        public bool enabled = true;
        /// <summary>Master switch for the editor squiggles; the tool window still lists findings.</summary>
        public bool showSquiggles = true;
        public bool showInfoSquiggles = false;
        public bool showHover = true;
        /// <summary>Rule id → "off", "info", "warning", "error".  Missing means "use the default".</summary>
        public Dictionary<string, string> ruleSeverity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public AnalysisSeverity? SeverityOverride(string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId) || ruleSeverity == null) return null;
            if (!ruleSeverity.TryGetValue(ruleId, out string value) || string.IsNullOrWhiteSpace(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "off": return null;
                case "info": return AnalysisSeverity.Info;
                case "warning": return AnalysisSeverity.Warning;
                case "error": return AnalysisSeverity.Error;
                default: return null;
            }
        }

        public bool IsRuleDisabled(string ruleId)
            => ruleSeverity != null && ruleSeverity.TryGetValue(ruleId, out string value)
                && string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

        public string Signature()
        {
            var parts = new List<string> { enabled ? "1" : "0", showSquiggles ? "1" : "0", showInfoSquiggles ? "1" : "0" };
            if (ruleSeverity != null)
                foreach (var pair in ruleSeverity.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    parts.Add(pair.Key + "=" + pair.Value);
            return string.Join(";", parts);
        }
    }

    internal sealed class RuleDefinition
    {
        public string Id;
        public string Category;
        public AnalysisSeverity DefaultSeverity;
        public bool EnabledByDefault;
        public string TitleZh;
        public string TitleEn;
        public string MessageZh;
        public string MessageEn;
        public string DescriptionZh;
        public string DescriptionEn;

        public string Title => LocalizationManager.IsChinese ? TitleZh : TitleEn;
        public string Description => LocalizationManager.IsChinese ? DescriptionZh : DescriptionEn;

        public string Format(params object[] args)
        {
            string template = LocalizationManager.IsChinese ? MessageZh : MessageEn;
            try { return args == null || args.Length == 0 ? template : string.Format(template, args); }
            catch (FormatException) { return template; }
        }
    }

    /// <summary>
    /// Per-statement analysis input.  Rules read tokens (comments and strings already carry
    /// their own token kind), the statement range and the lazily built scope tree.  Nothing is
    /// parsed into an AST, so a half-typed statement is analysed just as well as a finished one.
    /// </summary>
    internal sealed class RuleContext
    {
        public string Text;
        public List<SqlToken> Tokens;
        public SqlStatementRange Statement;
        public SqlDocumentModel Model;
        public List<AnalysisDiagnostic> Diagnostics = new List<AnalysisDiagnostic>();
        private SqlScopeTree scope;

        public int First => Statement == null ? 0 : Math.Max(0, Statement.FirstToken);
        public int Last => Statement == null ? (Tokens == null ? -1 : Tokens.Count - 1) : Math.Min(Statement.LastToken, Tokens.Count - 1);

        public SqlScopeTree Scope
        {
            get
            {
                if (scope != null) return scope;
                scope = SqlScopeTree.Build(Text, Tokens, Statement);
                return scope;
            }
        }

        public void Add(string ruleId, string message, int start, int length)
        {
            if (length <= 0 || start < 0) return;
            if (Diagnostics.Any(d => d.RuleId == ruleId && d.Start == start && d.Length == length)) return;
            Diagnostics.Add(new AnalysisDiagnostic { RuleId = ruleId, Message = message, Start = start, Length = length });
        }

        public void Add(RuleDefinition rule, int start, int length, params object[] args)
            => Add(rule.Id, rule.Format(args), start, length);

        public int Next(int index)
        {
            for (int i = index + 1; i <= Last && i < Tokens.Count; i++)
                if (!Tokens[i].IsComment) return i;
            return -1;
        }

        public int Previous(int index)
        {
            for (int i = index - 1; i >= First; i--)
                if (!Tokens[i].IsComment) return i;
            return -1;
        }

        public bool IsWord(int index, string value)
            => index >= 0 && index < Tokens.Count && Tokens[index].IsWordLike && Tokens[index].Is(value);

        public bool IsSymbol(int index, string value)
            => index >= 0 && index < Tokens.Count && Tokens[index].Text == value;

        public string Word(int index, bool unquote = false)
        {
            if (index < 0 || index >= Tokens.Count) return string.Empty;
            return unquote ? SqlTokenizer.Unquote(Tokens[index]) : Tokens[index].Text;
        }

        /// <summary>Token range [startIndex, endIndex) as text, comments blanked out.</summary>
        public string Slice(int startIndex, int endIndex)
        {
            if (startIndex < 0 || endIndex <= startIndex) return string.Empty;
            int from = Tokens[Math.Min(startIndex, Tokens.Count - 1)].Start;
            int to = Tokens[Math.Min(endIndex, Tokens.Count - 1)].End;
            if (to <= from) return string.Empty;
            var chars = Text.Substring(from, Math.Min(to, Text.Length) - from).ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '\r' || chars[i] == '\n' || chars[i] == '\t') continue;
                chars[i] = chars[i];
            }
            return new string(chars);
        }

        public int IndexOfWord(string word)
        {
            for (int i = First; i <= Last && i < Tokens.Count; i++)
                if (Tokens[i].IsWordLike && Tokens[i].Is(word)) return i;
            return -1;
        }

        public int DepthAt(int tokenIndex)
        {
            int depth = 0;
            for (int i = First; i < tokenIndex && i < Tokens.Count; i++)
            {
                if (Tokens[i].Text == "(") depth++;
                else if (Tokens[i].Text == ")") depth = Math.Max(0, depth - 1);
            }
            return depth;
        }
    }

    internal static class AnalysisResultsBroker
    {
        private static readonly object Gate = new object();

        public static string DocumentKey { get; private set; } = string.Empty;
        public static string DocumentText { get; private set; } = string.Empty;
        public static List<AnalysisDiagnostic> Results { get; private set; } = new List<AnalysisDiagnostic>();
        public static event EventHandler Changed;

        public static void Publish(string documentKey, string text, List<AnalysisDiagnostic> results, AnalysisSettings settings)
        {
            lock (Gate)
            {
                DocumentKey = documentKey ?? string.Empty;
                DocumentText = text ?? string.Empty;
                Results = results ?? new List<AnalysisDiagnostic>();
                LastSettings = settings ?? new AnalysisSettings();
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static AnalysisSettings LastSettings { get; private set; } = new AnalysisSettings();

        public static string RuleTitle(string ruleId)
        {
            RuleDefinition rule = RuleCatalog.Find(ruleId);
            return rule == null ? ruleId : rule.Title;
        }

        public static string RuleDescription(string ruleId)
        {
            RuleDefinition rule = RuleCatalog.Find(ruleId);
            return rule == null ? string.Empty : rule.Description;
        }
    }
}
