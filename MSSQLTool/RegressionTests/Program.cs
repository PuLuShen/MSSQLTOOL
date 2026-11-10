using MSSQLTool.Completion;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace MSSQLTool.RegressionTests
{
    internal static partial class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static int TestCount;
        private static int SkippedCount;

        [STAThread]
        private static int Main()
        {
            if (HasCommandLineSwitch("--format-probe")) return FormatterProbe.Run(Environment.GetCommandLineArgs());
            if (HasCommandLineSwitch("--list-analysis-rules")) return ListAnalysisRules();
            if (HasCommandLineSwitch("--storage")) return StorageProbe.Run(Environment.GetCommandLineArgs());
            if (HasCommandLineSwitch("--history-probe")) return HistoryProbe.Run();
            if (HasCommandLineSwitch("--history-inspect")) return HistoryProbe.Inspect(Environment.GetCommandLineArgs());
            if (HasCommandLineSwitch("--completion-perf")) return RunCompletionPerf(Environment.GetCommandLineArgs());

            // The insert-text assertions expect square brackets, but Quote()
            // follows the machine's saved useSquareBrackets setting. Pin the
            // setting in this process only: saving it (as an earlier version did)
            // rewrote the user's real setting on every test run.
            UseBracketedInsertTextForTests();
            CaptureLiveSettings();
            try
            {
                return MainCore();
            }
            finally
            {
                RestoreCompletionSettings();
            }
        }

        private static string liveSettingsBefore;
        private static IDictionary<string, string> liveRegistryBefore;

        /// <summary>Remembers the live settings so the run can prove it did not rewrite them.</summary>
        private static void CaptureLiveSettings()
        {
            try
            {
                string path = SettingsStore.FilePath;
                liveSettingsBefore = path != null && File.Exists(path)
                    ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
                liveRegistryBefore = new RegistrySettingsStore().Snapshot();
            }
            catch
            {
                liveSettingsBefore = null;
                liveRegistryBefore = null;
            }
        }

        /// <summary>
        /// Fails the run when it changed the settings of the machine it runs on.  A regression test
        /// used to force useSquareBrackets on through the real store, so every test run silently
        /// reset the user's completion options.
        /// </summary>
        private static void AssertLiveSettingsUnchanged()
        {
            string path = SettingsStore.FilePath;
            string after = path != null && File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
            True(string.Equals(liveSettingsBefore, after, StringComparison.Ordinal));

            if (liveRegistryBefore == null) return;
            IDictionary<string, string> registryAfter = new RegistrySettingsStore().Snapshot();
            foreach (var pair in liveRegistryBefore)
            {
                string current;
                True(registryAfter.TryGetValue(pair.Key, out current));
                True(string.Equals(pair.Value, current, StringComparison.Ordinal));
            }
        }

        private static bool HasCommandLineSwitch(string value)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int MainCore()
        {
            Run("Settings window constructs", TestSettingsWindowConstructs);
            Run("Update page shows the running version", () =>
            {
                // The Updates tab prints exactly this, so it must read 4.61 and not 4.61.0.0.
                Equal("4.61", UpdateChecker.FormatVersion(new Version(4, 61, 0, 0)));
                Equal("4.61.2", UpdateChecker.FormatVersion(new Version(4, 61, 2, 0)));
                Equal("4.61.2.7", UpdateChecker.FormatVersion(new Version(4, 61, 2, 7)));
            });
            Run("Automatic localization is extension-scoped", () => True(
                LocalizationManager.IsExtensionUiType(typeof(LocalizationManager))
                && !LocalizationManager.IsExtensionUiType(typeof(string))));
            Run("EXEC object context", () => Equal(CompletionContextKind.ExecuteObject, SqlCompletionAnalyzer.Analyze("EXEC dbo.us", 11).Kind));
            Run("EXEC argument context", () => Equal(CompletionContextKind.ExecuteArguments, SqlCompletionAnalyzer.Analyze("EXEC dbo.usp_Test @", 20).Kind));
            Run("Commented EXEC does not leak into FROM completion", TestCommentedExecuteBeforeSelect);
            Run("Completed EXEC does not leak into snippet SELECT", TestExecuteBeforeSnippetSelect);
            Run("Same-line EXEC does not absorb snippet SELECT", TestSameLineExecuteBeforeSnippetSelect);
            Run("Multiline EXEC keeps argument completion", TestMultilineExecuteArguments);
            Run("EXEC with parameters releases incomplete next statement", TestExecuteParametersBeforeIncompleteStatement);
            Run("EXEC parameters release on a new line after one letter", TestExecuteParametersReleaseOnNewLineWithOneLetter);
            Run("Completion: alias member inserts just the column", TestAliasMemberInsertsBareColumn);
            Run("Completion: UPDATE SET offers the target columns", TestUpdateSetOffersColumns);
            Run("Completion: data types are suggested", TestDataTypesAreOffered);
            Run("Completion: function entries explain their usage", TestFunctionEntriesCarryDocumentation);
            Run("Completion: UPDATE SET resolves aliases declared after the SET", TestUpdateSetResolvesAliasesDeclaredAfterTheSet);
            Run("Completion: keyword coverage includes table and query hints", TestCompletionKeywordCoverage);
            Run("EXEC without parameters releases incomplete next statement", TestExecuteBeforeIncompleteStatement);
            Run("EXEC positional value remains argument context", TestExecutePositionalValue);
            Run("Exact snippet prefix is context-independent", TestExactSnippetPrefix);
            Run("A completed snippet prefix rebuilds the stale cache", TestSnippetPrefixRebuildsCache);
            Run("Nested SELECT does not split statement scope", TestNestedSelectStatementScope);
            Run("Alias from ScriptDOM tokens", () => Equal("dbo.Customer", SqlSemanticModel.Create("SELECT c. FROM dbo.Customer AS c").Aliases["c"]));
            Run("Statement start comes from the parse", () =>
            {
                // Valid T-SQL written on one line with no semicolon: only the tree sees the boundary.
                // (An incomplete tail makes ScriptDOM drop the whole batch, which is why the text scan
                // stays as the baseline and this only ever moves the start forward.)
                const string sql = "DECLARE @a int SELECT h.HeaderName FROM dbo.Header h WHERE h.Amount > 1";
                Equal(sql.IndexOf("SELECT h.HeaderName", StringComparison.Ordinal), SqlSemanticModel.Create(sql, sql.Length).StatementStart);
            });
            Run("Two statements on one line do not share a statement text", () =>
            {
                const string sql = "DECLARE @a int SELECT h.HeaderName FROM dbo.Header h WHERE h.Amount > 1";
                CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
                True(!(context.CurrentStatement ?? string.Empty).Contains("DECLARE"),
                    "the first statement leaked into the current one: " + context.CurrentStatement);
            });
            Run("Aliases are filled from the parse without the text scan", () =>
            {
                CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Header h WHERE h.", "SELECT * FROM dbo.Header h WHERE h.".Length);
                Equal("dbo.Header", context.Aliases["h"]);
            });
            Run("Item key follows the text it is built from", () =>
            {
                var item = new CompletionItem { DisplayText = "Header", Kind = CompletionItemKind.Table };
                string before = item.Key;
                item.DisplayText = "Detail";
                True(before != item.Key, "the cached key did not follow DisplayText");
            });
            Run("A word after a source stays a source tail", () =>
            {
                const string sql = "SELECT *\nFROM dbo.ZZJGH A LJ";
                CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length - (sql.LastIndexOf('\n') + 1));
                Equal(CompletionContextKind.TableSourceTail, context.Kind);
                Equal("LJ", context.Prefix);
            });
            Run("A source tail offers the snippet but no columns", () =>
            {
                var snapshot = new MetadataSnapshot();
                var table = new DatabaseObjectMetadata { Schema = "dbo", Name = "ZZJGH", Kind = CompletionItemKind.Table };
                table.Columns.Add(new ColumnMetadata { Name = "LXTJGID", DataType = "int" });
                snapshot.Objects.Add(table);
                const string sql = "SELECT *\nFROM dbo.ZZJGH A LJ";
                CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length - (sql.LastIndexOf('\n') + 1));
                List<CompletionItem> shown = SqlCompletionAnalyzer.FilterCandidates(
                    SqlCompletionAnalyzer.BuildCandidates(context, snapshot), context.Prefix);
                True(shown.All(i => i.Kind != CompletionItemKind.Column),
                    "a column was offered at a table source: " + string.Join(", ", shown.Select(i => i.DisplayText)));
                True(shown.Any(i => i.DisplayText == "LJ"), "the exact snippet prefix is missing");
            });
            Run("An ON predicate offers the next join", () =>
            {
                const string sql = "SELECT *\nFROM dbo.ZZJGH A LEFT JOIN dbo.WDDDB B ON JS = A.DjLsh\nAND KHBM = A.DjState ";
                CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length - (sql.LastIndexOf('\n') + 1));
                Equal(CompletionContextKind.Predicate, context.Kind);
                Equal("ON", context.PredicateOwner);
                List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, new MetadataSnapshot());
                int and = items.FindIndex(i => i.DisplayText == "AND");
                int join = items.FindIndex(i => i.DisplayText == "LEFT JOIN");
                True(and >= 0, "AND is missing from a finished ON predicate");
                True(join > and, "LEFT JOIN should come after AND");
                True(items.All(i => i.DisplayText != "GROUP BY"), "GROUP BY cannot follow a join condition");
            });
            Run("A WHERE predicate does not offer joins", () =>
            {
                const string sql = "SELECT * FROM dbo.WDDDH h WHERE h.DjLsh = 1 ";
                CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
                Equal("WHERE", context.PredicateOwner);
                True(SqlCompletionAnalyzer.BuildItems(context, new MetadataSnapshot())
                        .All(i => !i.DisplayText.EndsWith("JOIN", StringComparison.Ordinal)),
                    "a join was offered after a WHERE condition");
            });
            Run("The resolved position publishes its clause and statement kind", () =>
            {
                const string select = "SELECT * FROM dbo.WDDDH h WHERE h.DjLsh = ";
                CompletionContext selectContext = SqlCompletionAnalyzer.Analyze(select, select.Length);
                Equal("WHERE", selectContext.EnclosingClause);
                Equal("SELECT", selectContext.StatementKind);
                Equal(SqlCaretKind.Predicate, selectContext.ResolvedKind);

                const string update = "UPDATE dbo.WDDDH SET DjLsh = 1 WHERE ";
                CompletionContext updateContext = SqlCompletionAnalyzer.Analyze(update, update.Length);
                Equal("UPDATE", updateContext.StatementKind);
                Equal("WHERE", updateContext.EnclosingClause);
            });
            // The semantic model patches an unfinished member with a placeholder, which moves every
            // later offset; the caret has to be mapped into that text or the position is read from
            // the wrong token.
            Run("Caret maps past an inserted placeholder", () =>
            {
                const string patched = "SELECT h. FROM dbo.Header h WHERE h.Amount > 1 ";
                Equal(SqlCaretKind.Predicate, SqlSemanticModel.Create(patched, patched.Length).Position.Kind);
            });
            Run("Member position does not need the caret at the end", () => Equal(SqlCaretKind.Member,
                SqlSemanticModel.Create("SELECT c. FROM dbo.Customer c", "SELECT c.".Length).Position.Kind));
            Run("Quoted member qualifier is read from tokens", () => Equal("dbo",
                SqlSemanticModel.Create("SELECT * FROM \"dbo\".", 20).Position.Qualifier));
            Run("Quoted alias", () => Equal("sales.Order Header", SqlSemanticModel.Create("SELECT o. FROM [sales].[Order Header] AS [o]").Aliases["o"]));
            Run("Comment is ignored", () => True(!SqlSemanticModel.Create("-- FROM dbo.Secret s\nSELECT 1").Aliases.ContainsKey("s")));
            Run("Language: default is English", () => Equal(LocalizationManager.EnglishLanguage, LocalizationManager.NormalizeLanguage(string.Empty)));
            Run("Language: unsupported value falls back to English", () => Equal(LocalizationManager.EnglishLanguage, LocalizationManager.NormalizeLanguage("xx-YY")));
            Run("Language: supported value is kept", () => Equal(LocalizationManager.ChineseLanguage, LocalizationManager.NormalizeLanguage("zh-CN")));
            Run("Unbounded UPDATE diagnostic", () => True(SqlSemanticModel.Create("UPDATE dbo.Customer SET Name='x'").Diagnostics.Exists(x => x.Code == "SQL_UNBOUNDED_DML")));
            Run("Bounded UPDATE", () => True(!SqlSemanticModel.Create("UPDATE dbo.Customer SET Name='x' WHERE Id=1").Diagnostics.Exists(x => x.Code == "SQL_UNBOUNDED_DML")));
            Run("WHERE context", () => Equal(CompletionContextKind.Predicate, SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c WHERE Na", 37).Kind));
            Run("GROUP BY context", () => Equal(CompletionContextKind.GroupBy, SqlCompletionAnalyzer.Analyze("SELECT COUNT(*) FROM dbo.Customer GROUP BY Na", 46).Kind));
            Run("ORDER BY context", () => Equal(CompletionContextKind.OrderBy, SqlCompletionAnalyzer.Analyze("SELECT Name AS CustomerName FROM dbo.Customer ORDER BY Cust", 59).Kind));
            Run("MERGE source context", () => Equal(CompletionContextKind.MergeSource, SqlCompletionAnalyzer.Analyze("MERGE dbo.Target t USING ", 25).Kind));
            Run("Function argument context", () => Equal(CompletionContextKind.FunctionArguments, SqlCompletionAnalyzer.Analyze("SELECT dbo.CalculateTax(@", 25).Kind));
            Run("SQL Server identifier", () => Equal("[sales].[Order Header]", DatabaseIdentifier.SqlServer("sales.Order Header")));
            Run("SQL Server closing bracket", () => Equal("[a]]b]", DatabaseIdentifier.SqlServer("a]b")));
            Run("SQL Server pre-escaped bracket", () => Equal("[a]]b]", DatabaseIdentifier.SqlServer("[a]]b]")));
            Run("SQL Server single identifier keeps dots", () => Equal("[server.example.com]", DatabaseIdentifier.SqlServerPart("server.example.com")));
            Run("Normalize bracketed dotted identifier", () => Equal("server.example.com.Warehouse.dbo.Table", DatabaseIdentifier.NormalizeSqlServer("[server.example.com].[Warehouse].[dbo].[Table]")));
            Run("Normalize escaped bracket identifier", () => Equal("a]b", DatabaseIdentifier.NormalizeSqlServer("[a]]b]")));
            Run("Normalize double-quoted identifier", () => Equal("sales.Order Header", DatabaseIdentifier.NormalizeSqlServer("\"sales\".\"Order Header\"")));
            Run("Local table rejects database qualifier", () => Throws(() => DatabaseIdentifier.SqlServerLocalObject("Database.dbo.Table")));
            Run("Allow omitted schema identifier", () => Equal("[Database]..[Table]", DatabaseIdentifier.SqlServer("Database..Table")));
            Run("Reject leading empty identifier part", () => Throws(() => DatabaseIdentifier.SqlServer(".dbo.Table")));
            Run("EXEC filters procedures", TestExecuteObjects);
            Run("Metadata connection preserves transport settings", TestMetadataConnectionSettings);
            Run("Metadata connection can trust server certificate", TestMetadataConnectionTrustOverride);
            Run("Metadata token connection removes conflicting authentication", TestMetadataTokenConnection);
            Run("Shortcut automation uses scalar binding", TestShortcutAutomationBinding);
            Run("F12 shortcut normalization", () => True(ShortcutManager.IsF12Shortcut(" f12 ")));
            Run("EXEC recommends parameters", TestExecuteParameters);
            Run("Member recommends columns with FROM after caret", TestMemberColumns);
            Run("Predicate recommends operators", TestPredicateOperators);
            Run("Completion keeps the selected item across refreshes", TestCompletionSelectionIsPreserved);
            Run("Filtering cached candidates matches a full build", TestFilteringFromCachedCandidatesMatchesFullBuild);
            Run("Recently used suggestions outrank stale ones", TestRecentlyUsedBoost);
            Run("Completion history survives a reload", TestCompletionHistoryRoundTrip);
            Run("Filtering 5000 candidates stays fast", TestFilterCandidatesStaysFast);
            Run("Statement window isolates the caret statement", TestStatementWindowUsesCaretStatement);
            Run("Statement window ignores quoted and commented separators", TestStatementWindowIgnoresQuotedAndCommentedSeparators);
            Run("Statement window splits on GO", TestStatementWindowSplitsOnGo);
            Run("Statement window keeps UPDATE SET together", TestStatementWindowKeepsUpdateSetTogether);
            Run("Statement window keeps CTE together", TestStatementWindowKeepsCteWithMainSelect);
            Run("Procedure body keeps SELECT separate after UPDATE without a semicolon", TestProcedureBodyUpdateWithoutSemicolonKeepsSelectContext);
            Run("Segment-boundary match outranks a buried substring", TestSegmentBoundaryRanksBeforeSubstring);
            Run("Outer scope does not leak a subquery alias", TestOuterScopeDoesNotLeakSubqueryAlias);
            Run("Completion commit key respects the setting", TestCompletionCommitKeySetting);
            Run("CTE local object", () => True(SqlCompletionAnalyzer.Analyze("WITH cte(Id,Name) AS (SELECT 1,'x') SELECT cte.", 50).LocalObjects.Exists(x => x.Name == "cte")));
            Run("Temp table local object", () => True(SqlCompletionAnalyzer.Analyze("CREATE TABLE #t(Id int, Name nvarchar(20)); SELECT #t.", 55).LocalObjects.Exists(x => x.Name == "#t")));
            Run("Temp table survives GO analysis", () => True(SqlCompletionAnalyzer.Analyze("CREATE TABLE #t(Id int);\nGO\nSELECT #t.", 39).LocalObjects.Exists(x => x.Name == "#t")));
            Run("Declared variable completion", TestDeclaredVariable);
            Run("Semicolon in string does not end suffix", () => Equal(" WHERE Note='a;b' ORDER BY Id", SqlCompletionAnalyzer.GetSemanticSuffix(" WHERE Note='a;b' ORDER BY Id; SELECT 2")));
            Run("GO in comment does not end suffix", () => Equal("\n-- GO\nFROM dbo.Customer", SqlCompletionAnalyzer.GetSemanticSuffix("\n-- GO\nFROM dbo.Customer; SELECT 2")));
            Run("VALUES is not a function", () => True(SqlCompletionAnalyzer.Analyze("INSERT dbo.T VALUES (", 21).Kind != CompletionContextKind.FunctionArguments));
            Run("IN is not a function", () => True(SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.T WHERE Id IN (", 34).Kind != CompletionContextKind.FunctionArguments));
            Run("INSERT column list beats function context", TestInsertColumnContext);
            Run("CREATE TABLE column list is not a function", () => True(SqlCompletionAnalyzer.Analyze("CREATE TABLE #t (", 17).Kind != CompletionContextKind.FunctionArguments));
            Run("Ctrl+Space snippet route has priority", () => True(KeypressCommandFilter.ShouldPreferSnippetOverCompletion(true,
                SettingsManager.SnippetReplaceKey.CtrlSpace, true, true)));
            Run("Snippet commit requests continuation completion", () => True(CompletionController.ShouldRequestCompletionAfterCommit(
                new CompletionItem { Kind = CompletionItemKind.Snippet })));
            Run("Derived query scope does not leak", TestDerivedScope);
            Run("Bracketed multipart symbol", () => Equal("[sales].[Order Header]", SqlSymbolResolver.ReadMultipartIdentifier("SELECT * FROM [sales].[Order Header]", 25)));
            Run("F12 resolves alias column owner", TestSymbolAlias);
            Run("F12 resolves procedure at identifier end", () => Equal("dbo.P_XCX_ADD",
                SqlSymbolResolver.ReadMultipartIdentifier("EXEC dbo.P_XCX_ADD", 18)));
            Run("F12 resolves CTE locally", TestLocalSymbol);
            Run("Cross database member qualifier", () => Equal("OtherDb.dbo", SqlCompletionAnalyzer.Analyze("SELECT * FROM OtherDb.dbo.", 26).Qualifier));
            Run("Cross database EXEC procedures", TestCrossDatabaseExecute);
            Run("Typed bit values", TestTypedBitValues);
            Run("GROUP BY excludes referenced column", TestGroupByDedupe);
            Run("SELECT INTO local object", () => True(SqlCompletionAnalyzer.Analyze("SELECT Id INTO #copy FROM dbo.Customer; SELECT #copy.", 52).LocalObjects.Exists(x => x.Name == "#copy")));
            Run("Database qualified matching", TestCrossDatabaseMember);
            Run("Linked server database member", TestLinkedServerDatabaseMember);
            Run("Linked server schema member", TestLinkedServerSchemaMember);
            Run("Linked server object member", TestLinkedServerObjectMember);
            Run("Nested predicate context", () => Equal(CompletionContextKind.Predicate, SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c WHERE c.Id IN (SELECT i.Id FROM dbo.Customer i WHERE ", 82).Kind));
            Run("FROM partial object context", () => Equal(CompletionContextKind.DataSource, SqlCompletionAnalyzer.Analyze("SELECT * FROM Cus", 17).Kind));
            Run("JOIN partial object context", () => Equal(CompletionContextKind.Join, SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c JOIN Ord", 37).Kind));
            Run("INSERT INTO partial object context", () => Equal(CompletionContextKind.DataSource, SqlCompletionAnalyzer.Analyze("INSERT INTO Log", 15).Kind));
            Run("Derived table columns", TestDerivedColumns);
            Run("Correlated subquery keeps outer alias", TestCorrelatedScope);
            Run("Ambiguous columns remain distinct", TestAmbiguousColumns);
            Run("GROUP BY keeps SELECT-only column", TestGroupBySelectColumn);
            Run("Unknown function does not invent arguments", TestUnknownFunctionArguments);
            Run("Nested built-in function arguments", () => Equal(CompletionContextKind.FunctionArguments, SqlCompletionAnalyzer.Analyze("SELECT COALESCE(ISNULL(Name, ''), ", 34).Kind));
            Run("Current database object inserts schema only", TestCurrentDatabaseObjectInsertion);
            Run("External object preserves database qualifier", TestExternalObjectInsertion);
            Run("Scalar function is not a FROM source", TestScalarFunctionSourceFiltering);
            Run("Table-valued function is a FROM source", TestTableValuedFunctionSourceFiltering);
            Run("Table-valued function explains how it is called", TestTableValuedFunctionDocumentation);
            Run("FROM offers the VALUES table constructor", TestTableValueConstructorItem);
            Run("Parameter type display", TestParameterTypeDisplay);
            Run("Completion suppresses comment trailing whitespace", () => True(SqlCompletionAnalyzer.Analyze("-- comment ", 11).Suppress));
            Run("Completion suppresses string trailing whitespace", () => True(SqlCompletionAnalyzer.Analyze("SELECT 'text ", 13).Suppress));
            Run("Double-quoted identifier does not start a comment", () => True(!SqlCompletionAnalyzer.Analyze("SELECT \"a--b\" FROM dbo.Cus", 26).Suppress));
            Run("Double-quoted schema member", () => Equal("dbo", SqlCompletionAnalyzer.Analyze("SELECT * FROM \"dbo\".", 20).Qualifier));
            Run("Comma-separated FROM source context", () => Equal(CompletionContextKind.DataSource, SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c, Ord", 33).Kind));
            Run("MERGE target source context", () => Equal(CompletionContextKind.DataSource, SqlCompletionAnalyzer.Analyze("MERGE Tar", 9).Kind));
            Run("TRUNCATE target source context", () => Equal(CompletionContextKind.DataSource, SqlCompletionAnalyzer.Analyze("TRUNCATE TABLE Log", 18).Kind));
            Run("Table variable does not survive GO", TestTableVariableBatchScope);
            Run("Dropped temp table is removed", TestDroppedTempTable);
            Run("Multiple DECLARE variables", TestMultipleVariables);
            Run("Qualified GROUP BY excludes only its owner", TestQualifiedGroupBy);
            Run("Qualified typed value uses correct owner", TestQualifiedTypedValue);
            Run("Data source recommends schemas", TestSchemaCandidate);
            Run("Qualified FROM filters non-data sources", TestQualifiedDataSourceFiltering);
            Run("Omitted schema member completion", TestOmittedSchemaCompletion);
            Run("Omitted schema INSERT columns", TestOmittedSchemaInsertColumns);
            Run("Built-in table functions in FROM", TestBuiltInTableFunctions);
            Run("Nested subquery columns do not leak", TestDerivedColumnIsolation);
            Run("Function argument position", () => Equal(2, SqlCompletionAnalyzer.Analyze("SELECT COALESCE(Id, Name, ", 26).ArgumentIndex));
            Run("ORDER BY equals alias", TestEqualsAlias);
            Run("Cross-database parameters stay isolated", TestCrossDatabaseParameterIsolation);
            Run("External clone does not mutate cache object", TestExternalClone);
            Run("Table does not commit on dot", () => True(!CompletionController.ShouldCommitOnCharacter(new CompletionItem { Kind = CompletionItemKind.Table }, '.')));
            Run("Schema commits on dot", () => True(CompletionController.ShouldCommitOnCharacter(new CompletionItem { Kind = CompletionItemKind.Schema }, '.')));
            Run("Completion snapshot rejects moved caret", () => True(!CompletionController.IsCompletionSnapshotCurrent("SELECT C", "SELECT C", 0, 8, "s|d", 1, 0, "s|d")));
            Run("Completion snapshot rejects changed connection", () => True(!CompletionController.IsCompletionSnapshotCurrent("SELECT C", "SELECT C", 0, 8, "s|d", 0, 8, "s|other")));
            Run("JOIN schema keeps smart join", TestQualifiedSmartJoin);
            Run("JOIN infers same-name key columns", TestSameNameSmartJoin);
            Run("JOIN honors custom column rule", TestCustomJoinRule);
            Run("Alias rules honor mappings and prefixes", TestAliasRules);
            Run("Completion items expose object details", TestCompletionDetails);
            Run("Column picker commits selected columns", TestColumnPickerCommit);
            Run("DDL execution invalidates metadata", TestMetadataInvalidationDetection);
            Run("Special SQL expressions are not functions", TestSpecialExpressionKinds);
            Run("Future CTE is not visible", TestFutureCteScope);
            Run("Asterisk analyzes INSERT SELECT", TestInsertSelectAsteriskContext);
            Run("Asterisk analyzes CREATE VIEW", TestCreateViewAsteriskContext);
            Run("Asterisk temp setup honors DROP and recreate", TestAsteriskTempLifecycle);
            Run("Built-in function does not suggest invalid DEFAULT", TestBuiltInDefaultValue);
            Run("Altered temp table columns", TestAlteredTempTable);
            Run("Derived SELECT star propagates metadata columns", TestDerivedStarColumns);
            Run("Asterisk request accepts unchanged snapshot", () => True(AsteriskExpansionService.IsExpansionSnapshotCurrent("SELECT *", "SELECT *", 0, 8, 7, 7, "", 0, 8, 7, 7, "")));
            Run("Asterisk request rejects changed text", () => True(!AsteriskExpansionService.IsExpansionSnapshotCurrent("SELECT *", "SELECT  *", 0, 8, 7, 7, "", 0, 9, 8, 8, "")));
            Run("Asterisk request rejects moved caret", () => True(!AsteriskExpansionService.IsExpansionSnapshotCurrent("SELECT *", "SELECT *", 0, 8, 7, 7, "", 0, 7, 7, 7, "")));
            Run("SELECT list context", () => Equal(CompletionContextKind.SelectList, SqlCompletionAnalyzer.Analyze("SELECT ", 7).Kind));
            Run("SELECT whole-column clause", TestSelectWholeColumnClause);
            Run("GROUP BY whole non-aggregate clause", TestGroupByWholeClause);
            Run("Window PARTITION context", () => Equal(CompletionContextKind.WindowPartitionBy, SqlCompletionAnalyzer.Analyze("SELECT ROW_NUMBER() OVER (PARTITION BY ", 39).Kind));
            Run("Window ORDER context", () => Equal(CompletionContextKind.WindowOrderBy, SqlCompletionAnalyzer.Analyze("SELECT ROW_NUMBER() OVER (ORDER BY ", 35).Kind));
            Run("OUTPUT inserted columns", TestOutputColumns);
            Run("Data source fuzzy match suggests subsequence hits", TestDataSourceAcronymMatch);
            Run("PIVOT clause context", () => Equal(CompletionContextKind.Pivot, SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c PIVOT (SUM(c.Id) FOR ", 49).Kind));
            Run("CREATE TABLE clause generation", TestCreateTableClause);
            Run("CREATE INDEX column completion", TestCreateIndexColumns);
            Run("ALTER TABLE action generation", TestAlterTableActions);
            Run("ALTER TABLE column completion", TestAlterTableColumns);
            Run("Constraint column completion", TestConstraintColumns);
            Run("Rich metadata details", TestRichMetadataDetails);
            Run("Independent parameter info", TestParameterInformation);
            Run("Resizable completion details pane", TestCompletionSplitter);
            Run("Completion popup constructs", TestCompletionPopupConstructs);

            Run("Model: tokenizer keeps positions", TestSqlTokenizerKeepsPositions);
            Run("Model: statement index splits batches", TestStatementIndexSplitsBatches);
            Run("Model: caret statement lookup", TestStatementAtPrefersTheCaretStatement);
            Run("Model: SELECT INTO scanner", TestSelectIntoScannerIsLinearAndExact);
            Run("Model: scope tree resolves aliases", TestScopeTreeResolvesAliases);
            Run("Model: scope tree reads comma-separated sources", TestScopeTreeReadsCommaSeparatedSources);
            Run("Model: scope tree isolates subqueries", TestScopeTreeDoesNotLeakSiblingSubqueries);
            Run("Completion: comma-separated FROM scopes both tables", TestCommaSeparatedFromScopesColumns);
            Run("Analysis: rule catalog is consistent", TestRuleCatalogIsConsistent);
        Run("Analysis: duplicate alias stays inside the query block", TestDuplicateAliasStaysInsideTheQueryBlock);
        Run("Completion: JOIN inserts only the table by default", TestJoinCompletionInsertsOnlyTheTableByDefault);
        Run("Completion: JOIN offers each foreign key separately", TestJoinCompletionOffersEachKeySeparately);
        Run("Completion: JOIN second stage appends only the ON clause", TestJoinCompletionSecondStageAppendsOnlyTheOnClause);
        Run("Completion: JOIN target ranks ON first", TestJoinTargetAwaitingOnRanksOnFirst);
        Run("Completion: JOIN target offers hints instead of AND/OR", TestJoinTargetTailOffersHints);
        Run("Completion: ambiguous columns show their alias", TestAmbiguousColumnsCarryTheirAlias);
        Run("Completion: FROM source tail ranks hint, joins and alias", TestFromSourceTailRanksContinuations);
        Run("Completion: SELECT lists one SUM entry with its usage", TestSelectListShowsOneSumEntry);
        Run("Completion: usage documentation is localized", TestUsageDocumentationIsLocalized);
        Run("Completion: unique columns stay unqualified", TestUniqueColumnsStayUnqualified);
            Run("Analysis: rules detect core patterns", TestRulesDetectCorePatterns);
            Run("Analysis: settings and suppression", TestRulesRespectSettingsAndSuppression);
            Run("Analysis: severity override", TestRuleSeverityOverrideIsApplied);
        Run("Analysis: squiggle switches change the signature", TestAnalysisSquiggleSwitchesChangeSignature);
            Run("Analysis: engine is deterministic", TestAnalysisEngineIsDeterministicAndCached);
            Run("Analysis: broken SQL does not throw", TestAnalysisRulesDoNotThrowOnBrokenSql);
            Run("Analysis: editor boundary", TestEditorBoundaryIsRespected);
            Run("Analysis: fix at caret picks the caret diagnostic", TestFixAtCaretFindsTheCaretDiagnostic);
            Run("Model: target scanner finds write targets", TestTargetScannerFindsWriteTargets);
            Run("Completion: INSERT uses the scanned target", TestInsertCompletionUsesTheScannedTarget);
            Run("Completion: CTE names are offered", TestCompletionOffersCteNames);
            Run("Completion: EXEC parameters use the scanned procedure", TestExecuteParametersUseTheScannedProcedure);
            Run("Completion: ALTER TABLE resolves the bracketed target", TestAlterTableCompletionResolvesBracketedTarget);
            Run("Completion: nested scope members", TestCompletionResolvesNestedScopeMembers);
            Run("Completion: joined columns are not duplicated", TestCompletionDoesNotDuplicateJoinedColumns);
            Run("Completion: predicate state is classified", TestPredicateStateClassification);
            Run("Completion: predicate prefers AND/OR after a complete condition", TestPredicateCompletionPrefersLogicalOperators);
            Run("Completion: predicate separates operands and operators", TestPredicateCompletionKeepsOperandsAndOperatorsApart);
            Run("Completion: predicate handles BETWEEN and groups", TestPredicateCompletionHandlesBetweenAndGroups);
            RunCompletionSelectionTests();
            RunCompletionCorpusTests();            RunCompletionPerformanceTests();
            RunCompletionParameterInfoTests();
            RunUpdateHelperTests();
            RunStorageTests();
        RunResultSetTests();
            RunFormattingTests();

            RunSqlServerIntegration();
            // Must stay last: it compares the live settings with the snapshot taken before the run.
            Run("Refactor: variable rename uses batch scope", TestRenameVariableUsesBatchScope);
        Run("Refactor: rename covers every reference in the statement", TestRenameChangesEveryReferenceInTheStatement);
        Run("Refactor: rename stays inside the caret statement", TestRenameStaysInsideTheCaretStatement);
        Run("Refactor: rename rejects a name that is already used", TestRenameRejectsANameThatIsAlreadyUsed);
        Run("Refactor: rename keeps the quoting style", TestRenameKeepsTheQuotingStyle);
         Run("Refactor: variable rename skips EXEC parameter names", TestRenameVariableSkipsExecParameterNames);
         Run("Refactor: variable rename matches the whole name only", TestRenameVariableMatchesTheWholeNameOnly);
         Run("Refactor: spans survive line/column conversion", TestRenameSpansSurviveLineColumnConversion);
        Run("Refactor: qualify adds schema only to table references", TestQualifyAddsSchemaOnlyToTableReferences);
        Run("Refactor: qualify skips CTEs, temp tables and qualified names", TestQualifySkipsCtesTempTablesAndQualifiedNames);
        Run("Refactor: qualify handles multiple tables and updates", TestQualifyHandlesMultipleTablesAndUpdates);
        Run("Highlight: every variable occurrence is related", TestHighlightIncludesEveryVariableOccurrence);
        Run("Completion: SELECT * inside OUTER APPLY expands only the subquery source", TestAsteriskInsideApplyExpandsOnlySubquerySource);
        Run("Results: analytics computes column statistics", TestGridAnalyticsComputesColumnStatistics);
        Run("Results: analytics respects the row cap", TestGridAnalyticsRespectsTheRowCap);
        Run("Results: analytics handles an empty table", TestGridAnalyticsHandlesAnEmptyTable);
        Run("Results: statistics bind to real properties", TestGridAnalyticsStatisticsAreBindableProperties);
        Run("Results: text columns are never summed", TestGridAnalyticsTextColumnIsNeverSummed);
        Run("Results: numeric and date ranges", TestGridAnalyticsNumericAndDateRanges);
        Run("Results: statistics display text is readable", TestGridAnalyticsDisplayTextIsReadable);
        Run("Results: analytics honours cancellation", TestGridAnalyticsHonoursCancellation);
        Run("Results: analytics text is tab separated", TestGridAnalyticsTextIsTabSeparated);
         Run("Results: analytics uses the real field name", TestGridAnalyticsUsesTheRealFieldName);
         Run("Results: column search cycles through matches", TestGridAnalyticsFindsTheNextColumn);
         Run("History: databases use the renamed files", TestHistoryDatabasesUseTheRenamedFiles);
        Run("History: query text entry shows a preview", TestQueryTextHistoryEntryShowsAPreview);
        Run("History: query text hash is stable", TestQueryTextHistoryHashIsStable);
        Run("History: SQL Prompt import follows the real schema", TestSqlPromptImportFollowsTheRealSchema);
        Run("History: SQL Prompt saved stamp accepts FILETIME", TestSqlPromptSavedStampAcceptsFileTime);
        Run("History: groups by file or tab", TestHistoryGroupsByFileOrTab);
        Run("History: grouping survives an existing database", TestHistoryGroupingProbe);
        Run("History: filter builds a conjunctive WHERE", TestHistoryFilterBuildsConjunctiveWhere);
        Run("About: the appreciation image is embedded", TestAboutPageEmbedsTheAppreciationImage);
         Run("Editor background: bundled pictures resolve", TestEditorBackgroundResolvesBundledPictures);
         Run("Editor background: user files resolve", TestEditorBackgroundResolvesUserFiles);
         Run("Editor background: bundled pictures are embedded", TestEditorBackgroundBundledPicturesAreEmbedded);
         Run("Editor background: opacity clamps and dims", TestEditorBackgroundOpacityClampsAndDims);
         Run("Editor background: settings normalize", TestEditorBackgroundSettingsNormalize);
         Run("Editor background: stretch modes", TestEditorBackgroundStretchModes);
         Run("Editor background: placement follows the fit mode", TestEditorBackgroundPlacementFollowsFit);
         Run("Editor background: the picture is painted once", TestEditorBackgroundPaintsOnce);
        Run("Storage: the test run leaves the live settings alone", AssertLiveSettingsUnchanged);

            foreach (string failure in Failures) Console.Error.WriteLine(failure);
            string skipped = SkippedCount == 0 ? string.Empty : $" {SkippedCount} integration test(s) skipped.";
            Console.WriteLine(Failures.Count == 0 ? $"All {TestCount} regression tests passed.{skipped}" : $"{Failures.Count} of {TestCount} regression test(s) failed.{skipped}");
            return Failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// Pins "insert square brackets" for the completion assertions.  The value is overridden in
        /// this process only: the earlier version saved it through the store, which reset the user's
        /// saved setting every time the suite ran.
        /// </summary>
        private static void UseBracketedInsertTextForTests()
        {
            SettingsManager.SqlCompletionSettings settings = SettingsManager.GetSqlCompletionSettings();
            settings.useSquareBrackets = true;
            SettingsManager.OverrideCachedValue("SqlCompletionSettings",
                Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        }

        /// <summary>Drops the process-local override so later reads use the real store again.</summary>
        private static void RestoreCompletionSettings()
        {
            SettingsManager.OverrideCachedValue("SqlCompletionSettings", null);
        }

        /// <summary>
        /// The grouping rules run in SQL against a real database, and the store binds its data folder
        /// once per process, so the probe verifies them in a child process with its own folder.
        /// </summary>
        private static void TestHistoryGroupingProbe()
        {
            string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var start = new ProcessStartInfo(executable, "--history-probe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("history probe failed: " + output.Trim() + " " + error.Trim());
            }
        }

        private static void Run(string name, Action test) { TestCount++; try { test(); } catch (Exception ex) { Failures.Add(name + ": " + ex); } }

        /// <summary>
        /// Runs a test that asserts Chinese output.  English is the product default, so a test that
        /// checks a translation has to ask for that language instead of relying on the default.
        /// </summary>
        private static void RunChinese(string name, Action test)
        {
            Run(name, () =>
            {
                string previous = LocalizationManager.CurrentLanguage;
                LocalizationManager.SetLanguage(LocalizationManager.ChineseLanguage, false);
                try { test(); }
                finally { LocalizationManager.SetLanguage(previous, false); }
            });
        }
        private static void True(bool value) { if (!value) throw new InvalidOperationException("expected true"); }
        private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected {expected}, actual {actual}"); }
        private static void Throws(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("expected exception"); }

        private static void TestCommentedExecuteBeforeSelect()
        {
            string sql = "--exec JXSFC_ZHENGSUAN_5 :起始日期,:截止日期,:库存组织id,'',:组织类型,:分称单位,:平台分组项目\nSELECT * FROM BBFL";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.DataSource, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).All(x => x.Kind != CompletionItemKind.Parameter));
        }

        private static void TestExecuteBeforeSnippetSelect()
        {
            string sql = "EXEC JXSFC_ZHENGSUAN_5 @QSSJ = NULL, @JZSJ = NULL, @KCZZID = NULL, @CKID = NULL, @zzlx = NULL, @fcdw = NULL, @ptfz = NULL\nSELECT *\nFROM Cus";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.DataSource, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "dbo.Customer"));
        }

        private static void TestSameLineExecuteBeforeSnippetSelect()
        {
            string sql = "EXEC JXSFC_ZHENGSUAN_5 @QSSJ = NULL, @JZSJ = NULL, @KCZZID = NULL, @CKID = NULL, @zzlx = NULL, @fcdw = NULL, @ptfz = NULL  SELECT *\nFROM Cus";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.DataSource, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "dbo.Customer"));
        }

        private static void TestMultilineExecuteArguments()
        {
            string sql = "EXEC dbo.usp_Test\n    @Id = 1,\n    @";
            Equal(CompletionContextKind.ExecuteArguments, SqlCompletionAnalyzer.Analyze(sql, sql.Length).Kind);
        }

        /// <summary>
        /// The first character of a statement on a new line must leave the EXEC scope: a parameter list
        /// has no closing bracket, so without this the popup stayed in "procedure arguments" and never
        /// suggested SELECT (or anything else) after the user pressed Enter past the last parameter.
        /// </summary>
        private static void TestExecuteParametersReleaseOnNewLineWithOneLetter()
        {
            string sql = "EXEC dbo.usp_Test\n    @Id = 1,\n    @Name = 'x'\nS";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.General, context.Kind);
            Equal("S", context.Prefix);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "SELECT"));

            // A parameter being typed on a new line stays in EXEC scope.
            string parameters = "EXEC dbo.usp_Test\n    @Id = 1,\n    @";
            Equal(CompletionContextKind.ExecuteArguments, SqlCompletionAnalyzer.Analyze(parameters, parameters.Length).Kind);
        }

        /// <summary>
        /// Constructs that are everywhere in real scripts must be in the keyword table; the popup can
        /// only offer what the table contains.
        /// </summary>
        private static void TestCompletionKeywordCoverage()
        {
            var settings = new SettingsManager.SqlCompletionSettings();
            List<string> keywords = SqlCompletionAnalyzer.AllKeywords(settings).ToList();

            foreach (string required in new[]
            {
                "SELECT", "CREATE", "DROP", "WITH", "WITH (NOLOCK)", "WITH (UPDLOCK)", "OPTION (RECOMPILE)", "OVER", "PARTITION BY",
                "ROWS BETWEEN", "PIVOT", "UNPIVOT", "MERGE INTO", "WHEN MATCHED", "OUTPUT", "OUTPUT INSERTED", "TRY_CONVERT", "STRING_AGG",
                "ROW_NUMBER", "OFFSET", "FETCH NEXT", "FOR JSON PATH", "OPENJSON", "SET NOCOUNT ON", "BEGIN TRY", "THROW", "@@ROWCOUNT"
            })
                True(keywords.Contains(required));

            // The list is what the user can extend, so a custom entry has to come through and duplicates
            // (built-in spelled again by the user) must not appear twice.
            var custom = new SettingsManager.SqlCompletionSettings { additionalKeywords = "WITH (NOLOCK);usp_MyHouseStyle\r\nWITH (READPAST)" };
            List<string> extended = SqlCompletionAnalyzer.AllKeywords(custom).ToList();
            True(extended.Contains("usp_MyHouseStyle"));
            True(extended.Contains("WITH (READPAST)"));
            Equal(1, extended.Count(k => string.Equals(k, "WITH (NOLOCK)", StringComparison.OrdinalIgnoreCase)));
            Equal(extended.Count, extended.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        /// <summary>
        /// With an alias in play, picking a column must insert just the column: the alias the user typed
        /// stays in front of it, instead of the engine putting the real table name there.
        /// </summary>
        private static void TestAliasMemberInsertsBareColumn()
        {
            string sql = "SELECT c. FROM dbo.Customer c";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, "SELECT c.".Length);
            Equal(CompletionContextKind.Member, context.Kind);
            Equal("c", context.Qualifier);

            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            CompletionItem id = items.FirstOrDefault(i => i.DisplayText == "Id");
            True(id != null);
            Equal("[Id]", id.InsertText);
            True(!items.Any(i => (i.InsertText ?? string.Empty).StartsWith("[Customer]", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>UPDATE ... SET must suggest the writable columns of the target.</summary>
        private static void TestUpdateSetOffersColumns()
        {
            string sql = "UPDATE dbo.Customer SET ";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.UpdateSet, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(i => i.DisplayText == "Name" && i.Kind == CompletionItemKind.Column));

            // Through an alias, and with the caret on a new line under the SET.
            string alias = "UPDATE c SET c. FROM dbo.Customer c";
            Equal(CompletionContextKind.Member, SqlCompletionAnalyzer.Analyze(alias, "UPDATE c SET c.".Length).Kind);
        }

        /// <summary>Data types have to be suggested where a type belongs.</summary>
        private static void TestDataTypesAreOffered()
        {
            // The popup filters by the typed prefix, so the test uses a position with an empty prefix.
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT CAST(x AS ", "SELECT CAST(x AS ".Length);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(i => i.DisplayText == "INT" && i.Kind == CompletionItemKind.Type));
            True(items.Any(i => i.DisplayText == "NVARCHAR(50)" && i.Kind == CompletionItemKind.Type));
            True(items.Any(i => i.DisplayText == "DATETIME2" && i.Kind == CompletionItemKind.Type));
        }
        private static MetadataSnapshot UpdateJoinMetadata()
        {
            var snapshot = new MetadataSnapshot();
            var header = new DatabaseObjectMetadata { Database = "db", Schema = "dbo", Name = "ygxxh", Kind = CompletionItemKind.Table };
            header.Columns.Add(new ColumnMetadata { Name = "djlsh", DataType = "int" });
            header.Columns.Add(new ColumnMetadata { Name = "HeaderName", DataType = "nvarchar" });
            var detail = new DatabaseObjectMetadata { Database = "db", Schema = "dbo", Name = "ygdab", Kind = CompletionItemKind.Table };
            detail.Columns.Add(new ColumnMetadata { Name = "djlsh", DataType = "int" });
            detail.Columns.Add(new ColumnMetadata { Name = "Memo", DataType = "nvarchar" });
            snapshot.Objects.Add(header);
            snapshot.Objects.Add(detail);
            return snapshot;
        }

        /// <summary>
        /// "UPDATE a SET | FROM ygxxh a INNER JOIN ygdab b ON ..." declares its aliases after the caret, so a
        /// scan limited to the text before the caret saw none of them and offered nothing.
        /// </summary>
        private static void TestUpdateSetResolvesAliasesDeclaredAfterTheSet()
        {
            MetadataSnapshot snapshot = UpdateJoinMetadata();
            const string tail = " FROM ygxxh a INNER JOIN ygdab b ON a.djlsh = b.djlsh";

            string set = "UPDATE a SET " + tail;
            CompletionContext setContext = SqlCompletionAnalyzer.Analyze(set.Substring(0, "UPDATE a SET ".Length), "UPDATE a SET ".Length, set.Substring("UPDATE a SET ".Length));
            Equal(CompletionContextKind.UpdateSet, setContext.Kind);
            Equal("ygxxh", setContext.TargetObject);
            List<CompletionItem> setItems = SqlCompletionAnalyzer.BuildItems(setContext, snapshot);
            True(setItems.Any(i => i.DisplayText == "HeaderName"));
            True(setItems.Any(i => i.DisplayText == "(all column assignments)"));

            // The alias of the update target only.
            string headerMember = "UPDATE a SET a." + tail;
            CompletionContext headerContext = SqlCompletionAnalyzer.Analyze(headerMember.Substring(0, "UPDATE a SET a.".Length), "UPDATE a SET a.".Length, headerMember.Substring("UPDATE a SET a.".Length));
            Equal(CompletionContextKind.Member, headerContext.Kind);
            List<CompletionItem> headerItems = SqlCompletionAnalyzer.BuildItems(headerContext, snapshot);
            True(headerItems.Any(i => i.DisplayText == "HeaderName" && i.InsertText == "[HeaderName]"));
            True(!headerItems.Any(i => i.DisplayText == "Memo"));

            // The shape from the report: one table, one alias, FROM on a later line.  The column must be
            // inserted bare - the alias map knows both "a" and "ygxxh" for this table and used to prefix
            // the insert with the table name.
            string single = "UPDATE a\nSET \nFROM ygxxh a";
            int singleCaret = single.IndexOf("SET ", StringComparison.Ordinal) + 4;
            CompletionContext singleContext = SqlCompletionAnalyzer.Analyze(single.Substring(0, singleCaret), singleCaret, single.Substring(singleCaret));
            List<CompletionItem> singleItems = SqlCompletionAnalyzer.BuildItems(singleContext, snapshot);
            CompletionItem headerOne = singleItems.FirstOrDefault(i => i.DisplayText == "HeaderName");
            True(headerOne != null);
            Equal("[HeaderName]", headerOne.InsertText);
            Equal(1, singleItems.Count(i => i.DisplayText == "HeaderName"));

            // The alias of the joined table.
            string detailMember = "UPDATE a SET b." + tail;
            CompletionContext detailContext = SqlCompletionAnalyzer.Analyze(detailMember.Substring(0, "UPDATE a SET b.".Length), "UPDATE a SET b.".Length, detailMember.Substring("UPDATE a SET b.".Length));
            List<CompletionItem> detailItems = SqlCompletionAnalyzer.BuildItems(detailContext, snapshot);
            True(detailItems.Any(i => i.DisplayText == "Memo" && i.InsertText == "[Memo]"));
        }
        /// <summary>Function entries in the popup must explain how the function is used.</summary>
        private static void TestFunctionEntriesCarryDocumentation()
        {
            // The helpers are what the popup uses for both the list line and the details panel.
            Equal("DATEADD(datepart, number, date)", SqlCompletionAnalyzer.FunctionSignature("DATEADD"));

            string doc = SqlCompletionAnalyzer.FunctionDocumentation("DATEADD");
            True(doc.Contains("DATEADD(datepart, number, date)"));
            True(doc.Contains("SELECT DATEADD(day, -7, GETDATE());"));

            string stringAgg = SqlCompletionAnalyzer.FunctionDocumentation("STRING_AGG");
            True(stringAgg.Contains("STRING_AGG(expression, separator)"));
            // The "Example" label is localized, so assert on the example body instead.
            True(stringAgg.Contains("WITHIN GROUP"));

            // A function without a hand-written entry still answers with a usable explanation.
            // The fallback text is localized, so only the function name is a stable assertion.
            True(SqlCompletionAnalyzer.FunctionDocumentation("SUSER_SNAME").Contains("SUSER_SNAME"));
        }

        private static void TestExecuteParametersBeforeIncompleteStatement()
        {
            string sql = "EXEC dbo.usp_Test @Id = NULL  CREA";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.General, context.Kind);
            Equal("CREA", context.Prefix);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "CREATE"));
        }

        private static void TestExecuteBeforeIncompleteStatement()
        {
            string sql = "EXEC dbo.usp_Test   CREA";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.General, context.Kind);
            Equal("CREA", context.Prefix);
        }

        private static void TestExecutePositionalValue()
        {
            string sql = "EXEC dbo.usp_Test DEFAULT";
            Equal(CompletionContextKind.ExecuteArguments, SqlCompletionAnalyzer.Analyze(sql, sql.Length).Kind);
        }

        private static void TestExactSnippetPrefix()
        {
            var snippets = new[] { new SnippetItem("ssf", "", "SELECT *\r\nFROM") };
            True(SqlCompletionAnalyzer.IsExactSnippetPrefix("SSF", true, snippets));
            True(!SqlCompletionAnalyzer.IsExactSnippetPrefix("SSF", false, snippets));
        }

        /// <summary>
        /// The editor serves a keystroke that only extends the word from the candidates built before
        /// that letter existed.  "OUTER APPLY (" + "SSF" filtered a list cached at the parenthesis,
        /// which never held the snippet, so the row only appeared on Ctrl+Space.
        /// </summary>
        private static void TestSnippetPrefixRebuildsCache()
        {
            var snippets = new[] { new SnippetItem("SSF", "", "SELECT *\r\nFROM") };
            var stale = new List<CompletionItem>
            {
                new CompletionItem { DisplayText = "SsfTable", Kind = CompletionItemKind.Table }
            };
            True(SqlCompletionAnalyzer.NeedsSnippetReanalysis("SSF", true, stale, snippets),
                "completing a snippet prefix kept serving the stale list");
            var fresh = new List<CompletionItem>(stale)
            {
                new CompletionItem { DisplayText = "SSF", Kind = CompletionItemKind.Snippet, InsertText = "SELECT *\r\nFROM" }
            };
            True(!SqlCompletionAnalyzer.NeedsSnippetReanalysis("SSF", true, fresh, snippets),
                "a list that already carries the snippet was rebuilt again");
            True(!SqlCompletionAnalyzer.NeedsSnippetReanalysis("SS", true, stale, snippets),
                "a partial prefix is not a snippet");
            True(!SqlCompletionAnalyzer.NeedsSnippetReanalysis("SSF", false, stale, snippets),
                "snippets are off, so there is nothing to surface");
        }

        /// <summary>
        /// "FROM dbo.Header AS A, dbo.Detail AS B" with an unfinished predicate: both sources have
        /// to be in scope, or typing after AND offered only the first table's columns (the shape
        /// reported from a real script, where "AND |" showed functions and one column only).
        /// </summary>
        private static void TestCommaSeparatedFromScopesColumns()
        {
            var metadata = new MetadataSnapshot();
            var header = new DatabaseObjectMetadata { Schema = "dbo", Name = "Header", Kind = CompletionItemKind.Table };
            header.Columns.Add(new ColumnMetadata { Name = "HeaderName", DataType = "nvarchar" });
            metadata.Objects.Add(header);
            var detail = new DatabaseObjectMetadata { Schema = "dbo", Name = "Detail", Kind = CompletionItemKind.Table };
            detail.Columns.Add(new ColumnMetadata { Name = "Memo", DataType = "nvarchar" });
            metadata.Objects.Add(detail);

            string sql = "SELECT *\nFROM dbo.Header AS A, dbo.Detail AS B\nWHERE A.djlsh = B.djlsh\n"
                + "AND A.HeaderName = 'x'\nAND ISNULL(B.Memo,'') <> ''\nAND ";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length - (sql.LastIndexOf('\n') + 1));
            Equal(CompletionContextKind.Predicate, context.Kind);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(i => i.Kind == CompletionItemKind.Column && i.DisplayText == "Memo"),
                "the second source's column is missing: " + string.Join(", ", items.Take(8).Select(i => i.DisplayText)));
        }

        private static void TestNestedSelectStatementScope()
        {
            string sql = "SELECT *\nFROM dbo.Customer c\nWHERE EXISTS (\n    SELECT 1\n    FROM dbo.Customer i\n    WHERE i.";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.Member, context.Kind);
            Equal("i", context.Qualifier);
        }

        private static void TestSameNameSmartJoin()
        {
            MetadataSnapshot metadata = Metadata();
            var orders = new DatabaseObjectMetadata { Schema = "dbo", Name = "Orders", Kind = CompletionItemKind.Table };
            orders.Columns.Add(new ColumnMetadata { Name = "Id", DataType = "int" });
            metadata.Objects.Add(orders);
            string sql = "SELECT * FROM dbo.Customer c JOIN Orders";
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze(sql, sql.Length), metadata);
            // ON is offered once the typed name is complete, one entry per key.
            True(items.Any(x => x.DisplayText.StartsWith("ON ", StringComparison.Ordinal) && x.InsertText.Contains(" ON ")
                && x.Description != null && x.Description.StartsWith("join on", StringComparison.Ordinal)));
            True(items.Any(x => x.DisplayText == "dbo.Orders" && !x.InsertText.Contains(" ON ")));
        }

        private static void TestCustomJoinRule()
        {
            DatabaseObjectMetadata customer = Metadata().Objects[0];
            var invoice = new DatabaseObjectMetadata { Schema = "sales", Name = "Invoice", Kind = CompletionItemKind.Table };
            invoice.Columns.Add(new ColumnMetadata { Name = "CustomerKey", DataType = "int" });
            var settings = new SettingsManager.SqlCompletionSettings
            {
                joinColumnRules = "dbo.Customer.Id=sales.Invoice.CustomerKey"
            };
            List<SqlCompletionAnalyzer.JoinColumnMatch> matches = SqlCompletionAnalyzer.InferJoinColumns(customer, invoice, settings);
            True(matches.Count == 1 && matches[0].IsCustom && matches[0].SourceColumn == "Id" && matches[0].TargetColumn == "CustomerKey");
        }

        private static void TestAliasRules()
        {
            var table = new DatabaseObjectMetadata { Schema = "dbo", Name = "vw_SalesOrder", Kind = CompletionItemKind.View };
            var custom = new SettingsManager.SqlCompletionSettings { customAliases = "dbo.vw_SalesOrder=so", aliasPrefixToIgnore = "vw_" };
            Equal("so2", SqlCompletionAnalyzer.MakeAlias(table, new[] { "so" }, custom));
            custom.customAliases = string.Empty;
            Equal("so", SqlCompletionAnalyzer.MakeAlias(table, Array.Empty<string>(), custom));
        }

        private static void TestCompletionDetails()
        {
            MetadataSnapshot metadata = Metadata();
            var view = new DatabaseObjectMetadata
            {
                Schema = "reporting", Name = "CustomerView", Kind = CompletionItemKind.View,
                Definition = "CREATE VIEW reporting.CustomerView AS SELECT Id FROM dbo.Customer"
            };
            view.Columns.Add(new ColumnMetadata { Name = "Id", DataType = "int" });
            metadata.Objects.Add(view);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(
                SqlCompletionAnalyzer.Analyze("SELECT * FROM CustomerV", 23), metadata);
            CompletionItem item = items.First(x => x.DisplayText == "reporting.CustomerView");
            True(item.DetailText.Contains("Id  int") && item.DetailText.Contains("CREATE VIEW"));
        }

        private static void TestColumnPickerCommit()
        {
            var id = new CompletionItem { Kind = CompletionItemKind.Column, InsertText = "[Id]" };
            var name = new CompletionItem { Kind = CompletionItemKind.Column, InsertText = "[Name]" };
            Equal("[Id], [Name]", CompletionController.GetCommitInsertText(id, new[] { id, name }));
        }

        private static void TestMetadataInvalidationDetection()
        {
            True(SqlMetadataCache.ShouldInvalidateAfterExecution("CREATE TABLE dbo.T(Id int)"));
            True(SqlMetadataCache.ShouldInvalidateAfterExecution("EXEC sys.sp_rename 'dbo.T', 'T2'"));
            True(!SqlMetadataCache.ShouldInvalidateAfterExecution("-- CREATE TABLE dbo.Hidden(Id int)\nSELECT 1"));
        }

        private static MetadataSnapshot Metadata()
        {
            var metadata = new MetadataSnapshot();
            var table = new DatabaseObjectMetadata { Schema = "dbo", Name = "Customer", Kind = CompletionItemKind.Table };
            table.Columns.Add(new ColumnMetadata { Name = "Id", DataType = "int" });
            table.Columns.Add(new ColumnMetadata { Name = "Name", DataType = "nvarchar", IsNullable = true });
            metadata.Objects.Add(table);
            metadata.Objects.Add(new DatabaseObjectMetadata { Schema = "dbo", Name = "usp_Test", Kind = CompletionItemKind.Procedure });
            metadata.Objects.Add(new DatabaseObjectMetadata { Schema = "dbo", Name = "CalculateTax", Kind = CompletionItemKind.Function });
            metadata.Parameters.Add(new RoutineParameterMetadata { ObjectName = "dbo.usp_Test", Name = "@Id", DataType = "int" });
            return metadata;
        }

        private static void TestSelectWholeColumnClause()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT ", 7, " FROM dbo.Customer c");
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "(all SELECT columns)" && x.InsertText.Contains("[Id]") && x.InsertText.Contains("[Name]")));
        }

        private static void TestGroupByWholeClause()
        {
            string sql = "SELECT c.Name, COUNT(*) AS Total FROM dbo.Customer c GROUP BY ";
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze(sql, sql.Length), Metadata());
            True(items.Any(x => x.DisplayText == "(all non-aggregated SELECT expressions)" && x.InsertText == "c.Name"));
        }

        private static void TestOutputColumns()
        {
            string sql = "UPDATE dbo.Customer SET Name = 'x' OUTPUT inserted.";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.Output, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "inserted.Id" && x.InsertText == "[Id]"));
        }

        private static void TestCreateTableClause()
        {
            string sql = "CREATE TABLE dbo.NewTable (";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.CreateTableDefinition, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.InsertText.Contains("PRIMARY KEY")));
        }

        private static void TestCreateIndexColumns()
        {
            string sql = "CREATE INDEX IX_Customer ON dbo.Customer (";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.IndexColumns, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "Id"));
        }

        private static void TestAlterTableActions()
        {
            string sql = "ALTER TABLE dbo.Customer ";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.AlterTableAction, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.InsertText.StartsWith("ADD CONSTRAINT", StringComparison.Ordinal)));
        }

        private static void TestAlterTableColumns()
        {
            string sql = "ALTER TABLE dbo.Customer ALTER COLUMN ";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.AlterTableColumn, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "Name"));
        }

        private static void TestConstraintColumns()
        {
            string sql = "ALTER TABLE dbo.Customer ADD CONSTRAINT PK_Customer PRIMARY KEY (";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.ConstraintColumns, context.Kind);
            True(SqlCompletionAnalyzer.BuildItems(context, Metadata()).Any(x => x.DisplayText == "Id"));
        }

        private static void TestRichMetadataDetails()
        {
            MetadataSnapshot metadata = Metadata();
            DatabaseObjectMetadata table = metadata.Objects[0];
            table.Description = "Customer master data";
            table.EstimatedRowCount = 12345;
            table.Columns[0].IsPrimaryKey = true;
            table.Columns[0].Description = "Surrogate key";
            table.Indexes.Add(new IndexMetadata { Name = "PK_Customer", IsPrimaryKey = true, TypeDescription = "CLUSTERED" });
            table.Indexes[0].KeyColumns.Add("Id");
            table.CheckConstraints.Add(new CheckConstraintMetadata { Name = "CK_Name", Definition = "([Name] <> '')" });
            CompletionItem item = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM Cus", 17), metadata).First(x => x.DisplayText == "dbo.Customer");
            True(item.DetailText.Contains("Customer master data") && item.DetailText.Contains("Estimated rows: 12,345")
                && item.DetailText.Contains("PK_Customer") && item.DetailText.Contains("CK_Name") && item.DetailText.Contains("PK Id"));
        }

        private static void TestParameterInformation()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Parameters.Add(new RoutineParameterMetadata { ObjectName = "dbo.usp_Test", Name = "@Name", DataType = "nvarchar", MaxLength = 40, Ordinal = 1, HasDefaultValue = true });
            CompletionContext context = SqlCompletionAnalyzer.Analyze("EXEC dbo.usp_Test @Id = NULL, @Name = ", 39);
            string info = CompletionPresenter.BuildParameterInfo(metadata, context);
            True(info.Contains("dbo.usp_Test") && info.Contains("> @Name nvarchar(20) = default"));
        }

        private static void TestCompletionSplitter()
        {
            Equal(594, CompletionPresenter.CalculateSplitterDistance(1000, 400, 260, 220, 6));
            Equal(260, CompletionPresenter.CalculateSplitterDistance(640, 900, 260, 220, 6));
        }

        private static void TestCompletionPopupConstructs()
        {
            using (var presenter = new CompletionPresenter())
                True(presenter != null);
        }

        private static void TestInsertColumnContext()
        {
            string sql = "INSERT INTO dbo.Customer (";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.InsertColumns, context.Kind);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "Id" && x.Kind == CompletionItemKind.Column));
        }

        private static void TestQualifiedDataSourceFiltering()
        {
            string sql = "SELECT * FROM dbo.";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            True(context.IsDataSourceMember);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "Customer" && x.Kind == CompletionItemKind.Table));
            True(!items.Any(x => x.Kind == CompletionItemKind.Procedure || x.Kind == CompletionItemKind.Function));
        }

        private static void TestOmittedSchemaCompletion()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Databases.Add("OtherDb");
            metadata.Objects[0].Database = "OtherDb";
            metadata.Objects[0].IsExternal = true;
            string sql = "SELECT * FROM OtherDb..Cus";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            True(context.HasOmittedSchemaQualifier && context.IsDataSourceMember);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(x => x.DisplayText == "Customer" && x.InsertText == "[Customer]"));
        }

        private static void TestOmittedSchemaInsertColumns()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Objects[0].Database = "OtherDb";
            string sql = "INSERT INTO OtherDb..Customer (";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, sql.Length);
            Equal(CompletionContextKind.InsertColumns, context.Kind);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(x => x.DisplayText == "Id" && x.Kind == CompletionItemKind.Column));
        }

        private static void TestExecuteObjects()
        {
            var items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("EXEC dbo.", 9), Metadata());
            True(items.Exists(x => x.DisplayText == "usp_Test"));
            True(!items.Exists(x => x.DisplayText == "Customer"));
        }

        private static void TestMetadataConnectionSettings()
        {
            var info = new ScriptFactoryAccess.ConnectionInfo
            {
                FullConnectionString = "Data Source=server;Initial Catalog=OriginalDb;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Application Intent=ReadOnly;Multi Subnet Failover=True"
            };
            using (var connection = info.CreateSqlConnection("OtherDb"))
            {
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
                Equal("OtherDb", builder.InitialCatalog);
                Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
                True(builder.TrustServerCertificate);
                Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, builder.ApplicationIntent);
                True(builder.MultiSubnetFailover);
            }
        }

        private static void TestMetadataTokenConnection()
        {
            var info = new ScriptFactoryAccess.ConnectionInfo
            {
                FullConnectionString = "Data Source=server;Initial Catalog=OriginalDb;Authentication=Active Directory Interactive;User ID=user@example.com;Encrypt=True",
                AccessToken = "test-token"
            };
            using (var connection = info.CreateSqlConnection())
            {
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
                Equal(Microsoft.Data.SqlClient.SqlAuthenticationMethod.NotSpecified, builder.Authentication);
                Equal(string.Empty, builder.UserID);
                Equal("test-token", connection.AccessToken);
            }
        }

        private static void TestMetadataConnectionTrustOverride()
        {
            var info = new ScriptFactoryAccess.ConnectionInfo
            {
                FullConnectionString = "Data Source=server;Initial Catalog=OriginalDb;Integrated Security=True;Encrypt=True;Trust Server Certificate=False"
            };
            using (var connection = info.CreateSqlConnection(trustServerCertificate: true))
            {
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
                True(builder.TrustServerCertificate);
                Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
            }
        }

        private static void TestShortcutAutomationBinding()
        {
            object binding = ShortcutManager.CreateAutomationBinding("F12");
            True(binding is string);
            Equal("Global::F12", (string)binding);
            True(ShortcutManager.CreateAutomationBinding(string.Empty) is object[]);
        }

        private static void TestSettingsWindowConstructs()
        {
            var control = new SettingsWindowControl();
            True(control != null);
        }

        private static void TestDeclaredVariable()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("DECLARE @CustomerId int; SELECT @Cus", 36);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "@CustomerId"));
        }

        private static void TestExecuteParameters()
        {
            var items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("EXEC dbo.usp_Test @", 19), Metadata());
            True(items.Exists(x => x.DisplayText == "@Id"));
        }

        private static void TestMemberColumns()
        {
            var items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT c.", 9, " FROM dbo.Customer c"), Metadata());
            True(items.Exists(x => x.DisplayText == "Id"));
        }

        private static void TestPredicateOperators()
        {
            var items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c WHERE ", 35), Metadata());
            True(items.Exists(x => x.DisplayText == "IS NULL"));
        }

        private static void TestDerivedScope()
        {
            string sql = "SELECT d.Id FROM (SELECT i.Id FROM dbo.InnerTable i) d JOIN dbo.OuterTable o ON o.Id=d.Id";
            SqlSemanticModel model = SqlSemanticModel.Create(sql, sql.IndexOf("d.Id", StringComparison.Ordinal));
            True(model.Aliases.ContainsKey("d"));
            True(model.Aliases.ContainsKey("o"));
            True(!model.Aliases.ContainsKey("i"));
        }

        private static void TestSymbolAlias()
        {
            string sql = "SELECT c.Name FROM dbo.Customer c";
            SqlSymbolResolution result = SqlSymbolResolver.Resolve(sql, sql.IndexOf("Name", StringComparison.Ordinal), Metadata());
            Equal("dbo.Customer", result.ObjectName);
        }

        private static void TestLocalSymbol()
        {
            string sql = "WITH Recent AS (SELECT 1 AS Id) SELECT * FROM Recent";
            SqlSymbolResolution result = SqlSymbolResolver.Resolve(sql, sql.LastIndexOf("Recent", StringComparison.Ordinal), Metadata());
            True(result.LocalDefinitionOffset >= 0);
        }

        private static void TestTypedBitValues()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Objects[0].Columns.Add(new ColumnMetadata { Name = "Enabled", DataType = "bit" });
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo.Customer c WHERE c.Enabled = ", 47);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(x => x.DisplayText == "0") && items.Any(x => x.DisplayText == "1"));
        }

        private static void TestGroupByDedupe()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT Name FROM dbo.Customer c GROUP BY Name, ", 47);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(!items.Any(x => x.DisplayText == "Name"));
        }

        private static void TestCrossDatabaseMember()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Databases.Add("OtherDb");
            metadata.Schemas.Add("sales");
            metadata.Objects.Add(new DatabaseObjectMetadata { Database = "OtherDb", Schema = "sales", Name = "Invoice", Kind = CompletionItemKind.Table });
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT * FROM OtherDb.", 22);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(x => x.DisplayText == "sales"));
        }

        private static MetadataSnapshot LinkedMetadata()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.LinkedServers.Add("ReportingServer");
            metadata.LinkedServerDatabases["ReportingServer"] = new List<string> { "Warehouse" };
            metadata.Objects.Add(new DatabaseObjectMetadata { Server = "ReportingServer", Database = "Warehouse", Schema = "sales", Name = "FactOrder", Kind = CompletionItemKind.Table });
            return metadata;
        }

        private static void TestLinkedServerDatabaseMember()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ReportingServer.", 30), LinkedMetadata());
            True(items.Any(x => x.DisplayText == "Warehouse" && x.Kind == CompletionItemKind.Database));
        }

        private static void TestLinkedServerSchemaMember()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ReportingServer.Warehouse.", 40), LinkedMetadata());
            True(items.Any(x => x.DisplayText == "sales" && x.Kind == CompletionItemKind.Schema));
        }

        private static void TestLinkedServerObjectMember()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ReportingServer.Warehouse.sales.", 46), LinkedMetadata());
            True(items.Any(x => x.DisplayText == "FactOrder" && x.Kind == CompletionItemKind.Table));
        }

        private static void TestUnknownFunctionArguments()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT dbo.Unknown(", 19), Metadata());
            True(!items.Any(x => x.Kind == CompletionItemKind.Parameter || x.DisplayText == "NULL" || x.DisplayText == "DEFAULT"));
        }

        private static void TestDerivedColumns()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT d. FROM (SELECT Id, Name FROM dbo.Customer) d", 9);
            DatabaseObjectMetadata derived = context.LocalObjects.FirstOrDefault(x => x.Name == "d");
            True(derived != null && derived.Columns.Any(x => x.Name == "Id") && derived.Columns.Any(x => x.Name == "Name"));
        }

        private static void TestCorrelatedScope()
        {
            string sql = "SELECT * FROM dbo.Customer c WHERE EXISTS (SELECT 1 FROM dbo.Customer i WHERE i.Id=c.)";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql.Substring(0, sql.IndexOf("c.)", StringComparison.Ordinal) + 2), sql.IndexOf("c.)", StringComparison.Ordinal) + 2, ")");
            True(context.Aliases.ContainsKey("c") && context.Aliases.ContainsKey("i"));
        }

        private static void TestAmbiguousColumns()
        {
            MetadataSnapshot metadata = Metadata();
            var orders = new DatabaseObjectMetadata { Schema = "dbo", Name = "Orders", Kind = CompletionItemKind.Table };
            orders.Columns.Add(new ColumnMetadata { Name = "Id", DataType = "int" });
            metadata.Objects.Add(orders);
            string sql = "SELECT  FROM dbo.Customer c JOIN dbo.Orders o ON c.Id=o.Id";
            const int caret = 7;
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql.Substring(0, caret), caret, sql.Substring(caret));
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            // Both sources own an Id, so each row names its own source instead of repeating "Id".
            Equal(2, items.Count(x => x.DisplayText == "c.Id" || x.DisplayText == "o.Id"));
            True(items.Any(x => x.DisplayText == "c.Id" && x.InsertText == "[c].[Id]"));
            True(items.Any(x => x.DisplayText == "o.Id" && x.InsertText == "[o].[Id]"));
        }

        private static void TestGroupBySelectColumn()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT Name FROM dbo.Customer c GROUP BY ", 41);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "Name"));
        }

        private static void TestCrossDatabaseExecute()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Objects.Add(new DatabaseObjectMetadata { Database = "OtherDb", Schema = "dbo", Name = "usp_Remote", Kind = CompletionItemKind.Procedure });
            CompletionContext context = SqlCompletionAnalyzer.Analyze("EXEC OtherDb.dbo.", 17);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(x => x.DisplayText == "usp_Remote"));
        }

        private static void TestDataSourceAcronymMatch()
        {
            MetadataSnapshot metadata = new MetadataSnapshot();
            metadata.Schemas.Add("dbo");
            metadata.Objects.Add(new DatabaseObjectMetadata { Schema = "dbo", Name = "WSDD", Kind = CompletionItemKind.Table });
            metadata.Objects.Add(new DatabaseObjectMetadata { Schema = "dbo", Name = "WsdData", Kind = CompletionItemKind.Table });

            // Typing WD in the FROM position must suggest both tables: WsdData
            // via its W-D initials and WSDD via the in-order %W%D% channel.
            string sql = "SELECT * FROM WD";
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze(sql, sql.Length), metadata);
            True(items.Any(x => x.DisplayText.EndsWith("WsdData", StringComparison.OrdinalIgnoreCase)));
            True(items.Any(x => x.DisplayText.EndsWith("WSDD", StringComparison.OrdinalIgnoreCase)));

            string qualified = "SELECT * FROM dbo.WD";
            List<CompletionItem> qualifiedItems = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze(qualified, qualified.Length), metadata);
            True(qualifiedItems.Any(x => x.DisplayText.EndsWith("WsdData", StringComparison.OrdinalIgnoreCase)));
            True(qualifiedItems.Any(x => x.DisplayText.EndsWith("WSDD", StringComparison.OrdinalIgnoreCase)));
        }

        private static void TestCurrentDatabaseObjectInsertion()
        {
            MetadataSnapshot metadata = Metadata();
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14);
            CompletionItem item = SqlCompletionAnalyzer.BuildItems(context, metadata).First(x => x.DisplayText == "dbo.Customer");
            Equal("[dbo].[Customer]", item.InsertText);
        }

        private static void TestExternalObjectInsertion()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Objects.Add(new DatabaseObjectMetadata { Database = "OtherDb", Schema = "sales", Name = "Invoice", Kind = CompletionItemKind.Table, IsExternal = true });
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14);
            CompletionItem item = SqlCompletionAnalyzer.BuildItems(context, metadata).First(x => x.DisplayText == "OtherDb.sales.Invoice");
            Equal("[OtherDb].[sales].[Invoice]", item.InsertText);
        }

        private static void TestScalarFunctionSourceFiltering()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), Metadata());
            True(!items.Any(x => x.DisplayText.IndexOf("CalculateTax", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static void TestTableValuedFunctionSourceFiltering()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Objects.Add(new DatabaseObjectMetadata { Schema = "dbo", Name = "GetCustomers", Kind = CompletionItemKind.Function, IsTableValuedFunction = true });
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), metadata);
            True(items.Any(x => x.DisplayText == "dbo.GetCustomers"));
        }

        private static void TestTableValuedFunctionDocumentation()
        {
            // Every built-in table-valued function used to answer with the bare summary line
            // "SQL Server table-valued function"; the details pane now carries its signature,
            // a usage note and an example, exactly like a scalar function.
            string[] builtIns = { "OPENJSON", "OPENXML", "STRING_SPLIT", "GENERATE_SERIES" };
            foreach (string name in builtIns)
            {
                True(SqlCompletionAnalyzer.HasUsageNotes(name));
                string english = SqlCompletionAnalyzer.FunctionDocumentation(name);
                True(english.StartsWith(name + "(", StringComparison.Ordinal));
                True(english.Contains("Example:"));
                // The note has to describe the FROM form, not only the name.
                True(english.IndexOf("table-valued", StringComparison.OrdinalIgnoreCase) >= 0);
            }

            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), Metadata());
            CompletionItem openJson = items.FirstOrDefault(i => i.DisplayText == "OPENJSON");
            True(openJson != null);
            True(openJson.DetailText.Contains("OPENJSON("));
            True(openJson.DetailText.Contains("WITH ("));

            // A user-defined table-valued function says how it is called before the column list.
            MetadataSnapshot metadata = Metadata();
            metadata.Objects.Add(new DatabaseObjectMetadata { Schema = "dbo", Name = "GetCustomers", Kind = CompletionItemKind.Function, IsTableValuedFunction = true });
            items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), metadata);
            CompletionItem tvf = items.FirstOrDefault(i => i.DisplayText == "dbo.GetCustomers");
            True(tvf != null);
            True(tvf.DetailText.Contains("FROM/JOIN"));
            True(tvf.DetailText.Contains("dbo.GetCustomers"));

            // The same notes are translated, so a Chinese popup does not fall back to English.
            string englishDoc = SqlCompletionAnalyzer.FunctionDocumentation("OPENJSON");
            LocalizationManager.SetLanguage(LocalizationManager.ChineseLanguage, false);
            try
            {
                True(!string.Equals(SqlCompletionAnalyzer.FunctionDocumentation("OPENJSON"), englishDoc, StringComparison.Ordinal));
                CompletionItem translated = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), metadata)
                    .FirstOrDefault(i => i.DisplayText == "dbo.GetCustomers");
                True(translated != null && translated.DetailText.Contains("表值函数"));
            }
            finally
            {
                LocalizationManager.SetLanguage(LocalizationManager.EnglishLanguage, false);
            }
        }

        private static void TestTableValueConstructorItem()
        {
            MetadataSnapshot metadata = Metadata();
            List<CompletionItem> sources = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), metadata);
            CompletionItem constructor = sources.FirstOrDefault(i => i.Kind == CompletionItemKind.Snippet && i.InsertText.Contains("VALUES"));
            if (constructor == null)
                throw new InvalidOperationException("VALUES snippet missing; list: " + string.Join(", ", sources.Select(i => i.Kind + ":" + i.DisplayText)));
            Equal("VALUES (...)", constructor.DisplayText);
            // It has to paste a whole derived table, not just the keyword.
            if (!constructor.InsertText.StartsWith("(", StringComparison.Ordinal)) throw new InvalidOperationException("insert='" + constructor.InsertText + "'");
            if (!constructor.InsertText.Contains("AS ")) throw new InvalidOperationException("insert='" + constructor.InsertText + "'");
            if (!constructor.InsertText.Contains("c1") || !constructor.InsertText.Contains("c2")) throw new InvalidOperationException("insert='" + constructor.InsertText + "'");
            if (!constructor.InsertText.Contains("${1:1}")) throw new InvalidOperationException("insert='" + constructor.InsertText + "'");
            if (!constructor.DetailText.Contains("column list")) throw new InvalidOperationException("detail='" + constructor.DetailText + "'");

            // Typing the name of the keyword brings it to the top of the source list.
            List<CompletionItem> filtered = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM VAL", 17), metadata);
            if (filtered.Count == 0 || filtered[0].DisplayText != "VALUES (...)")
                throw new InvalidOperationException("filtered: " + string.Join(", ", filtered.Select(i => i.DisplayText)));

            // It is a source, not something that can follow SELECT.
            List<CompletionItem> selectList = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT ", 7), metadata);
            True(!selectList.Any(i => i.DisplayText == "VALUES (...)"));

            // The usage note is translated for the Chinese popup.
            LocalizationManager.SetLanguage(LocalizationManager.ChineseLanguage, false);
            try
            {
                CompletionItem translated = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM ", 14), metadata)
                    .FirstOrDefault(i => i.DisplayText == "VALUES (...)");
                True(translated != null && translated.DetailText.Contains("临时表"));
            }
            finally
            {
                LocalizationManager.SetLanguage(LocalizationManager.EnglishLanguage, false);
            }
        }

        private static void TestParameterTypeDisplay()
        {
            Equal("nvarchar(20)", new RoutineParameterMetadata { DataType = "nvarchar", MaxLength = 40 }.TypeDisplay);
            Equal("decimal(18,4)", new RoutineParameterMetadata { DataType = "decimal", Precision = 18, Scale = 4 }.TypeDisplay);
            Equal("varchar(50)", new ColumnMetadata { DataType = "varchar", MaxLength = 50 }.TypeDisplay);
        }

        private static void TestTableVariableBatchScope()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("DECLARE @t TABLE(Id int);\nGO\nSELECT @t.", 10);
            True(!context.LocalObjects.Any(x => x.Name == "@t"));
        }

        private static void TestDroppedTempTable()
        {
            string sql = "CREATE TABLE #t(Id int); DROP TABLE #t; SELECT #t.";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, 10);
            True(!context.LocalObjects.Any(x => x.Name == "#t"));
        }

        private static void TestMultipleVariables()
        {
            CompletionContext context = SqlCompletionAnalyzer.Analyze("DECLARE @a int, @b nvarchar(20); SELECT @", 8);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "@a") && items.Any(x => x.DisplayText == "@b"));
        }

        private static void TestQualifiedGroupBy()
        {
            MetadataSnapshot metadata = Metadata();
            var orders = new DatabaseObjectMetadata { Database = "db", Schema = "dbo", Name = "Orders", Kind = CompletionItemKind.Table };
            orders.Columns.Add(new ColumnMetadata { Name = "Id", DataType = "int" });
            metadata.Objects.Add(orders);
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT c.Id FROM dbo.Customer c JOIN dbo.Orders o ON c.Id=o.Id GROUP BY c.Id, ", 80);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(items.Any(x => x.DisplayText == "o.Id" && x.InsertText == "[o].[Id]") && !items.Any(x => x.InsertText == "[c].[Id]"));
        }

        private static void TestQualifiedTypedValue()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Objects[0].Columns.Add(new ColumnMetadata { Name = "Enabled", DataType = "int" });
            var orders = new DatabaseObjectMetadata { Schema = "dbo", Name = "Orders", Kind = CompletionItemKind.Table };
            orders.Columns.Add(new ColumnMetadata { Name = "Enabled", DataType = "bit" });
            metadata.Objects.Add(orders);
            string sql = "SELECT * FROM dbo.Customer c JOIN dbo.Orders o ON c.Id=o.Id WHERE o.Enabled = ";
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze(sql, sql.Length), metadata);
            True(items.Any(x => x.DisplayText == "0") && items.Any(x => x.DisplayText == "1"));
        }

        private static void TestSchemaCandidate()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Schemas.Add("dbo");
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM dbo", 17), metadata);
            CompletionItem schema = items.First(x => x.DisplayText == "dbo" && x.Kind == CompletionItemKind.Schema);
            Equal("[dbo]", schema.InsertText);
        }

        private static void TestBuiltInTableFunctions()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.CompatibilityLevel = 170;
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT * FROM OPEN", 18), metadata);
            True(items.Any(x => x.DisplayText == "OPENJSON"));
        }

        private static void TestDerivedColumnIsolation()
        {
            string before = "SELECT d.";
            string after = " FROM (SELECT (SELECT i.Secret FROM dbo.InnerTable i) AS X, c.Id FROM dbo.Customer c) d";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(before, before.Length, after);
            DatabaseObjectMetadata derived = context.LocalObjects.First(x => x.Name == "d");
            True(derived.Columns.Any(x => x.Name == "X") && derived.Columns.Any(x => x.Name == "Id") && !derived.Columns.Any(x => x.Name == "Secret"));
        }

        private static void TestEqualsAlias()
        {
            string sql = "SELECT CustomerName = Name FROM dbo.Customer ORDER BY Cust";
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze(sql, sql.Length), Metadata());
            True(items.Any(x => x.DisplayText == "CustomerName"));
        }

        private static void TestCrossDatabaseParameterIsolation()
        {
            MetadataSnapshot metadata = Metadata();
            metadata.Parameters.Clear();
            metadata.Parameters.Add(new RoutineParameterMetadata { ObjectName = "CurrentDb.dbo.usp_Test", Name = "@Current", DataType = "int" });
            metadata.Parameters.Add(new RoutineParameterMetadata { ObjectName = "OtherDb.dbo.usp_Test", Name = "@Other", DataType = "int" });
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("EXEC OtherDb.dbo.usp_Test @", 27), metadata);
            True(items.Any(x => x.DisplayText == "@Other") && !items.Any(x => x.DisplayText == "@Current"));
        }

        private static void TestExternalClone()
        {
            var source = new DatabaseObjectMetadata { Database = "OtherDb", Schema = "dbo", Name = "T", Kind = CompletionItemKind.Table };
            source.Columns.Add(new ColumnMetadata { Name = "Id", DataType = "int" });
            DatabaseObjectMetadata clone = CompletionController.CloneObject(source, true);
            True(!source.IsExternal && clone.IsExternal && !ReferenceEquals(source.Columns[0], clone.Columns[0]));
        }

        private static void TestQualifiedSmartJoin()
        {
            MetadataSnapshot metadata = Metadata();
            var orders = new DatabaseObjectMetadata { Schema = "dbo", Name = "Orders", Kind = CompletionItemKind.Table };
            orders.Columns.Add(new ColumnMetadata { Name = "CustomerId", DataType = "int" });
            metadata.Objects.Add(orders);
            var fk = new ForeignKeyMetadata { ParentObject = "dbo.Orders", ReferencedObject = "dbo.Customer" };
            fk.ParentColumns.Add("CustomerId"); fk.ReferencedColumns.Add("Id"); metadata.ForeignKeys.Add(fk);
            string before = "SELECT * FROM dbo.Customer c JOIN dbo.Orders";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(before, before.Length);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, metadata);
            True(context.IsJoinSource && items.Any(x => x.DisplayText.StartsWith("ON ", StringComparison.Ordinal) && x.InsertText.Contains(" ON ")));
        }

        private static void TestSpecialExpressionKinds()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT CURRENT", 14), Metadata());
            True(items.Any(x => x.DisplayText == "CURRENT_TIMESTAMP" && x.Kind == CompletionItemKind.Keyword)
                && !items.Any(x => x.DisplayText == "CURRENT_TIMESTAMP" && x.Kind == CompletionItemKind.Function));
        }

        private static void TestFutureCteScope()
        {
            string sql = "WITH First AS (SELECT 1 AS Id), Future AS (SELECT 2 AS Id) SELECT * FROM First";
            int caret = sql.IndexOf("1 AS", StringComparison.Ordinal);
            SqlSemanticModel model = SqlSemanticModel.Create(sql, caret);
            True(!model.LocalObjects.Any(x => x.Name == "Future"));
        }

        private static void TestInsertSelectAsteriskContext()
        {
            string sql = "INSERT dbo.Copy SELECT * FROM dbo.Customer";
            True(AsteriskExpansionService.CanAnalyzeAsteriskContext(sql, sql.IndexOf('*')));
        }

        private static void TestCreateViewAsteriskContext()
        {
            string sql = "CREATE VIEW dbo.v AS SELECT * FROM dbo.Customer";
            True(AsteriskExpansionService.CanAnalyzeAsteriskContext(sql, sql.IndexOf('*')));
        }

        private static void TestAsteriskTempLifecycle()
        {
            string sql = "CREATE TABLE #t(Id int); ALTER TABLE #t ADD Name nvarchar(20); DROP TABLE #t; CREATE TABLE #t(FinalId bigint); SELECT * FROM #t;";
            string setup = AsteriskExpansionService.GetPriorTempTableCreateStatements(sql, sql.LastIndexOf("SELECT", StringComparison.Ordinal));
            True(setup.Contains("FinalId") && !setup.Contains("Id int") && !setup.Contains("ADD Name"));
        }

        private static void TestBuiltInDefaultValue()
        {
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT ISNULL(Name, ", 20), Metadata());
            True(items.Any(x => x.DisplayText == "NULL") && !items.Any(x => x.DisplayText == "DEFAULT"));
        }

        private static void TestAlteredTempTable()
        {
            string sql = "CREATE TABLE #t(Id int); ALTER TABLE #t ADD Name nvarchar(20), Enabled bit; SELECT #t.";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(sql, 10);
            DatabaseObjectMetadata table = context.LocalObjects.First(x => x.Name == "#t");
            True(table.Columns.Any(x => x.Name == "Name") && table.Columns.Any(x => x.Name == "Enabled"));
        }

        private static void TestDerivedStarColumns()
        {
            string before = "SELECT d.";
            string after = " FROM (SELECT * FROM dbo.Customer) d";
            CompletionContext context = SqlCompletionAnalyzer.Analyze(before, before.Length, after);
            List<CompletionItem> items = SqlCompletionAnalyzer.BuildItems(context, Metadata());
            True(items.Any(x => x.DisplayText == "Id") && items.Any(x => x.DisplayText == "Name"));
        }

        private static void RunSqlServerIntegration()
        {
            string connectionString = Environment.GetEnvironmentVariable("MSSQLTOOL_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                SkippedCount++;
                Console.WriteLine("SKIP: SQL Server metadata integration (set MSSQLTOOL_TEST_CONNECTION to enable)." );
                return;
            }

            Run("SQL Server metadata integration", () => TestSqlServerIntegration(connectionString));
        }

        private static void TestSqlServerIntegration(string connectionString)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            var info = new ScriptFactoryAccess.ConnectionInfo { FullConnectionString = connectionString, Database = builder.InitialCatalog, ServerName = builder.DataSource };
            MetadataSnapshot snapshot = SqlMetadataCache.RefreshAsync(info, CancellationToken.None).GetAwaiter().GetResult();
            True(snapshot.Databases.Count > 0);
            True(snapshot.Objects.Count > 0);
        }
    }
}
