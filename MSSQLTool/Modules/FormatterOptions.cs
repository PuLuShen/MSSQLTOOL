using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace MSSQLTool
{
    /// <summary>How a class of tokens should be cased by the formatter.</summary>
    public enum TokenCasing
    {
        Preserve,
        Uppercase,
        Lowercase,
        PascalCase
    }

    /// <summary>Built-in formatting styles offered by the options dialog.</summary>
    public enum FormatPreset
    {
        Standard,
        Compact,
        Readable,
        Team,
        Custom
    }

    /// <summary>Where a line break goes relative to a boolean operator in a condition list.</summary>
    public enum ConditionBreakPosition
    {
        BeforeOperator,
        AfterOperator
    }

    /// <summary>Text casing for keywords, functions, data types, identifiers, variables and aliases.</summary>
    public class CasingOptions
    {
        public TokenCasing keywords = TokenCasing.Uppercase;
        public TokenCasing functions = TokenCasing.Uppercase;
        public TokenCasing dataTypes = TokenCasing.Uppercase;
        public TokenCasing identifiers = TokenCasing.Preserve;
        public TokenCasing variables = TokenCasing.Preserve;
        public TokenCasing aliases = TokenCasing.Preserve;
    }

    /// <summary>Whitespace around commas, operators and function parentheses.</summary>
    public class SpacingOptions
    {
        public bool spaceAfterComma = true;
        public bool spaceBeforeComma = false;
        public bool spaceAroundOperators = true;
        public bool spaceBetweenFunctionAndParenthesis = false;
    }

    /// <summary>Clause-level line break switches shared by the common option groups.</summary>
    public class LineBreakOptions
    {
        public bool newLinePerSelectColumn = true;
        public bool newLinePerFromTable = true;
        public bool newLineBeforeJoin = true;
        public bool newLineBeforeOn = true;
        public bool newLineBeforeWhere = true;
        public bool newLinePerCondition = true;
        public bool newLineBeforeGroupBy = true;
        public bool newLineBeforeOrderBy = true;
    }

    /// <summary>Indentation switches and width.</summary>
    public class IndentOptions
    {
        public int indentSize = 4;
        public bool indentSubquery = true;
        public bool indentCase = true;
        public bool indentBlock = true;
    }

    /// <summary>Controls when short statements are collapsed onto a single line.</summary>
    public class CompactOptions
    {
        public bool keepShortQuerySingleLine = false;
        public bool keepShortSubquerySingleLine = true;
        public bool keepWithinRightMargin = false;
        public int singleLineThreshold = 50;

        /// <summary>
        /// Column the formatter treats as the right margin when
        /// <see cref="keepWithinRightMargin"/> is set.  A line only stays on a single line while
        /// it fits inside this width.
        /// </summary>
        public int rightMargin = 120;
    }

    public class SelectFormatOptions
    {
        public bool stackSelectColumns = true;
        public bool stackFromList = true;
        public bool newLineBeforeJoin = true;
        public bool indentOnCondition = true;
        public bool indentWhereCondition = true;
        public ConditionBreakPosition conditionBreak = ConditionBreakPosition.BeforeOperator;
        public bool stackGroupBy = false;
        public bool stackOrderBy = false;
    }

    public class SubqueryFormatOptions
    {
        public bool inheritMainQueryFormat = true;
        public bool allowSingleLine = true;
        public int singleLineThreshold = 50;
        public bool newLineBeforeOpenParenthesis = false;
        public bool newLineAfterOpenParenthesis = false;
        public bool newLineBeforeCloseParenthesis = false;
        public bool newLineAfterCloseParenthesis = true;
        public bool indentSubquery = true;
    }

    public class InsertFormatOptions
    {
        public bool newLineBeforeValues = true;
        public bool stackColumnList = false;
        public bool stackValuesExpressions = true;
        public bool stackMultipleValues = false;
        public bool newLineBeforeOutput = true;
    }

    public class UpdateFormatOptions
    {
        public bool newLineBeforeSet = true;
        public bool stackAssignments = true;
        public bool fromFollowsSelect = true;
        public bool whereFollowsSelect = true;
        public bool newLineBeforeOutput = true;
    }

    public class DeleteFormatOptions
    {
        public bool newLineBeforeFrom = true;
        public bool newLineBeforeJoin = true;
        public bool whereFollowsSelect = true;
        public bool newLineBeforeOutput = true;
    }

    public class MergeFormatOptions
    {
        /// <summary>"MERGE INTO" reads as one clause, so the default keeps INTO on the MERGE line.</summary>
        public bool newLineBeforeInto = false;
        public bool newLineBeforeUsing = true;
        public bool newLineBeforeOn = true;
        public bool newLineBeforeWhen = true;
        public bool newLineBeforeThen = true;
        public bool newLineBeforeOutput = true;
    }

    /// <summary>Routine (procedure / function) specific switches.</summary>
    public class RoutineFormatOptions
    {
        public bool stackParameters = false;
        public bool newLineBeforeReturns = true;
        public bool newLineBeforeAs = true;
        public bool indentBeginEnd = false;
        public bool indentBody = true;
    }

    public class TriggerFormatOptions
    {
        public bool newLineBeforeOn = true;
        public bool newLineBeforeFor = true;
        public bool newLineBeforeAs = true;
        public bool indentBeginEnd = false;
        public bool indentBody = true;
    }

    public class ViewFormatOptions
    {
        public bool stackColumns = true;
        public bool newLineBeforeAs = true;
        public bool indentSubquery = false;
    }

    public class CreateTableFormatOptions
    {
        public bool newLineAfterOpenParenthesis = true;
        public bool newLineBeforeCloseParenthesis = true;
        public bool stackColumns = true;
        public bool stackStorageOptions = true;
    }

    public class DeclareFormatOptions
    {
        public bool stackVariables = true;
        public bool cursorQuerySingleLine = false;
        public bool indentCursorSubquery = false;
    }

    public class CaseFormatOptions
    {
        public bool newLineBeforeWhen = true;
        public bool newLineBeforeThen = false;
        public bool newLineBeforeElse = true;
        public bool indentBody = true;
    }

    public class BlockFormatOptions
    {
        public bool statementPerLine = true;
        public bool newLineAfterBegin = true;
        public bool newLineBeforeEnd = true;
        public bool newLineAfterIfCondition = true;
        public bool newLineBeforeElse = true;
        public bool newLineAfterWhileCondition = true;
        public bool indentCode = true;
    }

    /// <summary>
    /// Complete T-SQL formatting configuration.  This is the single source of truth shared by
    /// the settings page, the Shift+Format options dialog and the registry-backed persistence,
    /// and it can be exported to / imported from a JSON profile file.
    /// </summary>
    public class FormatterOptions
    {
        public const string ProfileExtension = ".mssqltoolformat.json";

        public string preset = nameof(FormatPreset.Standard);

        /// <summary>Aligns the body of each clause under its clause keyword.</summary>
        public bool alignClauseBodies = false;

        /// <summary>
        /// Terminates every generated statement with a semicolon.  The script generator always
        /// emits the terminator, so switching this off is what removes it.
        /// </summary>
        public bool includeSemicolons = true;

        public CasingOptions casing = new CasingOptions();
        public SpacingOptions spacing = new SpacingOptions();
        public LineBreakOptions lineBreaks = new LineBreakOptions();
        public IndentOptions indent = new IndentOptions();
        public CompactOptions compact = new CompactOptions();

        public SelectFormatOptions select = new SelectFormatOptions();
        public SubqueryFormatOptions subquery = new SubqueryFormatOptions();
        public InsertFormatOptions insert = new InsertFormatOptions();
        public UpdateFormatOptions update = new UpdateFormatOptions();
        public DeleteFormatOptions delete = new DeleteFormatOptions();
        public MergeFormatOptions merge = new MergeFormatOptions();
        public RoutineFormatOptions routine = new RoutineFormatOptions();
        public TriggerFormatOptions trigger = new TriggerFormatOptions();
        public ViewFormatOptions view = new ViewFormatOptions();
        public CreateTableFormatOptions createTable = new CreateTableFormatOptions();
        public DeclareFormatOptions declare = new DeclareFormatOptions();
        public CaseFormatOptions caseExpression = new CaseFormatOptions();
        public BlockFormatOptions block = new BlockFormatOptions();

        /// <summary>
        /// Legacy toggles that predate the profile model.  They are preserved so existing
        /// installations keep the exact formatting they already had.
        /// </summary>
        public bool removeNewLineAfterJoin = false;
        public bool addTabAfterJoinOn = false;
        public bool moveCrossJoinToNewLine = false;
        public bool formatCaseAsMultiline = false;
        public bool addNewLineBetweenStatementsInBlocks = false;
        public bool breakSprocParametersPerLine = false;
        public bool uppercaseBuiltInFunctions = false;
        public bool unindentBeginEndBlocks = false;
        public bool breakVariableDefinitionsPerLine = false;
        public bool breakSprocDefinitionParametersPerLine = false;

        /// <summary>True when any legacy token-level rewrite is enabled.</summary>
        public bool HasLegacyRewrites()
        {
            return removeNewLineAfterJoin
                || addTabAfterJoinOn
                || moveCrossJoinToNewLine
                || formatCaseAsMultiline
                || addNewLineBetweenStatementsInBlocks
                || breakSprocParametersPerLine
                || uppercaseBuiltInFunctions
                || unindentBeginEndBlocks
                || breakVariableDefinitionsPerLine
                || breakSprocDefinitionParametersPerLine;
        }

        public FormatterOptions Clone()
        {
            return JsonConvert.DeserializeObject<FormatterOptions>(JsonConvert.SerializeObject(this))
                ?? new FormatterOptions();
        }

        /// <summary>
        /// Creates a preset profile.  "Custom" keeps the caller's current values, which is why
        /// it is resolved by the dialog rather than here.
        /// </summary>
        public static FormatterOptions CreatePreset(FormatPreset preset)
        {
            FormatterOptions options = new FormatterOptions();
            switch (preset)
            {
                case FormatPreset.Compact:
                    options.preset = nameof(FormatPreset.Compact);
                    options.lineBreaks = new LineBreakOptions
                    {
                        newLinePerSelectColumn = false,
                        newLinePerFromTable = false,
                        newLineBeforeJoin = false,
                        newLineBeforeOn = false,
                        newLineBeforeWhere = false,
                        newLinePerCondition = false,
                        newLineBeforeGroupBy = false,
                        newLineBeforeOrderBy = false
                    };
                    options.indent.indentSize = 2;
                    options.select = new SelectFormatOptions
                    {
                        stackSelectColumns = false,
                        stackFromList = false,
                        newLineBeforeJoin = false,
                        indentOnCondition = false,
                        indentWhereCondition = false,
                        stackGroupBy = false,
                        stackOrderBy = false
                    };
                    options.subquery.allowSingleLine = true;
                    options.subquery.singleLineThreshold = 80;
                    options.compact.keepShortQuerySingleLine = true;
                    options.compact.singleLineThreshold = 120;
                    options.insert.newLineBeforeValues = false;
                    options.insert.stackValuesExpressions = false;
                    options.update.newLineBeforeSet = false;
                    options.update.stackAssignments = false;
                    options.delete.newLineBeforeFrom = false;
                    options.delete.newLineBeforeJoin = false;
                    options.merge = new MergeFormatOptions
                    {
                        newLineBeforeInto = false,
                        newLineBeforeUsing = false,
                        newLineBeforeOn = false,
                        newLineBeforeWhen = false,
                        newLineBeforeThen = false,
                        newLineBeforeOutput = false
                    };
                    options.block.statementPerLine = true;
                    options.block.newLineAfterBegin = false;
                    options.block.newLineBeforeEnd = false;
                    break;

                case FormatPreset.Readable:
                    options.preset = nameof(FormatPreset.Readable);
                    options.subquery.allowSingleLine = false;
                    options.compact.keepShortQuerySingleLine = false;
                    options.compact.keepShortSubquerySingleLine = false;
                    options.caseExpression.newLineBeforeThen = true;
                    options.select.stackGroupBy = true;
                    options.select.stackOrderBy = true;
                    break;

                case FormatPreset.Team:
                    options.preset = nameof(FormatPreset.Team);
                    options.casing.keywords = TokenCasing.Uppercase;
                    options.casing.functions = TokenCasing.Uppercase;
                    options.casing.dataTypes = TokenCasing.Uppercase;
                    options.casing.identifiers = TokenCasing.Preserve;
                    options.casing.variables = TokenCasing.Preserve;
                    options.casing.aliases = TokenCasing.Preserve;
                    options.spacing.spaceAfterComma = true;
                    options.spacing.spaceBeforeComma = false;
                    options.spacing.spaceAroundOperators = true;
                    options.spacing.spaceBetweenFunctionAndParenthesis = false;
                    options.indent.indentSize = 4;
                    options.subquery.allowSingleLine = true;
                    options.compact.keepShortQuerySingleLine = false;
                    options.insert.newLineBeforeValues = true;
                    options.update.newLineBeforeSet = true;
                    options.delete.newLineBeforeFrom = true;
                    options.block.statementPerLine = true;
                    options.block.newLineAfterBegin = true;
                    options.block.newLineBeforeEnd = true;
                    options.removeNewLineAfterJoin = false;
                    options.addTabAfterJoinOn = false;
                    options.moveCrossJoinToNewLine = true;
                    break;

                case FormatPreset.Standard:
                default:
                    options.preset = nameof(FormatPreset.Standard);
                    break;
            }

            return options;
        }

        public void ApplyPreset(FormatPreset preset)
        {
            if (preset == FormatPreset.Custom)
            {
                this.preset = nameof(FormatPreset.Custom);
                return;
            }

            FormatterOptions source = CreatePreset(preset);
            CopyFrom(source);
        }

        /// <summary>Overwrites every value in this instance with the values from <paramref name="source"/>.</summary>
        public void CopyFrom(FormatterOptions source)
        {
            if (source == null) return;
            FormatterOptions copy = source.Clone();
            preset = copy.preset;
            alignClauseBodies = copy.alignClauseBodies;
            includeSemicolons = copy.includeSemicolons;
            casing = copy.casing;
            spacing = copy.spacing;
            lineBreaks = copy.lineBreaks;
            indent = copy.indent;
            compact = copy.compact;
            select = copy.select;
            subquery = copy.subquery;
            insert = copy.insert;
            update = copy.update;
            delete = copy.delete;
            merge = copy.merge;
            routine = copy.routine;
            trigger = copy.trigger;
            view = copy.view;
            createTable = copy.createTable;
            declare = copy.declare;
            caseExpression = copy.caseExpression;
            block = copy.block;
            removeNewLineAfterJoin = copy.removeNewLineAfterJoin;
            addTabAfterJoinOn = copy.addTabAfterJoinOn;
            moveCrossJoinToNewLine = copy.moveCrossJoinToNewLine;
            formatCaseAsMultiline = copy.formatCaseAsMultiline;
            addNewLineBetweenStatementsInBlocks = copy.addNewLineBetweenStatementsInBlocks;
            breakSprocParametersPerLine = copy.breakSprocParametersPerLine;
            uppercaseBuiltInFunctions = copy.uppercaseBuiltInFunctions;
            unindentBeginEndBlocks = copy.unindentBeginEndBlocks;
            breakVariableDefinitionsPerLine = copy.breakVariableDefinitionsPerLine;
            breakSprocDefinitionParametersPerLine = copy.breakSprocDefinitionParametersPerLine;
        }

        /// <summary>
        /// Flips every boolean switch in the profile.  Used by the options dialog's
        /// "check all" / "uncheck all" shortcuts; the six casing selectors and the numeric
        /// thresholds are deliberately left alone because they are not booleans.
        /// </summary>
        public void SetAllBooleanSwitches(bool enabled)
        {
            foreach (System.Reflection.FieldInfo field in GetType().GetFields())
            {
                if (field.FieldType == typeof(bool))
                    field.SetValue(this, enabled);
            }

            foreach (System.Reflection.FieldInfo groupField in GetType().GetFields())
            {
                if (!groupField.FieldType.IsClass || groupField.FieldType == typeof(string)) continue;
                foreach (System.Reflection.FieldInfo field in groupField.FieldType.GetFields())
                {
                    if (field.FieldType == typeof(bool))
                        field.SetValue(groupField.GetValue(this), enabled);
                }
            }
        }

        /// <summary>Serializes the profile for the registry or for an exported file.</summary>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        public static FormatterOptions FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new FormatterOptions();
            try
            {
                return JsonConvert.DeserializeObject<FormatterOptions>(json) ?? new FormatterOptions();
            }
            catch (JsonException)
            {
                return new FormatterOptions();
            }
        }

        /// <summary>
        /// Reads a profile from disk.  Both the JSON profile written by <see cref="ExportToFile"/>
        /// and a bare JSON body (for interoperability with hand-written profiles) are accepted.
        /// </summary>
        public static FormatterOptions ImportFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No profile path was supplied.", nameof(path));
            string json = File.ReadAllText(path);
            FormatterOptions options = FromJson(json);
            options.preset = nameof(FormatPreset.Custom);
            return options;
        }

        public void ExportToFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No profile path was supplied.", nameof(path));
            File.WriteAllText(path, ToJson());
        }
    }
}
