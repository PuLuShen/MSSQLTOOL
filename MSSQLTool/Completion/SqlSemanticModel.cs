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

        public static SqlSemanticModel Create(string sql, int caretOffset = -1)
        {
            var model = new SqlSemanticModel();
            try
            {
                string original = sql ?? string.Empty;
                int target = caretOffset < 0 ? original.Length : Math.Max(0, Math.Min(caretOffset, original.Length));
                string parseText = MakeParseable(original, target);
                var parser = new TSql170Parser(true);
                TSqlFragment fragment = parser.Parse(new StringReader(parseText), out IList<ParseError> errors);
                model.HasParseErrors = errors != null && errors.Count > 0;
                if (errors != null)
                    foreach (ParseError error in errors.Take(10))
                        model.Diagnostics.Add(new SqlEditorDiagnostic { Code = "SQL_PARSE", Message = error.Message, Offset = error.Offset, Length = 1 });
                if (fragment == null) return model;

                var finder = new QueryScopeFinder(target);
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
                fragment.Accept(new LocalObjectVisitor(model, target));
                fragment.Accept(new SafetyVisitor(model));
            }
            catch (Exception ex) { FeatureDiagnostics.Report("SQL Semantic Model", "ScriptDOM analysis failed", ex); }
            return model;
        }

        private static string MakeParseable(string sql, int caretOffset)
        {
            const string placeholder = "__mssqltool_symbol";
            if (caretOffset > 0 && sql[caretOffset - 1] == '.') return sql.Insert(caretOffset, placeholder);
            string before = sql.Substring(0, caretOffset).TrimEnd();
            if (before.EndsWith(",", StringComparison.Ordinal)) return sql.Insert(caretOffset, placeholder);
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\bSELECT(?:\s+DISTINCT)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return sql.Insert(caretOffset, placeholder);
            // A direct semantic-model caller may put the caret at the end while
            // leaving an incomplete member earlier in the statement.
            return System.Text.RegularExpressions.Regex.Replace(sql,
                @"\.(?=\s*(?:FROM|WHERE|JOIN|ON|GROUP|ORDER|HAVING|OPTION|$))", "." + placeholder,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

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
