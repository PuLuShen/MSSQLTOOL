using System;
using System.Collections.Generic;
using System.Linq;

namespace MSSQLTool.RegressionTests
{
    /// <summary>
    /// Formatting profile regression tests.  Every option the formatting UI exposes has to change
    /// the formatted output, otherwise the dialog promises behaviour the formatter does not have.
    /// </summary>
    internal static partial class Program
    {
        internal static void RunFormattingTests()
        {
            Run("Formatting keeps comments", TestFormattingKeepsComments);
            Run("Formatting preserves literals", TestFormattingPreservesLiterals);
            Run("Formatting rejects broken SQL", TestFormattingRejectsBrokenSql);
            Run("Formatting normalizes LF documents", TestFormattingKeepsLineEndings);
            Run("Formatting preset: compact collapses SELECT", TestCompactPreset);
            Run("Formatting preset: readable stacks GROUP BY", TestReadablePreset);
            Run("Formatting option: keyword casing", TestKeywordCasing);
            Run("Formatting option: keep keyword casing", TestKeepKeywordCasing);
            Run("Formatting option: identifier casing", TestIdentifierCasing);
            Run("Formatting option: alias casing", TestAliasCasing);
            Run("Formatting option: variable casing", TestVariableCasing);
            Run("Formatting option: data type casing", TestDataTypeCasing);
            Run("Formatting option: semicolons and alignment", TestSemicolonsAndAlignment);
            Run("Formatting option: spacing", TestSpacingOptions);
            Run("Formatting option: single line compaction", TestSingleLineCompaction);
            Run("Formatting option: right margin compaction", TestRightMarginCompaction);
            Run("Formatting option: new line before ON", TestNewLineBeforeOn);
            Run("Formatting option: condition break position", TestConditionBreakPosition);
            Run("Formatting option: WHERE condition indent", TestConditionIndent);
            Run("Formatting option: indentation switches", TestIndentationOptions);
            Run("Formatting option: GROUP BY and ORDER BY stacking", TestGroupOrderStacking);
            Run("Formatting option: FROM list stacking", TestFromStacking);
            Run("Formatting option: SELECT list stacking", TestSelectStacking);
            Run("Formatting option: subquery options", TestSubqueryOptions);
            Run("Formatting option: INSERT options", TestInsertOptions);
            Run("Formatting option: UPDATE options", TestUpdateOptions);
            Run("Formatting option: DELETE options", TestDeleteOptions);
            Run("Formatting option: MERGE options", TestMergeOptions);
            Run("Formatting option: CASE options", TestCaseOptions);
            Run("Formatting option: block options", TestBlockOptions);
            Run("Formatting option: DECLARE and cursor options", TestDeclareOptions);
            Run("Formatting option: routine options", TestRoutineOptions);
            Run("Formatting option: trigger options", TestTriggerOptions);
            Run("Formatting option: view options", TestViewOptions);
            Run("Formatting option: CREATE TABLE options", TestCreateTableOptions);
            Run("Formatting option: legacy rewrites", TestLegacyRewrites);
            Run("Formatting: profile round trip", TestProfileRoundTrip);
            Run("Formatting: all boolean switches", TestAllBooleanSwitches);
            Run("Formatting: exported profile is JSON", TestProfileExport);
            Run("Formatting: options dialog is localized", TestFormattingUiIsLocalized);
        }

        private static string Format(string sql, Action<FormatterOptions> configure)
        {
            FormatterOptions options = new FormatterOptions();
            configure?.Invoke(options);
            return TSqlFormatter.FormatCode(sql, options);
        }

        /// <summary>Asserts a substring and prints the whole formatted text when it is missing.</summary>
        private static void Has(string text, string expected)
        {
            string actual = Normalize(text);
            if (actual == null || actual.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("expected to find [" + expected + "]\n--- formatted ---\n" + actual);
        }

        private static void HasNot(string text, string unexpected)
        {
            string actual = Normalize(text);
            if (actual != null && actual.IndexOf(unexpected, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("did not expect [" + unexpected + "]\n--- formatted ---\n" + actual);
        }

        /// <summary>
        /// Line endings are compared with a plain newline: the formatter keeps the line endings of
        /// the document it is given, and most test inputs carry none.
        /// </summary>
        private static string Normalize(string text) => text?.Replace("\r\n", "\n");

        /// <summary>Leading whitespace of the line that contains <paramref name="fragment"/>.</summary>
        private static int IndentOf(string text, string fragment)
        {
            string normalized = Normalize(text);
            foreach (string line in normalized.Split('\n'))
            {
                if (line.TrimStart().StartsWith(fragment, StringComparison.Ordinal))
                    return line.Length - line.TrimStart().Length;
            }

            throw new InvalidOperationException("the formatted text has no line starting with [" + fragment + "]\n--- formatted ---\n" + normalized);
        }

        private static void EqualIndent(int expected, string text, string fragment)
        {
            int actual = IndentOf(text, fragment);
            if (actual != expected)
                throw new InvalidOperationException("expected indentation " + expected + " for [" + fragment + "] but got " + actual
                    + "\n--- formatted ---\n" + Normalize(text));
        }

        private static void StartsWithText(string text, string prefix)
        {
            string actual = Normalize(text);
            if (actual == null || !actual.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("expected the text to start with [" + prefix + "]\n--- formatted ---\n" + actual);
        }

        /// <summary>Asserts that one fragment is indented further than another and prints both.</summary>
        private static void Deeper(string deeperText, string deeperFragment, string shallowerText, string shallowerFragment)
        {
            int deeper = IndentOf(deeperText, deeperFragment);
            int shallower = IndentOf(shallowerText, shallowerFragment);
            if (deeper <= shallower)
                throw new InvalidOperationException("expected [" + deeperFragment + "] at " + deeper + " to be deeper than ["
                    + shallowerFragment + "] at " + shallower
                    + "\n--- deeper ---\n" + Normalize(deeperText) + "\n--- shallower ---\n" + Normalize(shallowerText));
        }

        private static void TestFormattingKeepsComments()
        {
            Has(Format("select 1 -- keep me\n", null), "-- keep me");
            Has(Format("select /* block */ 1", null), "/* block */");
            Has(Format("select 1 -- trailing", null), "-- trailing");
        }

        private static void TestFormattingPreservesLiterals()
        {
            string formatted = Format("select 'a,b  c' as x, N'ünïcode' as y", null);
            Has(formatted, "'a,b  c'");
            Has(formatted, "N'ünïcode'");
        }

        private static void TestFormattingRejectsBrokenSql()
        {
            Throws(() => Format("select from where", null));
        }

        private static void TestFormattingKeepsLineEndings()
        {
            // Compared raw: every other assertion normalizes line endings away.
            True(Format("select 1\nfrom t", null).IndexOf("\r\n", StringComparison.Ordinal) < 0);
            True(Format("select 1\r\nfrom t", null).IndexOf("\r\n", StringComparison.Ordinal) >= 0);
        }
        private static void TestCompactPreset()
        {
            string compact = TSqlFormatter.FormatCode("select a, b from t where a = 1", FormatterOptions.CreatePreset(FormatPreset.Compact));
            Equal("SELECT a, b FROM t WHERE a = 1;", compact.Trim());
        }

        private static void TestReadablePreset()
        {
            string readable = TSqlFormatter.FormatCode("select a, b from t group by a, b order by a, b",
                FormatterOptions.CreatePreset(FormatPreset.Readable));
            Has(readable, "GROUP BY a,\n");
            Has(readable, "ORDER BY a,\n");
        }

        private static void TestKeywordCasing()
        {
            Has(Format("select 1", o => o.casing.keywords = TokenCasing.Lowercase), "select");
            Has(Format("select 1", o => o.casing.keywords = TokenCasing.PascalCase), "Select");
            Has(Format("select 1", o => o.casing.keywords = TokenCasing.Uppercase), "SELECT");
        }

        private static void TestKeepKeywordCasing()
        {
            string lower = Format("select a from t", o => o.casing.keywords = TokenCasing.Preserve);
            Has(lower, "select");
            Has(lower, "from");
            HasNot(lower, "SELECT");
            HasNot(lower, "FROM");

            string upper = Format("SELECT a FROM t", o => o.casing.keywords = TokenCasing.Preserve);
            Has(upper, "SELECT");
            Has(upper, "FROM");
        }

        private static void TestIdentifierCasing()
        {
            Has(Format("select CustomerName from Customer", o => o.casing.identifiers = TokenCasing.Lowercase), "customername");
            Has(Format("select CustomerName from Customer", o => o.casing.identifiers = TokenCasing.Uppercase), "CUSTOMERNAME");
            // Bracketed identifiers keep their exact spelling.
            Has(Format("select [Mixed Name] from t", o => o.casing.identifiers = TokenCasing.Lowercase), "[Mixed Name]");
        }

        private static void TestAliasCasing()
        {
            string formatted = Format("select c.Id from Customer as c", o => o.casing.aliases = TokenCasing.Uppercase);
            Has(formatted, "AS C");
            Has(formatted, "c.Id");
        }

        private static void TestVariableCasing()
        {
            Has(Format("declare @myValue int", o => o.casing.variables = TokenCasing.Uppercase), "@MYVALUE");
            Has(Format("declare @MyValue int", o => o.casing.variables = TokenCasing.Lowercase), "@myvalue");
        }

        private static void TestDataTypeCasing()
        {
            Has(Format("declare @a NVARCHAR(20), @b INT", o => o.casing.dataTypes = TokenCasing.Lowercase), "nvarchar(20)");
            Has(Format("declare @a nvarchar(20)", o => o.casing.dataTypes = TokenCasing.Uppercase), "NVARCHAR(20)");
        }

        private static void TestSemicolonsAndAlignment()
        {
            Has(Format("select 1", o => o.includeSemicolons = true).TrimEnd(), ";");
            HasNot(Format("select 1", o => o.includeSemicolons = false), ";");
            HasNot(Format("select 1; select 2", o => o.includeSemicolons = false), ";");
            Has(Format("select 1 as first, 2 as second", o => o.alignClauseBodies = true), "SELECT 1");
        }

        private static void TestSpacingOptions()
        {
            const string sql = "select a, b from t where a = 1";
            Has(Format(sql, o => { o.select.stackSelectColumns = false; o.lineBreaks.newLinePerSelectColumn = false; o.spacing.spaceAfterComma = false; }), "a,b");
            Has(Format(sql, o => { o.select.stackSelectColumns = false; o.lineBreaks.newLinePerSelectColumn = false; o.spacing.spaceBeforeComma = true; }), "a , b");
            HasNot(Format(sql, o => o.spacing.spaceAroundOperators = false), "a = 1");
            Has(Format("select count(1) from t", o => o.spacing.spaceBetweenFunctionAndParenthesis = true), "COUNT (1)");
            HasNot(Format("select count(1) from t", null), "COUNT (");
        }

        private static void TestSingleLineCompaction()
        {
            const string sql = "select a from t where a = 1";
            Equal("SELECT a FROM t WHERE a = 1;",
                Format(sql, o => { o.compact.keepShortQuerySingleLine = true; o.compact.singleLineThreshold = 200; }).Trim());
            Has(Format(sql, o => { o.compact.keepShortQuerySingleLine = true; o.compact.singleLineThreshold = 5; }), "\n");
        }

        private static void TestRightMarginCompaction()
        {
            const string sql = "select a from t where a = 1";
            Equal("SELECT a FROM t WHERE a = 1;",
                Format(sql, o => { o.compact.keepShortQuerySingleLine = true; o.compact.keepWithinRightMargin = true; o.compact.rightMargin = 200; }).Trim());
            Has(Format(sql, o => { o.compact.keepShortQuerySingleLine = true; o.compact.keepWithinRightMargin = true; o.compact.rightMargin = 10; }), "\n");
        }

        private static void TestNewLineBeforeOn()
        {
            const string sql = "select * from a join b on b.Id = a.Id";
            Has(Format(sql, null), "ON b.Id = a.Id");
            HasNot(Format(sql, o => o.lineBreaks.newLineBeforeOn = false), "\nON ");
        }

        private static void TestConditionBreakPosition()
        {
            const string sql = "select * from t where a = 1 and b = 2";
            Has(Format(sql, null), "WHERE a = 1\n");
            Has(Format(sql, null), "AND b = 2");
            Has(Format(sql, o => o.select.conditionBreak = ConditionBreakPosition.AfterOperator), "WHERE a = 1 AND\n");
        }

        private static void TestConditionIndent()
        {
            const string sql = "select * from t where a = 1 and b = 2";
            EqualIndent(4, Format(sql, o => o.select.indentWhereCondition = true), "AND b = 2");
            EqualIndent(0, Format(sql, o => o.select.indentWhereCondition = false), "AND b = 2");

            const string join = "select * from a join b on b.Id = a.Id and b.X = 1";
            EqualIndent(4, Format(join, o => o.select.indentOnCondition = true), "AND b.X = 1");
            EqualIndent(0, Format(join, o => o.select.indentOnCondition = false), "AND b.X = 1");
        }

        private static void TestIndentationOptions()
        {
            const string block = "if @a = 1 begin select 1 end";
            EqualIndent(8, Format(block, null), "SELECT 1");
            EqualIndent(4, Format(block, o => { o.block.indentCode = false; o.indent.indentBlock = false; }), "SELECT 1");

            const string caseSql = "select case when a = 1 then 2 end from t";
            EqualIndent(4, Format(caseSql, null), "WHEN a = 1");
            EqualIndent(0, Format(caseSql, o => { o.caseExpression.indentBody = false; o.indent.indentCase = false; }), "WHEN a = 1");

            const string subquery = "select * from t where Id in (select Id, Name from Other where Name = 'a very long one')";
            string indented = Format(subquery, o => { o.subquery.allowSingleLine = false; o.compact.keepShortSubquerySingleLine = false; o.subquery.indentSubquery = true; });
            string flat = Format(subquery, o => { o.subquery.allowSingleLine = false; o.compact.keepShortSubquerySingleLine = false; o.subquery.indentSubquery = false; o.indent.indentSubquery = false; });
            Deeper(indented, "FROM Other", flat, "FROM Other");

            Has(Format("select 1 from t", o => o.indent.indentSize = 2), "SELECT 1");
        }

        private static void TestGroupOrderStacking()
        {
            const string sql = "select count(*) from t group by a, b order by a, b";
            string stacked = Format(sql, o => { o.select.stackGroupBy = true; o.select.stackOrderBy = true; });
            Has(stacked, "GROUP BY a,\n");
            Has(stacked, "ORDER BY a,\n");

            string flat = Format(sql, null);
            Has(flat, "GROUP BY a, b");
            Has(flat, "ORDER BY a, b");
        }

        private static void TestFromStacking()
        {
            const string sql = "select * from a, b";
            Has(Format(sql, o => o.select.stackFromList = true), "FROM a,\n");
            Has(Format(sql, o => o.select.stackFromList = false), "FROM a, b");
        }

        private static void TestSelectStacking()
        {
            const string sql = "select a, b from t";
            Has(Format(sql, o => o.select.stackSelectColumns = true), "SELECT a,\n");
            Has(Format(sql, o => { o.select.stackSelectColumns = false; o.lineBreaks.newLinePerSelectColumn = false; }), "SELECT a, b");
        }

        private static void TestSubqueryOptions()
        {
            // 77 characters once collapsed, well past the 50 character single-line threshold.
            const string longSubquery = "select * from t where Id in (select Id, Name from Other where Name = 'a very long name')";
            HasNot(Format(longSubquery, null), "(SELECT Id, Name FROM Other");
            Has(Format(longSubquery, o => { o.subquery.allowSingleLine = false; o.compact.keepShortSubquerySingleLine = false; }), "FROM Other");

            const string shortSubquery = "select * from t where Id in (select Id from Other)";
            Has(Format(shortSubquery, null), "(SELECT Id FROM Other)");

            string indented = Format(longSubquery, o => { o.subquery.allowSingleLine = false; o.compact.keepShortSubquerySingleLine = false; o.subquery.indentSubquery = true; });
            string flat = Format(longSubquery, o => { o.subquery.allowSingleLine = false; o.compact.keepShortSubquerySingleLine = false; o.subquery.indentSubquery = false; o.indent.indentSubquery = false; });
            Deeper(indented, "FROM Other", flat, "FROM Other");

            Has(Format(shortSubquery, o => o.subquery.inheritMainQueryFormat = false), "SELECT");
        }

        private static void TestInsertOptions()
        {
            const string sql = "insert into Log (Id, Name) values (1, 'a'), (2, 'b')";
            Has(Format(sql, o => o.insert.newLineBeforeValues = true), ")\nVALUES");
            HasNot(Format(sql, o => o.insert.newLineBeforeValues = false), ")\nVALUES");

            string stacked = Format(sql, o => o.insert.stackMultipleValues = true);
            Has(stacked, "(1, 'a'),\n");
            EqualIndent(7, stacked, "(2, 'b')");

            Has(Format(sql, o => o.insert.stackMultipleValues = false), "(1, 'a'), (2, 'b')");
        }

        private static void TestUpdateOptions()
        {
            const string sql = "update t set a = 1, b = 2 from t where a = 1";
            string formatted = Format(sql, null);
            Has(formatted, "SET a = 1,\n");
            Has(formatted, "\nFROM");
            Has(Format(sql, o => o.update.newLineBeforeSet = false), "SET");
        }

        private static void TestDeleteOptions()
        {
            const string sql = "delete from t where a = 1";
            Has(Format(sql, null), "WHERE a = 1");
            Has(Format("delete t from t join u on u.Id = t.Id where a = 1", o => o.delete.newLineBeforeJoin = true), "JOIN");
            Has(Format("delete t where a = 1", null), "WHERE a = 1");
        }

        private static void TestMergeOptions()
        {
            const string sql = "merge into Target as t using Source as s on t.Id = s.Id when matched then update set t.Name = s.Name;";
            string formatted = Format(sql, null);
            StartsWithText(formatted, "MERGE INTO Target AS t");
            Has(formatted, "\nUSING Source AS s");
            Has(formatted, "\n    ON t.Id = s.Id");
            Has(formatted, "\nWHEN MATCHED");
            Has(formatted, "\n    THEN UPDATE");

            StartsWithText(Format(sql, o => o.merge.newLineBeforeInto = true), "MERGE\nINTO");
            // The generator already breaks before WHEN; THEN is what the profile moves.
            Has(Format(sql, o => o.merge.newLineBeforeThen = false), "WHEN MATCHED THEN");
        }

        private static void TestCaseOptions()
        {
            const string sql = "select case when a = 1 then 2 else 3 end from t";
            string formatted = Format(sql, null);
            Has(formatted, "\n    WHEN a = 1");
            Has(formatted, "\n    ELSE 3");
            Has(Format(sql, o => o.caseExpression.newLineBeforeWhen = false), "CASE WHEN a = 1");
            Has(Format(sql, o => o.caseExpression.newLineBeforeThen = true), "WHEN a = 1\n");
            Has(Format("select case a when 1 then 2 end from t", o => o.caseExpression.newLineBeforeWhen = true), "\n");
        }

        private static void TestBlockOptions()
        {
            const string sql = "begin select 1 select 2 end";
            Has(Format(sql, o => o.block.newLineAfterBegin = true), "BEGIN\n");
            Has(Format(sql, o => o.block.newLineBeforeEnd = true), "\nEND");

            const string ifSql = "if @a = 1 begin select 1 end else begin select 2 end";
            Has(Format(ifSql, o => o.block.newLineAfterIfCondition = true), "IF @a = 1\n");
            Has(Format(ifSql, o => o.block.newLineBeforeElse = true), "\nELSE");

            const string whileSql = "while @i < 10 begin set @i = @i + 1 end";
            Has(Format(whileSql, o => o.block.newLineAfterWhileCondition = true), "WHILE @i < 10\n");
        }

        private static void TestDeclareOptions()
        {
            const string sql = "declare @a int, @b int";
            string stacked = Format(sql, o => o.declare.stackVariables = true);
            Has(stacked, "@a AS INT,\n");
            EqualIndent(8, stacked, "@b AS INT");
            Has(Format(sql, o => o.declare.stackVariables = false), "@a AS INT, @b AS INT");

            const string cursor = "declare c cursor for select Id, Name from T where Id > 1";
            HasNot(Format(cursor, o => o.declare.cursorQuerySingleLine = true), "SELECT Id,\n");
            Has(Format(cursor, o => o.declare.indentCursorSubquery = true), "SELECT Id,");
        }

        private static void TestRoutineOptions()
        {
            const string procedure = "create procedure dbo.P @Id int, @Name nvarchar(20) as begin select @Id, @Name end";
            Has(Format(procedure, o => o.routine.stackParameters = true), "@Id INT,\n");

            string indented = Format(procedure, o => o.routine.indentBeginEnd = true);
            EqualIndent(4, indented, "BEGIN");
            EqualIndent(4, indented, "END");
            EqualIndent(8, indented, "SELECT @Id");

            EqualIndent(0, Format(procedure, o => o.routine.indentBody = false), "SELECT @Id");
            Has(Format("create function dbo.F(@Id int) returns int as begin return @Id end", o => o.routine.newLineBeforeReturns = true), "\nRETURNS INT");
        }

        private static void TestTriggerOptions()
        {
            const string trigger = "create trigger dbo.T on dbo.Target after insert as begin select 1 end";
            string formatted = Format(trigger, null);
            Has(formatted, "\n    ON dbo.Target");
            Has(formatted, "\n    AFTER INSERT");
            Has(formatted, "\n    AS");
            Has(formatted, "BEGIN");
            Has(formatted, "\n    END");

            EqualIndent(4, Format(trigger, o => o.trigger.indentBeginEnd = true), "BEGIN");
        }

        private static void TestViewOptions()
        {
            const string view = "create view dbo.V as select Id, Name from T";
            Has(Format(view, o => o.view.stackColumns = true), "SELECT Id,\n");
            Has(Format(view, null), "\nAS\n");
            Has(Format(view, o => o.view.indentSubquery = true), "SELECT");
            Has(Format(view, o => o.view.newLineBeforeAs = false), "AS");
        }

        private static void TestCreateTableOptions()
        {
            const string table = "create table dbo.T (Id int not null, Name nvarchar(20) null) with (memory_optimized = on, durability = schema_only)";
            string stacked = Format(table, o => o.createTable.stackStorageOptions = true);
            Has(stacked, "MEMORY_OPTIMIZED = ON,\n");
            EqualIndent(6, stacked, "DURABILITY = SCHEMA_ONLY");

            Has(Format(table, o => o.createTable.stackStorageOptions = false), "MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY");
            Has(Format(table, o => o.createTable.stackColumns = true), "Id   INT");
        }

        private static void TestLegacyRewrites()
        {
            const string join = "select * from a inner join b on b.Id = a.Id";
            Has(Format(join, o => o.removeNewLineAfterJoin = true), "INNER JOIN b");
            Has(Format(join, o => o.addTabAfterJoinOn = true), "ON b.Id = a.Id");

            const string cross = "select * from a cross join b";
            Has(Format(cross, o => o.moveCrossJoinToNewLine = true), "CROSS");

            Has(Format("exec dbo.P @a = 1, @b = 2", o => o.breakSprocParametersPerLine = true), "@a");
            Has(Format("declare @a int, @b int", o => o.breakVariableDefinitionsPerLine = true), "@b");
            Has(Format("select COUNT(1) from t", o => o.uppercaseBuiltInFunctions = true), "COUNT(1)");
            Has(Format("begin select 1 end", o => o.unindentBeginEndBlocks = true), "BEGIN");
            Has(Format("select case when a = 1 then 2 end from t", o => o.formatCaseAsMultiline = true), "WHEN");
            Has(Format("begin select 1 select 2 end", o => o.addNewLineBetweenStatementsInBlocks = true), "SELECT 1");
            Has(Format("create procedure dbo.P @Id int as begin select @Id end", o => o.breakSprocDefinitionParametersPerLine = true), "@Id");
        }

        private static void TestProfileRoundTrip()
        {
            FormatterOptions options = new FormatterOptions();
            options.ApplyPreset(FormatPreset.Readable);
            options.indent.indentSize = 3;
            options.casing.identifiers = TokenCasing.Lowercase;

            FormatterOptions restored = FormatterOptions.FromJson(options.ToJson());
            Equal(nameof(FormatPreset.Readable), restored.preset);
            Equal(3, restored.indent.indentSize);
            Equal(TokenCasing.Lowercase, restored.casing.identifiers);

            Equal(options.ToJson(), options.Clone().ToJson());
        }

        private static void TestAllBooleanSwitches()
        {
            FormatterOptions options = new FormatterOptions();
            options.SetAllBooleanSwitches(true);
            True(options.HasLegacyRewrites());
            True(options.insert.newLineBeforeValues && options.block.indentCode && options.caseExpression.newLineBeforeWhen);

            options.SetAllBooleanSwitches(false);
            True(!options.HasLegacyRewrites());
            True(!options.insert.newLineBeforeValues && !options.block.indentCode && !options.caseExpression.newLineBeforeWhen);

            // The non-boolean settings must survive the sweep.
            Equal(4, options.indent.indentSize);
            Equal(TokenCasing.Uppercase, options.casing.keywords);
        }

        /// <summary>
        /// Every label the formatting UI shows has to exist in the Chinese dictionary.  The list is
        /// the exact text of the options dialog and of the Code Format settings page.
        /// </summary>
        private static void TestFormattingUiIsLocalized()
        {
            string[] labels =
            {
                "Formatting profile", "Preset", "Preset:", "Standard", "Compact", "Readable", "Team standard", "Custom",
                "Common", "Legacy rewrites", "Text casing", "Keywords:", "Built-in functions:", "Data types:",
                "Identifiers:", "Variables:", "Aliases:", "Keep as written", "Spacing", "Space after comma",
                "Space before comma", "Space around operators", "Space between a function name and its parenthesis",
                "Line breaks", "New line for each SELECT column", "New line for each FROM table", "New line before JOIN",
                "New line before ON", "New line before WHERE", "New line for each AND/OR condition",
                "One AND/OR condition per line", "New line before GROUP BY", "New line before ORDER BY", "Indent",
                "Indent width (spaces):", "Indent subqueries", "Indent CASE bodies", "Indent code blocks",
                "Single line", "Keep short queries on a single line", "Keep short subqueries on a single line",
                "Keep single lines within the right margin", "Single line threshold (characters):",
                "One column per line (SELECT list)", "One column per line (column list)", "One table per line (FROM list)",
                "Indent the ON condition", "Indent the WHERE condition", "Break conditions:", "Before the operator",
                "After the operator", "Subquery", "Inherit the main query format", "Allow single line subqueries",
                "New line before the opening parenthesis", "New line after the opening parenthesis",
                "New line before the closing parenthesis", "New line after the closing parenthesis",
                "Indent the subquery body", "New line before VALUES", "One value per line", "One VALUES row per line",
                "New line before SET", "One assignment per line", "New line before FROM",
                "Format the FROM clause like a SELECT", "Format the WHERE clause like a SELECT",
                "New line before INTO", "New line before USING", "New line before WHEN", "New line before THEN",
                "Procedure / function", "One parameter per line", "New line before RETURNS", "New line before AS",
                "Indent the BEGIN..END keywords", "Indent the routine body", "Trigger", "New line before FOR",
                "Indent the trigger body", "View", "Indent the view query", "One column definition per line",
                "One storage option per line", "One variable per line", "Keep the cursor query on a single line",
                "Indent the cursor query", "New line before ELSE", "Indent the CASE body", "Block",
                "One statement per line", "New line after BEGIN", "New line before END", "New line after an IF condition",
                "New line after a WHILE condition", "Indent the block content", "Export profile...", "Import profile...",
                "Advanced options...", "Formatted", "Add semicolons", "Align clause bodies", "Indent size:",
                "Built-in function casing:", "Data type casing:", "Identifier casing:", "Short statement threshold (characters):",
                "Filter settings pages by name", "Reset the settings on the current page to their default values",
                "Add new line between statements in blocks", "Start from a preset, then fine tune any individual option on the other tabs. Changing any option switches the profile to Custom."
            };

            foreach (string label in labels)
            {
                string translated = LocalizationManager.T(label);
                if (string.Equals(translated, label, StringComparison.Ordinal))
                    throw new InvalidOperationException("the formatting label has no Chinese translation: " + label);
            }

            // Messages that carry a trailing detail translate their prefix only.
            Has(LocalizationManager.T("The profile could not be imported: file is missing"), "无法导入配置：file is missing");
            Has(LocalizationManager.T("Refresh failed: timeout"), "刷新失败：timeout");
        }

        private static void TestProfileExport()        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "mssqltool-format-" + Guid.NewGuid().ToString("N") + FormatterOptions.ProfileExtension);
            try
            {
                FormatterOptions options = new FormatterOptions();
                options.ApplyPreset(FormatPreset.Team);
                options.ExportToFile(path);
                FormatterOptions imported = FormatterOptions.ImportFromFile(path);
                Equal(nameof(FormatPreset.Custom), imported.preset);
                True(imported.moveCrossJoinToNewLine);
                Has(imported.ToJson(), "moveCrossJoinToNewLine");
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { }
            }
        }
    }
}
