using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MSSQLTool.Completion
{
    internal sealed class SqlEditorDiagnostic
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
    }

    /// <summary>SQL Server ScriptDOM-backed semantic view of the caret scope.</summary>
    internal sealed class SqlSemanticModel
    {
        public Dictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<DatabaseObjectMetadata> LocalObjects { get; } = new List<DatabaseObjectMetadata>();
        public List<string> SelectAliases { get; } = new List<string>();
        public HashSet<string> ReferencedColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> GroupByColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<SqlEditorDiagnostic> Diagnostics { get; } = new List<SqlEditorDiagnostic>();
        public bool HasParseErrors { get; private set; }

        /// <summary>
        /// Where the caret sits, read out of the same tree the aliases and the columns come from.
        /// </summary>
        public SqlCaretPosition Position { get; private set; } = new SqlCaretPosition { Kind = SqlCaretKind.Unknown };

        /// <summary>
        /// Start of the top-level statement the caret is in, in the coordinates of the text handed to
        /// <see cref="Create"/>.  The text scan in <see cref="SqlTextContext"/> can only split on
        /// semicolons and line-leading starters, so two statements written on one line need this
        /// answer; the tree that produces it has already been parsed for everything else.
        /// </summary>
        public int StatementStart { get; private set; }

        /// <summary>
        /// Kind of the top-level statement the caret is in (SELECT, INSERT, UPDATE, DELETE, MERGE,
        /// CREATE, ALTER, DROP, EXEC, ...).  Taken from the tree; null when the text did not parse.
        /// </summary>
        public string StatementKind { get; private set; }

        public static SqlSemanticModel Create(string sql, int caretOffset = -1, int compatibilityLevel = 170)
        {
            var model = new SqlSemanticModel();
            try
            {
                string original = sql ?? string.Empty;
                int target = caretOffset < 0 ? original.Length : Math.Max(0, Math.Min(caretOffset, original.Length));
                // MakeParseable inserts placeholders to keep an unfinished member parseable.  Those
                // insertions move every later offset, so the caret is mapped into the rewritten
                // text once and everything downstream — position, scope, visitors — uses that offset
                // instead of the offset the caller passed.
                string parseText = MakeParseable(original, target, out int parseCaret, out List<int> insertedAt);
                TSqlParser parser = CreateParser(compatibilityLevel);
                TSqlFragment fragment = parser.Parse(new StringReader(parseText), out IList<ParseError> errors);
                model.HasParseErrors = errors != null && errors.Count > 0;
                if (errors != null)
                    foreach (ParseError error in errors.Take(10))
                        model.Diagnostics.Add(new SqlEditorDiagnostic { Code = "SQL_PARSE", Message = error.Message, Offset = error.Offset, Length = 1 });
                if (fragment == null) return model;

                model.StatementStart = ToOriginal(StatementStartOf(fragment as TSqlScript, parseCaret), insertedAt);
                model.StatementKind = StatementKindOf(fragment as TSqlScript, parseCaret);
                // Incomplete SQL makes ScriptDOM drop the whole batch, so the tree often has no
                // statement at all while the user is typing.  The leading keyword still answers the
                // one question the lists ask: is this a SELECT, an UPDATE, a DDL...
                if (string.IsNullOrEmpty(model.StatementKind)) model.StatementKind = LeadingKeyword(original);
                model.Position = SqlCaretPositionResolver.Resolve(parseText, parseCaret, fragment);

                var finder = new QueryScopeFinder(parseCaret);
                fragment.Accept(finder);
                foreach (QuerySpecification scope in finder.ContainingScopes.OrderByDescending(x => x.FragmentLength))
                {
                    scope.FromClause?.Accept(new SourceVisitor(model));
                }
                if (finder.Tightest != null)
                {
                    finder.Tightest.Accept(new ExpressionVisitor(model, finder.Tightest));
                    finder.Tightest.GroupByClause?.Accept(new GroupByColumnVisitor(model));
                }
                fragment.Accept(new LocalObjectVisitor(model, parseCaret));
                fragment.Accept(new SafetyVisitor(model));
            }
            catch (Exception ex) { FeatureDiagnostics.Report("SQL Semantic Model", "ScriptDOM analysis failed", ex); }
            return model;
        }

        private static string MakeParseable(string sql, int caretOffset, out int parseCaretOffset, out List<int> insertedAt)
        {
            parseCaretOffset = caretOffset;
            insertedAt = null;
            var atCaret = new List<int> { caretOffset };
            if (caretOffset > 0 && sql[caretOffset - 1] == '.') { insertedAt = atCaret; return sql.Insert(caretOffset, Placeholder); }
            string before = sql.Substring(0, caretOffset).TrimEnd();
            if (before.EndsWith(",", StringComparison.Ordinal)) { insertedAt = atCaret; return sql.Insert(caretOffset, Placeholder); }
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\bSELECT(?:\s+DISTINCT)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { insertedAt = atCaret; return sql.Insert(caretOffset, Placeholder); }
            // A direct semantic-model caller may put the caret at the end while leaving an
            // incomplete member earlier in the statement.  Each insertion sits before the caret, so
            // the caret has to move with it; the caret must never land inside a placeholder.
            var builder = new System.Text.StringBuilder(sql.Length + 32);
            int inserted = 0;
            int index = 0;
            List<int> positions = null;
            foreach (System.Text.RegularExpressions.Match match in MemberDotPattern.Matches(sql))
            {
                int dot = match.Index;
                if (dot >= caretOffset) break;
                builder.Append(sql, index, dot - index + 1);
                builder.Append(Placeholder);
                (positions ?? (positions = new List<int>())).Add(dot + 1 + inserted);
                inserted += Placeholder.Length;
                index = dot + 1;
            }
            if (inserted == 0) return sql;
            builder.Append(sql, index, sql.Length - index);
            parseCaretOffset = caretOffset + inserted;
            insertedAt = positions;
            return builder.ToString();
        }

        private static TSqlParser CreateParser(int compatibilityLevel)
        {
            // The connection's compatibility level decides which grammar can be trusted.  Parsing
            // a 2016 database with the 2022 grammar used to report errors for perfectly valid
            // statements; the old grammar is the safe direction when the server is older.
            if (compatibilityLevel >= 170) return new TSql170Parser(true);
            if (compatibilityLevel >= 160) return new TSql160Parser(true);
            if (compatibilityLevel >= 150) return new TSql150Parser(true);
            if (compatibilityLevel >= 140) return new TSql140Parser(true);
            if (compatibilityLevel >= 130) return new TSql130Parser(true);
            if (compatibilityLevel >= 120) return new TSql120Parser(true);
            if (compatibilityLevel >= 110) return new TSql110Parser(true);
            return new TSql100Parser(true);
        }

        private const string Placeholder = "__mssqltool_symbol";

        /// <summary>The top-level statement that owns the caret.</summary>
        private static int StatementStartOf(TSqlScript script, int caret)
        {
            if (script == null) return 0;
            int start = 0;
            foreach (TSqlBatch batch in script.Batches)
            {
                foreach (TSqlStatement statement in batch.Statements)
                {
                    // The caret can sit past the end of an incomplete last statement ("WHERE x > " is
                    // still being typed), so the nearest statement start at or before it wins.
                    if (statement.StartOffset <= caret) start = Math.Max(start, statement.StartOffset);
                }
            }
            return start;
        }

        /// <summary>Kind of the statement that owns the caret, from the statement type in the tree.</summary>
        private static string StatementKindOf(TSqlScript script, int caret)
        {
            if (script == null) return null;
            TSqlStatement current = null;
            foreach (TSqlBatch batch in script.Batches)
                foreach (TSqlStatement statement in batch.Statements)
                    if (statement.StartOffset <= caret) current = statement;
            if (current == null) return null;
            switch (current.GetType().Name)
            {
                case "SelectStatement": return "SELECT";
                case "InsertStatement": return "INSERT";
                case "UpdateStatement": return "UPDATE";
                case "DeleteStatement": return "DELETE";
                case "MergeStatement": return "MERGE";
                case "CreateTableStatement": return "CREATE";
                case "AlterTableStatement": return "ALTER";
                case "DropTableStatement": return "DROP";
                case "ExecuteStatement": return "EXEC";
                case "DeclareVariableStatement": return "DECLARE";
                default: return current.GetType().Name;
            }
        }

        /// <summary>First statement keyword, for text too incomplete to produce a tree.</summary>
        private static string LeadingKeyword(string text)
        {
            string value = text ?? string.Empty;
            int index = 0;
            while (index < value.Length && !char.IsLetter(value[index])) index++;
            int start = index;
            while (index < value.Length && char.IsLetter(value[index])) index++;
            if (start == index) return null;
            string word = value.Substring(start, index - start).ToUpperInvariant();
            switch (word)
            {
                case "SELECT":
                case "INSERT":
                case "UPDATE":
                case "DELETE":
                case "MERGE":
                case "CREATE":
                case "ALTER":
                case "DROP":
                case "TRUNCATE":
                case "EXEC":
                case "EXECUTE":
                case "DECLARE":
                case "USE":
                    return word == "EXECUTE" ? "EXEC" : word;
                default:
                    return null;
            }
        }

        /// <summary>Maps an offset in the patched text back to the text the caller passed in.</summary>
        private static int ToOriginal(int patchedOffset, List<int> insertedAt)
        {
            if (insertedAt == null || insertedAt.Count == 0) return patchedOffset;
            int shift = 0;
            foreach (int at in insertedAt) if (at < patchedOffset) shift++;
            return Math.Max(0, patchedOffset - shift * Placeholder.Length);
        }

        // A member dot that is the last token before a clause keyword (or the end of the text) is
        // the unfinished member this rewrite exists for.
        private static readonly System.Text.RegularExpressions.Regex MemberDotPattern = new System.Text.RegularExpressions.Regex(
            @"\.(?=\s*(?:(?:FROM|WHERE|JOIN|ON|GROUP|ORDER|HAVING|OPTION)\b|$))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        public void CopyAliasesTo(CompletionContext context)
        {
            foreach (var pair in Aliases) context.Aliases[pair.Key] = pair.Value;
            foreach (DatabaseObjectMetadata local in LocalObjects)
                if (!context.LocalObjects.Any(o => string.Equals(o.Name, local.Name, StringComparison.OrdinalIgnoreCase))) context.LocalObjects.Add(local);
        }

        private sealed class QueryScopeFinder : TSqlFragmentVisitor
        {
            private readonly int offset;
            public QuerySpecification Tightest { get; private set; }
            public List<QuerySpecification> ContainingScopes { get; } = new List<QuerySpecification>();
            private QuerySpecification closestBefore;
            public QueryScopeFinder(int offset) { this.offset = offset; }
            public override void Visit(QuerySpecification node)
            {
                if (node.StartOffset <= offset && offset <= node.StartOffset + node.FragmentLength)
                {
                    ContainingScopes.Add(node);
                    if (Tightest == null || node.FragmentLength <= Tightest.FragmentLength) Tightest = node;
                }
                else if (node.StartOffset <= offset && (closestBefore == null || node.StartOffset >= closestBefore.StartOffset)) closestBefore = node;
            }
            public override void ExplicitVisit(TSqlScript node)
            {
                base.ExplicitVisit(node);
                if (Tightest == null) Tightest = closestBefore;
            }
        }

        private sealed class SourceVisitor : TSqlFragmentVisitor
        {
            private readonly SqlSemanticModel model;
            public SourceVisitor(SqlSemanticModel model) { this.model = model; }
            public override void Visit(NamedTableReference node)
            {
                string objectName = node.SchemaObject == null ? string.Empty : string.Join(".", node.SchemaObject.Identifiers.Select(i => i.Value));
                if (string.IsNullOrWhiteSpace(objectName)) return;
                string implicitName = node.SchemaObject.BaseIdentifier?.Value;
                if (node.Alias != null && !string.IsNullOrWhiteSpace(node.Alias.Value)) model.Aliases[node.Alias.Value] = objectName;
                else if (!string.IsNullOrWhiteSpace(implicitName)) model.Aliases[implicitName] = objectName;
            }
            public override void ExplicitVisit(QueryDerivedTable node)
            {
                if (node.Alias != null)
                {
                    string alias = node.Alias.Value ?? string.Empty;
                    model.Aliases[alias] = alias;
                    var item = new DatabaseObjectMetadata { Name = alias, Kind = CompletionItemKind.View };
                    AddOutputColumns(node.QueryExpression, item);
                    if (!model.LocalObjects.Any(o => string.Equals(o.Name, alias, StringComparison.OrdinalIgnoreCase)))
                        model.LocalObjects.Add(item);
                }
                // The nested query owns a separate name scope.
            }
        }

        private sealed class GroupByColumnVisitor : TSqlFragmentVisitor
        {
            private readonly SqlSemanticModel model;
            public GroupByColumnVisitor(SqlSemanticModel model) { this.model = model; }
            public override void Visit(ColumnReferenceExpression node)
            {
                string value = node.MultiPartIdentifier == null ? null : string.Join(".", node.MultiPartIdentifier.Identifiers.Select(i => i.Value));
                if (!string.IsNullOrWhiteSpace(value)) model.GroupByColumns.Add(value);
            }
        }

        private sealed class ExpressionVisitor : TSqlFragmentVisitor
        {
            private readonly SqlSemanticModel model;
            private readonly QuerySpecification root;
            public ExpressionVisitor(SqlSemanticModel model, QuerySpecification root) { this.model = model; this.root = root; }
            public override void Visit(ColumnReferenceExpression node)
            {
                string value = node.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(value)) model.ReferencedColumns.Add(value);
            }
            public override void Visit(SelectScalarExpression node)
            {
                string alias = node.ColumnName?.Value;
                if (!string.IsNullOrWhiteSpace(alias) && !model.SelectAliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase))) model.SelectAliases.Add(alias);
            }
            public override void ExplicitVisit(QuerySpecification node)
            {
                if (ReferenceEquals(node, root)) base.ExplicitVisit(node);
            }
        }

        private sealed class LocalObjectVisitor : TSqlFragmentVisitor
        {
            private readonly SqlSemanticModel model;
            private readonly int caretOffset;
            public LocalObjectVisitor(SqlSemanticModel model, int caretOffset) { this.model = model; this.caretOffset = caretOffset; }
            public override void Visit(CommonTableExpression node)
            {
                if (node.StartOffset > caretOffset) return;
                string name = node.ExpressionName?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;
                var item = new DatabaseObjectMetadata { Name = name, Kind = CompletionItemKind.View };
                if (node.Columns != null && node.Columns.Count > 0)
                    foreach (Identifier column in node.Columns) item.Columns.Add(new ColumnMetadata { Name = column.Value });
                else AddOutputColumns(node.QueryExpression, item);
                Add(item);
            }
            public override void Visit(CreateTableStatement node)
            {
                string name = node.SchemaObjectName?.BaseIdentifier?.Value;
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("#", StringComparison.Ordinal)) return;
                var item = new DatabaseObjectMetadata { Name = name, Kind = CompletionItemKind.Table };
                if (node.Definition?.ColumnDefinitions != null)
                    foreach (ColumnDefinition column in node.Definition.ColumnDefinitions)
                        item.Columns.Add(new ColumnMetadata { Name = column.ColumnIdentifier.Value, DataType = column.DataType?.Name?.BaseIdentifier?.Value ?? string.Empty });
                Add(item);
            }
            public override void Visit(SelectStatement node)
            {
                string name = node.Into?.BaseIdentifier?.Value;
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("#", StringComparison.Ordinal)) return;
                var item = new DatabaseObjectMetadata { Name = name, Kind = CompletionItemKind.Table };
                AddOutputColumns(node.QueryExpression, item);
                Add(item);
            }
            private void Add(DatabaseObjectMetadata item)
            {
                if (!model.LocalObjects.Any(o => string.Equals(o.Name, item.Name, StringComparison.OrdinalIgnoreCase))) model.LocalObjects.Add(item);
            }
        }

        private static void AddOutputColumns(QueryExpression expression, DatabaseObjectMetadata target)
        {
            if (expression is QueryParenthesisExpression parenthesis)
            {
                AddOutputColumns(parenthesis.QueryExpression, target);
                return;
            }
            if (expression is BinaryQueryExpression binary)
            {
                AddOutputColumns(binary.FirstQueryExpression, target);
                return;
            }
            var query = expression as QuerySpecification;
            if (query == null) return;
            foreach (SelectScalarExpression scalar in query.SelectElements.OfType<SelectScalarExpression>())
            {
                var column = scalar.Expression as ColumnReferenceExpression;
                string name = scalar.ColumnName?.Value ?? column?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !target.Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                    target.Columns.Add(new ColumnMetadata { Name = name });
            }
            foreach (SelectStarExpression star in query.SelectElements.OfType<SelectStarExpression>())
            {
                string qualifier = star.Qualifier?.Identifiers.LastOrDefault()?.Value;
                var collector = new ProjectionSourceVisitor(qualifier);
                query.FromClause?.Accept(collector);
                foreach (string source in collector.Sources)
                    if (!target.ProjectionSources.Contains(source, StringComparer.OrdinalIgnoreCase)) target.ProjectionSources.Add(source);
            }
        }

        private sealed class ProjectionSourceVisitor : TSqlFragmentVisitor
        {
            private readonly string qualifier;
            public List<string> Sources { get; } = new List<string>();
            public ProjectionSourceVisitor(string qualifier) { this.qualifier = qualifier; }
            public override void Visit(NamedTableReference node)
            {
                string name = node.SchemaObject == null ? null : string.Join(".", node.SchemaObject.Identifiers.Select(i => i.Value));
                string alias = node.Alias?.Value ?? node.SchemaObject?.BaseIdentifier?.Value;
                if (!string.IsNullOrWhiteSpace(name) && (string.IsNullOrWhiteSpace(qualifier) || string.Equals(alias, qualifier, StringComparison.OrdinalIgnoreCase))) Sources.Add(name);
            }
            public override void ExplicitVisit(QueryDerivedTable node)
            {
                string alias = node.Alias?.Value;
                if (!string.IsNullOrWhiteSpace(alias) && (string.IsNullOrWhiteSpace(qualifier) || string.Equals(alias, qualifier, StringComparison.OrdinalIgnoreCase))) Sources.Add(alias);
            }
        }

        private sealed class SafetyVisitor : TSqlFragmentVisitor
        {
            private readonly SqlSemanticModel model;
            public SafetyVisitor(SqlSemanticModel model) { this.model = model; }
            public override void Visit(UpdateStatement node) { Check(node, node.UpdateSpecification?.WhereClause, "UPDATE"); }
            public override void Visit(DeleteStatement node) { Check(node, node.DeleteSpecification?.WhereClause, "DELETE"); }
            private void Check(TSqlFragment node, WhereClause where, string operation)
            {
                if (where == null) model.Diagnostics.Add(new SqlEditorDiagnostic { Code = "SQL_UNBOUNDED_DML", Message = operation + " has no WHERE clause", Offset = node.StartOffset, Length = operation.Length });
            }
        }
    }
}
