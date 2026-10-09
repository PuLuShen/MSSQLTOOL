using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using static MSSQLTool.MSSQLToolPackage;

namespace MSSQLTool
{
    /// <summary>
    /// Thrown when the T-SQL code cannot be formatted because it contains syntax errors.
    /// The original text is never modified in this case, so no code can be lost.
    /// </summary>
    public sealed class TSqlFormatException : Exception
    {
        public IList<ParseError> ParseErrors { get; }

        public TSqlFormatException(IList<ParseError> parseErrors)
            : base(BuildMessage(parseErrors))
        {
            ParseErrors = parseErrors;
        }

        private static string BuildMessage(IList<ParseError> parseErrors)
        {
            int count = parseErrors == null ? 0 : parseErrors.Count;
            var sb = new StringBuilder();
            sb.AppendLine(count == 1
                ? "The T-SQL code contains a syntax error and cannot be formatted:"
                : string.Format(CultureInfo.InvariantCulture, "The T-SQL code contains {0} syntax errors and cannot be formatted:", count));

            int shown = 0;
            if (parseErrors != null)
            {
                foreach (ParseError error in parseErrors)
                {
                    if (shown >= 5)
                    {
                        sb.AppendLine("  ...");
                        break;
                    }
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Line {0}, column {1}: {2}", error.Line, error.Column, error.Message));
                    shown++;
                }
            }

            return sb.ToString().TrimEnd();
        }
    }

    public static partial class TSqlFormatter
    {
        internal class OwnVisitor : TSqlFragmentVisitor
        {
            public List<QualifiedJoin> QualifiedJoins = new List<QualifiedJoin>();
            public List<UnqualifiedJoin> UnqualifiedJoins = new List<UnqualifiedJoin>();
            public List<SearchedCaseExpression> CaseExpressions = new List<SearchedCaseExpression>();
            public List<StatementList> ProcBodies = new List<StatementList>();
            public List<ExecuteStatement> ExecStatements = new List<ExecuteStatement>();
            public List<FunctionCall> FunctionCalls = new List<FunctionCall>();
            public List<BeginEndBlockStatement> BeginEndBlocks = new List<BeginEndBlockStatement>();
            public List<DeclareVariableStatement> DeclareStatements = new List<DeclareVariableStatement>();
            public List<CreateProcedureStatement> SprocDefinitionsCreate = new List<CreateProcedureStatement>();
            public List<AlterProcedureStatement> SprocDefinitionsAlter = new List<AlterProcedureStatement>();
            public List<CreateOrAlterProcedureStatement> SprocDefinitionsCreateAlter = new List<CreateOrAlterProcedureStatement>();

            public override void ExplicitVisit(QualifiedJoin node)
            {
                base.ExplicitVisit(node);
                QualifiedJoins.Add(node);
            }

            public override void ExplicitVisit(UnqualifiedJoin node)
            {
                base.ExplicitVisit(node);
                UnqualifiedJoins.Add(node);
            }

            public override void ExplicitVisit(SearchedCaseExpression node)
            {
                base.ExplicitVisit(node);
                CaseExpressions.Add(node);
            }

            public override void ExplicitVisit(CreateProcedureStatement node)
            {
                base.ExplicitVisit(node);
                SprocDefinitionsCreate.Add(node);
                CreateProcedureStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(CreateFunctionStatement node)
            {
                base.ExplicitVisit(node);
                CreateFunctionStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(CreateTriggerStatement node)
            {
                base.ExplicitVisit(node);
                CreateTriggerStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(AlterProcedureStatement node)
            {
                base.ExplicitVisit(node);
                SprocDefinitionsAlter.Add(node);
                AlterProcedureStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(AlterFunctionStatement node)
            {
                base.ExplicitVisit(node);
                AlterFunctionStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(AlterTriggerStatement node)
            {
                base.ExplicitVisit(node);
                AlterTriggerStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
            {
                base.ExplicitVisit(node);
                SprocDefinitionsCreateAlter.Add(node);
                CreateOrAlterProcedureStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(CreateOrAlterFunctionStatement node)
            {
                base.ExplicitVisit(node);
                CreateOrAlterFunctionStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
            {
                base.ExplicitVisit(node);
                CreateOrAlterTriggerStatements.Add(node);
                if (node.StatementList != null)
                    ProcBodies.Add(node.StatementList);
            }

            public override void ExplicitVisit(ExecuteStatement node)
            {
                base.ExplicitVisit(node);
                ExecStatements.Add(node);
            }

            public override void ExplicitVisit(FunctionCall node)
            {
                base.ExplicitVisit(node);
                FunctionCalls.Add(node);
            }

            public override void ExplicitVisit(BeginEndBlockStatement node)
            {
                base.ExplicitVisit(node);
                BeginEndBlocks.Add(node);
            }

            public override void ExplicitVisit(DeclareVariableStatement node)
            {
                base.ExplicitVisit(node);
                DeclareStatements.Add(node);
            }

            // Collections used by the layout passes in TsqlFormatterLayout.cs.
            public List<MergeStatement> MergeStatements = new List<MergeStatement>();
            public List<QuerySpecification> QuerySpecifications = new List<QuerySpecification>();
            public List<ScalarSubquery> ScalarSubqueries = new List<ScalarSubquery>();
            public List<CreateTableStatement> CreateTableStatements = new List<CreateTableStatement>();
            public List<CreateViewStatement> CreateViewStatements = new List<CreateViewStatement>();
            public List<DeclareCursorStatement> DeclareCursorStatements = new List<DeclareCursorStatement>();
            public List<InsertStatement> InsertStatements = new List<InsertStatement>();
            public List<UpdateStatement> UpdateStatements = new List<UpdateStatement>();
            public List<DeleteStatement> DeleteStatements = new List<DeleteStatement>();
            public List<CreateProcedureStatement> CreateProcedureStatements = new List<CreateProcedureStatement>();
            public List<AlterProcedureStatement> AlterProcedureStatements = new List<AlterProcedureStatement>();
            public List<CreateOrAlterProcedureStatement> CreateOrAlterProcedureStatements = new List<CreateOrAlterProcedureStatement>();
            public List<CreateFunctionStatement> CreateFunctionStatements = new List<CreateFunctionStatement>();
            public List<AlterFunctionStatement> AlterFunctionStatements = new List<AlterFunctionStatement>();
            public List<CreateOrAlterFunctionStatement> CreateOrAlterFunctionStatements = new List<CreateOrAlterFunctionStatement>();
            public List<CreateTriggerStatement> CreateTriggerStatements = new List<CreateTriggerStatement>();
            public List<AlterTriggerStatement> AlterTriggerStatements = new List<AlterTriggerStatement>();
            public List<CreateOrAlterTriggerStatement> CreateOrAlterTriggerStatements = new List<CreateOrAlterTriggerStatement>();

            public override void ExplicitVisit(MergeStatement node)
            {
                base.ExplicitVisit(node);
                MergeStatements.Add(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                base.ExplicitVisit(node);
                QuerySpecifications.Add(node);
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
                base.ExplicitVisit(node);
                ScalarSubqueries.Add(node);
            }

            public override void ExplicitVisit(CreateTableStatement node)
            {
                base.ExplicitVisit(node);
                CreateTableStatements.Add(node);
            }

            public override void ExplicitVisit(CreateViewStatement node)
            {
                base.ExplicitVisit(node);
                CreateViewStatements.Add(node);
            }

            public override void ExplicitVisit(DeclareCursorStatement node)
            {
                base.ExplicitVisit(node);
                DeclareCursorStatements.Add(node);
            }

            public override void ExplicitVisit(InsertStatement node)
            {
                base.ExplicitVisit(node);
                InsertStatements.Add(node);
            }

            public override void ExplicitVisit(UpdateStatement node)
            {
                base.ExplicitVisit(node);
                UpdateStatements.Add(node);
            }

            public override void ExplicitVisit(DeleteStatement node)
            {
                base.ExplicitVisit(node);
                DeleteStatements.Add(node);
            }
        }

        /// <summary>
        /// Recursively walks a StatementList, forces a double‐newline between each sibling statement,
        /// then dives into any nested StatementList inside control‐flow blocks (BEGIN/END, IF, WHILE, etc.).
        /// </summary>
        /// <param name="stmtList">The StatementList to process.</param>
        /// <param name="sqlFragment">The root TSqlFragment, used to access the ScriptTokenStream.</param>
        private static void InsertBlankLinesRecursive(StatementList stmtList, TSqlFragment sqlFragment, bool skipTopLevel = false)
        {

            // 1) Insert a blank line between each pair of sibling statements in this list
            var statements = stmtList.Statements;
            if (!skipTopLevel)
            {
                for (int i = 0; i < statements.Count - 1; i++)
                {
                    // Find the last token of the i‐th statement
                    int endOfStmt = statements[i].LastTokenIndex;
                    int nextIdx = endOfStmt + 1;

                    if (nextIdx < sqlFragment.ScriptTokenStream.Count)
                    {
                        TSqlParserToken nextToken = sqlFragment.ScriptTokenStream[nextIdx];
                        if (nextToken.TokenType == TSqlTokenType.WhiteSpace)
                        {
                            nextToken.Text = "\r\n\r\n";
                        }
                    }
                }
            }

            // 2) For each statement in this list, check if it contains its own StatementList
            foreach (TSqlStatement stmt in statements)
            {
                // ─── Handle BEGIN ... END blocks ───────────────────────────────────────────
                if (stmt is BeginEndBlockStatement beb && beb.StatementList != null)
                {
                    InsertBlankLinesRecursive(beb.StatementList, sqlFragment);
                }

                // ─── Handle IF ... THEN [ ... ] ELSE [ ... ] ──────────────────────────────
                if (stmt is IfStatement ifStmt)
                {
                    if (ifStmt.ThenStatement is BeginEndBlockStatement thenBlock && thenBlock.StatementList != null)
                    {
                        InsertBlankLinesRecursive(thenBlock.StatementList, sqlFragment);
                    }

                    if (ifStmt.ElseStatement is BeginEndBlockStatement elseBlock && elseBlock.StatementList != null)
                    {
                        InsertBlankLinesRecursive(elseBlock.StatementList, sqlFragment);
                    }
                }

                // ─── Handle WHILE loops ────────────────────────────────────────────────────
                if (stmt is WhileStatement ws && ws.Statement is BeginEndBlockStatement whBlock && whBlock.StatementList != null)
                {
                    InsertBlankLinesRecursive(whBlock.StatementList, sqlFragment);
                }

                // ─── Handle TRY...CATCH ───────────────────────────────────────────────────
                if (stmt is TryCatchStatement tryCatch)
                {
                    if (tryCatch.TryStatements is StatementList tryList)
                    {
                        InsertBlankLinesRecursive(tryList, sqlFragment);
                    }

                    if (tryCatch.CatchStatements is StatementList catchList)
                    {
                        InsertBlankLinesRecursive(catchList, sqlFragment);
                    }
                }
            }
        }


        public static string FormatCode(string oldCode, FormatterOptions settingsOverride = null)
        {
            if (string.IsNullOrWhiteSpace(oldCode))
                return oldCode;

            int compatibilityLevel = 170;
            try
            {
                // Formatting is also used to render the settings preview.  That preview must
                // remain available when no SQL editor is active or SSMS cannot expose its
                // connection service, so connection metadata is only an optional refinement.
                var currentConnection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (Completion.SqlMetadataCache.TryGetCached(currentConnection, out Completion.MetadataSnapshot metadata))
                    compatibilityLevel = metadata.CompatibilityLevel;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Unable to read the active connection while formatting; using the default compatibility level.");
            }

            FormatterOptions formatSettings = settingsOverride ?? SettingsManager.GetFormatterOptions();
            NormalizeOptionGroups(formatSettings);

            TSqlParser sqlParser = CreateParser(compatibilityLevel);
            IList<ParseError> parseErrors;
            TSqlFragment result;
            using (var reader = new StringReader(oldCode))
            {
                result = sqlParser.Parse(reader, out parseErrors);
            }

            // Syntax errors abort the whole operation: the caller keeps the original text,
            // so formatting can never destroy (part of) the code.
            if (parseErrors.Count > 0)
                throw new TSqlFormatException(parseErrors);

            // Comment-only or otherwise statement-less input: keep it exactly as-is.
            if (result is TSqlScript script && script.Batches.All(b => b.Statements.Count == 0))
                return oldCode;

            SqlScriptGenerator gen = CreateGenerator(compatibilityLevel);
            ApplyGeneratorOptions(gen, formatSettings);

            // Keyword casing is the one casing option the generator performs itself.  Every other
            // casing rule (functions, data types, identifiers, variables, aliases) is applied to
            // the generated token stream afterwards, because the generator writes identifiers from
            // the parse tree rather than from the source token text.
            WrittenKeywords written = formatSettings.casing.keywords == TokenCasing.Preserve
                ? BuildWrittenKeywordMap(result.ScriptTokenStream)
                : null;
            string resultCode;
            gen.GenerateScript(result, out resultCode);

            // Safety net: the generated script must never be empty when the source was not.
            if (string.IsNullOrWhiteSpace(resultCode))
                return oldCode;

            resultCode = NormalizeLineEndings(resultCode, oldCode);

            try
            {
                resultCode = ApplyGeneratedCasing(resultCode, formatSettings.casing, written, compatibilityLevel);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "An error occurred while applying the text casing options.");
            }

            try
            {
                resultCode = ApplyTextRewrites(resultCode, formatSettings);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "An error occurred while applying the spacing and single-line options.");
            }

            // Statement layout the ScriptDOM generator cannot express (per-statement line breaks,
            // body indentation, subquery compaction) is applied to the generated token stream.
            try
            {
                resultCode = FormatterLayoutRules.Apply(resultCode, formatSettings, compatibilityLevel);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "An error occurred while applying the layout options.");
            }

            if (formatSettings.HasLegacyRewrites())
            {
                try
                {
                    resultCode = ApplySpecialFormat(resultCode, sqlParser, formatSettings);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "An error occurred while applying special formatting to the code.");
                }
            }

            return resultCode;
        }

        /// <summary>
        /// Locates the top-level statement (or consecutive statements) overlapping the given
        /// character range.  Used to fall back to "format the current statement" when a
        /// selection is not valid T-SQL on its own.  Offsets are 0-based over the full text.
        /// The text is parsed tolerantly: statements that failed to parse are simply absent
        /// from the AST, and the caller only ever replaces a span that re-parses cleanly.
        /// </summary>
        public static bool TryExtractStatementSpan(string fullText, int selectionStart, int selectionEnd, out int spanStart, out int spanEnd)
        {
            spanStart = -1;
            spanEnd = -1;

            if (string.IsNullOrEmpty(fullText))
                return false;

            TSqlParser parser = new TSql170Parser(false);
            IList<ParseError> parseErrors;
            TSqlFragment fragment;
            using (var reader = new StringReader(fullText))
            {
                fragment = parser.Parse(reader, out parseErrors);
            }

            TSqlScript script = fragment as TSqlScript;
            if (script == null)
                return false;

            var tokens = fragment.ScriptTokenStream;
            var tokenStartOffset = new int[tokens.Count];
            int offset = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                tokenStartOffset[i] = offset;
                offset += tokens[i].Text?.Length ?? 0;
            }

            int bestStart = -1, bestEnd = -1;
            foreach (TSqlBatch batch in script.Batches)
            {
                foreach (TSqlStatement stmt in batch.Statements)
                {
                    int s = tokenStartOffset[stmt.FirstTokenIndex];
                    int e = tokenStartOffset[stmt.LastTokenIndex] + tokens[stmt.LastTokenIndex].Text.Length;

                    if (e <= selectionStart || s >= selectionEnd)
                        continue; // no overlap with the selection

                    if (bestStart < 0)
                    {
                        bestStart = s;
                        bestEnd = e;
                    }
                    else
                    {
                        bestStart = Math.Min(bestStart, s);
                        bestEnd = Math.Max(bestEnd, e);
                    }
                }
            }

            if (bestStart < 0 || bestEnd <= bestStart)
                return false;

            spanStart = bestStart;
            spanEnd = bestEnd;
            return true;
        }

        /// <summary>
        /// Projects the formatting profile onto the ScriptDOM generator.  Options ScriptDOM has no
        /// notion of (casing of functions, data types, identifiers, spacing, single-line
        /// compaction) are applied afterwards by <see cref="ApplyProfileRewrites"/>.
        /// </summary>
        /// <summary>
        /// Maps the formatting profile onto the ScriptDOM generator.  Options ScriptDOM has no
        /// notion of (statement-specific line breaks, casing of functions, data types, identifiers
        /// and aliases, spacing, single-line compaction) are applied afterwards by
        /// <see cref="ApplyTokenCasing"/> and <see cref="ApplyTextRewrites"/>.
        ///
        /// The shared options are OR-ed with the statement-specific ones so a user who switches a
        /// single statement group on still gets the layout they asked for, while the shared page
        /// keeps applying to every statement kind.
        /// </summary>
        /// <summary>
        /// Replaces option groups that are absent with their defaults.  A profile typed by hand or
        /// written by an older version can leave a group null, and every consumer below assumes it
        /// is present.
        /// </summary>
        private static void NormalizeOptionGroups(FormatterOptions options)
        {
            if (options.casing == null) options.casing = new CasingOptions();
            if (options.spacing == null) options.spacing = new SpacingOptions();
            if (options.lineBreaks == null) options.lineBreaks = new LineBreakOptions();
            if (options.indent == null) options.indent = new IndentOptions();
            if (options.compact == null) options.compact = new CompactOptions();
            if (options.select == null) options.select = new SelectFormatOptions();
            if (options.subquery == null) options.subquery = new SubqueryFormatOptions();
            if (options.insert == null) options.insert = new InsertFormatOptions();
            if (options.update == null) options.update = new UpdateFormatOptions();
            if (options.delete == null) options.delete = new DeleteFormatOptions();
            if (options.merge == null) options.merge = new MergeFormatOptions();
            if (options.routine == null) options.routine = new RoutineFormatOptions();
            if (options.trigger == null) options.trigger = new TriggerFormatOptions();
            if (options.view == null) options.view = new ViewFormatOptions();
            if (options.createTable == null) options.createTable = new CreateTableFormatOptions();
            if (options.declare == null) options.declare = new DeclareFormatOptions();
            if (options.caseExpression == null) options.caseExpression = new CaseFormatOptions();
            if (options.block == null) options.block = new BlockFormatOptions();
        }

        private static void ApplyGeneratorOptions(SqlScriptGenerator gen, FormatterOptions formatSettings)
        {
            SqlScriptGeneratorOptions options = gen.Options;
            LineBreakOptions lineBreaks = formatSettings.lineBreaks;

            // Comments must never be lost, so preservation is always on.
            options.PreserveComments = true;
            options.AlignClauseBodies = formatSettings.alignClauseBodies;
            options.IncludeSemicolons = formatSettings.includeSemicolons;
            options.KeywordCasing = ToKeywordCasing(formatSettings.casing.keywords);

            if (formatSettings.indent.indentSize >= 1 && formatSettings.indent.indentSize <= 16)
                options.IndentationSize = formatSettings.indent.indentSize;

            // Column / value list stacking.
            options.MultilineSelectElementsList = formatSettings.select.stackSelectColumns || lineBreaks.newLinePerSelectColumn;
            options.MultilineWherePredicatesList = lineBreaks.newLinePerCondition;
            options.MultilineSetClauseItems = formatSettings.update.stackAssignments;
            options.MultilineInsertTargetsList = formatSettings.insert.stackColumnList;
            options.MultilineInsertSourcesList = formatSettings.insert.stackValuesExpressions;
            options.MultilineViewColumnsList = formatSettings.view.stackColumns;
            options.AlignColumnDefinitionFields = formatSettings.createTable.stackColumns;
            options.AlignSetClauseItem = formatSettings.update.stackAssignments;
            options.NewlineFormattedCheckConstraint = formatSettings.createTable.stackColumns;
            options.NewLineFormattedIndexDefinition = formatSettings.createTable.stackColumns;

            // Clause-level line breaks.  SqlScriptGeneratorOptions are global, so a switch that the
            // profile scopes to one statement kind (for example DELETE's "new line before FROM")
            // has to be combined with the shared switch: the layout it asks for is then produced
            // for that clause wherever it appears.
            options.NewLineBeforeFromClause = lineBreaks.newLinePerFromTable || formatSettings.delete.newLineBeforeFrom;
            options.NewLineBeforeWhereClause = lineBreaks.newLineBeforeWhere || formatSettings.delete.whereFollowsSelect;
            options.NewLineBeforeGroupByClause = lineBreaks.newLineBeforeGroupBy;
            options.NewLineBeforeHavingClause = lineBreaks.newLineBeforeGroupBy;
            options.NewLineBeforeOrderByClause = lineBreaks.newLineBeforeOrderBy;
            options.NewLineBeforeJoinClause = formatSettings.select.newLineBeforeJoin
                || lineBreaks.newLineBeforeJoin
                || formatSettings.delete.newLineBeforeJoin;
            options.NewLineBeforeOutputClause = formatSettings.insert.newLineBeforeOutput
                || formatSettings.update.newLineBeforeOutput
                || formatSettings.delete.newLineBeforeOutput
                || formatSettings.merge.newLineBeforeOutput;
            options.NewLineBeforeOffsetClause = lineBreaks.newLineBeforeOrderBy;
            options.AsKeywordOnOwnLine = formatSettings.routine.newLineBeforeAs
                || formatSettings.view.newLineBeforeAs
                || formatSettings.trigger.newLineBeforeAs;

            // Parenthesis placement for stacked lists.
            options.NewLineBeforeOpenParenthesisInMultilineList = formatSettings.subquery.newLineBeforeOpenParenthesis
                || formatSettings.createTable.newLineAfterOpenParenthesis;
            options.NewLineBeforeCloseParenthesisInMultilineList = formatSettings.subquery.newLineBeforeCloseParenthesis
                || formatSettings.createTable.newLineBeforeCloseParenthesis;

            // Indentation nuance.
            options.IndentSetClause = formatSettings.update.stackAssignments;
            options.IndentViewBody = formatSettings.view.indentSubquery;

            // Spacing nuance.  A space between a name and its parenthesis is not a generator option;
            // ApplySpacing adds or removes it.  A data type keeps its parameters attached.
            options.SpaceBetweenDataTypeAndParameters = false;
            options.SpaceBetweenParametersInDataType = formatSettings.spacing.spaceAfterComma;

            if (formatSettings.block.statementPerLine)
                options.NumNewlinesAfterStatement = 1;
        }

        private static KeywordCasing ToKeywordCasing(TokenCasing casing)
        {
            switch (casing)
            {
                case TokenCasing.Lowercase:
                    return KeywordCasing.Lowercase;
                case TokenCasing.PascalCase:
                    return KeywordCasing.PascalCase;
                default:
                    return KeywordCasing.Uppercase;
            }
        }

        /// <summary>
        /// Applies the profile options ScriptDOM cannot express as text rewrites: comma, operator
        /// and parenthesis spacing, then single-line compaction for short statements.
        /// </summary>
        private static string ApplyTextRewrites(string script, FormatterOptions options)
        {
            string text = script;

            if (!options.spacing.spaceAfterComma || options.spacing.spaceBeforeComma
                || !options.spacing.spaceAroundOperators || options.spacing.spaceBetweenFunctionAndParenthesis)
            {
                text = ApplySpacing(text, options.spacing);
            }

            if (options.compact.keepShortQuerySingleLine)
            {
                // "Keep single lines within the right margin" decides the width on its own,
                // otherwise the shorter single-line threshold applies.
                text = CollapseToSingleLine(text, options.compact.keepWithinRightMargin
                    ? Math.Max(1, options.compact.rightMargin)
                    : options.compact.singleLineThreshold);
            }

            return text;
        }

        private static readonly HashSet<string> DataTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bigint", "binary", "bit", "char", "date", "datetime", "datetime2", "datetimeoffset", "decimal",
            "float", "geography", "geometry", "hierarchyid", "image", "int", "money", "nchar", "ntext",
            "numeric", "nvarchar", "real", "smalldatetime", "smallint", "smallmoney", "sql_variant",
            "sysname", "text", "time", "timestamp", "tinyint", "uniqueidentifier", "varbinary", "varchar",
            "xml"
        };

        /// <summary>
        /// Applies the casing profile to the generated script.  Only the text of existing tokens is
        /// rewritten, so nothing can be added, dropped or reordered.
        /// </summary>
        private static string ApplyGeneratedCasing(string generated, CasingOptions casing,
            WrittenKeywords writtenKeywords, int compatibilityLevel)
        {
            bool casingIdentifiers = casing.identifiers != TokenCasing.Preserve
                || casing.aliases != TokenCasing.Preserve
                || casing.functions != TokenCasing.Preserve
                || casing.dataTypes != TokenCasing.Preserve
                || casing.variables != TokenCasing.Preserve;
            if (!casingIdentifiers && writtenKeywords == null) return generated;

            TSqlParser parser = CreateParser(compatibilityLevel);
            IList<ParseError> parseErrors;
            TSqlFragment fragment;
            using (var reader = new StringReader(generated))
                fragment = parser.Parse(reader, out parseErrors);

            TSqlParserToken[] tokens = fragment?.ScriptTokenStream?.ToArray();
            if (tokens == null || tokens.Length == 0 || parseErrors.Count > 0) return generated;

            if (casingIdentifiers) ApplyTokenCasing(tokens, casing);
            if (writtenKeywords != null) RestoreWrittenKeywordCasing(tokens, writtenKeywords);

            StringBuilder builder = new StringBuilder(generated.Length);
            foreach (TSqlParserToken token in tokens) builder.Append(token.Text);
            return builder.ToString();
        }

        /// <summary>
        /// How the source spelled its keywords, so "keep as written" can be honoured even though
        /// the script generator can only upper-, lower- or Pascal-case keywords.
        /// </summary>
        private sealed class WrittenKeywords
        {
            public readonly Dictionary<TSqlTokenType, string> Spellings = new Dictionary<TSqlTokenType, string>();

            /// <summary>
            /// The casing most of the source keywords use.  Keywords the script generator adds
            /// itself (AS, INNER) were never written down, so they follow that majority instead of
            /// standing out in a different case.
            /// </summary>
            public TokenCasing Dominant = TokenCasing.Preserve;
        }

        /// <summary>
        /// Records how the source spelled each keyword.  A keyword the source spelled in several
        /// ways keeps its most common spelling.
        /// </summary>
        private static WrittenKeywords BuildWrittenKeywordMap(IList<TSqlParserToken> tokens)
        {
            if (tokens == null) return null;

            var counts = new Dictionary<TSqlTokenType, Dictionary<string, int>>();
            int lower = 0, upper = 0, pascal = 0;
            foreach (TSqlParserToken token in tokens)
            {
                if (token == null || !IsKeywordToken(token)) continue;
                if (!counts.TryGetValue(token.TokenType, out Dictionary<string, int> spellings))
                    counts[token.TokenType] = spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                spellings[token.Text] = spellings.TryGetValue(token.Text, out int count) ? count + 1 : 1;

                if (string.Equals(token.Text, token.Text.ToLowerInvariant(), StringComparison.Ordinal)) lower++;
                else if (string.Equals(token.Text, token.Text.ToUpperInvariant(), StringComparison.Ordinal)) upper++;
                else pascal++;
            }

            var result = new WrittenKeywords();
            foreach (var pair in counts)
            {
                string best = null;
                int bestCount = -1;
                foreach (var spelling in pair.Value)
                {
                    if (spelling.Value <= bestCount) continue;
                    best = spelling.Key;
                    bestCount = spelling.Value;
                }

                if (best != null) result.Spellings[pair.Key] = best;
            }

            result.Dominant = lower >= upper && lower >= pascal
                ? TokenCasing.Lowercase
                : upper >= pascal ? TokenCasing.Uppercase : TokenCasing.PascalCase;
            return result.Spellings.Count == 0 ? null : result;
        }

        private static void RestoreWrittenKeywordCasing(TSqlParserToken[] tokens, WrittenKeywords written)
        {
            foreach (TSqlParserToken token in tokens)
            {
                if (token == null || !IsKeywordToken(token)) continue;

                if (written.Spellings.TryGetValue(token.TokenType, out string text))
                {
                    // Only the spelling may change, never the keyword itself.
                    if (!string.Equals(token.Text, text, StringComparison.Ordinal)
                        && string.Equals(token.Text, text, StringComparison.OrdinalIgnoreCase))
                        token.Text = text;
                    continue;
                }

                token.Text = ApplyCasing(token.Text, written.Dominant);
            }
        }

        /// <summary>True for tokens that spell a keyword rather than carry a name or a value.</summary>
        private static bool IsKeywordToken(TSqlParserToken token)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.Identifier:
                case TSqlTokenType.QuotedIdentifier:
                case TSqlTokenType.Variable:
                case TSqlTokenType.AsciiStringLiteral:
                case TSqlTokenType.UnicodeStringLiteral:
                case TSqlTokenType.Integer:
                case TSqlTokenType.Numeric:
                case TSqlTokenType.Real:
                case TSqlTokenType.Money:
                case TSqlTokenType.HexLiteral:
                case TSqlTokenType.WhiteSpace:
                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                case TSqlTokenType.EndOfFile:
                case TSqlTokenType.Dot:
                case TSqlTokenType.Comma:
                case TSqlTokenType.Semicolon:
                case TSqlTokenType.LeftParenthesis:
                case TSqlTokenType.RightParenthesis:
                case TSqlTokenType.None:
                    return false;
            }

            string text = token.Text;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char value in text)
                if (!char.IsLetter(value) && value != '_') return false;
            return true;
        }

        private static void ApplyTokenCasing(TSqlParserToken[] tokens, CasingOptions casing)
        {
            for (int index = 0; index < tokens.Length; index++)
            {
                TSqlParserToken token = tokens[index];
                if (token == null || !IsPlainName(token.Text)) continue;

                // Parameter names ("@Id") follow the variable switch, whatever the token type
                // ScriptDOM assigned to them.
                if (token.Text[0] == '@')
                {
                    token.Text = ApplyCasing(token.Text, casing.variables);
                    continue;
                }

                switch (token.TokenType)
                {
                    case TSqlTokenType.Variable:
                        token.Text = ApplyCasing(token.Text, casing.variables);
                        break;

                    case TSqlTokenType.AsciiStringLiteral:
                    case TSqlTokenType.UnicodeStringLiteral:
                        break;

                    case TSqlTokenType.Identifier:
                        if (IsDataTypePosition(token, tokens, index))
                            token.Text = ApplyCasing(token.Text, casing.dataTypes);
                        else if (IsAliasPosition(token, tokens, index))
                            token.Text = ApplyCasing(token.Text, casing.aliases);
                        else if (IsFunctionNamePosition(token, tokens, index))
                            token.Text = ApplyCasing(token.Text, casing.functions);
                        else
                            token.Text = ApplyCasing(token.Text, casing.identifiers);
                        break;

                    default:
                        // Words that are keywords of the language keep the casing the keyword
                        // option asks for; only built-in type names are re-cased here.
                        if (IsDataTypePosition(token, tokens, index))
                            token.Text = ApplyCasing(token.Text, casing.dataTypes);
                        break;
                }
            }
        }

        /// <summary>
        /// An alias is the name that follows the AS keyword ("FROM dbo.Customer AS c",
        /// "SELECT Total AS Amount").  Only the explicit AS form is treated as an alias: a bare
        /// trailing identifier is far more often a table or column name, and re-casing it under the
        /// alias switch would be wrong.
        /// </summary>
        private static bool IsAliasPosition(TSqlParserToken token, TSqlParserToken[] tokens, int index)
        {
            if (token.TokenType != TSqlTokenType.Identifier) return false;
            if (token.Text.IndexOf('.') >= 0) return false;

            int previous = PreviousMeaningfulToken(tokens, index - 1);
            return previous >= 0 && tokens[previous].TokenType == TSqlTokenType.As;
        }

        /// <summary>
        /// Only plain (unbracketed, unquoted) names may be re-cased: rewriting "My Col" inside
        /// brackets or quotes would change the identifier itself.
        /// </summary>
        private static bool IsPlainName(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            char first = text[0];
            if (!char.IsLetter(first) && first != '_' && first != '@' && first != '#') return false;

            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '@' && c != '#' && c != '$') return false;
            }

            return true;
        }

        private static bool IsFunctionNamePosition(TSqlParserToken token, TSqlParserToken[] tokens, int index)
        {
            if (token.TokenType != TSqlTokenType.Identifier) return false;

            // A call is an identifier directly followed by "(" and not qualified by a schema.
            int next = NextMeaningfulToken(tokens, index + 1);
            if (next < 0 || tokens[next].TokenType != TSqlTokenType.LeftParenthesis) return false;

            int previous = PreviousMeaningfulToken(tokens, index - 1);
            return previous < 0 || tokens[previous].TokenType != TSqlTokenType.Dot;
        }

        private static bool IsDataTypePosition(TSqlParserToken token, TSqlParserToken[] tokens, int index)
        {
            // ScriptDOM tokenizes each keyword as its own token type, so data types are
            // recognised by their text rather than by a token category.
            if (token.TokenType != TSqlTokenType.Identifier && !DataTypeNames.Contains(token.Text))
                return false;

            // Types may carry parameters ("varchar(50)") and must not be schema-qualified.
            int previous = PreviousMeaningfulToken(tokens, index - 1);
            if (previous >= 0 && tokens[previous].TokenType == TSqlTokenType.Dot) return false;

            string name = token.Text;
            if (!DataTypeNames.Contains(name)) return false;

            int next = NextMeaningfulToken(tokens, index + 1);
            if (next < 0) return true;

            TSqlTokenType nextType = tokens[next].TokenType;
            return nextType == TSqlTokenType.LeftParenthesis
                || nextType == TSqlTokenType.Comma
                || nextType == TSqlTokenType.RightParenthesis
                || nextType == TSqlTokenType.Identifier
                || nextType == TSqlTokenType.QuotedIdentifier
                || nextType == TSqlTokenType.Null
                || nextType == TSqlTokenType.Semicolon;
        }

        private static int NextMeaningfulToken(TSqlParserToken[] tokens, int start)
        {
            for (int i = start; i < tokens.Length; i++)
                if (!IsTrivia(tokens[i])) return i;
            return -1;
        }

        private static int PreviousMeaningfulToken(TSqlParserToken[] tokens, int start)
        {
            for (int i = start; i >= 0; i--)
                if (!IsTrivia(tokens[i])) return i;
            return -1;
        }

        private static bool IsTrivia(TSqlParserToken token)
        {
            return token.TokenType == TSqlTokenType.WhiteSpace
                || token.TokenType == TSqlTokenType.SingleLineComment
                || token.TokenType == TSqlTokenType.MultilineComment;
        }

        private static string ApplyCasing(string text, TokenCasing casing)
        {
            switch (casing)
            {
                case TokenCasing.Uppercase:
                    return text.ToUpperInvariant();
                case TokenCasing.Lowercase:
                    return text.ToLowerInvariant();
                case TokenCasing.PascalCase:
                    return text.Length == 0
                        ? text
                        : char.ToUpperInvariant(text[0]) + text.Substring(1).ToLowerInvariant();
                default:
                    return text;
            }
        }

        /// <summary>
        /// Rewrites comma, operator and function-parenthesis spacing outside string literals,
        /// bracketed identifiers and comments.
        /// </summary>
        private static string ApplySpacing(string script, SpacingOptions spacing)
        {
            StringBuilder builder = new StringBuilder(script.Length + 32);
            int index = 0;

            while (index < script.Length)
            {
                char current = script[index];

                // Skip regions where a rewrite would change data rather than formatting.
                if (current == '\'')
                {
                    int end = SkipQuoted(script, index, '\'');
                    builder.Append(script, index, end - index);
                    index = end;
                    continue;
                }
                if (current == '"' || current == '[')
                {
                    char close = current == '"' ? '"' : ']';
                    int end = SkipQuoted(script, index, close);
                    builder.Append(script, index, end - index);
                    index = end;
                    continue;
                }
                if (current == '-' && index + 1 < script.Length && script[index + 1] == '-')
                {
                    int end = script.IndexOf('\n', index);
                    if (end < 0) end = script.Length;
                    builder.Append(script, index, end - index);
                    index = end;
                    continue;
                }
                if (current == '/' && index + 1 < script.Length && script[index + 1] == '*')
                {
                    int end = script.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    end = end < 0 ? script.Length : end + 2;
                    builder.Append(script, index, end - index);
                    index = end;
                    continue;
                }

                if (current == ',')
                {
                    if (spacing.spaceBeforeComma)
                    {
                        // The generator writes "a, b"; a space before the comma has to be added.
                        if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                            builder.Append(' ');
                    }
                    else
                    {
                        TrimTrailingSpaces(builder, true);
                    }

                    builder.Append(',');
                    index++;
                    if (spacing.spaceAfterComma) builder.Append(' ');
                    while (index < script.Length && script[index] == ' ') index++;
                    continue;
                }

                if (current == '(')
                {
                    if (spacing.spaceBetweenFunctionAndParenthesis)
                    {
                        // "count (1)": the generator writes "count(1)", so the space is added here.
                        if (builder.Length > 0 && builder[builder.Length - 1] != ' '
                            && (char.IsLetterOrDigit(builder[builder.Length - 1]) || builder[builder.Length - 1] == '_'))
                            builder.Append(' ');
                    }
                    // Remove the space a previous rewrite may have inserted between a name and "(".
                    else if (builder.Length > 0 && builder[builder.Length - 1] == ' ')
                    {
                        int lookback = builder.Length - 2;
                        if (lookback >= 0 && (char.IsLetterOrDigit(builder[lookback]) || builder[lookback] == '_'))
                            builder.Length--;
                    }

                    builder.Append(current);
                    index++;
                    continue;
                }

                if (IsOperatorCharacter(current) && !IsUnaryContext(script, index))
                {
                    TrimTrailingSpaces(builder, true);
                    builder.Append(current);
                    index++;
                    while (index < script.Length && script[index] == ' ') index++;
                    if (spacing.spaceAroundOperators) builder.Append(' ');
                    continue;
                }

                builder.Append(current);
                index++;
            }

            return builder.ToString();
        }

        private static int SkipQuoted(string script, int start, char close)
        {
            int index = start + 1;
            while (index < script.Length)
            {
                if (script[index] == close)
                {
                    // Doubled delimiters escape themselves inside both literals and brackets.
                    if (index + 1 < script.Length && script[index + 1] == close)
                    {
                        index += 2;
                        continue;
                    }

                    return index + 1;
                }

                index++;
            }

            return script.Length;
        }

        private static bool IsOperatorCharacter(char value)
        {
            return value == '=' || value == '<' || value == '>' || value == '+' || value == '*';
        }

        private static bool IsUnaryContext(string script, int index)
        {
            // "+1" / "-1" / "SELECT *" must not gain spaces around the sign or asterisk.
            if (script[index] == '*') return true;

            int previous = index - 1;
            while (previous >= 0 && script[previous] == ' ') previous--;
            if (previous < 0) return true;

            char before = script[previous];
            return before == '(' || before == ',' || before == '=' || before == '<' || before == '>'
                || before == '+' || before == '-' || before == '*' || before == '/';
        }

        private static void TrimTrailingSpaces(StringBuilder builder, bool enabled)
        {
            if (!enabled) return;
            while (builder.Length > 0 && builder[builder.Length - 1] == ' ') builder.Length--;
        }

        /// <summary>
        /// Collapses a statement onto one line when it is short enough and contains no comments.
        /// Anything longer or commented is left alone so the layout stays readable.
        /// </summary>
        private static string CollapseToSingleLine(string script, int threshold)
        {
            if (threshold <= 0 || script.IndexOf("--", StringComparison.Ordinal) >= 0 || script.IndexOf("/*", StringComparison.Ordinal) >= 0)
                return script;

            string collapsed = System.Text.RegularExpressions.Regex.Replace(script, @"\s+", " ").Trim();
            if (collapsed.Length > threshold) return script;
            if (collapsed.IndexOf('\'') >= 0) return script;

            return collapsed;
        }

        /// <summary>
        /// The ScriptDom generator always emits CRLF; keep LF-only documents LF-only.
        /// </summary>
        private static string NormalizeLineEndings(string text, string reference)
        {
            bool hasNewline = reference.IndexOf('\n') >= 0;
            bool hasCRLF = reference.IndexOf("\r\n", StringComparison.Ordinal) >= 0;

            if (hasNewline && !hasCRLF)
                return text.Replace("\r\n", "\n");

            return text;
        }

        internal static TSqlParser CreateParser(int level)
        {
            if (level >= 170) return new TSql170Parser(false);
            if (level >= 160) return new TSql160Parser(false);
            if (level >= 150) return new TSql150Parser(false);
            if (level >= 140) return new TSql140Parser(false);
            if (level >= 130) return new TSql130Parser(false);
            return new TSql120Parser(false);
        }

        private static SqlScriptGenerator CreateGenerator(int level)
        {
            if (level >= 170) return new Sql170ScriptGenerator();
            if (level >= 160) return new Sql160ScriptGenerator();
            if (level >= 150) return new Sql150ScriptGenerator();
            if (level >= 140) return new Sql140ScriptGenerator();
            if (level >= 130) return new Sql130ScriptGenerator();
            return new Sql120ScriptGenerator();
        }

        private static void RunFormatCase(Action action, string caseName)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, string.Format(CultureInfo.InvariantCulture, "The formatting option '{0}' failed and was skipped.", caseName));
            }
        }

        private static string ApplySpecialFormat(string oldCode, TSqlParser sqlParser, FormatterOptions formatSettings)
        {
            IList<ParseError> parseErrors;

            TSqlFragment sqlFragment;
            using (var reader = new StringReader(oldCode))
            {
                sqlFragment = sqlParser.Parse(reader, out parseErrors);
            }

            // Never run token surgery on a broken parse tree: keep the generated code as-is.
            if (parseErrors.Count > 0)
            {
                MSSQLToolPackage._logger?.Error("Special formatting skipped: the generated script failed to re-parse.");
                return oldCode;
            }

            OwnVisitor visitor = new OwnVisitor();
            sqlFragment.Accept(visitor);

            var tokens = sqlFragment.ScriptTokenStream;

            // special case #1 - remove new line after JOIN
            if (formatSettings.removeNewLineAfterJoin)
                RunFormatCase(() => RemoveNewLineAfterJoinCase(tokens, visitor.QualifiedJoins), "RemoveNewLineAfterJoin");

            // special case #2 - JOIN .. ON -> add an indent before ON
            if (formatSettings.addTabAfterJoinOn)
                RunFormatCase(() => AddIndentAfterJoinOnCase(tokens, visitor.QualifiedJoins, formatSettings.indent.indentSize), "AddTabAfterJoinOn");

            // special case #3 - CROSS/OUTER JOIN/APPLY should be on the new line
            if (formatSettings.moveCrossJoinToNewLine)
                RunFormatCase(() =>
                {
                    foreach (UnqualifiedJoin CrossJoin in visitor.UnqualifiedJoins)
                    {
                        MoveJoinKeywordToNewLine(
                            sqlFragment,
                            CrossJoin.SecondTableReference.FirstTokenIndex,
                            CrossJoin.StartColumn,
                            TSqlTokenType.Cross,
                            TSqlTokenType.Outer);
                    }
                }, "MoveCrossJoinToNewLine");

            // special case #4 - CASE <new line + indent> WHEN <new line + deeper indent> THEN <new line + indent> ELSE <new line> END
            if (formatSettings.formatCaseAsMultiline)
                RunFormatCase(() => FormatCaseAsMultilineCase(tokens, visitor.CaseExpressions), "FormatCaseAsMultiline");

            // special case #5.1 - add two new lines after each PROC/FUNC/TRIGGER statement
            // special case #5.2 - also add blank lines in every anonymous batch
            if (formatSettings.addNewLineBetweenStatementsInBlocks)
                RunFormatCase(() =>
                {
                    foreach (StatementList topLevel in visitor.ProcBodies)
                    {
                        InsertBlankLinesRecursive(topLevel, sqlFragment);
                    }

                    if (sqlFragment is TSqlScript script)
                    {
                        foreach (var batch in script.Batches)
                        {
                            // build a StatementList wrapper around the batch's statements
                            var anonList = new StatementList { Statements = { } };
                            foreach (var stmt in batch.Statements)
                                anonList.Statements.Add(stmt);

                            InsertBlankLinesRecursive(anonList, sqlFragment, skipTopLevel: true);
                        }
                    }
                }, "AddNewLineBetweenStatementsInBlocks");

            // special case #6 - break sproc parameters onto separate lines
            if (formatSettings.breakSprocParametersPerLine)
                RunFormatCase(() => BreakSprocParametersPerLineCase(tokens, visitor.ExecStatements), "BreakSprocParametersPerLine");

            // special case #7 - uppercase built-in function names
            if (formatSettings.uppercaseBuiltInFunctions)
                RunFormatCase(() => UppercaseBuiltInFunctionsCase(tokens, visitor.FunctionCalls), "UppercaseBuiltInFunctions");

            // special case #8 – do not indent BEGIN/END in control‐flow
            if (formatSettings.unindentBeginEndBlocks)
                RunFormatCase(() =>
                {
                    string indentString = new string(' ', Math.Max(1, formatSettings.indent.indentSize));

                    foreach (var beb in visitor.BeginEndBlocks)
                    {
                        // include the whitespace before BEGIN (at FirstTokenIndex-1)
                        // and every whitespace token up through the token before END
                        int startIdx = Math.Max(0, beb.FirstTokenIndex - 1);
                        int endIdx = Math.Max(0, beb.LastTokenIndex - 1);

                        for (int ti = startIdx; ti <= endIdx && ti < tokens.Count; ti++)
                        {
                            var tok = tokens[ti];
                            if (tok.TokenType == TSqlTokenType.WhiteSpace && tok.Column == 1)
                                tok.Text = RemoveOneIndent(tok.Text, indentString);
                        }
                    }
                }, "UnindentBeginEndBlocks");

            // special case #9 - break DECLARE variables per line
            if (formatSettings.breakVariableDefinitionsPerLine)
                RunFormatCase(() => BreakVariableDefinitionsPerLineCase(tokens, visitor.DeclareStatements), "BreakVariableDefinitionsPerLine");

            // special case #10 - split sproc definition parameters per line + add one tab
            if (formatSettings.breakSprocDefinitionParametersPerLine)
                RunFormatCase(() => BreakSprocDefinitionParametersPerLineCase(tokens, visitor.SprocDefinitionsCreate, visitor.SprocDefinitionsAlter, visitor.SprocDefinitionsCreateAlter), "BreakSprocDefinitionParametersPerLine");

            // return full recompiled result
            StringBuilder sqlText = new StringBuilder();
            foreach (var Token in tokens)
            {
                sqlText.Append(Token.Text);
            }

            return sqlText.ToString();
        }

        private static void RemoveNewLineAfterJoinCase(IList<TSqlParserToken> tokens, List<QualifiedJoin> qualifiedJoins)
        {
            foreach (QualifiedJoin QJoin in qualifiedJoins)
            {
                int NextTokenNumber = QJoin.SecondTableReference.FirstTokenIndex;

                while (true)
                {
                    TSqlParserToken NextToken = tokens[NextTokenNumber - 1];

                    if (NextToken.TokenType == TSqlTokenType.WhiteSpace)
                        if (NextToken.Text == "\r\n")
                            NextToken.Text = " ";
                        else if (NextToken.Text.Trim() == "")
                            NextToken.Text = "";

                    if (NextToken.TokenType == TSqlTokenType.Join)
                        break;

                    NextTokenNumber -= 1;

                    //just in case
                    if (NextTokenNumber < 1)
                        break;
                }
            }
        }

        private static void AddIndentAfterJoinOnCase(IList<TSqlParserToken> tokens, List<QualifiedJoin> qualifiedJoins, int indentationSize)
        {
            foreach (QualifiedJoin QJoin in qualifiedJoins)
            {
                int NextTokenNumber = QJoin.SearchCondition.FirstTokenIndex;

                while (true)
                {
                    TSqlParserToken NextToken = tokens[NextTokenNumber];

                    if (NextToken.TokenType == TSqlTokenType.On)
                    { // replace previous white-space with the new line and a number of spaces for offset

                        TSqlParserToken PreviousToken = tokens[NextTokenNumber - 1];
                        if (PreviousToken.TokenType == TSqlTokenType.WhiteSpace)
                        {
                            PreviousToken.Text = PreviousToken.Text + new string(' ', Math.Max(1, indentationSize));
                            break;
                        }

                    }

                    NextTokenNumber -= 1;

                    //just in case
                    if (NextTokenNumber < 1)
                        break;
                }
            }
        }

        private static readonly HashSet<TSqlTokenType> ClauseBoundaryTokenTypes = new HashSet<TSqlTokenType>
        {
            TSqlTokenType.When, TSqlTokenType.Case, TSqlTokenType.Else, TSqlTokenType.End,
            TSqlTokenType.Comma, TSqlTokenType.Semicolon, TSqlTokenType.On, TSqlTokenType.Join,
            TSqlTokenType.Where, TSqlTokenType.From, TSqlTokenType.Select
        };

        /// <summary>
        /// Walks backwards from startIndex to locate the given clause keyword (THEN/ELSE),
        /// never crossing into a neighbouring clause.  Returns the keyword's token index or -1.
        /// </summary>
        private static int FindClauseKeywordBackward(IList<TSqlParserToken> tokens, int startIndex, TSqlTokenType keywordType)
        {
            for (int i = startIndex; i >= 0; i--)
            {
                TSqlParserToken token = tokens[i];
                if (token.TokenType == keywordType)
                    return i;

                if (ClauseBoundaryTokenTypes.Contains(token.TokenType))
                    return -1;
            }

            return -1;
        }

        private static void FormatCaseAsMultilineCase(IList<TSqlParserToken> tokens, List<SearchedCaseExpression> caseExpressions)
        {
            foreach (SearchedCaseExpression CaseExpr in caseExpressions)
            {
                // add new line and spaces+4 before WHEN
                foreach (WhenClause WC in CaseExpr.WhenClauses)
                {
                    int FirstTokenNumber = WC.FirstTokenIndex;

                    int WhenIdent = 0;

                    while (true)
                    {
                        TSqlParserToken NextToken = tokens[FirstTokenNumber];

                        if (NextToken.TokenType == TSqlTokenType.WhiteSpace)
                        { // replace previous white-space with the new line and a number of spaces for offset
                            NextToken.Text = "\r\n" + new string(' ', CaseExpr.StartColumn + 4);

                            WhenIdent = CaseExpr.StartColumn + 4;

                            break;
                        }

                        FirstTokenNumber -= 1;

                        //just in case
                        if (FirstTokenNumber < 0)
                            break;
                    }

                    //multi-line expression inside WHEN might be too far to the right, move it to the left
                    if (WhenIdent > 0 && WhenIdent != WC.StartColumn)
                    {
                        MoveClauseLinesToLeft(tokens, WC.FirstTokenIndex, WC.LastTokenIndex, WhenIdent + 5);
                    }
                }

                // add new line and spaces+8 before THEN, anchored on the THEN keyword
                foreach (WhenClause WC in CaseExpr.WhenClauses)
                {
                    int ThenKeywordIndex = FindClauseKeywordBackward(tokens, WC.ThenExpression.FirstTokenIndex - 1, TSqlTokenType.Then);

                    if (ThenKeywordIndex > 0 && tokens[ThenKeywordIndex - 1].TokenType == TSqlTokenType.WhiteSpace)
                    {
                        int ThenIdent = CaseExpr.StartColumn + 8;
                        tokens[ThenKeywordIndex - 1].Text = "\r\n" + new string(' ', ThenIdent);

                        //multi-line expression inside THEN might be too far to the right, move it to the left
                        if (ThenIdent != WC.ThenExpression.StartColumn)
                        {
                            MoveClauseLinesToLeft(tokens, WC.ThenExpression.FirstTokenIndex, WC.ThenExpression.LastTokenIndex, ThenIdent + 5);
                        }
                    }
                }

                // add new line and spaces+4 before ELSE, anchored on the ELSE keyword
                if (CaseExpr.ElseExpression != null)
                {
                    int ElseKeywordIndex = FindClauseKeywordBackward(tokens, CaseExpr.ElseExpression.FirstTokenIndex - 1, TSqlTokenType.Else);

                    if (ElseKeywordIndex > 0 && tokens[ElseKeywordIndex - 1].TokenType == TSqlTokenType.WhiteSpace)
                    {
                        int ElseIdent = CaseExpr.StartColumn + 4;
                        tokens[ElseKeywordIndex - 1].Text = "\r\n" + new string(' ', ElseIdent);

                        //multi-line expression inside ELSE might be too far to the right, move it to the left
                        if (ElseIdent != CaseExpr.ElseExpression.StartColumn)
                        {
                            MoveClauseLinesToLeft(tokens, CaseExpr.ElseExpression.FirstTokenIndex, CaseExpr.ElseExpression.LastTokenIndex, ElseIdent + 5);
                        }
                    }
                }

                // add new line and spaces before END
                int LastTokenNumber = CaseExpr.LastTokenIndex;

                while (true)
                {
                    TSqlParserToken NextToken = tokens[LastTokenNumber];

                    if (NextToken.TokenType == TSqlTokenType.WhiteSpace)
                    { // replace previous white-space with the new line and a number of spaces for offset
                        NextToken.Text = "\r\n" + new string(' ', CaseExpr.StartColumn - 1);
                        break;
                    }

                    LastTokenNumber -= 1;

                    //just in case
                    if (LastTokenNumber < 0)
                        break;
                }
            }
        }

        /// <summary>
        /// Whitespace tokens that start a line inside [firstTokenIndex, lastTokenIndex) are
        /// re-indented to the given width, so multi-line expressions follow their clause.
        /// </summary>
        private static void MoveClauseLinesToLeft(IList<TSqlParserToken> tokens, int firstTokenIndex, int lastTokenIndex, int indentWidth)
        {
            for (int i = firstTokenIndex; i < lastTokenIndex; i++)
            {
                TSqlParserToken token = tokens[i];
                if (token.TokenType == TSqlTokenType.WhiteSpace && token.Column == 1)
                {
                    token.Text = new string(' ', indentWidth);
                }
            }
        }

        private static void BreakSprocParametersPerLineCase(IList<TSqlParserToken> tokens, List<ExecuteStatement> execStatements)
        {
            // for every EXEC … call
            foreach (var execStmt in execStatements)
            {
                var spec = execStmt.ExecuteSpecification;
                if (spec == null)
                    continue;

                // Dynamic SQL, variables and similar targets have no procedure parameters.
                if (!(spec.ExecutableEntity is ExecutableProcedureReference execEntry))
                    continue;

                if (execEntry.Parameters.Count > 1)
                {
                    // 0) figure out how much indent EXEC itself already has
                    string baseIndent = "";
                    int wsIdxBeforeExec = execStmt.FirstTokenIndex - 1;
                    if (wsIdxBeforeExec >= 0 && tokens[wsIdxBeforeExec].TokenType == TSqlTokenType.WhiteSpace)
                    {
                        var wsText = tokens[wsIdxBeforeExec].Text;
                        // grab whatever is after the last newline
                        int lastNl = wsText.LastIndexOf("\r\n", StringComparison.Ordinal);
                        baseIndent = lastNl >= 0
                            ? wsText.Substring(lastNl + 2)
                            : wsText;
                    }

                    // 1) put first param on its own indented line
                    int insertPos = execEntry.ProcedureReference.LastTokenIndex + 1;
                    if (insertPos < tokens.Count && tokens[insertPos].TokenType == TSqlTokenType.WhiteSpace)
                        tokens[insertPos].Text = "\r\n"
                                                + baseIndent
                                                + "\t";

                    // 2) for every comma between params, break+indent by the same amount
                    for (int i = spec.FirstTokenIndex; i <= spec.LastTokenIndex; i++)
                    {
                        if (tokens[i].TokenType == TSqlTokenType.Comma)
                        {
                            int wsAfterComma = i + 1;
                            if (wsAfterComma < tokens.Count
                             && tokens[wsAfterComma].TokenType == TSqlTokenType.WhiteSpace)
                            {
                                tokens[wsAfterComma].Text = " "         // space after comma
                                                         + "\r\n"
                                                         + baseIndent
                                                         + "\t";
                            }
                        }
                    }
                }
            }
        }

        private static void UppercaseBuiltInFunctionsCase(IList<TSqlParserToken> tokens, List<FunctionCall> functionCalls)
        {
            foreach (var func in functionCalls)
            {
                // skip if schema-qualified (e.g. dbo.MyFunc)
                if (func.CallTarget != null)
                    continue;

                // FunctionName spans one or more identifier tokens immediately before "("
                int start = func.FunctionName.FirstTokenIndex;
                int end = func.FunctionName.LastTokenIndex;

                for (int i = start; i <= end; i++)
                {
                    var tok = tokens[i];
                    if (tok.TokenType == TSqlTokenType.Identifier)
                        tok.Text = tok.Text.ToUpperInvariant();
                }
            }
        }

        private static void BreakVariableDefinitionsPerLineCase(IList<TSqlParserToken> tokens, List<DeclareVariableStatement> declareStatements)
        {
            foreach (var decl in declareStatements)
            {
                // only split if more than one variable
                if (decl.Declarations.Count > 1)
                {
                    // figure out how much indent DECLARE already has
                    string baseIndent = "";
                    int wsBefore = decl.FirstTokenIndex - 1;
                    if (wsBefore >= 0 && tokens[wsBefore].TokenType == TSqlTokenType.WhiteSpace)
                    {
                        var txt = tokens[wsBefore].Text;
                        int lastNl = txt.LastIndexOf("\r\n", StringComparison.Ordinal);
                        baseIndent = lastNl >= 0
                            ? txt.Substring(lastNl + 2)
                            : txt;
                    }

                    // 1) break after the first declaration
                    int endOfFirst = decl.Declarations[0].LastTokenIndex + 1;
                    if (endOfFirst < tokens.Count && tokens[endOfFirst].TokenType == TSqlTokenType.WhiteSpace)
                    {
                        tokens[endOfFirst].Text = "\r\n"
                                                 + baseIndent
                                                 + "\t";
                    }

                    // 2) for each comma in the DECLARE, break+indent
                    for (int i = decl.FirstTokenIndex; i <= decl.LastTokenIndex; i++)
                    {
                        if (tokens[i].TokenType == TSqlTokenType.Comma)
                        {
                            int afterComma = i + 1;
                            if (afterComma < tokens.Count
                                && tokens[afterComma].TokenType == TSqlTokenType.WhiteSpace)
                            {
                                tokens[afterComma].Text = " "       // keep a space
                                                           + "\r\n"
                                                           + baseIndent
                                                           + "\t";
                            }
                        }
                    }
                }
            }
        }

        private static void BreakSprocDefinitionParametersPerLineCase(
            IList<TSqlParserToken> tokens,
            List<CreateProcedureStatement> createProcs,
            List<AlterProcedureStatement> alterProcs,
            List<CreateOrAlterProcedureStatement> createOrAlterProcs)
        {
            const string indent = "\t";

            // helper to process any one proc‐definition
            void SplitParams(IList<ProcedureParameter> parameters)
            {
                if (parameters.Count <= 1)
                    return;

                // break before the very first param
                int wsIdx = parameters[0].FirstTokenIndex - 1;
                if (wsIdx >= 0 && tokens[wsIdx].TokenType == TSqlTokenType.WhiteSpace)
                    tokens[wsIdx].Text = "\r\n" + indent;

                // now break at every comma
                int start = parameters.First().FirstTokenIndex;
                int end = parameters.Last().LastTokenIndex;
                for (int i = start; i <= end; i++)
                {
                    if (tokens[i].TokenType == TSqlTokenType.Comma)
                    {
                        int after = i + 1;
                        if (after < tokens.Count
                         && tokens[after].TokenType == TSqlTokenType.WhiteSpace)
                            tokens[after].Text = " "      // keep one space
                                               + "\r\n"
                                               + indent;
                    }
                }
            }

            // apply to all three collections
            foreach (var proc in createProcs)
                SplitParams(proc.Parameters);
            foreach (var proc in alterProcs)
                SplitParams(proc.Parameters);
            foreach (var proc in createOrAlterProcs)
                SplitParams(proc.Parameters);
        }

        // Helper: for a whitespace token that looks like "\r\n    …",
        // remove exactly one instance of indentString after each newline.
        private static string RemoveOneIndent(string wsText, string indentString)
        {
            var lines = wsText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            // if it’s just spaces/tabs, drop one indent if present
            if (lines.Length == 1)
            {
                return lines[0].StartsWith(indentString, StringComparison.Ordinal)
                    ? lines[0].Substring(indentString.Length)
                    : lines[0];
            }
            // otherwise for each line after the first, drop one indent if present
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(indentString, StringComparison.Ordinal))
                    lines[i] = lines[i].Substring(indentString.Length);
            }
            return string.Join("\r\n", lines);
        }

        private static void MoveJoinKeywordToNewLine(
            TSqlFragment sqlFragment,
            int startTokenIndex,
            int startColumn,
            params TSqlTokenType[] tokenTypes)
        {
            var tokenSet = new HashSet<TSqlTokenType>(tokenTypes);
            int nextTokenNumber = startTokenIndex;

            while (true)
            {
                TSqlParserToken nextToken = sqlFragment.ScriptTokenStream[nextTokenNumber];

                if (tokenSet.Contains(nextToken.TokenType))
                {
                    TSqlParserToken previousToken = sqlFragment.ScriptTokenStream[nextTokenNumber - 1];
                    if (previousToken.TokenType == TSqlTokenType.WhiteSpace)
                        previousToken.Text = "\r\n" + new string(' ', startColumn - 1);
                    break;
                }

                nextTokenNumber -= 1;

                //just in case
                if (nextTokenNumber < 0)
                    break;
            }
        }


    }
}
