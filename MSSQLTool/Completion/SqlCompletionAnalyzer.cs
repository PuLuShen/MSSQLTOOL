using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MSSQLTool.Completion
{
    internal static class SqlCompletionAnalyzer
    {
        private const string Id = @"(?:\[(?:\]\]|[^\]])+\]|""(?:""""|[^""])+""|[#@\w$]+)";
        private static readonly Regex AliasPattern = new Regex(@"\b(?:FROM|JOIN|APPLY)\s+(?<obj>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?(?<alias>" + Id + @"))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Patterns on the per-keystroke path are precompiled; interpreted
        // matching dominated profile time on large statements.
        private static readonly Regex PrefixPattern = new Regex(@"(?<prefix>[@#\w$\[\]]*)$", RegexOptions.Compiled);
        private static readonly Regex InsertColumnListPattern = new Regex(@"\bINSERT\s+(?:INTO\s+)?(?<target>(?:" + Id + @"(?:\s*\.\s*" + Id + @"){0,3}|" + Id + @"(?:\s*\.\s*" + Id + @")?\s*\.\s*\.\s*" + Id + @"))\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CompletedInsertTargetPattern = new Regex(@"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InsertBodyPattern = new Regex(@"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OmittedSchemaMemberPattern = new Regex(@"(?<qual>" + Id + @"(?:\s*\.\s*" + Id + @")?)\s*\.\s*\.\s*(?<prefix>" + Id + @")?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MemberPattern = new Regex(@"(?<qual>" + Id + @"(?:\s*\.\s*" + Id + @"){0,2})\s*\.\s*(?<prefix>" + Id + @")?$", RegexOptions.Compiled);
        private static readonly Regex JoinSourcePattern = new Regex(@"\bJOIN\s+" + Id + @"(?:\s*\.\s*" + Id + @"){0,2}\s*\.\s*(?:" + Id + @")?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UpdateSetPattern = new Regex(@"\bUPDATE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?" + Id + @")?\s+SET\s+[^;]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DmlTailPattern = new Regex(@"\b(?:MERGE|TRUNCATE\s+TABLE|DELETE\s+FROM)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PredicateColumnPattern = new Regex(@"(?<column>" + Id + @"(?:\s*\.\s*" + Id + @")?)\s*(?:=|<>|!=|>=|<=|>|<|LIKE|IN|BETWEEN)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FunctionTargetTailPattern = new Regex(@"(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NonFunctionBeforeTargetPattern = new Regex(@"\b(?:INSERT(?:\s+INTO)?|CREATE\s+TABLE|ALTER\s+TABLE|REFERENCES)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WindowOverPattern = new Regex(@"\bOVER\s*\((?<body>[^()]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex WindowOrderByPattern = new Regex(@"\bORDER\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WindowPartitionByPattern = new Regex(@"\bPARTITION\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OutputTailPattern = new Regex(@"\bOUTPUT\s+(?:(?<qual>inserted|deleted)\s*\.\s*)?(?<prefix>" + Id + @")?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OutputTargetPattern = new Regex(@"\b(?:INSERT\s+(?:INTO\s+)?|UPDATE\s+|DELETE\s+FROM\s+|MERGE\s+(?:INTO\s+)?)(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IndexColumnsPattern = new Regex(@"\bCREATE\s+(?:UNIQUE\s+)?(?:CLUSTERED\s+|NONCLUSTERED\s+)?INDEX\s+" + Id + @"\s+ON\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex AlterDropColumnPattern = new Regex(@"\bALTER\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+(?:ALTER|DROP)\s+COLUMN\s+(?:" + Id + @")?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AlterTableTailPattern = new Regex(@"\bALTER\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+(?:" + Id + @")?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CreateTablePattern = new Regex(@"\bCREATE\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*\((?<body>[\s\S]*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConstraintColumnsPattern = new Regex(@"\b(?:PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY)\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AlterConstraintPattern = new Regex(@"\bALTER\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})[\s\S]*\b(?:PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY)\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex KeywordStartPattern = new Regex(@"^[ \t]*(?<keyword>EXEC(?:UTE)?|SELECT|INSERT|UPDATE|DELETE|MERGE|DECLARE|SET|CREATE|ALTER|DROP|TRUNCATE|USE|PRINT|IF|WHILE|BEGIN|DBCC)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExecBodyPattern = new Regex(@"\bEXEC(?:UTE)?\b(?<body>[^;]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ExecAsTailPattern = new Regex(@"^AS\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NamedParameterAssignPattern = new Regex(@"^@\w+\s*=\s*", RegexOptions.Compiled);
        private static readonly Regex ExecObjectTailPattern = new Regex(@"^" + Id + @"(?:\s*\.\s*" + Id + @"){0,3}\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExecObjectHeadPattern = new Regex(@"^(?<obj>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex UsedParameterPattern = new Regex(@"(?<!@)@\w+\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ActiveParameterValuePattern = new Regex(@"(?<parameter>@\w+)\s*=\s*(?<value>[^,]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ActiveParameterPrefixPattern = new Regex(@"(?<prefix>@\w*)$", RegexOptions.Compiled);
        private static readonly Regex DetachedExecPattern = new Regex(@"^\s*EXEC(?:UTE)?\s+" + Id, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DetachedPrefixPattern = new Regex(@"(?:[ \t]{2,}|\r?\n[ \t]*)(?<prefix>[A-Za-z_][\w$]*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ArgumentPrefixPattern = new Regex(@"(?<prefix>[@#\w$]*)$", RegexOptions.Compiled);
        private static readonly Regex DeclareTablePattern = new Regex(@"\bDECLARE\s+(?<name>@\w+)\s+TABLE\s*\((?<cols>[^;]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TempCreatePattern = new Regex(@"\bCREATE\s+TABLE\s+(?<name>\#\#?\w+)\s*\((?<cols>[^;]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex SelectIntoPattern = new Regex(@"\bSELECT\s+(?<select>.*?)\s+INTO\s+(?<name>\#\#?\w+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TempAlterPattern = new Regex(@"\bALTER\s+TABLE\s+(?<name>\#\#?\w+)\s+ADD\s+(?<cols>[^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TempDropPattern = new Regex(@"\bDROP\s+TABLE(?:\s+IF\s+EXISTS)?\s+(?<name>\#\#?\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DefinitionColumnPattern = new Regex(@"^(?<name>" + Id + @")\s+(?<type>[\w]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectAliasNamePattern = new Regex(@"(?:\bAS\s+|\s+)(?<name>" + Id + @")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SimpleWordPattern = new Regex(@"^[#@\w]+$", RegexOptions.Compiled);
        private static readonly Regex DeclareBodyPattern = new Regex(@"\bDECLARE\s+(?<body>[^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VariableDeclarationPattern = new Regex(@"^(?<name>@\w+)\s+(?<type>[\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectFromListPattern = new Regex(@"\bSELECT\s+(?<list>.*?)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex AggregationPattern = new Regex(@"\b(?:AVG|COUNT|GROUPING|MAX|MIN|STDEV|STDEVP|STRING_AGG|SUM|VAR|VARP)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EqualAliasPattern = new Regex(@"^" + Id + @"\s*=\s*(?<value>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TrailingAliasPattern = new Regex(@"\s+(?:AS\s+)?" + Id + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectAliasInListPattern = new Regex(@"(?:^(?<equals>" + Id + @")\s*=|(?:\bAS\s+|\s+)(?<name>" + Id + @")\s*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InsertKeywordPattern = new Regex(@"\bINSERT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DeleteKeywordPattern = new Regex(@"\bDELETE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static CompletionContext Analyze(string textBeforeCaret, int caretColumn, string textAfterCaret = null)
        {
            string source = textBeforeCaret ?? string.Empty;
            // One mask pass over the full session text (text before the caret
            // plus the semantic suffix). Masking is a left-to-right state
            // machine, so every other phase reuses prefixes of this result
            // instead of re-scanning the same characters.
            string suffix = GetSemanticSuffix(textAfterCaret);
            string sessionText = source + suffix;
            string maskedSession = SqlTextContext.MaskCommentsAndStrings(sessionText);
            string maskedSource = maskedSession.Length >= source.Length ? maskedSession.Substring(0, source.Length) : maskedSession;
            SqlTextContext text = SqlTextContext.Create(source, maskedSource);
            string statement = text.StatementText;
            string masked = text.MaskedStatement;
            var context = new CompletionContext { Kind = CompletionContextKind.General, CurrentStatement = statement, CurrentBatch = text.BatchText, MaskedStatement = masked };
            bool insideCommentOrString, lastMasked;
            SqlTextContext.ScanState(source, source.Length, out insideCommentOrString, out lastMasked);
            if (insideCommentOrString)
            {
                context.Suppress = true;
                return context;
            }
            if (source.Length > 0 && !char.IsWhiteSpace(source[source.Length - 1]) && lastMasked)
            {
                context.Suppress = true;
                return context;
            }
            string semanticText = statement + suffix;
            // Session-scoped #temp tables survive GO, so inspect all text before
            // the caret rather than only the current batch.
            AddLocalObjects(sessionText, maskedSession, text.BatchText, text.MaskedBatch, context);
            SqlSemanticModel semanticModel = SqlSemanticModel.Create(semanticText, statement.Length);
            semanticModel.CopyAliasesTo(context);
            foreach (string column in semanticModel.ReferencedColumns) context.ReferencedColumns.Add(column);
            foreach (string column in semanticModel.GroupByColumns) context.GroupByColumns.Add(column);
            context.SelectAliases.AddRange(semanticModel.SelectAliases);
            context.Diagnostics.AddRange(semanticModel.Diagnostics);
            context.HasParseErrors = semanticModel.HasParseErrors;
            if (context.Aliases.Count == 0) AddAliases(masked, context);
            Match word = PrefixPattern.Match(statement);
            context.Prefix = Clean(word.Groups["prefix"].Value);
            context.ReplacementStartColumn = Math.Max(0, caretColumn - word.Groups["prefix"].Length);
            ClauseScan clauseScan = null;

            bool snippetsEnabled = SettingsManager.GetSnippetSettings().useSnippets;
            if (snippetsEnabled && IsExactSnippetPrefix(context.Prefix, true, SnippetService.GetAllSnippets()))
            {
                // Snippets are editor constructs, not EXEC positional values. An
                // exact prefix therefore wins in every SQL context so Ctrl+Space
                // can present and replace it just like the configured Tab key.
                context.Kind = CompletionContextKind.General;
                context.TargetObject = null;
                return context;
            }
            if (TryAnalyzeDetachedStatementOrSnippet(statement, masked, caretColumn, context)) return context;
            if (TryAnalyzeExecute(masked, caretColumn, context)) return context;
            if (TryAnalyzeAdvancedContext(masked, caretColumn, context)) return context;

            // INSERT column lists use the same "identifier(" shape as a function
            // call, so they must be recognized before generic function arguments.
            Match insert = InsertColumnListPattern.Match(masked);
            if (insert.Success)
            {
                context.Kind = CompletionContextKind.InsertColumns;
                context.TargetObject = CleanQualified(insert.Groups["target"].Value);
                return context;
            }

            if (TryFindFunctionCall(masked, out string functionTarget, out string functionArguments) && IsFunctionTarget(functionTarget))
            {
                context.Kind = CompletionContextKind.FunctionArguments;
                context.TargetObject = CleanQualified(functionTarget);
                Match argumentPrefix = ArgumentPrefixPattern.Match(functionArguments);
                context.Prefix = argumentPrefix.Groups["prefix"].Value;
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
                context.ArgumentIndex = CountTopLevelArguments(functionArguments);
                return context;
            }

            Match completedInsertTarget = CompletedInsertTargetPattern.Match(masked);
            if (completedInsertTarget.Success)
            {
                context.Kind = CompletionContextKind.InsertBody;
                context.TargetObject = CleanQualified(completedInsertTarget.Groups["target"].Value);
                context.Prefix = string.Empty;
                context.ReplacementStartColumn = caretColumn;
                return context;
            }

            Match omittedSchemaMember = OmittedSchemaMemberPattern.Match(statement);
            if (omittedSchemaMember.Success)
            {
                context.Kind = CompletionContextKind.Member;
                context.Qualifier = CleanQualified(omittedSchemaMember.Groups["qual"].Value);
                context.Prefix = Clean(omittedSchemaMember.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - omittedSchemaMember.Groups["prefix"].Length);
                context.HasOmittedSchemaQualifier = true;
                context.IsDataSourceMember = IsDataSourceMemberContext(masked, omittedSchemaMember.Index, ref clauseScan);
                return context;
            }

            Match member = MemberPattern.Match(statement);
            if (member.Success)
            {
                context.Kind = CompletionContextKind.Member;
                context.IsJoinSource = JoinSourcePattern.IsMatch(masked);
                context.IsDataSourceMember = context.IsJoinSource || IsDataSourceMemberContext(masked, member.Index, ref clauseScan);
                context.Qualifier = CleanQualified(member.Groups["qual"].Value);
                context.Prefix = Clean(member.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - member.Groups["prefix"].Length);
                return context;
            }

            string withoutPrefix = statement.Substring(0, Math.Max(0, statement.Length - word.Groups["prefix"].Length));
            Match insertBody = InsertBodyPattern.Match(masked);
            Match update = UpdateSetPattern.Match(masked);
            if (insertBody.Success) { context.Kind = CompletionContextKind.InsertBody; context.TargetObject = CleanQualified(insertBody.Groups["target"].Value); }
            else if (update.Success) { context.Kind = CompletionContextKind.UpdateSet; context.TargetObject = ResolveAlias(CleanQualified(update.Groups["target"].Value), context); }
            else if (DmlTailPattern.IsMatch(withoutPrefix)) context.Kind = CompletionContextKind.DataSource;
            else context.Kind = DetectClauseContext(withoutPrefix, masked, ref clauseScan);
            if (context.Kind == CompletionContextKind.Predicate)
            {
                Match activeColumn = PredicateColumnPattern.Matches(masked).Cast<Match>().LastOrDefault();
                if (activeColumn != null && activeColumn.Success) context.ActiveColumn = CleanQualified(activeColumn.Groups["column"].Value);
            }
            return context;
        }

        /// <summary>
        /// Clause context derived from one shared tokenization of the masked
        /// statement. The token stream of any prefix of the statement is the
        /// prefix of the token stream (analyze boundaries always land between
        /// tokens), so a single scan answers member-context and tail-context
        /// questions without re-running the parser per call.
        /// </summary>
        private sealed class ClauseScan
        {
            private readonly List<TSqlParserToken> tokens = new List<TSqlParserToken>();
            private readonly List<int> depths = new List<int>();

            public ClauseScan(string maskedSql)
            {
                try
                {
                    var parser = new TSql170Parser(true);
                    IList<TSqlParserToken> all = parser.GetTokenStream(new StringReader(maskedSql ?? string.Empty), out IList<ParseError> _);
                    int depth = 0;
                    foreach (TSqlParserToken token in all)
                    {
                        if (token.TokenType == TSqlTokenType.WhiteSpace || token.TokenType == TSqlTokenType.SingleLineComment
                            || token.TokenType == TSqlTokenType.MultilineComment || token.TokenType == TSqlTokenType.EndOfFile) continue;
                        if (token.TokenType == TSqlTokenType.RightParenthesis) depth = Math.Max(0, depth - 1);
                        depths.Add(depth);
                        tokens.Add(token);
                        if (token.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                    }
                }
                catch { }
            }

            public CompletionContextKind KindAt(int maxOffset)
            {
                try
                {
                    int count = tokens.Count;
                    while (count > 0 && tokens[count - 1].Offset + (tokens[count - 1].Text?.Length ?? 0) > maxOffset) count--;
                    if (count == 0) return CompletionContextKind.General;
                    int caretDepth = depths[count - 1] + (tokens[count - 1].TokenType == TSqlTokenType.LeftParenthesis ? 1 : 0);
                    int clause = -1;
                    for (int i = 0; i < count; i++)
                        if (depths[i] == caretDepth && IsClauseToken(tokens[i].Text)) clause = i;
                    if (clause < 0) return CompletionContextKind.General;
                    string word = tokens[clause].Text.ToUpperInvariant();
                    string previous = clause > 0 && depths[clause - 1] == caretDepth ? tokens[clause - 1].Text.ToUpperInvariant() : string.Empty;
                    bool atClauseStart = clause == count - 1;
                    bool afterSeparator = count > 0 && (tokens[count - 1].Text == "," || tokens[count - 1].Text.Equals("APPLY", StringComparison.OrdinalIgnoreCase));
                    if (word == "JOIN" && atClauseStart) return CompletionContextKind.Join;
                    if ((word == "FROM" || word == "APPLY" || word == "UPDATE" || word == "INTO") && (atClauseStart || afterSeparator)) return CompletionContextKind.DataSource;
                    if (word == "MERGE" && atClauseStart) return CompletionContextKind.DataSource;
                    if (word == "USING" && atClauseStart) return CompletionContextKind.MergeSource;
                    if (word == "WHERE" || word == "HAVING" || word == "ON") return CompletionContextKind.Predicate;
                    if (word == "BY" && previous == "GROUP") return CompletionContextKind.GroupBy;
                    if (word == "BY" && previous == "ORDER") return CompletionContextKind.OrderBy;
                    if (word == "SELECT") return CompletionContextKind.SelectList;
                    return CompletionContextKind.General;
                }
                catch { return CompletionContextKind.General; }
            }
        }

        private static CompletionContextKind DetectClauseContext(string sql, string maskedStatementForScan, ref ClauseScan scan)
        {
            // `sql` and `maskedStatementForScan` are the same length (masking
            // preserves offsets), so the shared token stream covers both.
            ClauseScan value = scan;
            if (value == null)
            {
                value = new ClauseScan(maskedStatementForScan ?? sql);
                scan = value;
            }
            return value.KindAt((sql ?? string.Empty).Length);
        }

        private static bool IsClauseToken(string value) => ClauseTokens.Contains(value ?? string.Empty);
        private static readonly HashSet<string> ClauseTokens = new HashSet<string>(new[]
        { "SELECT", "FROM", "JOIN", "APPLY", "WHERE", "HAVING", "ON", "GROUP", "ORDER", "BY", "UNION", "EXCEPT", "INTERSECT", "OPTION", "UPDATE", "INTO", "SET", "VALUES", "USING", "WHEN", "MERGE" }, StringComparer.OrdinalIgnoreCase);

        private static bool IsDataSourceMemberContext(string maskedStatement, int memberStart, ref ClauseScan scan)
        {
            CompletionContextKind kind = DetectClauseContext(maskedStatement?.Substring(0, Math.Min(memberStart, maskedStatement.Length)) ?? string.Empty, maskedStatement, ref scan);
            return kind == CompletionContextKind.DataSource || kind == CompletionContextKind.Join || kind == CompletionContextKind.MergeSource;
        }

        private static bool TryAnalyzeAdvancedContext(string masked, int caretColumn, CompletionContext context)
        {
            Match window = WindowOverPattern.Match(masked);
            if (window.Success)
            {
                string body = window.Groups["body"].Value;
                if (WindowOrderByPattern.IsMatch(body)) context.Kind = CompletionContextKind.WindowOrderBy;
                else if (WindowPartitionByPattern.IsMatch(body)) context.Kind = CompletionContextKind.WindowPartitionBy;
                else context.Kind = CompletionContextKind.WindowPartitionBy;
                return true;
            }

            Match output = OutputTailPattern.Match(masked);
            if (output.Success)
            {
                context.Kind = CompletionContextKind.Output;
                context.Qualifier = Clean(output.Groups["qual"].Value);
                context.Prefix = Clean(output.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - output.Groups["prefix"].Length);
                Match target = OutputTargetPattern.Match(masked);
                if (target.Success) context.TargetObject = ResolveAlias(CleanQualified(target.Groups["target"].Value), context);
                return true;
            }

            if (HasUnclosedClause(masked, "PIVOT") || HasUnclosedClause(masked, "UNPIVOT"))
            {
                context.Kind = CompletionContextKind.Pivot;
                return true;
            }

            Match index = IndexColumnsPattern.Match(masked);
            if (index.Success)
            {
                context.Kind = CompletionContextKind.IndexColumns;
                context.TargetObject = CleanQualified(index.Groups["target"].Value);
                return true;
            }

            Match alterColumn = AlterDropColumnPattern.Match(masked);
            if (alterColumn.Success)
            {
                context.Kind = CompletionContextKind.AlterTableColumn;
                context.TargetObject = CleanQualified(alterColumn.Groups["target"].Value);
                return true;
            }
            Match alter = AlterTableTailPattern.Match(masked);
            if (alter.Success)
            {
                context.Kind = CompletionContextKind.AlterTableAction;
                context.TargetObject = CleanQualified(alter.Groups["target"].Value);
                return true;
            }

            Match create = CreateTablePattern.Match(masked);
            if (create.Success)
            {
                context.TargetObject = CleanQualified(create.Groups["target"].Value);
                string body = create.Groups["body"].Value;
                context.Kind = ConstraintColumnsPattern.IsMatch(body)
                    ? CompletionContextKind.ConstraintColumns
                    : CompletionContextKind.CreateTableDefinition;
                return true;
            }
            Match constraint = AlterConstraintPattern.Match(masked);
            if (constraint.Success)
            {
                context.Kind = CompletionContextKind.ConstraintColumns;
                context.TargetObject = CleanQualified(constraint.Groups["target"].Value);
                return true;
            }
            return false;
        }

        private static bool HasUnclosedClause(string sql, string keyword)
        {
            Match match = Regex.Matches(sql ?? string.Empty, @"\b" + keyword + @"\s*\(", RegexOptions.IgnoreCase).Cast<Match>().LastOrDefault();
            if (match == null) return false;
            int depth = 0;
            for (int i = match.Index; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')') depth--;
            }
            return depth > 0;
        }

        private static int CountTopLevelArguments(string value)
        {
            int depth = 0, index = 0;
            foreach (char c in value ?? string.Empty)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0) index++;
            }
            return index;
        }

        internal static string GetSemanticSuffix(string textAfterCaret)
        {
            if (string.IsNullOrEmpty(textAfterCaret)) return string.Empty;
            int depth = 0;
            bool singleQuote = false, doubleQuote = false, bracket = false, lineComment = false, blockComment = false;
            int lineStart = 0;
            for (int i = 0; i < textAfterCaret.Length; i++)
            {
                char c = textAfterCaret[i], next = i + 1 < textAfterCaret.Length ? textAfterCaret[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') { lineComment = false; lineStart = i + 1; }
                    continue;
                }
                if (blockComment)
                {
                    if (c == '*' && next == '/') { blockComment = false; i++; }
                    continue;
                }
                if (singleQuote)
                {
                    if (c == '\'' && next == '\'') i++;
                    else if (c == '\'') singleQuote = false;
                    continue;
                }
                if (doubleQuote)
                {
                    if (c == '"' && next == '"') i++;
                    else if (c == '"') doubleQuote = false;
                    continue;
                }
                if (bracket)
                {
                    if (c == ']' && next == ']') i++;
                    else if (c == ']') bracket = false;
                    continue;
                }
                if (c == '-' && next == '-') { lineComment = true; i++; continue; }
                if (c == '/' && next == '*') { blockComment = true; i++; continue; }
                if (c == '\'') { singleQuote = true; continue; }
                if (c == '"') { doubleQuote = true; continue; }
                if (c == '[') { bracket = true; continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
                if (c == ';' && depth == 0) return textAfterCaret.Substring(0, i);
                if ((c == '\r' || c == '\n') && depth == 0)
                {
                    string line = textAfterCaret.Substring(lineStart, i - lineStart).Trim();
                    if (SqlTextContext.IsGoLine(line)) return textAfterCaret.Substring(0, lineStart);
                    lineStart = i + 1;
                }
            }
            if (depth == 0)
            {
                string lastLine = textAfterCaret.Substring(Math.Min(lineStart, textAfterCaret.Length)).Trim();
                if (SqlTextContext.IsGoLine(lastLine)) return textAfterCaret.Substring(0, lineStart);
            }
            return textAfterCaret;
        }

        private static bool TryFindFunctionCall(string masked, out string target, out string arguments)
        {
            target = arguments = string.Empty;
            string value = masked ?? string.Empty;
            int nested = 0;
            for (int index = value.Length - 1; index >= 0; index--)
            {
                if (value[index] == ')') { nested++; continue; }
                if (value[index] != '(') continue;
                if (nested > 0) { nested--; continue; }
                Match match = FunctionTargetTailPattern.Match(value.Substring(0, index));
                if (!match.Success) return false;
                string beforeTarget = value.Substring(0, match.Index);
                if (NonFunctionBeforeTargetPattern.IsMatch(beforeTarget))
                    return false;
                target = match.Groups["target"].Value;
                arguments = value.Substring(index + 1);
                return true;
            }
            return false;
        }

        private static bool IsFunctionTarget(string target)
        {
            string name = CleanQualified(target);
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);
            return !NonFunctionParentheses.Contains(name);
        }

        private static readonly HashSet<string> NonFunctionParentheses = new HashSet<string>(new[]
        {
            "VALUES", "IN", "EXISTS", "IF", "WHILE", "CHECK", "CONSTRAINT", "PRIMARY", "FOREIGN",
            "REFERENCES", "TABLE", "BEGIN", "OVER", "WITHIN", "GROUPING", "RETURN", "THROW", "RAISERROR"
        }, StringComparer.OrdinalIgnoreCase);

        private static bool TryAnalyzeExecute(string masked, int caretColumn, CompletionContext context)
        {
            // StatementText arrives pre-masked from the analyze pipeline, which
            // preserves offsets so replacement columns stay aligned with the
            // editor text. Masking also prevents example text such as
            // "-- EXEC dbo.usp_Test ..." from turning a later SELECT into EXEC
            // argument completion.
            string executableStatement = masked ?? string.Empty;
            Match activeStatement = KeywordStartPattern.Matches(executableStatement)
                .Cast<Match>()
                .LastOrDefault();
            if (activeStatement != null)
            {
                string keyword = activeStatement.Groups["keyword"].Value;
                if (!keyword.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
                    && !keyword.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
                    return false;
                executableStatement = executableStatement.Substring(activeStatement.Index);
            }
            Match exec = ExecBodyPattern.Match(executableStatement);
            if (!exec.Success) return false;
            string body = exec.Groups["body"].Value;
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("(") || ExecAsTailPattern.IsMatch(trimmed)) return false;
            trimmed = NamedParameterAssignPattern.Replace(trimmed, string.Empty);
            if (!char.IsWhiteSpace(body.LastOrDefault()) && ExecObjectTailPattern.IsMatch(trimmed))
            {
                int dot = trimmed.LastIndexOf('.');
                context.Kind = CompletionContextKind.ExecuteObject;
                context.Qualifier = dot >= 0 ? CleanQualified(trimmed.Substring(0, dot)) : null;
                context.Prefix = dot >= 0 ? Clean(trimmed.Substring(dot + 1)) : Clean(trimmed);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
                return true;
            }
            Match target = ExecObjectHeadPattern.Match(trimmed);
            if (!target.Success)
            {
                context.Kind = CompletionContextKind.ExecuteObject;
                context.Prefix = Clean(trimmed);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - trimmed.Length);
                return true;
            }
            string objectText = target.Groups["obj"].Value;
            string rest = target.Groups["rest"].Value;
            context.TargetObject = CleanQualified(objectText);
            foreach (Match used in UsedParameterPattern.Matches(rest)) context.UsedParameters.Add(used.Value.TrimEnd(' ', '\t', '='));
            Match active = ActiveParameterValuePattern.Match(rest);
            if (active.Success)
            {
                context.Kind = CompletionContextKind.ExecuteArgumentValue;
                context.ActiveParameter = active.Groups["parameter"].Value;
                context.Prefix = active.Groups["value"].Value.TrimStart();
                context.ReplacementStartColumn = Math.Max(0, caretColumn - active.Groups["value"].Length);
            }
            else
            {
                context.Kind = CompletionContextKind.ExecuteArguments;
                Match prefix = ActiveParameterPrefixPattern.Match(rest);
                context.Prefix = prefix.Success ? prefix.Groups["prefix"].Value : string.Empty;
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
            }
            return true;
        }

        private static bool TryAnalyzeDetachedStatementOrSnippet(string statement, string masked, int caretColumn, CompletionContext context)
        {
            if (!DetachedExecPattern.IsMatch(masked ?? string.Empty)) return false;

            // A stored procedure may be followed by another statement without a
            // semicolon. While that next token is still incomplete (for example
            // "SEL" or a custom "SSF" snippet), ScriptDom cannot split it yet.
            // Two spaces or a new line are treated as the user's statement
            // separator; named parameters such as "    @Id" stay in EXEC scope.
            Match detached = DetachedPrefixPattern.Match(masked);
            if (!detached.Success) return false;

            string prefix = detached.Groups["prefix"].Value;
            bool snippetPrefix = SettingsManager.GetSnippetSettings().useSnippets
                && SnippetService.GetAllSnippets().Any(s => !string.IsNullOrWhiteSpace(s.Prefix)
                    && s.Prefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            bool statementPrefix = prefix.Length >= 2 && DetachedStatementStarters.Any(s =>
                s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!snippetPrefix && !statementPrefix) return false;

            context.Kind = CompletionContextKind.General;
            context.Prefix = prefix;
            context.ReplacementStartColumn = Math.Max(0, caretColumn - detached.Groups["prefix"].Length);
            context.TargetObject = null;
            context.UsedParameters.Clear();
            return true;
        }

        internal static bool IsExactSnippetPrefix(string prefix, bool snippetsEnabled, IEnumerable<SnippetItem> snippets)
            => snippetsEnabled && !string.IsNullOrWhiteSpace(prefix) && (snippets ?? Enumerable.Empty<SnippetItem>())
                .Any(s => string.Equals(s?.Prefix, prefix, StringComparison.OrdinalIgnoreCase));

        // Settings are read once per BuildItems call; per-item helpers (Quote,
        // MakeAlias, join rules) consume this instance instead of re-reading
        // settings for every suggested candidate. The dialog always saves a
        // fresh instance, so per-call reads pick changes up immediately.
        [ThreadStatic] private static SettingsManager.SqlCompletionSettings threadCompletionSettings;

        private static SettingsManager.SqlCompletionSettings CompletionSettings()
            => threadCompletionSettings ?? SettingsManager.GetSqlCompletionSettings();

        public static List<CompletionItem> BuildItems(CompletionContext context, MetadataSnapshot metadata)
        {
            metadata = metadata ?? MetadataSnapshot.Empty;
            var result = new List<CompletionItem>();
            var objects = metadata.Objects.Concat(context.LocalObjects).ToList();
            try
            {
                threadCompletionSettings = SettingsManager.GetSqlCompletionSettings();
                HydrateProjectedColumns(metadata, context.LocalObjects);
                AddDeclaredVariables(result, context);
                switch (context.Kind)
                {
                    case CompletionContextKind.ExecuteObject:
                        result.AddRange(metadata.Objects.Where(o => o.Kind == CompletionItemKind.Procedure && MatchesContainer(o, context.Qualifier)).Select(o => ObjectItem(o, o.IsSystem ? 25 : 120, !string.IsNullOrEmpty(context.Qualifier)))); break;
                    case CompletionContextKind.ExecuteArguments: AddParameters(result, context, metadata); break;
                    case CompletionContextKind.ExecuteArgumentValue: AddValues(result, context, metadata); break;
                    case CompletionContextKind.FunctionArguments: AddFunctionArguments(result, context, metadata, objects); break;
                    case CompletionContextKind.Member:
                        if (context.IsJoinSource) result.AddRange(BuildJoinItems(context, metadata));
                        else AddMembers(result, context, objects, metadata);
                        break;
                    case CompletionContextKind.Join: result.AddRange(BuildJoinItems(context, metadata)); break;
                    case CompletionContextKind.InsertColumns:
                    case CompletionContextKind.UpdateSet: AddWritableColumns(result, context, metadata); break;
                    case CompletionContextKind.InsertBody: AddInsertTemplate(result, context, metadata); break;
                    case CompletionContextKind.DataSource:
                        result.AddRange(objects.Where(IsDataSource).Select(o => DataSourceItem(o, context, 40)));
                        result.AddRange(metadata.Schemas.Select(s => ContainerItem(s, CompletionItemKind.Schema, 60)));
                        result.AddRange(metadata.Databases.Select(d => ContainerItem(d, CompletionItemKind.Database, 35)));
                        result.AddRange(metadata.LinkedServers.Select(s => ContainerItem(s, CompletionItemKind.Server, 30)));
                        AddBuiltInTableFunctions(result, metadata.CompatibilityLevel);
                        break;
                    case CompletionContextKind.MergeSource: result.AddRange(objects.Where(IsDataSource).Select(o => ObjectItem(o, 55, false))); break;
                    case CompletionContextKind.Predicate: AddScopedColumns(result, context, metadata, 85, false); AddPredicateItems(result); AddTypedValues(result, context, metadata); break;
                    case CompletionContextKind.SelectList: AddSelectListItems(result, context, metadata, objects); break;
                    case CompletionContextKind.WindowPartitionBy:
                    case CompletionContextKind.WindowOrderBy: AddWindowItems(result, context, metadata, objects); break;
                    case CompletionContextKind.Output: AddOutputItems(result, context, metadata, objects); break;
                    case CompletionContextKind.Pivot: AddPivotItems(result, context, metadata); break;
                    case CompletionContextKind.CreateTableDefinition: AddCreateTableItems(result); break;
                    case CompletionContextKind.ConstraintColumns:
                    case CompletionContextKind.IndexColumns:
                    case CompletionContextKind.AlterTableColumn: AddTargetColumns(result, context, metadata); break;
                    case CompletionContextKind.AlterTableAction: AddAlterTableItems(result); break;
                    case CompletionContextKind.GroupBy: AddScopedColumns(result, context, metadata, 90, true); AddGroupByClause(result, context); break;
                    case CompletionContextKind.OrderBy: AddScopedColumns(result, context, metadata, 90); AddSelectAliases(result, context); break;
                    default: AddGeneral(result, context, metadata, objects); break;
                }
            }
            finally { threadCompletionSettings = null; }
            foreach (CompletionItem item in result) item.ScopeKey = context.MetadataScope;
            return FilterAndSort(result.GroupBy(i => i.Kind + "|" + i.DisplayText + "|" + i.InsertText, StringComparer.OrdinalIgnoreCase).Select(g => g.First()), context.Prefix);
        }

        /// <summary>
        /// Resolves a name the way the previous linear
        /// <c>FirstOrDefault(ObjectMatches)</c> scans did, using the snapshot's
        /// prebuilt index first and falling back to the local temp-table list.
        /// </summary>
        private static DatabaseObjectMetadata FindObject(CompletionContext context, MetadataSnapshot metadata, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            MetadataObjectIndex index = metadata?.GetIndex();
            DatabaseObjectMetadata match = index?.Find(CleanQualified(target),
                (o, t) => ObjectMatches(o.QualifiedName, t) || ObjectMatches(o.Name, t));
            if (match != null) return match;
            List<DatabaseObjectMetadata> locals = context?.LocalObjects;
            if (locals != null && locals.Count > 0)
                foreach (DatabaseObjectMetadata local in locals)
                    if (ObjectMatches(local.QualifiedName, target) || ObjectMatches(local.Name, target)) return local;
            return null;
        }

        private static void HydrateProjectedColumns(MetadataSnapshot metadata, List<DatabaseObjectMetadata> localObjects)
        {
            if (localObjects.Count == 0 || !localObjects.Any(o => o.ProjectionSources.Count > 0)) return;
            for (int pass = 0; pass <= localObjects.Count; pass++)
            {
                bool changed = false;
                foreach (DatabaseObjectMetadata local in localObjects.Where(o => o.ProjectionSources.Count > 0))
                    foreach (string sourceName in local.ProjectionSources)
                    {
                        DatabaseObjectMetadata source = FindObject(null, metadata, sourceName);
                        if (source == null || ReferenceEquals(source, local)) continue;
                        foreach (ColumnMetadata column in source.Columns)
                            if (!local.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                local.Columns.Add(new ColumnMetadata
                                {
                                    Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                                    IsIdentity = column.IsIdentity, IsComputed = column.IsComputed,
                                    IsPrimaryKey = column.IsPrimaryKey, IsForeignKey = column.IsForeignKey, IsUnique = column.IsUnique,
                                    Ordinal = column.Ordinal, MaxLength = column.MaxLength, Precision = column.Precision, Scale = column.Scale,
                                    DefaultDefinition = column.DefaultDefinition, Description = column.Description
                                });
                                changed = true;
                            }
                    }
                if (!changed) break;
            }
        }

        private static void AddFunctionArguments(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            bool knownFunction = metadata.GetIndex().Any(CleanQualified(context.TargetObject), (o, t) => ObjectMatches(o.QualifiedName, t), o => o.Kind == CompletionItemKind.Function)
                || BuiltInFunctions.Contains(LastPart(context.TargetObject));
            if (!knownFunction)
            {
                AddScopedColumns(result, context, metadata, 75);
                return;
            }
            AddScopedColumns(result, context, metadata, 80);
            AddValues(result, context, metadata);
        }

        private static void AddBuiltInTableFunctions(List<CompletionItem> result, int compatibilityLevel)
        {
            foreach (string name in new[] { "OPENXML" })
                result.Add(new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function, Description = "SQL Server table-valued function", Score = 48 });
            if (compatibilityLevel >= 130)
                foreach (string name in new[] { "OPENJSON", "STRING_SPLIT" })
                    result.Add(new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function, Description = "SQL Server table-valued function", Score = 48 });
            if (compatibilityLevel >= 160)
                result.Add(new CompletionItem { DisplayText = "GENERATE_SERIES", InsertText = "GENERATE_SERIES", Kind = CompletionItemKind.Function, Description = "SQL Server table-valued function", Score = 48 });
        }

        private static void AddSelectListItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            AddGeneral(result, context, metadata, objects);
            AddScopedColumns(result, context, metadata, 95);
            var columns = new List<string>();
            bool qualify = context.Aliases.Count > 1;
            foreach (var source in context.Aliases)
            {
                DatabaseObjectMetadata item = FindObject(context, metadata, source.Value);
                if (item == null) continue;
                columns.AddRange(item.Columns.Select(c => (qualify ? Quote(source.Key) + "." : string.Empty) + Quote(c.Name)));
            }
            if (columns.Count > 1)
                result.Add(new CompletionItem { DisplayText = "(all SELECT columns)", InsertText = string.Join("," + Environment.NewLine + "    ", columns), Kind = CompletionItemKind.Snippet, Description = columns.Count + " columns", Score = 145 });
            foreach (string expression in new[] { "COUNT(*)", "SUM(${1:expression})", "CASE WHEN ${1:condition} THEN ${2:value} ELSE ${3:value} END" })
                result.Add(new CompletionItem { DisplayText = expression.Split('$')[0], InsertText = expression, Kind = CompletionItemKind.Snippet, Description = "SELECT expression", Score = 65 });
        }

        private static void AddWindowItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            AddScopedColumns(result, context, metadata, 100);
            if (context.Kind == CompletionContextKind.WindowPartitionBy)
            {
                result.Add(new CompletionItem { DisplayText = "PARTITION BY ... ORDER BY ...", InsertText = "PARTITION BY ${1:column}" + Environment.NewLine + "ORDER BY ${2:column}", Kind = CompletionItemKind.Snippet, Description = "window clause", Score = 130 });
                result.Add(new CompletionItem { DisplayText = "ORDER BY ...", InsertText = "ORDER BY ${1:column}", Kind = CompletionItemKind.Snippet, Description = "window ordering", Score = 115 });
            }
            else
            {
                foreach (string value in new[] { "ASC", "DESC", "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW", "ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING" })
                    result.Add(new CompletionItem { DisplayText = value, InsertText = value, Kind = CompletionItemKind.Keyword, Description = "window ordering/frame", Score = 75 });
            }
        }

        private static void AddOutputItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            DatabaseObjectMetadata target = FindObject(context, metadata, context.TargetObject);
            if (target == null) return;
            string statement = context.MaskedStatement ?? context.CurrentStatement ?? string.Empty;
            var qualifiers = !string.IsNullOrWhiteSpace(context.Qualifier) ? new[] { context.Qualifier }
                : InsertKeywordPattern.IsMatch(statement) ? new[] { "inserted" }
                : DeleteKeywordPattern.IsMatch(statement) ? new[] { "deleted" }
                : new[] { "inserted", "deleted" };
            foreach (string qualifier in qualifiers)
                foreach (ColumnMetadata column in target.Columns)
                {
                    CompletionItem item = ColumnItem(column, 105, target);
                    item.DisplayText = qualifier + "." + column.Name;
                    item.InsertText = string.IsNullOrWhiteSpace(context.Qualifier) ? qualifier + "." + Quote(column.Name) : Quote(column.Name);
                    item.Description = "OUTPUT " + item.Description;
                    result.Add(item);
                }
            if (target.Columns.Count > 1)
            {
                string qualifier = qualifiers[0];
                result.Add(new CompletionItem { DisplayText = "(all OUTPUT columns)", InsertText = string.Join("," + Environment.NewLine + "    ", target.Columns.Select(c => qualifier + "." + Quote(c.Name))), Kind = CompletionItemKind.Snippet, Description = target.Columns.Count + " columns", Score = 135 });
            }
        }

        private static void AddPivotItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            AddScopedColumns(result, context, metadata, 90);
            foreach (string value in new[] { "SUM(${1:value}) FOR ${2:pivot_column} IN (${3:values})", "COUNT(${1:value}) FOR ${2:pivot_column} IN (${3:values})", "FOR ${1:pivot_column} IN (${2:values})" })
                result.Add(new CompletionItem { DisplayText = value.StartsWith("FOR", StringComparison.Ordinal) ? "FOR ... IN (...)" : value.Substring(0, value.IndexOf('(')) + "(...) FOR ... IN (...)" , InsertText = value, Kind = CompletionItemKind.Snippet, Description = "PIVOT clause", Score = 120 });
        }

        private static void AddCreateTableItems(List<CompletionItem> result)
        {
            foreach (string value in new[] { "${1:ColumnName} INT NOT NULL", "${1:ColumnName} NVARCHAR(${2:100}) NULL", "${1:ColumnName} DECIMAL(${2:18}, ${3:2}) NULL", "${1:ColumnName} DATETIME2 NULL", "CONSTRAINT ${1:PK_Table} PRIMARY KEY (${2:Id})", "CONSTRAINT ${1:FK_Table_Parent} FOREIGN KEY (${2:ParentId}) REFERENCES ${3:dbo.Parent} (${4:Id})", "CONSTRAINT ${1:CK_Table_Column} CHECK (${2:condition})" })
                result.Add(new CompletionItem { DisplayText = value.Replace("${1:", string.Empty).Split('}')[0] + (value.StartsWith("CONSTRAINT", StringComparison.Ordinal) ? " constraint" : " column"), InsertText = value, Kind = CompletionItemKind.Snippet, Description = "CREATE TABLE definition", Score = 100 });
        }

        private static void AddTargetColumns(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            DatabaseObjectMetadata target = FindTarget(context, metadata);
            if (target != null) result.AddRange(target.Columns.Select(c => ColumnItem(c, 110, target)));
        }

        private static void AddAlterTableItems(List<CompletionItem> result)
        {
            foreach (string value in new[] { "ADD ${1:ColumnName} ${2:INT} NULL", "ALTER COLUMN ${1:ColumnName} ${2:INT} NOT NULL", "DROP COLUMN ${1:ColumnName}", "ADD CONSTRAINT ${1:PK_Table} PRIMARY KEY (${2:Id})", "ADD CONSTRAINT ${1:FK_Table_Parent} FOREIGN KEY (${2:ParentId}) REFERENCES ${3:dbo.Parent} (${4:Id})", "DROP CONSTRAINT ${1:ConstraintName}" })
                result.Add(new CompletionItem { DisplayText = Regex.Replace(value, @"\$\{\d+:([^}]+)\}", "$1"), InsertText = value, Kind = CompletionItemKind.Snippet, Description = "ALTER TABLE action", Score = 120 });
        }

        private static void AddGroupByClause(List<CompletionItem> result, CompletionContext context)
        {
            Match select = SelectFromListPattern.Match(context.MaskedStatement ?? SqlTextContext.MaskCommentsAndStrings(context.CurrentStatement ?? string.Empty));
            if (!select.Success) return;
            var expressions = SplitTopLevel(select.Groups["list"].Value)
                .Select(RemoveSelectAlias)
                .Where(x => !string.IsNullOrWhiteSpace(x) && !AggregationPattern.IsMatch(x))
                .ToList();
            if (expressions.Count > 0)
                result.Add(new CompletionItem { DisplayText = "(all non-aggregated SELECT expressions)", InsertText = string.Join("," + Environment.NewLine + "    ", expressions), Kind = CompletionItemKind.Snippet, Description = expressions.Count + " expressions", Score = 140 });
        }

        private static string RemoveSelectAlias(string expression)
        {
            string value = (expression ?? string.Empty).Trim();
            Match equals = EqualAliasPattern.Match(value);
            if (equals.Success) return equals.Groups["value"].Value.Trim();
            return TrailingAliasPattern.Replace(value, string.Empty).Trim();
        }

        private static DatabaseObjectMetadata FindTarget(CompletionContext context, MetadataSnapshot metadata)
            => FindObject(context, metadata, context.TargetObject);

        private static readonly HashSet<string> BuiltInFunctions = new HashSet<string>(new[]
        {
            "ABS", "AVG", "CAST", "CEILING", "COALESCE", "CONCAT", "CONVERT", "COUNT", "DATEADD", "DATEDIFF",
            "DATENAME", "DATEPART", "EOMONTH", "FLOOR", "FORMAT", "GETDATE", "ISNULL", "JSON_VALUE", "LEFT",
            "LEN", "LOWER", "LTRIM", "MAX", "MIN", "NEWID", "NULLIF", "REPLACE", "RIGHT", "ROUND", "RTRIM",
            "ASCII", "CHAR", "CHARINDEX", "DATALENGTH", "DIFFERENCE", "NCHAR", "PATINDEX", "QUOTENAME",
            "REPLICATE", "REVERSE", "SOUNDEX", "SPACE", "STR", "STRING_AGG", "STRING_ESCAPE", "STRING_SPLIT",
            "STUFF", "SUBSTRING", "TRANSLATE", "UNICODE", "SUM", "TRIM", "UPPER",
            "DATEFROMPARTS", "DATETIME2FROMPARTS", "DATETIMEFROMPARTS", "DATETIMEOFFSETFROMPARTS",
            "DAY", "GETUTCDATE", "ISDATE", "MONTH", "SMALLDATETIMEFROMPARTS", "SWITCHOFFSET", "SYSDATETIME",
            "SYSDATETIMEOFFSET", "SYSUTCDATETIME", "TIMEFROMPARTS", "TODATETIMEOFFSET", "YEAR",
            "CHOOSE", "IIF", "ISJSON", "JSON_MODIFY", "JSON_QUERY", "JSON_VALUE", "TRY_CAST", "TRY_CONVERT",
            "APP_NAME", "CONNECTIONPROPERTY", "DB_ID", "DB_NAME", "HOST_NAME", "OBJECT_ID",
            "OBJECT_NAME", "ORIGINAL_LOGIN", "SCOPE_IDENTITY", "SCHEMA_ID", "SCHEMA_NAME", "SERVERPROPERTY",
            "SUSER_SNAME", "USER_NAME", "XACT_STATE"
        }, StringComparer.OrdinalIgnoreCase);

        private static void AddScopedColumns(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, int score, bool excludeReferenced = false)
        {
            foreach (var source in context.Aliases)
            {
                DatabaseObjectMetadata obj = FindObject(context, metadata, source.Value);
                if (obj == null) continue;
                bool qualify = context.Aliases.Count > 1;
                foreach (ColumnMetadata column in obj.Columns)
                {
                    if (excludeReferenced && IsGroupedColumn(context, source.Key, source.Value, column.Name)) continue;
                    CompletionItem item = ColumnItem(column, score, obj);
                    if (qualify)
                    {
                        item.InsertText = Quote(source.Key) + "." + Quote(column.Name);
                        item.Description = (item.Description ?? string.Empty) + " · " + source.Key;
                    }
                    result.Add(item);
                }
            }
        }

        private static bool IsGroupedColumn(CompletionContext context, string alias, string objectName, string column)
        {
            if (context.GroupByColumns.Contains(column)) return true;
            return context.GroupByColumns.Contains(alias + "." + column)
                || context.GroupByColumns.Contains(LastPart(objectName) + "." + column);
        }

        private static void AddTypedValues(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            if (string.IsNullOrWhiteSpace(context.ActiveColumn)) return;
            string columnName = LastPart(context.ActiveColumn);
            string[] activeParts = CleanQualified(context.ActiveColumn).Split('.');
            string activeOwner = activeParts.Length > 1 ? ResolveAlias(activeParts[activeParts.Length - 2], context) : null;
            ColumnMetadata column = null;
            foreach (string source in context.Aliases.Values)
            {
                if (!string.IsNullOrWhiteSpace(activeOwner) && !ObjectMatches(source, activeOwner)) continue;
                DatabaseObjectMetadata owner = FindObject(context, metadata, source);
                column = owner?.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                if (column != null) break;
            }
            string type = (column?.DataType ?? string.Empty).ToLowerInvariant();
            if (type == "bit")
            {
                result.Add(new CompletionItem { DisplayText = "0", InsertText = "0", Kind = CompletionItemKind.Keyword, Description = "bit value", Score = 95 });
                result.Add(new CompletionItem { DisplayText = "1", InsertText = "1", Kind = CompletionItemKind.Keyword, Description = "bit value", Score = 95 });
            }
            if (type.Contains("date") || type.Contains("time"))
                result.Add(new CompletionItem { DisplayText = "GETDATE()", InsertText = "GETDATE()", Kind = CompletionItemKind.Function, Description = "current date/time", Score = 90 });
            if (column?.IsNullable == true)
                result.Add(new CompletionItem { DisplayText = "NULL", InsertText = "NULL", Kind = CompletionItemKind.Keyword, Description = type, Score = 80 });
        }

        private static void AddPredicateItems(List<CompletionItem> result)
        {
            foreach (string value in new[] { "=", "<>", ">", ">=", "<", "<=", "LIKE", "IN", "BETWEEN", "IS NULL", "IS NOT NULL", "EXISTS" })
                result.Add(new CompletionItem { DisplayText = value, InsertText = value, Kind = CompletionItemKind.Keyword, Description = "predicate", Score = 45 });
        }

        private static void AddSelectAliases(List<CompletionItem> result, CompletionContext context)
        {
            foreach (string semanticAlias in context.SelectAliases)
                result.Add(new CompletionItem { DisplayText = semanticAlias, InsertText = Quote(semanticAlias), Kind = CompletionItemKind.Column, Description = "SELECT alias", Score = 105 });
            Match select = SelectFromListPattern.Match(context.MaskedStatement ?? SqlTextContext.MaskCommentsAndStrings(context.CurrentStatement ?? string.Empty));
            if (!select.Success) return;
            foreach (string expression in SplitTopLevel(select.Groups["list"].Value))
            {
                Match alias = SelectAliasInListPattern.Match(expression);
                string aliasName = alias.Groups["equals"].Success ? Clean(alias.Groups["equals"].Value) : Clean(alias.Groups["name"].Value);
                if (alias.Success) result.Add(new CompletionItem { DisplayText = aliasName, InsertText = Quote(aliasName), Kind = CompletionItemKind.Column, Description = "SELECT alias", Score = 105 });
            }
        }

        private static void AddParameters(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var parameters = metadata.Parameters.Where(p => ObjectMatches(p.ObjectName, context.TargetObject) && !context.UsedParameters.Contains(p.Name) && !string.IsNullOrEmpty(p.Name)).ToList();
            result.AddRange(parameters.Select(p => new CompletionItem { DisplayText = p.Name, InsertText = p.Name + " = ", Kind = CompletionItemKind.Parameter, Description = p.TypeDisplay + (p.HasDefaultValue ? " optional" : string.Empty) + (p.IsOutput ? " OUTPUT" : string.Empty), Score = 100 }));
            if (parameters.Count > 1 && context.UsedParameters.Count == 0) result.Add(new CompletionItem { DisplayText = "(all parameters)", InsertText = string.Join(", ", parameters.Select((p, i) => p.Name + " = ${" + (i + 1) + ":" + (p.HasDefaultValue ? "DEFAULT" : "NULL") + "}" + (p.IsOutput ? " OUTPUT" : string.Empty))), Kind = CompletionItemKind.Snippet, Description = parameters.Count + " parameters", Score = 130 });
        }

        private static void AddValues(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var matching = metadata.Parameters.Where(x => ObjectMatches(x.ObjectName, context.TargetObject)).OrderBy(x => x.Ordinal).ToList();
            var p = !string.IsNullOrWhiteSpace(context.ActiveParameter)
                ? matching.FirstOrDefault(x => string.Equals(x.Name, context.ActiveParameter, StringComparison.OrdinalIgnoreCase))
                : matching.ElementAtOrDefault(context.ArgumentIndex);
            result.Add(new CompletionItem { DisplayText = "NULL", InsertText = "NULL", Kind = CompletionItemKind.Keyword, Description = p?.DataType, Score = 70 });
            if (p?.HasDefaultValue == true)
                result.Add(new CompletionItem { DisplayText = "DEFAULT", InsertText = "DEFAULT", Kind = CompletionItemKind.Keyword, Description = p.TypeDisplay + " optional parameter", Score = 60 });
            foreach (var variable in GetDeclaredVariables(context)) result.Add(new CompletionItem { DisplayText = variable.Name, InsertText = variable.Name, Kind = CompletionItemKind.Column, Description = variable.Type + " variable", Score = 80 });
        }

        private static void AddDeclaredVariables(List<CompletionItem> result, CompletionContext context)
        {
            foreach (var declaration in GetDeclaredVariables(context))
                result.Add(new CompletionItem
                {
                    DisplayText = declaration.Name,
                    InsertText = declaration.Name,
                    Kind = CompletionItemKind.Parameter,
                    Description = declaration.Type + " local variable",
                    Score = 88
                });
        }

        private static List<DeclaredVariableInfo> GetDeclaredVariables(CompletionContext context)
        {
            // Parsing DECLARE statements re-masks the whole batch; the result is
            // memoized on the context because both AddDeclaredVariables and
            // AddValues need the same list per completion request.
            List<DeclaredVariableInfo> declarations = context.DeclaredVariables;
            if (declarations != null) return declarations;
            string batch = SqlTextContext.MaskCommentsAndStrings(context.CurrentBatch ?? string.Empty);
            declarations = new List<DeclaredVariableInfo>();
            foreach (Match statement in DeclareBodyPattern.Matches(batch))
                foreach (string part in SplitTopLevel(statement.Groups["body"].Value))
                {
                    Match declaration = VariableDeclarationPattern.Match(part.Trim());
                    if (declaration.Success) declarations.Add(new DeclaredVariableInfo { Name = declaration.Groups["name"].Value, Type = declaration.Groups["type"].Value });
                }
            context.DeclaredVariables = declarations;
            return declarations;
        }

        private static void AddMembers(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects, MetadataSnapshot metadata)
        {
            string resolved = ResolveAlias(context.Qualifier, context);
            if (context.HasOmittedSchemaQualifier)
            {
                string[] omittedParts = CleanQualified(resolved).Split('.');
                IEnumerable<DatabaseObjectMetadata> omittedObjects = omittedParts.Length == 2
                    ? objects.Where(o => string.Equals(o.Server, omittedParts[0], StringComparison.OrdinalIgnoreCase)
                        && string.Equals(o.Database, omittedParts[1], StringComparison.OrdinalIgnoreCase))
                    : objects.Where(o => string.Equals(o.Database, omittedParts[0], StringComparison.OrdinalIgnoreCase));
                result.AddRange(omittedObjects.Where(o => !context.IsDataSourceMember || IsDataSource(o)).Select(o => ObjectItem(o, 80, true)));
                return;
            }
            var obj = FindObject(context, metadata, resolved);
            if (obj != null) result.AddRange(obj.Columns.Select(c => ColumnItem(c, 90, obj)));
            else
            {
                string[] parts = CleanQualified(resolved).Split('.');
                if (parts.Length == 1 && metadata.LinkedServerDatabases.TryGetValue(parts[0], out List<string> linkedDatabases))
                    result.AddRange(linkedDatabases.Select(d => ContainerItem(d, CompletionItemKind.Database, 100)));
                else if (parts.Length == 2 && metadata.LinkedServers.Any(s => string.Equals(s, parts[0], StringComparison.OrdinalIgnoreCase)))
                    result.AddRange(objects.Where(o => string.Equals(o.Server, parts[0], StringComparison.OrdinalIgnoreCase) && string.Equals(o.Database, parts[1], StringComparison.OrdinalIgnoreCase))
                        .Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => ContainerItem(s, CompletionItemKind.Schema, 95)));
                else if (parts.Length == 1 && metadata.Databases.Any(d => string.Equals(d, parts[0], StringComparison.OrdinalIgnoreCase)))
                    result.AddRange(objects.Where(o => string.Equals(o.Database, parts[0], StringComparison.OrdinalIgnoreCase)).Select(o => o.Schema)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Select(s => ContainerItem(s, CompletionItemKind.Schema, 90)));
                else result.AddRange(objects.Where(o => EndsWith(ContainerName(o), resolved) && (!context.IsDataSourceMember || IsDataSource(o))).Select(o => ObjectItem(o, 70, true)));
            }
        }

        private static void AddWritableColumns(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var target = FindObject(context, metadata, context.TargetObject);
            if (target == null) return;
            var writable = target.Columns.Where(c => !c.IsIdentity && !c.IsComputed && !string.Equals(c.DataType, "timestamp", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.DataType, "rowversion", StringComparison.OrdinalIgnoreCase)).ToList();
            result.AddRange(writable.Select(c => ColumnItem(c, 90, target)));
            if (writable.Count > 1)
            {
                string insertion = context.Kind == CompletionContextKind.UpdateSet
                    ? string.Join("," + Environment.NewLine, writable.Select((c, i) => Quote(c.Name) + " = ${" + (i + 1) + ":NULL}"))
                    : string.Join(", ", writable.Select(c => Quote(c.Name)));
                result.Add(new CompletionItem { DisplayText = context.Kind == CompletionContextKind.UpdateSet ? "(all column assignments)" : "(all writable columns)", InsertText = insertion, Kind = CompletionItemKind.Snippet, Description = writable.Count + " columns", Score = 120 });
            }
        }

        private static void AddInsertTemplate(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var target = FindObject(context, metadata, context.TargetObject);
            if (target == null) return;
            var columns = target.Columns.Where(c => !c.IsIdentity && !c.IsComputed && !string.Equals(c.DataType, "timestamp", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.DataType, "rowversion", StringComparison.OrdinalIgnoreCase)).ToList();
            if (columns.Count == 0) return;
            string names = string.Join("," + Environment.NewLine + "    ", columns.Select(c => Quote(c.Name)));
            string values = string.Join("," + Environment.NewLine + "    ", columns.Select((c, i) => "${" + (i + 1) + ":" + DefaultValue(c) + "}"));
            result.Add(new CompletionItem { DisplayText = "(INSERT columns and VALUES)", InsertText = Environment.NewLine + "(" + Environment.NewLine + "    " + names + Environment.NewLine + ")" + Environment.NewLine + "VALUES" + Environment.NewLine + "(" + Environment.NewLine + "    " + values + Environment.NewLine + ");", Kind = CompletionItemKind.Snippet, Description = columns.Count + " writable columns", Score = 140 });
        }

        private static string DefaultValue(ColumnMetadata column)
        {
            string type = (column.DataType ?? string.Empty).ToLowerInvariant();
            if (type.Contains("char") || type.Contains("text") || type == "xml" || type == "uniqueidentifier") return "''";
            if (type == "date" || type.Contains("time")) return "GETDATE()";
            if (type == "bit") return "0";
            return column.IsNullable ? "NULL" : "0";
        }

        private static void AddGeneral(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            AddScopedColumns(result, context, metadata, 60);
            result.AddRange(objects.Where(o => o.Kind == CompletionItemKind.Function).Select(o => ObjectItem(o, o.IsSystem ? 20 : 45, false)));
            result.AddRange(BuiltInFunctions.Select(name => new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function, Description = BuiltInFunctionDescription(name), Score = 35 }));
            result.AddRange(Keywords.Select(k => new CompletionItem { DisplayText = k, InsertText = k, Kind = CompletionItemKind.Keyword, Description = "keyword", Score = 20 }));
            foreach (var s in SnippetService.GetAllSnippets()) result.Add(new CompletionItem { DisplayText = s.Prefix, InsertText = s.Body, Kind = CompletionItemKind.Snippet, Description = s.Description, Score = 30 });
        }

        private static string BuiltInFunctionDescription(string name)
        {
            switch ((name ?? string.Empty).ToUpperInvariant())
            {
                case "ISNULL": return "ISNULL(check_expression, replacement_value)";
                case "COALESCE": return "COALESCE(expression, ...)";
                case "DATEADD": return "DATEADD(datepart, number, date)";
                case "DATEDIFF": return "DATEDIFF(datepart, startdate, enddate)";
                case "SUBSTRING": return "SUBSTRING(expression, start, length)";
                case "REPLACE": return "REPLACE(expression, pattern, replacement)";
                case "CAST": return "CAST(expression AS data_type)";
                case "CONVERT": return "CONVERT(data_type, expression [, style])";
                default: return "SQL Server built-in function";
            }
        }

        private static IEnumerable<CompletionItem> BuildJoinItems(CompletionContext context, MetadataSnapshot metadata)
        {
            var sources = context.Aliases.Where(p => !string.Equals(p.Key, LastPart(p.Value), StringComparison.OrdinalIgnoreCase)).ToList();
            if (sources.Count == 0) sources = context.Aliases.ToList();
            if (sources.Count == 0) yield break;
            // Prefilter: ObjectMatches can only succeed when the last name parts
            // are equal, so a hash set of source name tails avoids running the
            // normalization-heavy comparison for every table in the database.
            var sourceNameTails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
                sourceNameTails.Add(LastPart(source.Value));
            MetadataObjectIndex index = metadata.GetIndex();
            bool autoAddAliases = CompletionSettings().autoAddAliases;
            foreach (var target in metadata.Objects.Where(o => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View))
            {
                if (context.IsJoinSource && !MatchesContainer(target, context.Qualifier)) continue;
                string targetName = target.QualifiedName;
                if (sourceNameTails.Contains(LastPart(targetName))
                    && sources.Any(s => ObjectMatches(s.Value, targetName))) continue;
                string alias = MakeAlias(target, context.Aliases.Keys);
                List<ForeignKeyMetadata> foreignKeyCandidates = index.GetForeignKeysBySimpleName(LastPart(CleanQualified(targetName)));
                var relationships = foreignKeyCandidates == null
                    ? new List<ForeignKeyMetadata>()
                    : foreignKeyCandidates.Where(f => (ObjectMatches(f.ParentObject, targetName) || ObjectMatches(f.ReferencedObject, targetName))
                        && sources.Any(s => ObjectMatches(f.ParentObject, s.Value) || ObjectMatches(f.ReferencedObject, s.Value))).ToList();
                string targetQualifier = autoAddAliases ? Quote(alias) : Quote(target.Name);
                string targetInsert = QualifiedInsert(target) + (autoAddAliases ? " AS " + Quote(alias) : string.Empty);
                if (relationships.Count == 0)
                {
                    foreach (var source in sources)
                    {
                        DatabaseObjectMetadata sourceObject = FindObject(context, metadata, source.Value);
                        List<JoinColumnMatch> inferred = InferJoinColumns(sourceObject, target);
                        if (inferred.Count == 0) continue;
                        string conditions = string.Join(" AND ", inferred.Select(p => Quote(source.Key) + "." + Quote(p.SourceColumn)
                            + " = " + targetQualifier + "." + Quote(p.TargetColumn)));
                        yield return new CompletionItem
                        {
                            DisplayText = DisplayQualifiedName(target), InsertText = targetInsert + " ON " + conditions,
                            Kind = CompletionItemKind.Join,
                            Description = inferred.Any(p => p.IsCustom) ? "custom-rule join" : "same-name column join",
                            DetailTextProvider = () => ObjectDetail(target), Score = inferred.Any(p => p.IsCustom) ? 100 : 70
                        };
                    }
                    yield return new CompletionItem { DisplayText = DisplayQualifiedName(target), InsertText = targetInsert, Kind = CompletionItemKind.Join, Description = "table (no known relationship)", DetailTextProvider = () => ObjectDetail(target), Score = 10 };
                    continue;
                }
                foreach (var fk in relationships)
                {
                    var source = sources.First(s => ObjectMatches(fk.ParentObject, s.Value) || ObjectMatches(fk.ReferencedObject, s.Value));
                    bool sourceParent = ObjectMatches(fk.ParentObject, source.Value);
                    var conditions = fk.ParentColumns.Select((c, i) => Quote(source.Key) + "." + Quote(sourceParent ? c : fk.ReferencedColumns[i]) + " = " + targetQualifier + "." + Quote(sourceParent ? fk.ReferencedColumns[i] : c));
                    yield return new CompletionItem { DisplayText = DisplayQualifiedName(target), InsertText = targetInsert + " ON " + string.Join(" AND ", conditions), Kind = CompletionItemKind.Join, Description = "foreign-key join", DetailTextProvider = () => ObjectDetail(target), Score = 110 };
                }
            }
        }

        internal sealed class JoinColumnMatch { public string SourceColumn; public string TargetColumn; public bool IsCustom; }

        internal static List<JoinColumnMatch> InferJoinColumns(DatabaseObjectMetadata source, DatabaseObjectMetadata target, SettingsManager.SqlCompletionSettings settings = null)
        {
            var result = new List<JoinColumnMatch>();
            if (source == null || target == null) return result;
            settings = settings ?? CompletionSettings();
            var targetColumns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (ColumnMetadata targetColumn in target.Columns)
                if (!targetColumns.ContainsKey(targetColumn.Name)) targetColumns[targetColumn.Name] = targetColumn;
            foreach (string rule in (settings.joinColumnRules ?? string.Empty).Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] sides = rule.Split('=');
                if (sides.Length != 2) continue;
                string leftObject, leftColumn, rightObject, rightColumn;
                SplitJoinRuleSide(sides[0], out leftObject, out leftColumn);
                SplitJoinRuleSide(sides[1], out rightObject, out rightColumn);
                bool forward = RuleObjectMatches(leftObject, source) && RuleObjectMatches(rightObject, target);
                bool reverse = RuleObjectMatches(leftObject, target) && RuleObjectMatches(rightObject, source);
                string sourceColumn = forward ? leftColumn : reverse ? rightColumn : null;
                string targetColumn = forward ? rightColumn : reverse ? leftColumn : null;
                if (sourceColumn != null && source.Columns.Any(c => c.Name.Equals(sourceColumn, StringComparison.OrdinalIgnoreCase))
                    && targetColumns.ContainsKey(targetColumn))
                    result.Add(new JoinColumnMatch { SourceColumn = sourceColumn, TargetColumn = targetColumn, IsCustom = true });
            }
            if (result.Count > 0) return result;
            foreach (ColumnMetadata column in source.Columns)
            {
                if (!targetColumns.TryGetValue(column.Name, out ColumnMetadata match)) continue;
                bool keyLike = column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
                    || column.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                    || column.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
                if (keyLike) result.Add(new JoinColumnMatch { SourceColumn = column.Name, TargetColumn = match.Name });
                if (result.Count == 3) break;
            }
            return result;
        }

        private static void SplitJoinRuleSide(string value, out string objectName, out string column)
        {
            string cleaned = CleanQualified(value);
            int dot = cleaned.LastIndexOf('.');
            objectName = dot < 0 ? string.Empty : cleaned.Substring(0, dot);
            column = dot < 0 ? cleaned : cleaned.Substring(dot + 1);
        }

        private static bool RuleObjectMatches(string ruleObject, DatabaseObjectMetadata metadata)
            => string.IsNullOrWhiteSpace(ruleObject) || ObjectMatches(ruleObject, metadata.QualifiedName) || ObjectMatches(ruleObject, metadata.Name);

        private static CompletionItem ColumnItem(ColumnMetadata c, int score, DatabaseObjectMetadata owner = null)
        {
            return new CompletionItem
            {
                DisplayText = c.Name, InsertText = Quote(c.Name), Kind = CompletionItemKind.Column,
                Description = c.TypeDisplay + (c.IsNullable ? " null" : " not null"),
                DetailTextProvider = () => (owner == null ? string.Empty : owner.QualifiedName + Environment.NewLine) + ColumnDetail(c), Score = score
            };
        }
        private static CompletionItem ContainerItem(string name, CompletionItemKind kind, int score) => new CompletionItem { DisplayText = name, InsertText = Quote(name), Kind = kind, Description = kind.ToString(), Score = score };
        private static CompletionItem ObjectItem(DatabaseObjectMetadata o, int score, bool omitSchema) => new CompletionItem { DisplayText = omitSchema ? o.Name : DisplayQualifiedName(o), InsertText = omitSchema ? Quote(o.Name) : QualifiedInsert(o), Kind = o.Kind, Description = o.DescriptionSummary, DetailTextProvider = () => ObjectDetail(o), Score = score };
        private static CompletionItem DataSourceItem(DatabaseObjectMetadata o, CompletionContext context, int score)
        {
            CompletionItem item = ObjectItem(o, score, false);
            if (CompletionSettings().autoAddAliases && (o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || o.Kind == CompletionItemKind.Synonym))
                item.InsertText += " AS " + Quote(MakeAlias(o, context.Aliases.Keys));
            return item;
        }

        private static string ColumnDetail(ColumnMetadata column)
        {
            string flags = (column.IsPrimaryKey ? "PK " : string.Empty)
                + (column.IsForeignKey ? "FK " : string.Empty)
                + (column.IsUnique && !column.IsPrimaryKey ? "UQ " : string.Empty);
            string detail = (string.IsNullOrWhiteSpace(flags) ? "   " : flags.PadRight(3))
                + column.Name + "  " + column.TypeDisplay + (column.IsNullable ? " NULL" : " NOT NULL")
                + (column.IsIdentity ? "  IDENTITY" : string.Empty) + (column.IsComputed ? "  COMPUTED" : string.Empty)
                + (string.IsNullOrWhiteSpace(column.DefaultDefinition) ? string.Empty : "  DEFAULT " + column.DefaultDefinition);
            if (!string.IsNullOrWhiteSpace(column.Description)) detail += "  -- " + column.Description;
            return detail;
        }

        private static string ObjectDetail(DatabaseObjectMetadata item)
        {
            // The full detail string is memoized on the metadata object; detail
            // panels are only rendered for the selected candidate.
            if (item == null) return string.Empty;
            return item.CachedDetail;
        }
        private static string DisplayQualifiedName(DatabaseObjectMetadata o) => (o.IsExternal && !string.IsNullOrWhiteSpace(o.Server) ? o.Server + "." : "") + (o.IsExternal && !string.IsNullOrWhiteSpace(o.Database) ? o.Database + "." : "") + (string.IsNullOrEmpty(o.Schema) ? o.Name : o.Schema + "." + o.Name);
        private static string QualifiedInsert(DatabaseObjectMetadata o) => (o.IsExternal && !string.IsNullOrWhiteSpace(o.Server) ? Quote(o.Server) + "." : "") + (o.IsExternal && !string.IsNullOrWhiteSpace(o.Database) ? Quote(o.Database) + "." : "") + (string.IsNullOrEmpty(o.Schema) ? Quote(o.Name) : Quote(o.Schema) + "." + Quote(o.Name));
        private static bool IsDataSource(DatabaseObjectMetadata o) => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || (o.Kind == CompletionItemKind.Function && o.IsTableValuedFunction) || o.Kind == CompletionItemKind.Synonym;
        private static bool MatchesContainer(DatabaseObjectMetadata item, string qualifier)
        {
            if (string.IsNullOrWhiteSpace(qualifier)) return true;
            return EndsWith(ContainerName(item), CleanQualified(qualifier));
        }

        private static string ContainerName(DatabaseObjectMetadata item) =>
            (string.IsNullOrWhiteSpace(item.Server) ? "" : item.Server + ".") +
            (string.IsNullOrWhiteSpace(item.Database) ? "" : item.Database + ".") +
            item.Schema;

        private static List<CompletionItem> FilterAndSort(IEnumerable<CompletionItem> items, string prefix)
        {
            prefix = Clean(prefix ?? string.Empty).TrimStart('@');
            var settings = CompletionSettings();
            return items.Select(i => new { Item = i, Match = MatchItem(i, prefix) }).Where(x => x.Match >= 0).OrderByDescending(x => x.Item.Score + (settings.learnFromUsage ? CompletionUsageStore.GetScore(x.Item) : 0) + x.Match).ThenBy(x => x.Item.DisplayText, StringComparer.OrdinalIgnoreCase).Take(settings.maximumItems).Select(x => x.Item).ToList();
        }

        private static int MatchItem(CompletionItem item, string prefix)
        {
            string display = (item.DisplayText ?? string.Empty).TrimStart('@');
            int score = MatchScore(display, prefix);
            int dot = display.LastIndexOf('.');
            if (dot < 0) return score;
            // DataSource candidates display schema-qualified names such as
            // "dbo.WSDD". The acronym and substring channels must also see the
            // bare object name, or typing "WD" suggests "WsdData" after
            // "dbo." but nothing in the unqualified FROM position.
            int bare = MatchScore(display.Substring(dot + 1), prefix);
            return Math.Max(score, bare);
        }

        private static int MatchScore(string value, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return 0;
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 40;
            if (StartsWithInitials(value, prefix)) return 25;
            if (value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) return 10;
            // SQL LIKE %W%D% style: every typed character must appear in the
            // name in order, so "WD" still suggests "WSDD". Ranked below the
            // contiguous/acronym channels, above the typo-tolerance fallback.
            if (ContainsInOrder(value, prefix)) return 5;
            int window = value.Length > prefix.Length + 2 ? prefix.Length + 2 : value.Length;
            return LevenshteinBounded(value, window, prefix) <= Math.Max(1, prefix.Length / 3) ? 2 : -1;
        }

        // Acronym channel without per-item string building: walks the value
        // once comparing initials against the prefix.
        private static bool StartsWithInitials(string value, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            int at = 0;
            for (int i = 0; i < value.Length && at < prefix.Length; i++)
            {
                if (i == 0 || char.IsUpper(value[i]) || value[i - 1] == '_')
                {
                    if (char.ToUpperInvariant(value[i]) != char.ToUpperInvariant(prefix[at])) return false;
                    at++;
                }
            }
            return at == prefix.Length;
        }

        private static bool ContainsInOrder(string value, string prefix)
        {
            if (prefix.Length > value.Length) return false;
            int at = 0;
            for (int i = 0; i < prefix.Length; i++)
            {
                char wanted = char.ToUpperInvariant(prefix[i]);
                while (true)
                {
                    if (at >= value.Length) return false;
                    if (char.ToUpperInvariant(value[at]) == wanted) { at++; break; }
                    at++;
                }
            }
            return true;
        }

        // Levenshtein distance limited to the first `limit` characters of a,
        // reusing a per-thread buffer so the fuzzy fallback channel does not
        // allocate an array for every non-matching candidate.
        [ThreadStatic] private static int[] levenshteinRow;
        private static int LevenshteinBounded(string a, int limitA, string b)
        {
            int lengthA = Math.Min(limitA, a.Length);
            int lengthB = b.Length;
            if (lengthB == 0) return lengthA;
            int[] row = levenshteinRow ?? (levenshteinRow = new int[64]);
            if (row.Length < lengthB + 1) row = levenshteinRow = new int[lengthB + 1];
            for (int j = 0; j <= lengthB; j++) row[j] = j;
            for (int i = 1; i <= lengthA; i++)
            {
                int prev = row[0];
                row[0] = i;
                char ca = char.ToUpperInvariant(a[i - 1]);
                for (int j = 1; j <= lengthB; j++)
                {
                    int old = row[j];
                    int insert = row[j] + 1;
                    int delete = row[j - 1] + 1;
                    int substitute = prev + (ca == char.ToUpperInvariant(b[j - 1]) ? 0 : 1);
                    int best = insert < delete ? insert : delete;
                    row[j] = best < substitute ? best : substitute;
                    prev = old;
                }
            }
            return row[lengthB];
        }
        private static void AddAliases(string statement, CompletionContext context)
        {
            int caretDepth = GetDepth(statement, statement.Length);
            // Alias matches arrive in source order; the paren depth at each
            // match is advanced incrementally instead of rescanning the whole
            // statement for every match.
            int lastIndex = 0, lastDepth = 0;
            foreach (Match m in AliasPattern.Matches(statement))
            {
                int depth = lastDepth;
                for (int i = lastIndex; i < m.Index; i++)
                {
                    if (statement[i] == '(') depth++;
                    else if (statement[i] == ')') depth = Math.Max(0, depth - 1);
                }
                lastIndex = m.Index;
                lastDepth = depth;
                // Outer aliases remain visible to correlated subqueries; aliases from a
                // completed deeper subquery must not leak back into its parent scope.
                if (depth > caretDepth) continue;
                string obj = CleanQualified(m.Groups["obj"].Value);
                string alias = Clean(m.Groups["alias"].Value);
                context.Aliases[LastPart(obj)] = obj;
                if (!string.IsNullOrEmpty(alias) && !ClauseKeywords.Contains(alias)) context.Aliases[alias] = obj;
            }
        }

        private static int GetDepth(string text, int before)
        {
            int depth = 0;
            for (int i = 0; i < before && i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth = Math.Max(0, depth - 1);
            }
            return depth;
        }

        private static void AddLocalObjects(string sessionText, string maskedSession, string currentBatch, string maskedBatch, CompletionContext context)
        {
            string batch = maskedBatch ?? SqlTextContext.MaskCommentsAndStrings(currentBatch ?? string.Empty);
            foreach (Match m in DeclareTablePattern.Matches(batch)) AddLocal(context, m.Groups["name"].Value, ParseDefinitions(m.Groups["cols"].Value));
            string sql = maskedSession ?? SqlTextContext.MaskCommentsAndStrings(sessionText ?? string.Empty);
            var events = new List<LocalObjectEvent>();
            foreach (Match m in TempCreatePattern.Matches(sql)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseDefinitions(m.Groups["cols"].Value).ToList() });
            foreach (Match m in SelectIntoPattern.Matches(sql)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseSelect(m.Groups["select"].Value).ToList() });
            foreach (Match m in TempAlterPattern.Matches(sql)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseDefinitions(m.Groups["cols"].Value).ToList(), IsAlter = true });
            foreach (Match m in TempDropPattern.Matches(sql)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, IsDrop = true });
            foreach (LocalObjectEvent item in events.OrderBy(e => e.Index))
            {
                string name = Clean(item.Name);
                if (item.IsDrop) { context.LocalObjects.RemoveAll(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)); context.Aliases.Remove(name); }
                else if (item.IsAlter)
                {
                    DatabaseObjectMetadata existing = context.LocalObjects.LastOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) foreach (ColumnMetadata column in item.Columns) if (!existing.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase))) existing.Columns.Add(column);
                }
                else AddLocal(context, name, item.Columns);
            }
        }

        private sealed class LocalObjectEvent { public int Index; public string Name; public bool IsDrop; public bool IsAlter; public List<ColumnMetadata> Columns; }

        private static IEnumerable<ColumnMetadata> ParseDefinitions(string value) => SplitTopLevel(value).Select(p => DefinitionColumnPattern.Match(p.Trim())).Where(m => m.Success).Select(m => new ColumnMetadata { Name = Clean(m.Groups["name"].Value), DataType = m.Groups["type"].Value });
        private static IEnumerable<ColumnMetadata> ParseSelect(string value) => SplitTopLevel(value).Select(p => { Match a = SelectAliasNamePattern.Match(p.Trim()); string n = a.Success ? Clean(a.Groups["name"].Value) : Clean(p.Trim().Split('.').Last()); return new ColumnMetadata { Name = n, DataType = string.Empty }; }).Where(c => SimpleWordPattern.IsMatch(c.Name));
        private static List<string> SplitTopLevel(string value) { var result = new List<string>(); int depth = 0, start = 0; for (int i = 0; i < value.Length; i++) { if (value[i] == '(') depth++; else if (value[i] == ')') depth = Math.Max(0, depth - 1); else if (value[i] == ',' && depth == 0) { result.Add(value.Substring(start, i - start)); start = i + 1; } } result.Add(value.Substring(start)); return result; }
        private static void AddLocal(CompletionContext c, string name, IEnumerable<ColumnMetadata> columns) { var o = new DatabaseObjectMetadata { Schema = string.Empty, Name = Clean(name), Kind = CompletionItemKind.Table }; o.Columns.AddRange(columns); c.LocalObjects.RemoveAll(x => string.Equals(x.Name, o.Name, StringComparison.OrdinalIgnoreCase)); c.LocalObjects.Add(o); c.Aliases[o.Name] = o.Name; }
        private static string ResolveAlias(string value, CompletionContext c) => c.Aliases.TryGetValue(value ?? string.Empty, out string resolved) ? resolved : value;
        private static bool ObjectMatches(string a, string b)
        {
            string left = CleanQualified(a), right = CleanQualified(b);
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            string[] leftQualified = left.Split('.'), rightQualified = right.Split('.');
            if ((left.Contains("..") || right.Contains("..")) && leftQualified.Length == rightQualified.Length)
                return leftQualified.Zip(rightQualified, (x, y) => string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)
                    || string.Equals(x, y, StringComparison.OrdinalIgnoreCase)).All(matches => matches);
            int leftParts = left.Count(c => c == '.') + 1, rightParts = right.Count(c => c == '.') + 1;
            if (leftParts == 1 || rightParts == 1) return string.Equals(LastPart(left), LastPart(right), StringComparison.OrdinalIgnoreCase);
            return leftParts > rightParts
                ? left.EndsWith("." + right, StringComparison.OrdinalIgnoreCase)
                : right.EndsWith("." + left, StringComparison.OrdinalIgnoreCase);
        }
        private static bool EndsWith(string a, string b) => string.Equals(CleanQualified(a), CleanQualified(b), StringComparison.OrdinalIgnoreCase) || CleanQualified(a).EndsWith("." + CleanQualified(b), StringComparison.OrdinalIgnoreCase);
        private static string LastPart(string value) => CleanQualified(value).Split('.').LastOrDefault() ?? string.Empty;
        private static string CleanQualified(string value) => DatabaseIdentifier.NormalizeSqlServer(value);
        private static string Clean(string value) => DatabaseIdentifier.UnquoteSqlServerPart(value);
        private static string Quote(string value) => CompletionSettings().useSquareBrackets ? "[" + (value ?? string.Empty).Replace("]", "]]" ) + "]" : value ?? string.Empty;
        internal static string MakeAlias(DatabaseObjectMetadata item, IEnumerable<string> existing, SettingsManager.SqlCompletionSettings settings = null)
        {
            string qualified = CleanQualified(item?.QualifiedName), name = item?.Name ?? "t";
            settings = settings ?? CompletionSettings();
            foreach (string mapping in (settings.customAliases ?? string.Empty).Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = mapping.Split('=');
                if (pair.Length == 2 && (ObjectMatches(pair[0], qualified) || ObjectMatches(pair[0], name)))
                    return UniqueAlias(Clean(pair[1]), existing);
            }
            foreach (string prefix in (settings.aliasPrefixToIgnore ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)) { name = name.Substring(prefix.Trim().Length); break; }
            string seed = new string((name ?? "t").Where(char.IsUpper).ToArray()).ToLowerInvariant();
            if (string.IsNullOrEmpty(seed)) seed = (name ?? "t").Substring(0, 1).ToLowerInvariant();
            return UniqueAlias(seed, existing);
        }

        private static string UniqueAlias(string seed, IEnumerable<string> existing)
        {
            if (string.IsNullOrWhiteSpace(seed)) seed = "t";
            var used = new HashSet<string>(existing ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string result = seed; int n = 2;
            while (used.Contains(result)) result = seed + n++;
            return result;
        }
        private static readonly HashSet<string> ClauseKeywords = new HashSet<string>(new[] { "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "ON", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "OFFSET", "FETCH", "FOR" }, StringComparer.OrdinalIgnoreCase);
        private static readonly string[] DetachedStatementStarters = {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "SET", "CREATE", "ALTER", "DROP", "TRUNCATE", "USE", "PRINT", "EXEC", "BEGIN", "COMMIT", "ROLLBACK", "GRANT", "DENY", "REVOKE"
        };
        private static readonly string[] Keywords = {
            "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AS",
            "GROUP BY", "ORDER BY", "HAVING", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE", "AND", "OR", "NOT", "NULL",
            "IS NULL", "IS NOT NULL", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "TOP", "UNION", "UNION ALL", "EXISTS", "IN", "LIKE",
            "BETWEEN", "DECLARE", "EXEC", "CREATE", "ALTER", "DROP", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE VIEW", "ALTER VIEW",
            "CREATE PROCEDURE", "ALTER PROCEDURE", "CREATE FUNCTION", "ALTER FUNCTION", "TRUNCATE TABLE", "BEGIN", "BEGIN TRANSACTION", "COMMIT",
            "ROLLBACK", "BEGIN TRY", "END TRY", "BEGIN CATCH", "END CATCH", "THROW", "GRANT", "DENY", "REVOKE", "CREATE USER", "CREATE ROLE",
            "CURRENT_TIMESTAMP", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "XMLNAMESPACES"
        };
    }
}
