using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MSSQLTool.Model;
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
        private static readonly Regex InsertColumnListPattern = new Regex(@"\bINSERT\s+(?:INTO\s+)?(?<target>(?:" + Id + @"(?:\s*\.\s*" + Id + @"){0,3}|" + Id + @"(?:\s*\.\s*" + Id + @")?\s*\.\s*\.\s*" + Id + @"))\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CompletedInsertTargetPattern = new Regex(@"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InsertBodyPattern = new Regex(@"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // The tail of a join target with nothing after it: the ON clause is still missing.
        private static readonly Regex UpdateSetPattern = new Regex(@"\bUPDATE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?" + Id + @")?\s+SET\s+[^;]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DmlTailPattern = new Regex(@"\b(?:MERGE|TRUNCATE\s+TABLE|DELETE\s+FROM)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

        public static CompletionContext Analyze(string textBeforeCaret, int caretColumn, string textAfterCaret = null,
            IReadOnlyList<DatabaseObjectMetadata> sessionLocalObjects = null, int compatibilityLevel = 170)
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
            var context = new CompletionContext { Kind = CompletionContextKind.General, CurrentStatement = statement, CurrentBatch = text.BatchText, MaskedStatement = masked, DocumentText = sessionText, CaretOffset = source.Length };
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
            // The editor hands in a line-relative column, and the caret may sit before the end of the
            // statement (text after the caret on the same line).  The column becomes an offset once,
            // here; everything below uses offsets and never treats a column as one.
            int caretLineStart = source.LastIndexOf('\n') + 1;
            int caretInSource = Math.Min(source.Length, caretLineStart + Math.Max(0, caretColumn));
            int caretInStatement = Math.Max(0, Math.Min(statement.Length, caretInSource - text.StatementStart));
            context.CaretOffset = caretInSource;
            // Session-scoped #temp tables survive GO, so inspect all text before
            // the caret rather than only the current batch.  The window scanner hands the
            // declarations from previous statements in, so this pass only sees the current
            // statement (and stays cheap on a large script).
            AddSessionLocalObjects(context, sessionLocalObjects);
            AddLocalObjects(sessionText, maskedSession, text.BatchText, text.MaskedBatch, context);
            SqlSemanticModel semanticModel = SqlSemanticModel.Create(semanticText, caretInStatement, compatibilityLevel);
            // The scan above splits on semicolons and line-leading starters only.  Two statements on
            // one line are visible in the tree, and the tree is already parsed, so it supplies the
            // boundary instead of a second parser pass over the same text.
            if (semanticModel.StatementStart > 0)
            {
                int statementShift = semanticModel.StatementStart;
                text.AdvanceStatement(statementShift);
                statement = text.StatementText;
                masked = text.MaskedStatement;
                caretInStatement = Math.Max(0, caretInStatement - statementShift);
                context.CurrentStatement = statement;
                context.MaskedStatement = masked;
                semanticModel = SqlSemanticModel.Create(statement + suffix, caretInStatement, compatibilityLevel);
            }
            semanticModel.CopyAliasesTo(context);
            foreach (string column in semanticModel.ReferencedColumns) context.ReferencedColumns.Add(column);
            foreach (string column in semanticModel.GroupByColumns) context.GroupByColumns.Add(column);
            context.SelectAliases.AddRange(semanticModel.SelectAliases);
            context.Diagnostics.AddRange(semanticModel.Diagnostics);
            context.HasParseErrors = semanticModel.HasParseErrors;
            // FROM/JOIN aliases come from the parse (CopyAliasesTo above) with the scoping the query
            // actually has, so the text scan only runs when the parse produced nothing usable.
            if (context.Aliases.Count == 0) AddAliases(masked + suffix, context);
            if (context.Aliases.Count == 0) AddAliases(maskedSession, context);
            PruneAliasesToCurrentStatement(context, masked + suffix);
            // The word being typed comes from the position resolver's character walk, so the prefix
            // the popup replaces and the prefix the member qualifier is read against are one string.
            context.Prefix = Clean(semanticModel.Position?.Prefix);
            context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);

            bool snippetsEnabled = SettingsManager.GetSnippetSettings().useSnippets;
            context.SnippetsEnabled = snippetsEnabled;
            if (snippetsEnabled && IsExactSnippetPrefix(context.Prefix, true, SnippetService.GetAllSnippets()))
            {
                // An exact prefix still wins in every SQL context (a snippet is an editor construct,
                // not an EXEC positional value), but it no longer replaces the resolved position
                // with "general": that switch offered columns and functions at a table source, so
                // "FROM dbo.T t LJ" showed the table's LXTJGID among the join snippets.
                context.ExactSnippetPrefix = context.Prefix;
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

            // Member access, including "SELECT c." where ScriptDOM dropped the trailing dot: the
            // position resolver already read the qualifier, the prefix and whether the member is
            // itself a table source, so there is nothing left to match here.
            SqlCaretPosition caretPosition = semanticModel.Position;
            if (caretPosition != null && caretPosition.Kind == SqlCaretKind.Member)
            {
                context.Kind = CompletionContextKind.Member;
                context.Qualifier = CleanQualified(caretPosition.Qualifier);
                context.Prefix = Clean(caretPosition.Prefix);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
                context.HasOmittedSchemaQualifier = caretPosition.OmittedSchemaQualifier;
                context.IsJoinSource = caretPosition.IsJoinSource;
                context.IsDataSourceMember = caretPosition.IsDataSourceMember;
                return context;
            }

            string withoutPrefix = statement.Substring(0, Math.Max(0, statement.Length - context.Prefix.Length));
            Match insertBody = InsertBodyPattern.Match(masked);
            Match update = UpdateSetPattern.Match(masked);
            if (insertBody.Success) { context.Kind = CompletionContextKind.InsertBody; context.TargetObject = CleanQualified(insertBody.Groups["target"].Value); }
            else if (update.Success) { context.Kind = CompletionContextKind.UpdateSet; context.TargetObject = ResolveAlias(CleanQualified(update.Groups["target"].Value), context); }
            else if (DmlTailPattern.IsMatch(withoutPrefix)) context.Kind = CompletionContextKind.DataSource;

            // The parse tree answers "where is the caret": the same tree resolved the aliases and the
            // columns, so the three cannot disagree.  When it has nothing to say, the resolver reads
            // the tokens it already has behind the caret — there is no second classifier here.
            ApplyParserPosition(context, semanticModel);
            // Everything the item lists need to know about the position, published once.  A list must
            // read these facts instead of re-reading the text.
            SqlCaretPosition resolved = semanticModel.Position;
            context.ResolvedKind = resolved?.Kind ?? SqlCaretKind.Unknown;
            context.EnclosingClause = resolved?.EnclosingClause;
            context.StatementKind = semanticModel.StatementKind;

            string predicateSql = statement;
            if (!string.IsNullOrEmpty(context.Prefix) && !context.Prefix.All(char.IsDigit))
                predicateSql = statement.Substring(0, Math.Max(0, statement.Length - context.Prefix.Length));

            if (context.Kind == CompletionContextKind.Predicate)
            {
                context.PredicateState = SqlPredicateStateClassifier.Classify(predicateSql, out int predicateDepth);
                context.PredicateDepth = predicateDepth;
                // The column the condition compares comes from the position resolver's tokens, not
                // from a second regex over the masked text.
                context.ActiveColumn = caretPosition?.ActiveColumn;
                context.PredicateOwner = caretPosition?.PredicateOwner;
            }
            return context;
        }

        /// <summary>
        /// Translates the resolved position into the context the item builder understands.  Only the
        /// generic positions are taken over; specialised contexts (INSERT column list, EXEC
        /// arguments, MERGE target, window clauses, PIVOT...) keep the detection that carries their
        /// extra data.
        /// </summary>
        private static void ApplyParserPosition(CompletionContext context, SqlSemanticModel model)
        {
            SqlCaretPosition position = model?.Position;
            if (position == null || position.Kind == SqlCaretKind.Unknown) return;
            // The SET list assigns with "=", so ScriptDOM may call that position a predicate.
            // Keep UPDATE SET only for that ambiguous case; a resolved SELECT list or data
            // source belongs to a nested query or the next statement and must win.
            if (context.Kind == CompletionContextKind.UpdateSet
                && position.Kind == SqlCaretKind.Predicate
                && !position.InPredicateClause) return;
            switch (context.Kind)
            {
                case CompletionContextKind.General:
                case CompletionContextKind.Predicate:
                case CompletionContextKind.SelectList:
                case CompletionContextKind.DataSource:
                case CompletionContextKind.Join:
                case CompletionContextKind.MergeSource:
                case CompletionContextKind.TableSourceTail:
                case CompletionContextKind.JoinSourceTail:
                case CompletionContextKind.GroupBy:
                case CompletionContextKind.OrderBy:
                case CompletionContextKind.UpdateSet:
                    break;
                default:
                    return;
            }

            CompletionContextKind? mapped = MapCaretKind(position.Kind);
            if (mapped == null) return;
            CompletionContextKind kind = mapped.Value;
            CompletionContextKind previousKind = context.Kind;
            context.Kind = kind;
            // A target captured from a preceding UPDATE must not scope a different query.
            if (previousKind == CompletionContextKind.UpdateSet
                && kind != CompletionContextKind.UpdateSet
                && kind != CompletionContextKind.Predicate)
                context.TargetObject = null;
            context.PositionResolved = true;
            context.PositionDecidedByTree = position.DecidedByTree;
            if (kind == CompletionContextKind.TableSourceTail || kind == CompletionContextKind.JoinSourceTail)
            {
                context.TableSourceName = position.SourceName;
                context.TableAliasWritten = position.AliasWritten;
                // ON only belongs to a join that takes one (not CROSS JOIN / APPLY).
                context.AwaitingJoinPredicate = kind == CompletionContextKind.JoinSourceTail && position.TakesOn;
            }
            else
            {
                context.AwaitingJoinPredicate = false;
            }
        }

        /// <summary>
        /// The context a resolved caret kind stands for.  Null when that answer has no generic
        /// context: member access is handled before this point and needs its qualifier, not a kind.
        /// Keeping the mapping in one place is what lets a test prove the answer was not replaced.
        /// </summary>
        internal static CompletionContextKind? MapCaretKind(SqlCaretKind kind)
        {
            switch (kind)
            {
                case SqlCaretKind.DataSource: return CompletionContextKind.DataSource;
                case SqlCaretKind.Join: return CompletionContextKind.Join;
                case SqlCaretKind.MergeSource: return CompletionContextKind.MergeSource;
                case SqlCaretKind.TableSourceTail: return CompletionContextKind.TableSourceTail;
                case SqlCaretKind.JoinSourceTail: return CompletionContextKind.JoinSourceTail;
                case SqlCaretKind.SelectList: return CompletionContextKind.SelectList;
                case SqlCaretKind.Predicate: return CompletionContextKind.Predicate;
                case SqlCaretKind.GroupBy: return CompletionContextKind.GroupBy;
                case SqlCaretKind.OrderBy: return CompletionContextKind.OrderBy;
                default: return null;
            }
        }

        /// <summary>
        /// The bare ON token, plus the space that separates it from the alias.  Inserting it asks for
        /// the continuation popup (<see cref="CompletionController.ShouldRequestCompletionAfterCommit"/>)
        /// so the key columns are offered as soon as ON is in the document.
        /// </summary>
        private static CompletionItem JoinPredicateItem(CompletionContext context)
        {
            string text = context?.DocumentText ?? string.Empty;
            int caret = context?.CaretOffset ?? -1;
            bool needsLeadingSpace = caret > 0 && caret <= text.Length && !char.IsWhiteSpace(text[caret - 1]);
            return new CompletionItem
            {
                DisplayText = "ON",
                InsertText = needsLeadingSpace ? " ON " : "ON ",
                Kind = CompletionItemKind.Join,
                Description = Doc(
                    "Starts the join condition.",
                    "Tab accepts it and the key columns of both sources are offered right away.",
                    "Pair the new table's key with the existing one."),
                // Above the table hints: ON is the only mandatory token at a join target.
                Score = 260
            };
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

            // "FROM dbo.T WITH (|" and "... OPTION (|" are the bodies of the hint clauses.  The
            // parenthesis belongs to the clause keyword, so it is neither a function call nor a table
            // source: only the hint name is missing and the closing parenthesis is typed after it.
            if (HasUnclosedClause(masked, "WITH"))
            {
                context.Kind = CompletionContextKind.TableHint;
                return true;
            }
            if (HasUnclosedClause(masked, "OPTION"))
            {
                context.Kind = CompletionContextKind.QueryHint;
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
            "REFERENCES", "TABLE", "BEGIN", "OVER", "WITHIN", "GROUPING", "RETURN", "THROW", "RAISERROR",
            // "WHERE (a = 1 AND |" is a boolean group, not a function call: the clause keyword owns the
            // parenthesis, so offering function arguments there is wrong.
            "WHERE", "HAVING", "ON", "AND", "OR", "NOT", "SELECT", "WHEN", "THEN", "ELSE", "SET",
            // A source keyword opens a derived table: "FROM (SELECT ...", "JOIN (SELECT ...",
            // "OUTER APPLY (SELECT ...".  They used to be read as calls named FROM / JOIN / APPLY, so
            // the popup offered function arguments and data types instead of the tables of the inner
            // query.  The same reading applies to the other clauses that own a parenthesis.
            "FROM", "JOIN", "APPLY", "USING", "INTO", "MERGE", "PIVOT", "UNPIVOT", "TABLESAMPLE",
            "WITH", "OPTION"
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
            // One letter is enough: the starters are a curated list, and requiring two made the popup
            // stay in the EXEC scope while the user typed the first character of the next statement
            // (typing "S" after a parameter list still offered parameters instead of SELECT).
            bool statementPrefix = prefix.Length >= 1 && DetachedStatementStarters.Any(s =>
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

        /// <summary>
        /// True when the typed word has just completed a snippet prefix that the cached list cannot
        /// show.  The editor serves a word-only keystroke from the candidates built before that
        /// letter existed, and a snippet is only added when the analyzer reads the whole prefix, so
        /// "OUTER APPLY (" cached at the parenthesis never contained "SSF"; typing the F kept
        /// filtering that list and only Ctrl+Space (a full analysis) surfaced the snippet.
        /// </summary>
        internal static bool NeedsSnippetReanalysis(string prefix, bool snippetsEnabled,
            IEnumerable<CompletionItem> cachedItems, IEnumerable<SnippetItem> snippets)
            => IsExactSnippetPrefix(prefix, snippetsEnabled, snippets)
               && !(cachedItems ?? Enumerable.Empty<CompletionItem>()).Any(i => i != null
                   && i.Kind == CompletionItemKind.Snippet
                   && string.Equals(i.DisplayText, prefix, StringComparison.OrdinalIgnoreCase));

        // Settings are read once per BuildItems call; per-item helpers (Quote,
        // MakeAlias, join rules) consume this instance instead of re-reading
        // settings for every suggested candidate. The dialog always saves a
        // fresh instance, so per-call reads pick changes up immediately.
        [ThreadStatic] private static SettingsManager.SqlCompletionSettings threadCompletionSettings;

        private static SettingsManager.SqlCompletionSettings CompletionSettings()
            => threadCompletionSettings ?? SettingsManager.GetSqlCompletionSettings();

        public static List<CompletionItem> BuildItems(CompletionContext context, MetadataSnapshot metadata)
            => FilterCandidates(BuildCandidates(context, metadata), context?.Prefix);

        /// <summary>
        /// Every candidate the context can offer, independent of the typed prefix.  Caching this
        /// list lets the editor re-filter on the UI thread for each keystroke instead of re-running
        /// the parser and rebuilding the whole candidate set.
        /// </summary>
        internal static List<CompletionItem> BuildCandidates(CompletionContext context, MetadataSnapshot metadata)
        {
            metadata = metadata ?? MetadataSnapshot.Empty;
            var result = new List<CompletionItem>();
            if (!string.IsNullOrEmpty(context.ExactSnippetPrefix))
            {
                // The snippet the user already named in full goes on top of what the position allows.
                SnippetItem exact = SnippetService.GetAllSnippets()
                    .FirstOrDefault(s => string.Equals(s.Prefix, context.ExactSnippetPrefix, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    result.Add(new CompletionItem
                    {
                        DisplayText = exact.Prefix, InsertText = exact.Body, Kind = CompletionItemKind.Snippet,
                        Description = exact.Description, Score = 300
                    });
            }
            // Local (temp/CTE) tables are usually absent; only build the combined list when there
            // is something to combine.
            List<DatabaseObjectMetadata> objects = context.LocalObjects.Count == 0
                ? metadata.Objects
                : metadata.Objects.Concat(context.LocalObjects).ToList();
            try
            {
                threadCompletionSettings = SettingsManager.GetSqlCompletionSettings();
                HydrateProjectedColumns(metadata, context.LocalObjects);
                AddDeclaredVariables(result, context);
                switch (context.Kind)
                {
                    case CompletionContextKind.ExecuteObject:
                        result.AddRange(metadata.GetIndex().Procedures.Where(o => MatchesContainer(o, context.Qualifier)).Select(o => ObjectItem(o, o.IsSystem ? 25 : 120, !string.IsNullOrEmpty(context.Qualifier)))); break;
                    case CompletionContextKind.ExecuteArguments: AddParameters(result, context, metadata); break;
                    case CompletionContextKind.ExecuteArgumentValue: AddValues(result, context, metadata); break;
                    case CompletionContextKind.FunctionArguments: AddFunctionArguments(result, context, metadata, objects); break;
                    case CompletionContextKind.Member:
                        if (context.IsJoinSource) result.AddRange(BuildJoinItems(context, metadata));
                        else AddMembers(result, context, objects, metadata);
                        break;
                    case CompletionContextKind.Join: result.AddRange(BuildJoinItems(context, metadata)); AddCteNames(result, context, 100); break;
                    case CompletionContextKind.InsertColumns:
                    case CompletionContextKind.UpdateSet: AddWritableColumns(result, context, metadata); break;
                    case CompletionContextKind.InsertBody: AddInsertTemplate(result, context, metadata); break;
                    case CompletionContextKind.DataSource:
                        AddDataSourceItems(result, context, metadata);
                        break;
                    case CompletionContextKind.TableSourceTail:
                    case CompletionContextKind.JoinSourceTail:
                        // A source slot takes the next join, a hint, an alias or the next clause.  A
                        // column cannot follow a source here (its ON operands are reached through
                        // the predicate context), so offering one only added noise: "FROM dbo.T t
                        // LJ" used to suggest the table's LXTJGID through the fuzzy channel.
                        AddTableSourceTailItems(result, context, metadata);
                        break;
                    case CompletionContextKind.TableHint: AddTableHintBodyItems(result); break;
                    case CompletionContextKind.QueryHint: AddQueryHintItems(result); break;
                    case CompletionContextKind.MergeSource: result.AddRange(objects.Where(IsDataSource).Select(o => ObjectItem(o, 55, false))); break;
                    case CompletionContextKind.Predicate:
                        AddScopedColumns(result, context, metadata, 85, false);
                        // "UPDATE dbo.T SET ... WHERE |" has no FROM clause, so the target table is the
                        // only source of columns there.
                        if (!string.IsNullOrEmpty(context.TargetObject)) AddWritableColumns(result, context, metadata);
                        AddPredicateItems(result, context);
                        AddTypedValues(result, context, metadata);
                        break;
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

                // "FROM dbo.A a JOIN dbo.B b |": everything the statement can say next hinges on ON,
                // so it outranks the columns instead of being one keyword among many.
                if (context.AwaitingJoinPredicate) result.Add(JoinPredicateItem(context));

                // Data types belong wherever an expression or a declaration can appear: DECLARE, CAST and
                // CONVERT targets, column definitions.  They do not belong in a WHERE/ON/HAVING
                // predicate list.
                if (context.Kind != CompletionContextKind.Member
                    && context.Kind != CompletionContextKind.DataSource
                    && context.Kind != CompletionContextKind.Predicate
                    && context.Kind != CompletionContextKind.TableHint
                    && context.Kind != CompletionContextKind.QueryHint)
                    result.AddRange(DataTypes.Select(t => new CompletionItem { DisplayText = t, InsertText = t, Kind = CompletionItemKind.Type, Description = "data type", Score = 18 }));
            }
            finally { threadCompletionSettings = null; }
            foreach (CompletionItem item in result) item.ScopeKey = context.MetadataScope;
            return MergeDuplicates(result);
        }

        /// <summary>
        /// One row per name.  In a SELECT list SUM used to appear three times — the snippet "SUM(",
        /// the built-in function and the keyword — and only the function carried the usage text, so
        /// the list looked broken.  Language elements (keyword, function, snippet) merge on the bare
        /// name; the highest score wins (the snippet that inserts the parentheses) and the
        /// documentation is carried across from whichever duplicate has it.
        /// Columns, tables and other name spaces keep their own key: a column called SUM is not the
        /// SUM function.
        /// </summary>
        private static List<CompletionItem> MergeDuplicates(List<CompletionItem> items)
        {
            var order = new List<string>();
            var byKey = new Dictionary<string, CompletionItem>(StringComparer.OrdinalIgnoreCase);
            foreach (CompletionItem item in items)
            {
                if (item == null) continue;
                string key = DuplicateKey(item);
                CompletionItem existing;
                if (!byKey.TryGetValue(key, out existing))
                {
                    byKey[key] = item;
                    order.Add(key);
                    continue;
                }

                CompletionItem keep = existing.Score >= item.Score ? existing : item;
                CompletionItem drop = ReferenceEquals(keep, existing) ? item : existing;
                if (string.IsNullOrWhiteSpace(keep.DetailText) && !string.IsNullOrWhiteSpace(drop.DetailText))
                {
                    keep.DetailText = drop.DetailText;
                    keep.Description = drop.Description;
                }
                byKey[key] = keep;
            }

            var merged = new List<CompletionItem>(order.Count);
            foreach (string key in order) merged.Add(byKey[key]);
            return merged;
        }

        private static string DuplicateKey(CompletionItem item)
        {
            string text = item.DisplayText ?? string.Empty;
            bool languageElement = item.Kind == CompletionItemKind.Keyword
                                   || item.Kind == CompletionItemKind.Function
                                   || item.Kind == CompletionItemKind.Snippet;
            if (!languageElement) return item.Kind + "|" + text;
            // Only a snippet spells the call form in its display text ("SUM("); a hint keeps its
            // parentheses ("WITH (NOLOCK)") or every hint would collapse into one "WITH" row.
            if (item.Kind == CompletionItemKind.Snippet)
            {
                int paren = text.IndexOf('(');
                if (paren > 0) text = text.Substring(0, paren);
            }
            return "L|" + text.Trim();
        }

        /// <summary>Prefix filter and ranking over an already-built candidate list.</summary>
        internal static List<CompletionItem> FilterCandidates(IEnumerable<CompletionItem> candidates, string prefix)
            => FilterAndSort(candidates ?? Enumerable.Empty<CompletionItem>(), prefix);

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
                result.Add(TableFunctionItem(name));
            if (compatibilityLevel >= 130)
                foreach (string name in new[] { "OPENJSON", "STRING_SPLIT" })
                    result.Add(TableFunctionItem(name));
            if (compatibilityLevel >= 160)
                result.Add(TableFunctionItem("GENERATE_SERIES"));
        }

        /// <summary>
        /// A built-in table-valued function carries the same usage note as a scalar function: the
        /// signature says it is called in FROM, and the example shows the WITH column clause.
        /// </summary>
        private static CompletionItem TableFunctionItem(string name) => new CompletionItem
        {
            DisplayText = name,
            InsertText = name,
            Kind = CompletionItemKind.Function,
            Description = "SQL Server table-valued function",
            DetailTextProvider = () => FunctionDocumentation(name),
            Score = 48
        };

        private static void AddSelectListItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            AddGeneral(result, context, metadata, objects);
            AddScopedColumns(result, context, metadata, 95);
            // Only the caret query's own FROM feeds SELECT *; outer aliases stay visible for
            // correlated references but must not be swept into the expansion.
            List<KeyValuePair<string, string>> sources = OwnScopeSources(context);
            if (sources.Count == 0) sources = ScopedSources(context);
            var columns = new List<string>();
            bool qualify = sources.Count > 1;
            foreach (var source in sources)
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
            var target = FindObject(context, metadata, context.TargetObject);
            if (target == null) target = FindObject(context, metadata, ModelTarget(context));
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
        {
            DatabaseObjectMetadata target = FindObject(context, metadata, context.TargetObject);
            if (target == null) target = FindObject(context, metadata, ModelTarget(context));
            return target;
        }

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

        /// <summary>
        /// Sources visible at the caret according to the shared document model.  The heuristic
        /// alias map is built from the text before the parser runs, so it can miss an alias that
        /// is declared inside a nested subquery; the model knows the declaration depth.
        /// </summary>
        private static List<KeyValuePair<string, string>> ModelScopeSources(CompletionContext context)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (context == null || string.IsNullOrEmpty(context.DocumentText)) return result;
            // Completion runs on the UI thread; the model build is linear but this keeps a huge
            // script from paying for it during a keystroke.  Analysis already owns the big-file path.
            if (context.DocumentText.Length > 150000) return result;
            int caret = context.CaretOffset;
            if (caret < 0 || caret > context.DocumentText.Length) caret = context.DocumentText.Length;
            try
            {
                SqlDocumentModel model = SqlDocumentModel.Get(context.DocumentText);
                SqlStatementRange statement = model.StatementAt(caret);
                if (statement == null) return result;
                SqlScopeTree scope = model.ScopeOf(statement);
                foreach (SqlSource source in scope.VisibleAt(caret))
                {
                    if (string.IsNullOrEmpty(source.Alias) || string.IsNullOrEmpty(source.ObjectName)) continue;
                    result.Add(new KeyValuePair<string, string>(source.Alias, source.ObjectName));
                    string bare = LastPart(source.ObjectName);
                    if (!string.IsNullOrEmpty(bare) && !string.Equals(bare, source.Alias, StringComparison.OrdinalIgnoreCase))
                        result.Add(new KeyValuePair<string, string>(bare, source.ObjectName));
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug("COMPLETION model scope fallback failed: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// The table the caret statement writes to, from the token scanner.  Used only when the
        /// regex inference produced a name that metadata cannot resolve (bracketed names, a CTE
        /// before the INSERT, a half-typed statement).
        /// </summary>
        private static string ModelTarget(CompletionContext context, SqlTargetKind? expected = null)
        {
            if (context == null || string.IsNullOrEmpty(context.DocumentText)) return null;
            if (context.DocumentText.Length > 150000) return null;
            try
            {
                SqlDocumentModel model = SqlDocumentModel.Get(context.DocumentText);
                int caret = context.CaretOffset;
                if (caret < 0 || caret > context.DocumentText.Length) caret = context.DocumentText.Length;
                SqlStatementRange statement = model.StatementAt(caret);
                if (statement == null) return null;
                SqlTargetReference target = SqlTargetScanner.Find(context.DocumentText, statement.Start, statement.Length);
                if (target == null) return null;
                if (expected.HasValue && target.Kind != expected.Value) return null;
                return target.Name;
            }
            catch (Exception) { return null; }
        }

        private static SqlSource ResolveModelSource(CompletionContext context, string name)
        {
            if (context == null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(context.DocumentText)) return null;
            if (context.DocumentText.Length > 150000) return null;
            int caret = context.CaretOffset;
            if (caret < 0 || caret > context.DocumentText.Length) caret = context.DocumentText.Length;
            try
            {
                SqlDocumentModel model = SqlDocumentModel.Get(context.DocumentText);
                SqlStatementRange statement = model.StatementAt(caret);
                if (statement == null) return null;
                return model.ScopeOf(statement).Resolve(name, caret);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The alias map with the model's scoped sources merged in, collapsed to one entry per
        /// object.  Shared by every context that lists scoped columns, so a nested subquery sees
        /// the same set whether it completes a member, a predicate or a SELECT list.
        /// </summary>
        private static List<KeyValuePair<string, string>> ScopedSources(CompletionContext context)
        {
            // The parser knows which aliases are in scope at the caret.  Prefer that answer when it
            // has one: the heuristic map keeps aliases from sibling subqueries, which used to leak
            // columns into an outer SELECT after the nested query ended.  Only fall back to the
            // text map when the statement is too incomplete for the scope tree to contribute.
            List<KeyValuePair<string, string>> modelSources = ModelScopeSources(context);
            var candidates = modelSources.Count > 0
                ? new List<KeyValuePair<string, string>>(modelSources)
                : new List<KeyValuePair<string, string>>(context.Aliases);
            // The parser's scope tree is the authority whenever it contributed a source.  Merging
            // the text alias map back in used to keep a same-table alias from a nested subquery,
            // which made "i.Id" suggest columns that are out of scope at the caret.

            return CollapseScopes(candidates);
        }

        /// <summary>
        /// Sources of the query block that owns the caret, without the outer scopes a correlated
        /// subquery can merely reference.  SELECT * must expand only what its own FROM declares:
        /// inside OUTER APPLY (SELECT * FROM B WHERE A.x = B.x) the star is B's columns, not A's.
        /// </summary>
        private static List<KeyValuePair<string, string>> OwnScopeSources(CompletionContext context)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (context == null || string.IsNullOrEmpty(context.DocumentText)) return result;
            if (context.DocumentText.Length > 150000) return result;
            int caret = context.CaretOffset;
            if (caret < 0 || caret > context.DocumentText.Length) caret = context.DocumentText.Length;
            try
            {
                SqlDocumentModel model = SqlDocumentModel.Get(context.DocumentText);
                SqlStatementRange statement = model.StatementAt(caret);
                if (statement == null) return result;
                SqlScopeTree scope = model.ScopeOf(statement);
                foreach (SqlSource source in scope.OwnSourcesAt(caret))
                {
                    if (string.IsNullOrEmpty(source.Alias) || string.IsNullOrEmpty(source.ObjectName)) continue;
                    result.Add(new KeyValuePair<string, string>(source.Alias, source.ObjectName));
                    string bare = LastPart(source.ObjectName);
                    if (!string.IsNullOrEmpty(bare) && !string.Equals(bare, source.Alias, StringComparison.OrdinalIgnoreCase))
                        result.Add(new KeyValuePair<string, string>(bare, source.ObjectName));
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug("COMPLETION own scope failed: " + ex.Message);
            }
            return CollapseScopes(result);
        }

        private static List<KeyValuePair<string, string>> CollapseScopes(List<KeyValuePair<string, string>> candidates)
        {
            var scopes = new List<KeyValuePair<string, string>>();
            foreach (var source in candidates)
            {
                int existing = scopes.FindIndex(s => string.Equals(s.Value, source.Value, StringComparison.OrdinalIgnoreCase));
                if (existing < 0) { scopes.Add(source); continue; }

                bool existingIsTableName = string.Equals(LastPart(scopes[existing].Value), scopes[existing].Key, StringComparison.OrdinalIgnoreCase);
                bool candidateIsAlias = !string.Equals(LastPart(source.Value), source.Key, StringComparison.OrdinalIgnoreCase);
                if (existingIsTableName && candidateIsAlias) scopes[existing] = source;
            }
            return scopes;
        }

        private static void AddScopedColumns(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, int score, bool excludeReferenced = false)
        {
            // The alias map holds both "A" and "YGDAH" for the same table (the bare name is registered so
            // that "YGDAH." works without an alias).  Collapse to one entry per object and prefer the
            // short user alias.
            var scopes = ScopedSources(context);
            // The alias map can hold several keys for one table ("A" and the bare name), and the same
            // alias may appear with and without the schema.  Counting those as separate sources used
            // to qualify every column — a unique "KSBM" came out as "WDDDB.KSBM" just because the
            // statement happened to read two tables.  Collapse by the object each entry resolves to.
            var resolved = new List<KeyValuePair<string, DatabaseObjectMetadata>>();
            var seenObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in scopes)
            {
                DatabaseObjectMetadata obj = FindObject(context, metadata, source.Value);
                if (obj == null) continue;
                if (!seenObjects.Add(CleanQualified(obj.QualifiedName))) continue;
                resolved.Add(new KeyValuePair<string, DatabaseObjectMetadata>(source.Key, obj));
            }
            if (resolved.Count == 0) return;

            // Only a name that two in-scope tables both own needs the qualifier; a unique column
            // reads better bare and stays valid SQL.
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resolved.Count > 1)
            {
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in resolved)
                    foreach (ColumnMetadata column in source.Value.Columns)
                        if (!seenNames.Add(column.Name)) ambiguous.Add(column.Name);
            }

            foreach (var source in resolved)
            {
                DatabaseObjectMetadata obj = source.Value;
                foreach (ColumnMetadata column in obj.Columns)
                {
                    if (excludeReferenced && IsGroupedColumn(context, source.Key, obj.QualifiedName, column.Name)) continue;
                    CompletionItem item = ColumnItem(column, score, obj);
                    if (ambiguous.Contains(column.Name))
                    {
                        item.InsertText = Quote(source.Key) + "." + Quote(column.Name);
                        item.DisplayText = source.Key + "." + column.Name;
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

        private static void AddPredicateItems(List<CompletionItem> result, CompletionContext context)
        {
            switch (context.PredicateState)
            {
                case SqlPredicateState.LogicalExpected:
                    // The predicate is complete: the next useful tokens are the boolean connectors
                    // and the clause that follows the WHERE/ON/HAVING block.
                    AddKeyword(result, "AND", 126, "boolean operator");
                    AddKeyword(result, "OR", 124, "boolean operator");
                    if (context.PredicateDepth > 0) AddKeyword(result, ")", 118, "close the predicate group");
                    if (string.Equals(context.PredicateOwner, "ON", StringComparison.OrdinalIgnoreCase))
                    {
                        // A finished ON predicate can be followed by another join, which is why the
                        // join forms belong here; GROUP BY / ORDER BY / HAVING cannot follow a join
                        // condition, so they are replaced instead of ranked lower.
                        AddJoinContinuationItems(result, 104);
                        AddKeyword(result, "WHERE", 82, "clause");
                    }
                    else
                    {
                        AddKeyword(result, "GROUP BY", 78, "clause");
                        AddKeyword(result, "ORDER BY", 78, "clause");
                        AddKeyword(result, "HAVING", 62, "clause");
                    }
                    AddKeyword(result, "UNION", 52, "set operator");
                    AddKeyword(result, "UNION ALL", 50, "set operator");
                    AddKeyword(result, "OPTION", 38, "query hint");
                    AddKeyword(result, "FOR JSON", 30, "output clause");
                    AddKeyword(result, "FOR XML", 30, "output clause");
                    break;
                case SqlPredicateState.BetweenLowerBound:
                    AddKeyword(result, "AND", 132, "BETWEEN range");
                    break;
                case SqlPredicateState.OperatorExpected:
                    AddComparisonOperators(result);
                    break;
                case SqlPredicateState.OperandExpected:
                    AddOperandStarters(result);
                    // Operators stay available but rank below columns/functions here; the existing
                    // "WHERE |" behaviour relied on them being reachable.
                    AddComparisonOperators(result, -70);
                    break;
                default:
                    AddComparisonOperators(result);
                    AddOperandStarters(result);
                    break;
            }

            if (context.PredicateState == SqlPredicateState.OperandExpected
                || context.PredicateState == SqlPredicateState.Unknown)
            {
                // Predicate operands may be function calls; without these, "WHERE ISN" offered
                // nothing even though ISNULL is one of the most common predicates in T-SQL.
                foreach (string name in BuiltInFunctions)
                    result.Add(new CompletionItem
                    {
                        DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function,
                        Description = FunctionSignature(name), Score = 45
                    });
            }
        }

        private static void AddComparisonOperators(List<CompletionItem> result, int scoreOffset = 0)
        {
            AddKeyword(result, "=", 112 + scoreOffset, "comparison");
            AddKeyword(result, "<>", 110 + scoreOffset, "comparison");
            AddKeyword(result, ">", 108 + scoreOffset, "comparison");
            AddKeyword(result, ">=", 108 + scoreOffset, "comparison");
            AddKeyword(result, "<", 108 + scoreOffset, "comparison");
            AddKeyword(result, "<=", 108 + scoreOffset, "comparison");
            AddKeyword(result, "LIKE", 106 + scoreOffset, "pattern match");
            AddKeyword(result, "IS NULL", 106 + scoreOffset, "null test");
            AddKeyword(result, "IS NOT NULL", 106 + scoreOffset, "null test");
            AddKeyword(result, "IN", 104 + scoreOffset, "set membership");
            AddKeyword(result, "BETWEEN", 102 + scoreOffset, "range");
            AddKeyword(result, "NOT LIKE", 98 + scoreOffset, "negated pattern match");
            AddKeyword(result, "NOT IN", 96 + scoreOffset, "negated set membership");
            AddKeyword(result, "NOT BETWEEN", 94 + scoreOffset, "negated range");
        }

        private static void AddOperandStarters(List<CompletionItem> result)
        {
            AddKeyword(result, "NOT", 95, "negation");
            AddKeyword(result, "EXISTS", 90, "subquery test");
            AddKeyword(result, "(", 88, "predicate group");
            AddKeyword(result, "NULL", 70, "null literal");
            AddKeyword(result, "CASE", 60, "conditional expression");
        }

        private static void AddKeyword(List<CompletionItem> result, string text, int score, string description)
        {
            result.Add(new CompletionItem
            {
                DisplayText = text, InsertText = text, Kind = CompletionItemKind.Keyword,
                Description = description, Score = score
            });
        }

        private static void AddSelectAliases(List<CompletionItem> result, CompletionContext context)
        {
            var aliases = new List<string>();
            foreach (string alias in ModelSelectAliases(context)) AddAlias(aliases, alias);
            foreach (string semanticAlias in context.SelectAliases) AddAlias(aliases, semanticAlias);

            // Text fallback only when neither the model nor the parser found an alias; it is what
            // keeps "expr AS name" working in a half-typed statement.
            if (aliases.Count == 0)
            {
                Match select = SelectFromListPattern.Match(context.MaskedStatement ?? SqlTextContext.MaskCommentsAndStrings(context.CurrentStatement ?? string.Empty));
                if (select.Success)
                {
                    foreach (string expression in SplitTopLevel(select.Groups["list"].Value))
                    {
                        Match alias = SelectAliasInListPattern.Match(expression);
                        if (!alias.Success) continue;
                        string aliasName = alias.Groups["equals"].Success ? Clean(alias.Groups["equals"].Value) : Clean(alias.Groups["name"].Value);
                        AddAlias(aliases, aliasName);
                    }
                }
            }

            foreach (string aliasName in aliases)
                result.Add(new CompletionItem { DisplayText = aliasName, InsertText = Quote(aliasName), Kind = CompletionItemKind.Column, Description = "SELECT alias", Score = 105 });
        }

        private static void AddAlias(List<string> aliases, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (aliases.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase))) return;
            aliases.Add(value);
        }

        private static List<string> ModelSelectAliases(CompletionContext context)
        {
            var result = new List<string>();
            if (context == null || string.IsNullOrEmpty(context.DocumentText)) return result;
            if (context.DocumentText.Length > 150000) return result;
            int caret = context.CaretOffset;
            if (caret < 0 || caret > context.DocumentText.Length) caret = context.DocumentText.Length;
            try
            {
                SqlDocumentModel model = SqlDocumentModel.Get(context.DocumentText);
                SqlStatementRange statement = model.StatementAt(caret);
                if (statement == null) return result;
                result.AddRange(model.ScopeOf(statement).SelectAliases);
            }
            catch (Exception) { }
            return result;
        }

        /// <summary>
        /// The procedure whose parameters are being completed.  The regex-derived target wins
        /// when metadata knows it; otherwise the EXEC target scanned from the caret statement is
        /// used, which covers bracketed names and EXEC preceded by other tokens.
        /// </summary>
        private static string RoutineTarget(CompletionContext context, MetadataSnapshot metadata)
        {
            string target = context.TargetObject;
            if (!string.IsNullOrWhiteSpace(target)
                && metadata.Parameters.Any(p => ObjectMatches(p.ObjectName, target))) return target;
            return ModelTarget(context, SqlTargetKind.Exec) ?? target;
        }

        private static void AddParameters(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            string routine = RoutineTarget(context, metadata);
            var parameters = metadata.Parameters.Where(p => ObjectMatches(p.ObjectName, routine) && !context.UsedParameters.Contains(p.Name) && !string.IsNullOrEmpty(p.Name)).ToList();
            result.AddRange(parameters.Select(p => new CompletionItem { DisplayText = p.Name, InsertText = p.Name + " = ", Kind = CompletionItemKind.Parameter, Description = p.TypeDisplay + (p.HasDefaultValue ? " optional" : string.Empty) + (p.IsOutput ? " OUTPUT" : string.Empty), Score = 100 }));
            if (parameters.Count > 1 && context.UsedParameters.Count == 0) result.Add(new CompletionItem { DisplayText = "(all parameters)", InsertText = string.Join(", ", parameters.Select((p, i) => p.Name + " = ${" + (i + 1) + ":" + (p.HasDefaultValue ? "DEFAULT" : "NULL") + "}" + (p.IsOutput ? " OUTPUT" : string.Empty))), Kind = CompletionItemKind.Snippet, Description = parameters.Count + " parameters", Score = 130 });
        }

        private static void AddValues(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var matching = metadata.Parameters.Where(x => ObjectMatches(x.ObjectName, RoutineTarget(context, metadata))).OrderBy(x => x.Ordinal).ToList();
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
            if (obj == null)
            {
                SqlSource scoped = ResolveModelSource(context, context.Qualifier);
                if (scoped != null) obj = FindObject(context, metadata, scoped.ObjectName);
            }
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
            if (target == null) target = FindObject(context, metadata, ModelTarget(context));
            if (target == null)
            {
                // The target may be a temp table, a name the metadata has not seen, or an alias the
                // parser could not resolve; offering the columns of the tables in scope beats offering
                // nothing at all.
                AddScopedColumns(result, context, metadata, 90);
                return;
            }
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
            if (target == null) target = FindObject(context, metadata, ModelTarget(context));
            if (target == null)
            {
                // The target may be a temp table, a name the metadata has not seen, or an alias the
                // parser could not resolve; offering the columns of the tables in scope beats offering
                // nothing at all.
                AddScopedColumns(result, context, metadata, 90);
                return;
            }
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
            result.AddRange(BuiltInFunctions.Select(name => new CompletionItem
            {
                DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function,
                // Built on first read: a select list carries ~200 candidates and only the selected
                // row ever asks for its text.
                DescriptionProvider = () => FunctionSignature(name),
                DetailTextProvider = () => FunctionDocumentation(name), Score = 35
            }));
            result.AddRange(AllKeywords(CompletionSettings()).Select(k => new CompletionItem
            {
                DisplayText = k, InsertText = k, Kind = CompletionItemKind.Keyword,
                DescriptionProvider = () => KeywordDescription(k), Score = 20
            }));
            foreach (var s in SnippetService.GetAllSnippets()) result.Add(new CompletionItem { DisplayText = s.Prefix, InsertText = s.Body, Kind = CompletionItemKind.Snippet, Description = s.Description, Score = 30 });
        }

        /// <summary>
        /// Data types offered as completions (kind Type).  They belong in type positions: a declaration,
        /// a CAST/CONVERT target or a CREATE/ALTER TABLE column.
        /// </summary>
        internal static readonly string[] DataTypes = {
            "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT", "DECIMAL(18, 2)", "NUMERIC(18, 2)", "MONEY", "SMALLMONEY", "FLOAT", "REAL",
            "DATE", "TIME", "DATETIME", "DATETIME2", "SMALLDATETIME", "DATETIMEOFFSET",
            "CHAR(10)", "VARCHAR(50)", "VARCHAR(MAX)", "NCHAR(10)", "NVARCHAR(50)", "NVARCHAR(MAX)", "TEXT", "NTEXT",
            "BINARY(16)", "VARBINARY(50)", "VARBINARY(MAX)", "IMAGE", "UNIQUEIDENTIFIER", "XML", "SQL_VARIANT", "ROWVERSION",
            "HIERARCHYID", "GEOGRAPHY", "GEOMETRY", "TABLE"
        };

        /// <summary>
        /// Built-in keywords plus the user's own list from the completion settings, de-duplicated.  The
        /// extra list is what makes the keyword coverage a configuration rather than a fixed table.
        /// The result only changes when that setting changes, so it is cached by the setting text:
        /// re-trimming and re-hashing ~150 names on every keystroke was pure waste.
        /// </summary>
        internal static IEnumerable<string> AllKeywords(SettingsManager.SqlCompletionSettings settings)
            => KeywordsFor(settings?.additionalKeywords);

        private sealed class KeywordTable
        {
            public string Extra;
            public string[] Words;
        }

        private static volatile KeywordTable keywordTable;

        internal static string[] KeywordsFor(string extra)
        {
            string key = extra ?? string.Empty;
            KeywordTable cached = keywordTable;
            if (cached != null && string.Equals(cached.Extra, key, StringComparison.Ordinal)) return cached.Words;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var words = new List<string>();
            foreach (string keyword in Keywords)
                if (!string.IsNullOrWhiteSpace(keyword) && seen.Add(keyword.Trim()))
                    words.Add(keyword.Trim());

            if (!string.IsNullOrWhiteSpace(extra))
            {
                foreach (string keyword in extra.Split(new[] { ';', '\r', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = keyword.Trim();
                    // The list is compared as one whole keyword, so a trailing separator would look wrong.
                    trimmed = trimmed.TrimEnd(';', '|', ',').Trim();
                    if (trimmed.Length == 0 || trimmed.Length > 200) continue;
                    if (seen.Add(trimmed)) words.Add(trimmed);
                }
            }

            string[] built = words.ToArray();
            keywordTable = new KeywordTable { Extra = key, Words = built };
            return built;
        }

        private static string KeywordDescription(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return "keyword";
            if (keyword.StartsWith("WITH (", StringComparison.OrdinalIgnoreCase)
                || keyword.StartsWith("WITH (NOLOCK", StringComparison.OrdinalIgnoreCase)) return "table hint";
            if (keyword.StartsWith("OPTION (", StringComparison.OrdinalIgnoreCase)) return "query hint";
            if (keyword.StartsWith("@@", StringComparison.Ordinal)) return "system function";
            return "keyword";
        }

        /// <summary>
        /// Signature, a short note and a copy-ready example for the built-in functions offered by the
        /// popup.  The signature stays in the list; the whole entry is shown in the details panel so the
        /// user can see how a function is called before inserting it.
        /// </summary>
        private static readonly Dictionary<string, string[]> FunctionDocs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ABS"] = new[] { "ABS(numeric_expression)", "absolute value", "SELECT ABS(-12.5);  -- 12.5" },
            ["AVG"] = new[] { "AVG([ALL|DISTINCT] expression)", "average of a group, ignores NULL", "SELECT AVG(UnitPrice) FROM dbo.SalesOrderLine;" },
            ["CAST"] = new[] { "CAST(expression AS data_type[(length)])", "converts to another type; error when the value does not fit", "SELECT CAST('2026-01-31' AS date);" },
            ["CEILING"] = new[] { "CEILING(numeric_expression)", "smallest integer >= the value", "SELECT CEILING(4.2);  -- 5" },
            ["CHARINDEX"] = new[] { "CHARINDEX(substring, string [, start_location])", "1-based position, 0 when not found", "SELECT CHARINDEX('bb', 'aabbcc');  -- 3" },
            ["COALESCE"] = new[] { "COALESCE(expression, expression [, ...])", "first non-NULL argument", "SELECT COALESCE(NULL, NULL, 'fallback');" },
            ["CONCAT"] = new[] { "CONCAT(value [, value ...])", "joins values, NULL becomes an empty string", "SELECT CONCAT('Order ', OrderId, ' / ', Status) FROM dbo.SalesOrderHeader;" },
            ["CONVERT"] = new[] { "CONVERT(data_type[(length)], expression [, style])", "like CAST, plus style codes for dates and money", "SELECT CONVERT(varchar(10), GETDATE(), 120);  -- yyyy-mm-dd" },
            ["COUNT"] = new[] { "COUNT([ALL|DISTINCT] expression) | COUNT(*)", "number of rows, COUNT(*) includes NULLs", "SELECT COUNT(*) FROM dbo.SalesOrderHeader WHERE Status = 'Open';" },
            ["DATEADD"] = new[] { "DATEADD(datepart, number, date)", "adds an interval; datepart is year/month/day/hour/minute/second", "SELECT DATEADD(day, -7, GETDATE());" },
            ["DATEDIFF"] = new[] { "DATEDIFF(datepart, startdate, enddate)", "difference in whole dateparts", "SELECT DATEDIFF(day, OrderDate, ShipDate) FROM dbo.SalesOrderHeader;" },
            ["DATENAME"] = new[] { "DATENAME(datepart, date)", "name of the datepart as text", "SELECT DATENAME(month, GETDATE());  -- January" },
            ["DATEPART"] = new[] { "DATEPART(datepart, date)", "datepart as an integer", "SELECT DATEPART(quarter, OrderDate) FROM dbo.SalesOrderHeader;" },
            ["EOMONTH"] = new[] { "EOMONTH(start_date [, month_to_add])", "last day of the month", "SELECT EOMONTH(GETDATE());" },
            ["FLOOR"] = new[] { "FLOOR(numeric_expression)", "largest integer <= the value", "SELECT FLOOR(4.8);  -- 4" },
            ["FORMAT"] = new[] { "FORMAT(value, format [, culture])", "formats using .NET format strings (slower than CONVERT)", "SELECT FORMAT(GETDATE(), 'yyyy-MM-dd HH:mm');" },
            ["GETDATE"] = new[] { "GETDATE()", "current date and time of the server", "SELECT GETDATE();" },
            ["GETUTCDATE"] = new[] { "GETUTCDATE()", "current UTC date and time", "SELECT GETUTCDATE();" },
            ["IIF"] = new[] { "IIF(condition, true_value, false_value)", "inline if/else", "SELECT IIF(Status = 'Open', 1, 0) FROM dbo.SalesOrderHeader;" },
            ["ISNULL"] = new[] { "ISNULL(check_expression, replacement_value)", "replaces NULL (two arguments only)", "SELECT ISNULL(Comment, '') FROM dbo.SalesOrderHeader;" },
            ["LAG"] = new[] { "LAG(expression [, offset [, default]]) OVER (ORDER BY ...)", "previous row in the window", "SELECT OrderId, LAG(OrderId) OVER (ORDER BY OrderId) AS PreviousId FROM dbo.SalesOrderHeader;" },
            ["LEAD"] = new[] { "LEAD(expression [, offset [, default]]) OVER (ORDER BY ...)", "next row in the window", "SELECT OrderId, LEAD(OrderId) OVER (ORDER BY OrderId) AS NextId FROM dbo.SalesOrderHeader;" },
            ["LEFT"] = new[] { "LEFT(string, number_of_characters)", "first n characters", "SELECT LEFT('MSSQLTOOL', 4);  -- MSSQ" },
            ["LEN"] = new[] { "LEN(string)", "length without trailing spaces", "SELECT LEN('abc  ');  -- 3" },
            ["LOWER"] = new[] { "LOWER(string)", "lower case", "SELECT LOWER('MSSQLTool');  -- mssqltool" },
            ["NEWID"] = new[] { "NEWID()", "new uniqueidentifier", "SELECT NEWID();" },
            ["NULLIF"] = new[] { "NULLIF(expression, expression)", "NULL when both are equal", "SELECT NULLIF(Quantity, 0) FROM dbo.SalesOrderLine;" },
            ["PATINDEX"] = new[] { "PATINDEX('%pattern%', string)", "1-based position of a LIKE pattern, 0 when absent", "SELECT PATINDEX('%bb%', 'aabbcc');  -- 3" },
            ["REPLACE"] = new[] { "REPLACE(string, old, new)", "replaces every occurrence", "SELECT REPLACE('a-b-c', '-', '/');  -- a/b/c" },
            ["ROUND"] = new[] { "ROUND(numeric, length [, function])", "rounds to length digits", "SELECT ROUND(12.3456, 2);  -- 12.3500" },
            ["ROW_NUMBER"] = new[] { "ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)", "1-based row number inside the window", "SELECT ROW_NUMBER() OVER (ORDER BY OrderDate DESC) AS RowNo, OrderId FROM dbo.SalesOrderHeader;" },
            ["RTRIM"] = new[] { "RTRIM(string)", "removes trailing spaces", "SELECT RTRIM('abc   ');" },
            ["SCOPE_IDENTITY"] = new[] { "SCOPE_IDENTITY()", "last identity value inserted in the current scope", "INSERT INTO dbo.Customer(Name) VALUES ('A'); SELECT SCOPE_IDENTITY();" },
            ["STRING_AGG"] = new[] { "STRING_AGG(expression, separator) WITHIN GROUP (ORDER BY ...)", "concatenates rows into one string (SQL Server 2017+)", "SELECT STRING_AGG(Name, ', ') WITHIN GROUP (ORDER BY Name) FROM dbo.Customer;" },
            ["SUBSTRING"] = new[] { "SUBSTRING(string, start, length)", "1-based start", "SELECT SUBSTRING('MSSQLTOOL', 1, 5);  -- MSSQL" },
            ["SUM"] = new[] { "SUM([ALL|DISTINCT] expression)", "sum of a group, ignores NULL", "SELECT SUM(LineTotal) FROM dbo.SalesOrderLine;" },
            ["SYSDATETIME"] = new[] { "SYSDATETIME()", "current datetime2 with higher precision", "SELECT SYSDATETIME();" },
            ["TRY_CAST"] = new[] { "TRY_CAST(expression AS data_type[(length)])", "like CAST but returns NULL instead of failing", "SELECT TRY_CAST('not a date' AS date);  -- NULL" },
            ["TRY_CONVERT"] = new[] { "TRY_CONVERT(data_type[(length)], expression [, style])", "like CONVERT but returns NULL instead of failing", "SELECT TRY_CONVERT(int, '12x');  -- NULL" },
            ["UPPER"] = new[] { "UPPER(string)", "upper case", "SELECT UPPER('mssqltool');  -- MSSQLTOOL" },
            ["MAX"] = new[] { "MAX([ALL|DISTINCT] expression)", "largest value in the group, ignores NULL", "SELECT MAX(OrderDate) FROM dbo.SalesOrderHeader;" },
            ["MIN"] = new[] { "MIN([ALL|DISTINCT] expression)", "smallest value in the group, ignores NULL", "SELECT MIN(OrderDate) FROM dbo.SalesOrderHeader;" },

            // Text functions.
            ["ASCII"] = new[] { "ASCII(character_expression)", "code point of the first character", "SELECT ASCII('A');  -- 65" },
            ["CHAR"] = new[] { "CHAR(integer_expression)", "character for an ASCII code (0-255)", "SELECT CHAR(65);  -- A" },
            ["NCHAR"] = new[] { "NCHAR(integer_expression)", "Unicode character for a code point", "SELECT NCHAR(20013);  -- 中" },
            ["UNICODE"] = new[] { "UNICODE(nchar_expression)", "Unicode code point of the first character", "SELECT UNICODE('中');  -- 20013" },
            ["DATALENGTH"] = new[] { "DATALENGTH(expression)", "bytes used, including trailing spaces and Unicode width", "SELECT DATALENGTH(N'abc');  -- 6" },
            ["DIFFERENCE"] = new[] { "DIFFERENCE(expression, expression)", "0-4 how similar two SOUNDEX values are", "SELECT DIFFERENCE('Smith', 'Smyth');  -- 4" },
            ["SOUNDEX"] = new[] { "SOUNDEX(string)", "phonetic code for English names", "SELECT SOUNDEX('Smith');  -- S530" },
            ["QUOTENAME"] = new[] { "QUOTENAME(name [, quote_char])", "wraps an identifier in brackets so it is safe to build SQL", "SELECT QUOTENAME('Order Header');  -- [Order Header]" },
            ["REPLICATE"] = new[] { "REPLICATE(string, count)", "repeats a string", "SELECT REPLICATE('ab', 3);  -- ababab" },
            ["REVERSE"] = new[] { "REVERSE(string)", "the string backwards", "SELECT REVERSE('abc');  -- cba" },
            ["SPACE"] = new[] { "SPACE(count)", "a run of spaces", "SELECT 'a' + SPACE(3) + 'b';" },
            ["STR"] = new[] { "STR(float [, length [, decimal_places]])", "number to text, padded to a fixed width", "SELECT STR(12.345, 8, 2);  -- '   12.35'" },
            ["STRING_ESCAPE"] = new[] { "STRING_ESCAPE(text, 'json')", "escapes characters for JSON output", "SELECT STRING_ESCAPE('a\"b', 'json');" },
            ["STRING_SPLIT"] = new[] { "STRING_SPLIT(string, separator)", "splits a delimited string into rows (2016+)", "SELECT value FROM STRING_SPLIT('a,b,c', ',');" },
            ["STUFF"] = new[] { "STUFF(string, start, length, replacement)", "overwrites part of a string with another", "SELECT STUFF('abcdef', 2, 3, 'X');  -- aXef" },
            ["TRANSLATE"] = new[] { "TRANSLATE(string, from, to)", "replaces characters one for one, like REPLACE per character", "SELECT TRANSLATE('2*[3+4]', '[]', '()');" },
            ["TRIM"] = new[] { "TRIM([characters FROM] string)", "removes leading and trailing spaces (2017+)", "SELECT TRIM('  abc  ');" },
            ["LTRIM"] = new[] { "LTRIM(string)", "removes leading spaces", "SELECT LTRIM('   abc');" },
            ["RIGHT"] = new[] { "RIGHT(string, number_of_characters)", "last n characters", "SELECT RIGHT('MSSQLTOOL', 4);  -- TOOL" },

            // Date and time construction.
            ["DATEFROMPARTS"] = new[] { "DATEFROMPARTS(year, month, day)", "builds a date, error when the parts are invalid", "SELECT DATEFROMPARTS(2026, 1, 31);" },
            ["DATETIMEFROMPARTS"] = new[] { "DATETIMEFROMPARTS(year, month, day, hour, minute, seconds, milliseconds)", "builds a datetime", "SELECT DATETIMEFROMPARTS(2026, 1, 31, 13, 45, 30, 500);" },
            ["DATETIME2FROMPARTS"] = new[] { "DATETIME2FROMPARTS(year, month, day, hour, minute, seconds, fractions, precision)", "builds a datetime2", "SELECT DATETIME2FROMPARTS(2026, 1, 31, 13, 45, 30, 500, 3);" },
            ["SMALLDATETIMEFROMPARTS"] = new[] { "SMALLDATETIMEFROMPARTS(year, month, day, hour, minute)", "builds a smalldatetime (minute precision)", "SELECT SMALLDATETIMEFROMPARTS(2026, 1, 31, 13, 45);" },
            ["DATETIMEOFFSETFROMPARTS"] = new[] { "DATETIMEOFFSETFROMPARTS(year, month, day, hour, minute, seconds, fractions, hour_offset, minute_offset, precision)", "builds a datetimeoffset with its time zone offset", "SELECT DATETIMEOFFSETFROMPARTS(2026, 1, 31, 13, 45, 30, 0, 8, 0, 0);" },
            ["TIMEFROMPARTS"] = new[] { "TIMEFROMPARTS(hour, minute, seconds, fractions, precision)", "builds a time value", "SELECT TIMEFROMPARTS(13, 45, 30, 0, 0);" },
            ["DAY"] = new[] { "DAY(date)", "day of the month (1-31)", "SELECT DAY('2026-01-31');  -- 31" },
            ["MONTH"] = new[] { "MONTH(date)", "month number (1-12)", "SELECT MONTH('2026-01-31');  -- 1" },
            ["YEAR"] = new[] { "YEAR(date)", "four digit year", "SELECT YEAR('2026-01-31');  -- 2026" },
            ["ISDATE"] = new[] { "ISDATE(expression)", "1 when the value converts to a date, 0 otherwise", "SELECT ISDATE('2026-02-30');  -- 0" },
            ["SWITCHOFFSET"] = new[] { "SWITCHOFFSET(datetimeoffset, '+hh:mm')", "same instant in another offset", "SELECT SWITCHOFFSET(SYSDATETIMEOFFSET(), '+08:00');" },
            ["TODATETIMEOFFSET"] = new[] { "TODATETIMEOFFSET(datetime, '+hh:mm')", "attaches the offset that makes a datetime a datetimeoffset", "SELECT TODATETIMEOFFSET(GETDATE(), '+08:00');" },
            ["SYSDATETIMEOFFSET"] = new[] { "SYSDATETIMEOFFSET()", "current datetimeoffset of the server", "SELECT SYSDATETIMEOFFSET();" },
            ["SYSUTCDATETIME"] = new[] { "SYSUTCDATETIME()", "current UTC datetime2, higher precision than GETUTCDATE", "SELECT SYSUTCDATETIME();" },

            // JSON.
            ["CHOOSE"] = new[] { "CHOOSE(index, value1, value2, ...)", "1-based pick from a list, NULL outside the range", "SELECT CHOOSE(2, 'a', 'b', 'c');  -- b" },
            ["ISJSON"] = new[] { "ISJSON(expression)", "1 when the text is valid JSON", "SELECT ISJSON('{\"a\":1}');  -- 1" },
            ["JSON_MODIFY"] = new[] { "JSON_MODIFY(json, path, value)", "returns a new JSON text with one value changed", "SELECT JSON_MODIFY('{\"a\":1}', '$.a', 2);" },
            ["JSON_QUERY"] = new[] { "JSON_QUERY(json, path)", "an object or array out of JSON, as JSON", "SELECT JSON_QUERY('{\"a\":[1,2]}', '$.a');" },
            ["JSON_VALUE"] = new[] { "JSON_VALUE(json, path)", "scalar value out of JSON as text", "SELECT JSON_VALUE('{\"a\":1}', '$.a');  -- 1" },

            // Server and security context.
            ["APP_NAME"] = new[] { "APP_NAME()", "application name of the current connection", "SELECT APP_NAME();" },
            ["CONNECTIONPROPERTY"] = new[] { "CONNECTIONPROPERTY(property)", "connection attributes such as net_transport or client_net_address", "SELECT CONNECTIONPROPERTY('client_net_address');" },
            ["DB_ID"] = new[] { "DB_ID([database_name])", "database id, current database when omitted", "SELECT DB_ID();" },
            ["DB_NAME"] = new[] { "DB_NAME([database_id])", "database name by id", "SELECT DB_NAME(DB_ID());" },
            ["HOST_NAME"] = new[] { "HOST_NAME()", "client computer name", "SELECT HOST_NAME();" },
            ["OBJECT_ID"] = new[] { "OBJECT_ID(name [, type])", "object id; NULL when the object does not exist", "SELECT OBJECT_ID('dbo.SalesOrderHeader', 'U');" },
            ["OBJECT_NAME"] = new[] { "OBJECT_NAME(object_id [, database_id])", "object name by id", "SELECT OBJECT_NAME(OBJECT_ID('dbo.SalesOrderHeader'));" },
            ["ORIGINAL_LOGIN"] = new[] { "ORIGINAL_LOGIN()", "the login that opened the connection, even after EXECUTE AS", "SELECT ORIGINAL_LOGIN();" },
            ["SCHEMA_ID"] = new[] { "SCHEMA_ID([schema_name])", "schema id", "SELECT SCHEMA_ID('dbo');" },
            ["SCHEMA_NAME"] = new[] { "SCHEMA_NAME([schema_id])", "schema name by id", "SELECT SCHEMA_NAME();" },
            ["SERVERPROPERTY"] = new[] { "SERVERPROPERTY(property)", "server attributes such as ProductVersion or Collation", "SELECT SERVERPROPERTY('ProductVersion');" },
            ["SUSER_SNAME"] = new[] { "SUSER_SNAME([server_user_sid])", "login name behind the security context", "SELECT SUSER_SNAME();" },
            ["USER_NAME"] = new[] { "USER_NAME([user_id])", "database user name of the current context", "SELECT USER_NAME();" },
            ["XACT_STATE"] = new[] { "XACT_STATE()", "1 open, -1 doomed, 0 none: decides whether to commit or roll back", "IF XACT_STATE() = 1 COMMIT;" },

            // Table-valued functions: they are FROM sources, so the signature is how they are called.
            ["OPENJSON"] = new[] { "OPENJSON(json [, path]) [WITH (column type 'path' [,...])]", "table-valued: one row per JSON array element; WITH turns named paths into columns", "SELECT j.Id, j.Name FROM OPENJSON(@payload) WITH (Id int '$.id', Name nvarchar(50) '$.name') AS j;" },
            ["OPENXML"] = new[] { "OPENXML(document_handle, rowpattern [, flags]) [WITH (column type 'path' [,...])]", "table-valued: reads XML through a handle from sp_xml_preparedocument; release it with sp_xml_removedocument", "DECLARE @h int; EXEC sp_xml_preparedocument @h OUTPUT, @xml; SELECT * FROM OPENXML(@h, '/Orders/Order') WITH (Id int '@Id');" },
            ["STRING_SPLIT"] = new[] { "STRING_SPLIT(string, separator [, enable_ordinal])", "table-valued: one row per part, column value (plus ordinal when enabled_ordinal = 1)", "SELECT value FROM STRING_SPLIT('a,b,c', ',');" },
            ["GENERATE_SERIES"] = new[] { "GENERATE_SERIES(start, stop [, step])", "table-valued: numeric sequence, column value (SQL Server 2022, compatibility level 160)", "SELECT value FROM GENERATE_SERIES(1, 10, 2);" }
        };

        /// <summary>Names the popup offers as built-in functions; each one must carry a usage note.</summary>
        internal static IEnumerable<string> BuiltInFunctionNames => BuiltInFunctions;

        /// <summary>True when the function has a usage note of its own instead of the generic fallback.</summary>
        internal static bool HasUsageNotes(string name) => FunctionDocs.ContainsKey(name ?? string.Empty);

        internal static string FunctionSignature(string name)
        {
            string[] doc;
            return FunctionDocs.TryGetValue(name ?? string.Empty, out doc) ? doc[0] : "SQL Server built-in function";
        }

        internal static string FunctionDocumentation(string name)
        {
            string[] doc;
            if (!FunctionDocs.TryGetValue(name ?? string.Empty, out doc))
                return LocalizationManager.Format("{0}: SQL Server built-in function.", name) + Environment.NewLine
                    + LocalizationManager.T("No usage notes are available for this function yet.");

            return doc[0] + Environment.NewLine + LocalizationManager.T(doc[1]) + Environment.NewLine
                + LocalizationManager.T("Example:") + " " + doc[2];
        }

        /// <summary>Function names that carry a usage note, so the tests can prove each one is translated.</summary>
        internal static IEnumerable<string> DocumentedFunctions => FunctionDocs.Keys;

        private static void AddCteNames(List<CompletionItem> result, CompletionContext context, int score)
        {
            foreach (string name in ModelCteNames(context))
            {
                result.Add(new CompletionItem
                {
                    DisplayText = name,
                    InsertText = Quote(name),
                    Kind = CompletionItemKind.Table,
                    Description = "CTE",
                    Score = score
                });
            }
        }

        private static List<string> ModelCteNames(CompletionContext context)
        {
            var result = new List<string>();
            if (context == null || string.IsNullOrEmpty(context.DocumentText)) return result;
            if (context.DocumentText.Length > 150000) return result;
            int caret = context.CaretOffset;
            if (caret < 0 || caret > context.DocumentText.Length) caret = context.DocumentText.Length;
            try
            {
                SqlDocumentModel model = SqlDocumentModel.Get(context.DocumentText);
                SqlStatementRange statement = model.StatementAt(caret);
                if (statement == null) return result;
                result.AddRange(model.ScopeOf(statement).CteNames);
            }
            catch (Exception) { }
            return result;
        }

        /// <summary>
        /// FROM/JOIN source list.  Kept as one unit so the DataSource branch stays readable and
        /// so the completion tests can exercise it without going through BuildItems.
        /// </summary>
        private static void AddDataSourceItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            foreach (DatabaseObjectMetadata source in metadata.GetIndex().DataSources)
                result.Add(DataSourceItem(source, context, 40));
            // Session-local tables (#temp, derived) are not part of the snapshot index, so add
            // the ones the current statement can see; the list is usually tiny.
            foreach (DatabaseObjectMetadata local in context.LocalObjects)
                if (IsDataSource(local)) result.Add(DataSourceItem(local, context, 45));
            // The shortest way to write a handful of fixed rows is a table value constructor;
            // it belongs in the source list because that is where it is written.
            result.Add(new CompletionItem
            {
                DisplayText = "VALUES (...)",
                InsertText = "(" + Environment.NewLine
                    + "    VALUES (${1:1}, ${2:2})" + Environment.NewLine
                    + ") AS ${3:t} (${4:c1}, ${5:c2})",
                Kind = CompletionItemKind.Snippet,
                Description = "table value constructor",
                DetailTextProvider = () => Doc(
                    "A few fixed rows written inline, without a temporary table.",
                    "SQL Server needs the column list after AS; every VALUES row must carry the same number of values."),
                Score = 38
            });
            result.AddRange(metadata.Schemas.Select(s => ContainerItem(s, CompletionItemKind.Schema, 60)));
            result.AddRange(metadata.Databases.Select(d => ContainerItem(d, CompletionItemKind.Database, 35)));
            result.AddRange(metadata.LinkedServers.Select(s => ContainerItem(s, CompletionItemKind.Server, 30)));
            AddBuiltInTableFunctions(result, metadata.CompatibilityLevel);
            AddCteNames(result, context, 105);
        }

        /// <summary>
        /// Every join form that can follow a written source, ranked from <paramref name="firstScore"/>
        /// downwards.  They are the continuation of a source ("FROM dbo.T t |") and of a finished ON
        /// predicate.  The APPLY forms belong here as well: they join a derived table or a table-valued
        /// function to every row of the source, and leave only CROSS / OUTER to be typed.
        /// </summary>
        private static void AddJoinContinuationItems(List<CompletionItem> result, int firstScore)
        {
            AddKeyword(result, "INNER JOIN", firstScore, Doc(
                "Keeps only rows that match on both sides.",
                "Write the ON condition right after it."));
            AddKeyword(result, "LEFT JOIN", firstScore - 2, Doc(
                "Keeps every row of the left source; the right side is NULL when nothing matches.",
                "A filter on the right table belongs in ON, not in WHERE."));
            AddKeyword(result, "LEFT OUTER JOIN", firstScore - 3, Doc(
                "Keeps every row of the left source; the right side is NULL when nothing matches.",
                "OUTER is optional: LEFT JOIN means the same thing to SQL Server."));
            AddKeyword(result, "RIGHT JOIN", firstScore - 4, Doc(
                "Keeps every row of the right source; the left side is NULL when nothing matches.",
                "Most code rewrites it as a LEFT JOIN."));
            AddKeyword(result, "RIGHT OUTER JOIN", firstScore - 5, Doc(
                "Keeps every row of the right source; the left side is NULL when nothing matches.",
                "OUTER is optional: RIGHT JOIN means the same thing to SQL Server."));
            AddKeyword(result, "FULL JOIN", firstScore - 6, Doc(
                "Keeps unmatched rows from both sides, NULL-filled.",
                "Rarely what a report wants: it hides missing keys."));
            AddKeyword(result, "FULL OUTER JOIN", firstScore - 7, Doc(
                "Keeps unmatched rows from both sides, NULL-filled.",
                "OUTER is optional: FULL JOIN means the same thing to SQL Server."));
            AddKeyword(result, "CROSS JOIN", firstScore - 8, Doc(
                "Pairs every row of the left source with every row of the right one.",
                "It takes no ON clause and multiplies the row count."));
            AddKeyword(result, "CROSS APPLY", firstScore - 9, Doc(
                "Runs the right side once for every row of the left source.",
                "The right side can reference columns of the left source, so a derived table or a table-valued function sees the current row.",
                "It takes no ON clause; left rows the right side returns nothing for are dropped."));
            AddKeyword(result, "OUTER APPLY", firstScore - 10, Doc(
                "Runs the right side once for every row of the left source, like CROSS APPLY.",
                "A left row whose right side returns nothing is kept with NULLs instead of being dropped.",
                "It takes no ON clause."));
        }

        /// <summary>
        /// "FROM dbo.Orders |" and "FROM dbo.Orders o |": table hints, joins and the alias come
        /// first because they are the only grammar that can follow a written source; the columns
        /// stay underneath, at a low score, as a reminder of what the table holds.
        /// A hint goes after the alias ("FROM dbo.Orders o WITH (NOLOCK)"), so both positions get
        /// the same list.
        /// </summary>
        private static void AddTableSourceTailItems(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            AddTableHintItems(result);

            AddJoinContinuationItems(result, 220);

            if (!context.TableAliasWritten)
            {
                DatabaseObjectMetadata source = FindObject(context, metadata, context.TableSourceName);
                string alias = source == null ? null : MakeAlias(source, context.Aliases.Keys);
                string aliasText = string.IsNullOrEmpty(alias) ? "AS" : "AS " + alias;
                result.Add(new CompletionItem
                {
                    DisplayText = aliasText, InsertText = aliasText,
                    // Below every join / APPLY form: the alias names the source that is already written,
                    // so a join is the more likely continuation.
                    Kind = CompletionItemKind.Keyword, Score = 200,
                    Description = Doc(
                        "Names this source so its columns can be written as alias.Column.",
                        "A short alias keeps long joins readable.")
                });
            }

            AddKeyword(result, "WHERE", 120, Doc(
                "Filters rows after the joins.",
                "On an outer join a WHERE condition on the right table turns it into an inner join.",
                "Write that filter in ON instead."));
            AddKeyword(result, "GROUP BY", 116, Doc(
                "Collapses rows into groups before the aggregates run.",
                "Every non-aggregated SELECT column must be listed here."));
            AddKeyword(result, "ORDER BY", 112, Doc(
                "Sorts the final result.",
                "TOP and paging need it to be stable."));
        }

        /// <summary>
        /// The table hints worth typing at a source: the locking behaviour is what people actually
        /// reach for, and the description explains when each one is right and what it costs.  The
        /// list is short on purpose — the popup is not a T-SQL reference.
        /// </summary>
        private static void AddTableHintItems(List<CompletionItem> result)
        {
            foreach (TableHintDefinition hint in TableHints)
                AddTableHint(result, hint.DisplayText, hint.InsertText, hint.Score, hint.Description);
        }

        /// <summary>
        /// The same hints inside "WITH (|": the wrapper is already in the document, so only the hint
        /// name is inserted and the user closes the parenthesis.
        /// </summary>
        private static void AddTableHintBodyItems(List<CompletionItem> result)
        {
            foreach (TableHintDefinition hint in TableHints)
                result.Add(new CompletionItem
                {
                    DisplayText = hint.BodyDisplay,
                    InsertText = hint.BodyText,
                    Kind = CompletionItemKind.Keyword,
                    Description = Doc(hint.Description),
                    Score = hint.Score
                });
        }

        /// <summary>
        /// "... OPTION (|": the query hints that change the plan, each with the situation it is for.
        /// The names are the body of the clause; the closing parenthesis is typed by the user.
        /// </summary>
        private static void AddQueryHintItems(List<CompletionItem> result)
        {
            AddKeyword(result, "RECOMPILE", 240, Doc(
                "Recompiles the plan for this statement instead of reusing the cached one.",
                "Use it when parameter sniffing produces a bad plan.",
                "It costs a compile on every execution."));
            AddKeyword(result, "MAXDOP 1", 238, Doc(
                "Runs this statement on one CPU.",
                "Use it to keep a small OLTP query from going parallel.",
                "The cap applies only to this statement."));
            AddKeyword(result, "FAST 100", 236, Doc(
                "Returns the first 100 rows as soon as they are ready.",
                "Use it for the first screen of a large report.",
                "The full result still takes just as long."));
            AddKeyword(result, "HASH JOIN", 234, Doc(
                "Forces hash joins for this statement.",
                "Use it to test a plan; the optimizer usually chooses better."));
            AddKeyword(result, "MERGE JOIN", 232, Doc(
                "Forces merge joins for this statement.",
                "Both inputs must already be sorted on the join key."));
            AddKeyword(result, "LOOP JOIN", 230, Doc(
                "Forces nested-loop joins for this statement.",
                "Works well for a few rows with an index on the inner side."));
            AddKeyword(result, "FORCE ORDER", 228, Doc(
                "Keeps the join order exactly as written.",
                "Use it to compare plans, not as a permanent fix."));
            AddKeyword(result, "KEEP PLAN", 226, Doc(
                "Stops the plan being recompiled because of statistics updates.",
                "Use it for a statement whose plan keeps changing."));
            AddKeyword(result, "OPTIMIZE FOR UNKNOWN", 224, Doc(
                "Compiles with averaged statistics instead of the parameter values.",
                "Use it when one plan has to serve very different values."));
        }

        private sealed class TableHintDefinition
        {
            public string DisplayText;
            public string InsertText;
            public string BodyDisplay;
            public string BodyText;
            public int Score;
            public string[] Description;
        }

        private static readonly TableHintDefinition[] TableHints =
        {
            new TableHintDefinition { DisplayText = "WITH (NOLOCK)", BodyDisplay = "NOLOCK", BodyText = "NOLOCK", Score = 240, Description = new[] {
                "Read uncommitted: no shared locks, so readers never wait for writers.",
                "Use it on a busy OLTP table for reports where approximate counts are fine.",
                "Risk: dirty reads, and rows read twice or skipped during page splits.",
                "Never use it for balances or anything you will write back.",
                "Same as WITH (READUNCOMMITTED)." } },
            new TableHintDefinition { DisplayText = "WITH (READPAST)", BodyDisplay = "READPAST", BodyText = "READPAST", Score = 238, Description = new[] {
                "Skips rows another transaction has locked instead of waiting for them.",
                "Use it on work queues together with UPDLOCK and ROWLOCK.",
                "Two workers then never pick up the same row.",
                "Skipped rows stay in the queue and must be retried later.",
                "Ignored under the SNAPSHOT isolation level." } },
            new TableHintDefinition { DisplayText = "WITH (UPDLOCK)", BodyDisplay = "UPDLOCK", BodyText = "UPDLOCK", Score = 236, Description = new[] {
                "Reads the row with an update lock held until the transaction ends.",
                "Use it when you SELECT a row and then UPDATE it.",
                "It stops two sessions upgrading to exclusive locks at once (a deadlock).",
                "Other readers still get through: it does not block plain SELECTs.",
                "Add HOLDLOCK when the pattern is check-then-insert." } },
            new TableHintDefinition { DisplayText = "WITH (HOLDLOCK)", BodyDisplay = "HOLDLOCK", BodyText = "HOLDLOCK", Score = 234, Description = new[] {
                "Applies SERIALIZABLE to this table for the whole transaction.",
                "Range locks stop anyone inserting a row that matches what you just read.",
                "Use it for 'insert if not exists' on a unique value.",
                "Without it two sessions can both find nothing and both insert.",
                "Keep the transaction short: it blocks inserts in the locked range." } },
            new TableHintDefinition { DisplayText = "WITH (ROWLOCK)", BodyDisplay = "ROWLOCK", BodyText = "ROWLOCK", Score = 232, Description = new[] {
                "Asks for row-level locks instead of page or table locks.",
                "Unrelated rows on the same page then stop waiting on each other.",
                "Use it on queue tables together with UPDLOCK and READPAST.",
                "It is a preference: a scan can still escalate to a table lock." } },
            new TableHintDefinition { DisplayText = "WITH (TABLOCK)", BodyDisplay = "TABLOCK", BodyText = "TABLOCK", Score = 230, Description = new[] {
                "Takes a shared table lock for the statement.",
                "Use it for a full-table read that must see one consistent snapshot.",
                "It also stops lock escalation deciding when to take the table lock.",
                "Blocks every writer while it runs; on a hot table prefer NOLOCK or SNAPSHOT.",
                "TABLOCKX takes an exclusive lock instead." } },
            new TableHintDefinition { DisplayText = "WITH (SNAPSHOT)", BodyDisplay = "SNAPSHOT", BodyText = "SNAPSHOT", Score = 228, Description = new[] {
                "Reads the last committed version of each row from tempdb.",
                "Readers see one consistent snapshot and never block writers.",
                "Requires ALLOW_SNAPSHOT_ISOLATION ON for the database.",
                "The consistent replacement for NOLOCK; it costs tempdb version-store space." } },
            new TableHintDefinition { DisplayText = "WITH (NOWAIT)", BodyDisplay = "NOWAIT", BodyText = "NOWAIT", Score = 226, Description = new[] {
                "Fails at once with error 1222 when a needed lock is held.",
                "Without it the statement waits for LOCK_TIMEOUT and then fails.",
                "Use it for screens or jobs that should report 'busy, retry'." } },
            new TableHintDefinition { DisplayText = "WITH (FORCESEEK)", BodyDisplay = "FORCESEEK", BodyText = "FORCESEEK", Score = 224, Description = new[] {
                "Forces an index seek, on the given index or on any usable one.",
                "Use it when stale statistics push the optimizer into a scan.",
                "It fails with error 8622 when no seek plan exists, so keep it out of shared code." } },
            new TableHintDefinition { DisplayText = "WITH (INDEX(...))", InsertText = "WITH (INDEX(", BodyDisplay = "INDEX(...)", BodyText = "INDEX(", Score = 222, Description = new[] {
                "Pins the index this statement uses, e.g. WITH (INDEX(IX_Orders_CustomerId)).",
                "Use it to test a plan or to work around a bad one.",
                "Type the index name, then the closing parenthesis.",
                "INDEX(0) forces a heap scan." } }
        };

        /// <summary>
        /// Joins the lines of a description and translates each of them.  One line per dictionary
        /// entry keeps the Chinese text readable (and keeps zh-TW working through the same table)
        /// instead of translating one long block the layout has to survive.
        /// </summary>
        internal static string Doc(params string[] lines)
        {
            if (lines == null || lines.Length == 0) return string.Empty;
            return string.Join(Environment.NewLine, lines.Select(LocalizationManager.T));
        }

        private static void AddTableHint(List<CompletionItem> result, string displayText, string insertText,
            int score, params string[] descriptionLines)
        {
            result.Add(new CompletionItem
            {
                DisplayText = displayText,
                // Hints end in a space so the statement keeps flowing; the index hint stops at the
                // open parenthesis so the index name can be typed straight away.
                InsertText = insertText ?? displayText + " ",
                Kind = CompletionItemKind.Keyword,
                Description = Doc(descriptionLines),
                Score = score
            });
        }

        private static IEnumerable<CompletionItem> BuildJoinItems(CompletionContext context, MetadataSnapshot metadata)
        {
            var allSources = ScopedSources(context);
            var sources = allSources.Where(p => !string.Equals(p.Key, LastPart(p.Value), StringComparison.OrdinalIgnoreCase)).ToList();
            if (sources.Count == 0) sources = allSources;
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

                bool prefixComplete = PrefixCompletesName(context.Prefix, target);
                bool nameWritten = !prefixComplete && NameAlreadyWritten(context, target);

                if (!prefixComplete && !nameWritten)
                {
                    // Still picking a table: offer the name and nothing else.
                    yield return new CompletionItem
                    {
                        DisplayText = DisplayQualifiedName(target), InsertText = targetInsert,
                        Kind = CompletionItemKind.Join,
                        Description = "table",
                        DetailTextProvider = () => ObjectDetail(target), Score = 115
                    };
                    continue;
                }

                // The name is already in the document, so the popup was raised right after it and
                // only the ON clause is missing; otherwise the entry has to carry the name too.
                string onInsert = nameWritten ? " ON " : targetInsert + " ON ";
                int onBaseScore = nameWritten ? 130 : 75;
                if (!nameWritten)
                {
                    yield return new CompletionItem
                    {
                        DisplayText = DisplayQualifiedName(target), InsertText = targetInsert,
                        Kind = CompletionItemKind.Join,
                        Description = "table",
                        DetailTextProvider = () => ObjectDetail(target), Score = 115
                    };
                }

                if (relationships.Count == 0)
                {
                    foreach (var source in sources)
                    {
                        DatabaseObjectMetadata sourceObject = FindObject(context, metadata, source.Value);
                        List<JoinColumnMatch> inferred = InferJoinColumns(sourceObject, target);
                        foreach (JoinColumnMatch match in inferred)
                        {
                            string condition = Quote(source.Key) + "." + Quote(match.SourceColumn)
                                + " = " + targetQualifier + "." + Quote(match.TargetColumn);
                            yield return new CompletionItem
                            {
                                DisplayText = "ON " + condition, InsertText = onInsert + condition,
                                Kind = CompletionItemKind.Join,
                                Description = match.IsCustom ? "join on (custom rule)" : "join on (same-name column)",
                                DetailTextProvider = () => ObjectDetail(target),
                                Score = match.IsCustom ? onBaseScore + 5 : onBaseScore
                            };
                        }
                    }

                    continue;
                }

                foreach (var fk in relationships)
                {
                    var source = sources.First(s => ObjectMatches(fk.ParentObject, s.Value) || ObjectMatches(fk.ReferencedObject, s.Value));
                    bool sourceParent = ObjectMatches(fk.ParentObject, source.Value);

                    // One entry per key column pair: DjGuid and DjLsh are separate choices.
                    for (int i = 0; i < fk.ParentColumns.Count; i++)
                    {
                        string condition = Quote(source.Key) + "." + Quote(sourceParent ? fk.ParentColumns[i] : fk.ReferencedColumns[i])
                            + " = " + targetQualifier + "." + Quote(sourceParent ? fk.ReferencedColumns[i] : fk.ParentColumns[i]);
                        yield return new CompletionItem
                        {
                            DisplayText = "ON " + condition, InsertText = onInsert + condition,
                            Kind = CompletionItemKind.Join,
                            Description = "join on (foreign key)",
                            DetailTextProvider = () => ObjectDetail(target), Score = onBaseScore + 10
                        };
                    }
                }
            }
        }

        /// <summary>
        /// True when the token being typed is already the whole table name (or the whole qualified
        /// name), which is the point where the ON variants become useful.  A shorter prefix means the
        /// user is still picking a table, so only the name is offered.
        /// </summary>
        private static bool PrefixCompletesName(string prefix, DatabaseObjectMetadata target)
        {
            if (string.IsNullOrEmpty(prefix) || target == null) return false;

            string value = SqlTokenizer.Unquote(prefix.Trim().TrimEnd(']'));
            if (value.Length == 0) return false;

            if (string.Equals(LastPart(value), target.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(value, CleanQualified(target.QualifiedName), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the token that ends right at the caret is already the whole table name, which is
        /// the state after the name has been inserted: the only thing left to add is the ON clause.
        /// </summary>
        internal static bool NameAlreadyWritten(CompletionContext context, DatabaseObjectMetadata target)
        {
            if (context == null || target == null) return false;
            string text = context.DocumentText;
            int caret = context.CaretOffset;
            if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length) return false;

            int start = caret;
            while (start > 0)
            {
                char c = text[start - 1];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$'
                    || c == ']' || c == '[' || c == '"' || c == '\'')
                    start--;
                else
                    break;
            }

            if (start == caret) return false;

            string token = text.Substring(start, caret - start).Trim('[', ']', '"', '\'');
            return string.Equals(LastPart(token), target.Name, StringComparison.OrdinalIgnoreCase);
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
        private static CompletionItem ObjectItem(DatabaseObjectMetadata o, int score, bool omitSchema) => new CompletionItem { DisplayText = omitSchema ? o.Name : o.DisplayQualifiedName, ObjectSource = o, OmitSchema = omitSchema, Kind = o.Kind, Score = score };
        private static CompletionItem DataSourceItem(DatabaseObjectMetadata o, CompletionContext context, int score)
        {
            CompletionItem item = ObjectItem(o, score, false);
            if (CompletionSettings().autoAddAliases && (o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || o.Kind == CompletionItemKind.Synonym))
            {
                item.AutoAlias = true;
                item.AliasContext = context;
            }
            return item;
        }

        internal static string ObjectInsertText(DatabaseObjectMetadata o, bool omitSchema, CompletionContext aliasContext)
        {
            string text = omitSchema ? Quote(o.Name) : QualifiedInsert(o);
            if (aliasContext != null && CompletionSettings().autoAddAliases
                && (o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || o.Kind == CompletionItemKind.Synonym))
                text += " AS " + Quote(MakeAlias(o, aliasContext.Aliases.Keys));
            return text;
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

        internal static string ObjectDetail(DatabaseObjectMetadata item)
        {
            // The full detail string is memoized on the metadata object; detail
            // panels are only rendered for the selected candidate.
            if (item == null) return string.Empty;
            if (item.Kind == CompletionItemKind.Function && item.IsTableValuedFunction)
            {
                // A table-valued function is a FROM source, not a value; say how it is called
                // before the column list starts talking about its result shape.
                return LocalizationManager.T("Table-valued function: call it in FROM/JOIN with its arguments, for example SELECT * FROM dbo.MyFunction(1) AS f.")
                    + Environment.NewLine + item.CachedDetail;
            }
            return item.CachedDetail;
        }
        private static string DisplayQualifiedName(DatabaseObjectMetadata o) => (o.IsExternal && !string.IsNullOrWhiteSpace(o.Server) ? o.Server + "." : "") + (o.IsExternal && !string.IsNullOrWhiteSpace(o.Database) ? o.Database + "." : "") + (string.IsNullOrEmpty(o.Schema) ? o.Name : o.Schema + "." + o.Name);
        private static string QualifiedInsert(DatabaseObjectMetadata o) => (o.IsExternal && !string.IsNullOrWhiteSpace(o.Server) ? Quote(o.Server) + "." : "") + (o.IsExternal && !string.IsNullOrWhiteSpace(o.Database) ? Quote(o.Database) + "." : "") + (string.IsNullOrEmpty(o.Schema) ? Quote(o.Name) : Quote(o.Schema) + "." + Quote(o.Name));
        internal static bool IsDataSource(DatabaseObjectMetadata o) => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || (o.Kind == CompletionItemKind.Function && o.IsTableValuedFunction) || o.Kind == CompletionItemKind.Synonym;
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
            List<CompletionItem> list = items as List<CompletionItem>;
            if (list == null) list = items?.ToList() ?? new List<CompletionItem>();
            return CompletionFilter.Filter(list, prefix, CompletionSettings().maximumItems);
        }

        internal static int MatchItem(CompletionItem item, string prefix)
            => MatchItem(item, prefix, true);

        internal static int MatchItem(CompletionItem item, string prefix, bool allowFuzzy)
        {
            string display = item.MatchText;
            int score = MatchScore(display, prefix, allowFuzzy);
            if (score >= 40 || display.IndexOf('.') < 0) return score;
            // DataSource candidates display schema-qualified names such as
            // "dbo.WSDD". The acronym and substring channels must also see the
            // bare object name, or typing "WD" suggests "WsdData" after
            // "dbo." but nothing in the unqualified FROM position.
            int bare = MatchScore(item.BareMatchText, prefix, allowFuzzy);
            return Math.Max(score, bare);
        }

        private static int MatchScore(string value, string prefix)
            => MatchScore(value, prefix, true);

        private static int MatchScore(string value, string prefix, bool allowFuzzy)
        {
            if (string.IsNullOrEmpty(prefix)) return 0;
            // A fully typed name must beat any longer name that merely starts with the same text,
            // even when the longer one carries the whole history boost (bounded at 12 here against
            // a 20 point gap).  Without this tier "WDDDH" sat behind "WDDDH_BAK" once that table
            // had been used.
            if (value.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return 60;
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 40;
            int segment = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            // A match at a name-segment boundary ("Name" in "CustomerName", after a dot or an
            // underscore) is semantically stronger than a match buried in the middle of a word.
            if (segment > 0 && IsSegmentStart(value, segment)) return 30;
            if (StartsWithInitials(value, prefix)) return 25;
            if (segment >= 0) return 10;
            // SQL LIKE %W%D% style: every typed character must appear in the
            // name in order, so "WD" still suggests "WSDD". Ranked below the
            // contiguous/acronym channels, above the typo-tolerance fallback.
            if (ContainsInOrder(value, prefix)) return 5;
            if (!allowFuzzy) return -1;
            // Fuzzy matching is only useful near the typed length.  Running the bounded
            // Levenshtein loop for every one of 20k long table names made a single letter
            // cost 16 ms; the length gate rejects those rows in O(1) instead.
            if (Math.Abs(value.Length - prefix.Length) > 2) return -1;
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

        private static bool IsSegmentStart(string value, int index)
        {
            if (index <= 0) return true;
            char previous = value[index - 1];
            if (previous == '.' || previous == '_' || previous == ' ' || previous == '#' || previous == '@') return true;
            return char.IsUpper(value[index]) && char.IsLower(previous);
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
        /// <summary>
        /// Session temp-table objects are kept in LocalObjects, but aliases from earlier statements
        /// must not contribute columns to the current SELECT.  A key/value is kept only when the
        /// current statement + semantic suffix actually mentions it.
        /// </summary>
        private static void PruneAliasesToCurrentStatement(CompletionContext context, string currentStatement)
        {
            if (context?.Aliases == null || context.Aliases.Count == 0) return;
            foreach (string key in context.Aliases.Keys.ToList())
            {
                if (ContainsIdentifier(currentStatement, key)) continue;
                string value = context.Aliases[key];
                string tail = value?.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(tail) && ContainsIdentifier(currentStatement, tail)) continue;
                context.Aliases.Remove(key);
            }
        }

        private static bool ContainsIdentifier(string text, string identifier)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(identifier)) return false;
            string value = Clean(identifier);
            if (value.Length == 0) return false;
            // A boundary walk instead of a dynamically built regex: the pattern would enter the
            // static regex cache (15 entries) and push out every other identifier's pattern.
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool startsWord = index == 0 || !IsIdentifierCharacter(text[index - 1]);
                int end = index + value.Length;
                bool endsWord = end >= text.Length || !IsIdentifierCharacter(text[end]);
                if (startsWord && endsWord) return true;
                index++;
            }
            return false;
        }

        private static bool IsIdentifierCharacter(char value) =>
            char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$';

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

        private static void AddSessionLocalObjects(CompletionContext context, IReadOnlyList<DatabaseObjectMetadata> locals)
        {
            if (context == null || locals == null) return;
            foreach (DatabaseObjectMetadata local in locals)
            {
                if (local == null || string.IsNullOrWhiteSpace(local.Name)) continue;
                var copy = new DatabaseObjectMetadata
                {
                    Schema = local.Schema, Name = local.Name, Kind = local.Kind, IsSystem = local.IsSystem
                };
                foreach (ColumnMetadata column in local.Columns)
                {
                    copy.Columns.Add(new ColumnMetadata
                    {
                        Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                        IsIdentity = column.IsIdentity, IsComputed = column.IsComputed,
                        IsPrimaryKey = column.IsPrimaryKey, IsForeignKey = column.IsForeignKey,
                        IsUnique = column.IsUnique, Ordinal = column.Ordinal
                    });
                }
                context.LocalObjects.RemoveAll(o => string.Equals(o.Name, copy.Name, StringComparison.OrdinalIgnoreCase));
                context.LocalObjects.Add(copy);
                context.Aliases[copy.Name] = copy.Name;
            }
        }

        /// <summary>
        /// Temp-table definitions declared before the caret statement.  The controller caches the
        /// result by prefix hash, so a large script pays this regex pass only when the prefix
        /// actually changes, not on every keystroke.
        /// </summary>
        internal static List<DatabaseObjectMetadata> CollectSessionLocalObjects(string sessionPrefix)
        {
            var context = new CompletionContext();
            string text = sessionPrefix ?? string.Empty;
            if (text.Length == 0) return context.LocalObjects;
            string masked = SqlTextContext.MaskCommentsAndStrings(text);
            AddLocalObjects(text, masked, text, masked, context);
            return context.LocalObjects;
        }

        private static void AddLocalObjects(string sessionText, string maskedSession, string currentBatch, string maskedBatch, CompletionContext context)
        {
            string batch = maskedBatch ?? SqlTextContext.MaskCommentsAndStrings(currentBatch ?? string.Empty);
            foreach (Match m in DeclareTablePattern.Matches(batch)) AddLocal(context, m.Groups["name"].Value, ParseDefinitions(m.Groups["cols"].Value));
            string sql = maskedSession ?? SqlTextContext.MaskCommentsAndStrings(sessionText ?? string.Empty);
            var events = new List<LocalObjectEvent>();
            foreach (Match m in TempCreatePattern.Matches(sql)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseDefinitions(m.Groups["cols"].Value).ToList() });
            foreach (SelectIntoReference reference in SqlSelectIntoScanner.Find(sql))
                events.Add(new LocalObjectEvent { Index = reference.Index, Name = reference.Name, Columns = ParseSelect(reference.SelectList).ToList() });
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
        internal static string Clean(string value) => DatabaseIdentifier.UnquoteSqlServerPart(value);
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
        private static readonly HashSet<string> ClauseKeywords = new HashSet<string>(new[] { "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "ON", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "OFFSET", "FETCH", "FOR", "WITH", "APPLY", "OUTER", "PIVOT", "UNPIVOT", "WHEN", "THEN", "ELSE", "OUTPUT", "INTO", "VALUES", "SET", "OVER", "PARTITION", "ROWS", "RANGE" }, StringComparer.OrdinalIgnoreCase);
        private static readonly string[] DetachedStatementStarters = {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "SET", "CREATE", "ALTER", "DROP", "TRUNCATE", "USE", "PRINT", "EXEC", "EXECUTE", "BEGIN", "COMMIT", "ROLLBACK", "GRANT", "DENY", "REVOKE",
            "WITH", "THROW", "RETURN", "WAITFOR", "RAISERROR", "GOTO", "BREAK", "CONTINUE", "SAVE", "BULK", "DBCC", "IF", "WHILE", "OPEN", "CLOSE", "DEALLOCATE", "FETCH", "KILL", "CHECKPOINT", "RECONFIGURE"
        };

        /// <summary>
        /// Built-in completion keywords.  The list is deliberately exhaustive for common T-SQL: the popup
        /// filters it by the typed prefix, so a construct that is not listed here can never be completed
        /// (typing "with" used to offer nothing even though WITH (NOLOCK) is everywhere in real scripts).
        /// Users can extend it further with the "additional keywords" completion setting.
        /// </summary>
        private static readonly string[] Keywords = {
            // statement heads
            "SELECT", "SELECT DISTINCT", "SELECT TOP", "SELECT INTO", "INSERT INTO", "INSERT", "UPDATE", "DELETE", "DELETE FROM", "MERGE", "TRUNCATE TABLE",
            "DECLARE", "SET", "EXEC", "EXECUTE", "USE", "PRINT", "RAISERROR", "THROW", "RETURN", "WAITFOR", "WAITFOR DELAY", "WAITFOR TIME",
            "CREATE", "ALTER", "DROP", "TRUNCATE",
            "BEGIN", "BEGIN TRANSACTION", "BEGIN TRY", "BEGIN CATCH", "COMMIT", "COMMIT TRANSACTION", "ROLLBACK", "ROLLBACK TRANSACTION",
            "SAVE TRANSACTION", "END TRY", "END CATCH", "GOTO", "BREAK", "CONTINUE", "GO",
            "GRANT", "DENY", "REVOKE", "CREATE USER", "CREATE ROLE", "CREATE LOGIN", "CREATE SCHEMA", "CREATE SEQUENCE", "CREATE SYNONYM", "CREATE INDEX",
            "ALTER INDEX", "DROP INDEX", "DBCC", "CHECKPOINT", "KILL", "RECONFIGURE", "SHUTDOWN", "BULK INSERT", "OPEN", "CLOSE", "DEALLOCATE", "FETCH NEXT",
            // clauses
            "FROM", "WHERE", "INTO", "VALUES", "SET", "OUTPUT", "AS", "ON", "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "EXISTS", "IN", "LIKE", "BETWEEN",
            "GROUP BY", "GROUPING SETS", "ROLLUP", "CUBE", "WITH ROLLUP", "WITH CUBE", "HAVING", "ORDER BY", "ASC", "DESC",
            "OFFSET", "FETCH FIRST", "FETCH NEXT", "ROWS ONLY", "ROW ONLY", "NEXT", "FIRST",
            "JOIN", "INNER JOIN", "LEFT JOIN", "LEFT OUTER JOIN", "RIGHT JOIN", "RIGHT OUTER JOIN", "FULL JOIN", "FULL OUTER JOIN", "CROSS JOIN",
            "CROSS APPLY", "OUTER APPLY", "APPLY", "PIVOT", "UNPIVOT", "UNION", "UNION ALL", "EXCEPT", "INTERSECT",
            "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "TOP", "PERCENT", "WITH TIES",
            // CTE and windowing
            "WITH", "WITH (NOLOCK)", "WITH (ROWLOCK)", "WITH (UPDLOCK)", "WITH (XLOCK)", "WITH (HOLDLOCK)", "WITH (READPAST)", "WITH (READCOMMITTED)",
            "WITH (READUNCOMMITTED)", "WITH (REPEATABLEREAD)", "WITH (SERIALIZABLE)", "WITH (SNAPSHOT)", "WITH (INDEX(index_name))", "WITH (FORCESEEK)",
            "WITH (FORCESCAN)", "WITH (NOLOCK, INDEX(index_name))", "OPTION (RECOMPILE)", "OPTION (MAXDOP 1)", "OPTION (FAST 100)", "OPTION (HASH JOIN)",
            "OPTION (MERGE JOIN)", "OPTION (LOOP JOIN)", "OPTION (FORCE ORDER)", "OPTION (KEEP PLAN)", "OPTION (OPTIMIZE FOR UNKNOWN)",
            "OVER", "PARTITION BY", "ROWS BETWEEN", "RANGE BETWEEN", "UNBOUNDED PRECEDING", "CURRENT ROW", "UNBOUNDED FOLLOWING", "PRECEDING", "FOLLOWING",
            // MERGE and set operations
            "MERGE INTO", "WHEN MATCHED", "WHEN NOT MATCHED", "WHEN NOT MATCHED BY SOURCE", "USING", "MATCHED", "THEN INSERT", "THEN UPDATE", "THEN DELETE",
            // built-in functions and system values
            "COUNT", "COUNT(*)", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STDEV", "VAR", "STRING_AGG", "STRING_SPLIT", "CONCAT", "CONCAT_WS", "FORMAT",
            "ISNULL", "COALESCE", "NULLIF", "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE", "PARSE", "IIF", "CHOOSE", "ABS", "CEILING", "FLOOR",
            "ROUND", "POWER", "SQRT", "SIGN", "LEN", "DATALENGTH", "SUBSTRING", "LEFT", "RIGHT", "CHARINDEX", "PATINDEX", "REPLACE", "STUFF", "REPLICATE",
            "REVERSE", "TRIM", "LTRIM", "RTRIM", "UPPER", "LOWER", "SPACE", "QUOTENAME", "STR", "TRANSLATE",
            "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "CURRENT_TIMESTAMP", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEFROMPARTS", "DATENAME",
            "DATEPART", "DAY", "MONTH", "YEAR", "EOMONTH", "ISDATE", "SWITCHOFFSET", "TODATETIMEOFFSET",
            "NEWID", "NEWSEQUENTIALID", "SCOPE_IDENTITY", "IDENT_CURRENT", "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE",
            "LAST_VALUE", "PERCENT_RANK", "CUME_DIST", "GROUPING", "GROUPING_ID", "HASHBYTES", "CHECKSUM", "BINARY_CHECKSUM", "ISNUMERIC", "OBJECT_ID",
            "OBJECT_NAME", "OBJECT_DEFINITION", "SCHEMA_NAME", "DB_NAME", "DB_ID", "COL_NAME", "COL_LENGTH", "TYPE_NAME", "ERROR_MESSAGE", "ERROR_NUMBER",
            "ERROR_SEVERITY", "ERROR_STATE", "ERROR_LINE", "ERROR_PROCEDURE", "@@ROWCOUNT", "@@ERROR", "@@IDENTITY", "@@TRANCOUNT", "@@SPID", "@@SERVERNAME",
            "@@VERSION", "@@FETCH_STATUS", "@@DATEFIRST", "@@LANGUAGE", "@@NESTLEVEL",
            // JSON, XML and external data
            "FOR JSON", "FOR JSON PATH", "FOR JSON AUTO", "FOR XML", "FOR XML PATH", "FOR XML RAW", "FOR XML AUTO", "OPENJSON", "OPENXML", "OPENROWSET",
            "OPENQUERY", "OPENROWSET(BULK ...)", "XMLNAMESPACES", "WITH XMLNAMESPACES", "JSON_VALUE", "JSON_QUERY", "JSON_MODIFY", "ISJSON",
            // DDL
            "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE VIEW", "ALTER VIEW", "DROP VIEW", "CREATE PROCEDURE", "ALTER PROCEDURE", "DROP PROCEDURE",
            "CREATE FUNCTION", "ALTER FUNCTION", "DROP FUNCTION", "CREATE TRIGGER", "ALTER TRIGGER", "DROP TRIGGER", "CREATE DATABASE", "ALTER DATABASE",
            "DROP DATABASE", "CREATE TYPE", "ALTER TYPE", "DROP TYPE", "CREATE OR ALTER", "ADD", "ADD COLUMN", "ALTER COLUMN", "DROP COLUMN", "ADD CONSTRAINT",
            "DROP CONSTRAINT", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "CHECK", "DEFAULT", "IDENTITY", "IDENTITY(1,1)", "NOT NULL", "WITH CHECK",
            "WITH NOCHECK", "ON DELETE CASCADE", "ON UPDATE CASCADE", "INCLUDE", "CLUSTERED", "NONCLUSTERED", "ONLINE = ON", "FILLFACTOR", "NEXT VALUE FOR",
            "SEQUENCE", "SCHEMABINDING", "RETURNS", "RETURNS TABLE", "RETURNING", "EXECUTE AS", "WITH RECOMPILE", "WITH ENCRYPTION", "OUTPUT INSERTED",
            // session settings
            "SET NOCOUNT ON", "SET NOCOUNT OFF", "SET XACT_ABORT ON", "SET XACT_ABORT OFF", "SET ANSI_NULLS ON", "SET QUOTED_IDENTIFIER ON", "SET STATISTICS IO ON",
            "SET STATISTICS TIME ON", "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "USER_NAME", "SUSER_SNAME"
        };
    }
}
