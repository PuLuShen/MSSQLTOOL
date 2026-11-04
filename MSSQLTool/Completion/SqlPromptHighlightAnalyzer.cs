using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MSSQLTool.Completion
{
    internal sealed class HighlightSpan
    {
        public int Start;
        public int Length;
    }

    /// <summary>
    /// Finds the SQL Prompt-style related tokens for the identifier under the caret and, for
    /// INSERT ... SELECT, the corresponding target/source column pairs.
    /// </summary>
    internal static class SqlPromptHighlightAnalyzer
    {
        private static readonly Regex IdentifierPattern = new Regex(@"[A-Za-z_#@][A-Za-z0-9_@$#]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static List<HighlightSpan> Analyze(string sql, int caretOffset)
        {
            var spans = new List<HighlightSpan>();
            if (string.IsNullOrEmpty(sql) || caretOffset < 0 || caretOffset > sql.Length) return spans;

            string masked = SqlTextContext.MaskCommentsAndStrings(sql);
            var tokens = new List<Token>();
            foreach (Match match in IdentifierPattern.Matches(masked))
            {
                if (IsKeyword(match.Value)) continue;
                tokens.Add(new Token { Start = match.Index, Length = match.Length, Text = match.Value });
            }

            Token current = tokens.LastOrDefault(t => t.Start <= caretOffset && caretOffset <= t.Start + t.Length)
                ?? tokens.LastOrDefault(t => t.Start + t.Length == caretOffset);

            TSqlScript script = null;
            try
            {
                script = new TSql170Parser(true).Parse(new StringReader(sql), out _) as TSqlScript;
            }
            catch (Exception)
            {
            }

            TSqlStatement statement = script?.Batches.SelectMany(b => b.Statements)
                .FirstOrDefault(s => s.StartOffset <= caretOffset
                    && caretOffset <= s.StartOffset + s.FragmentLength);
            int statementStart = statement?.StartOffset ?? 0;
            int statementEnd = statement != null ? statement.StartOffset + statement.FragmentLength : sql.Length;

            var visitor = new SemanticVisitor();
            script?.Accept(visitor);

            ColumnRef currentColumn = current == null
                ? null
                : visitor.Columns.FirstOrDefault(c => c.Start == current.Start);
            bool qualifiedColumn = currentColumn != null && !string.IsNullOrWhiteSpace(currentColumn.Qualifier);

            if (current != null && !qualifiedColumn)
            {
                foreach (Token token in tokens)
                {
                    if (token.Start < statementStart || token.Start + token.Length > statementEnd) continue;
                    if (!string.Equals(token.Text, current.Text, StringComparison.OrdinalIgnoreCase)) continue;
                    Add(spans, token.Start, token.Length);
                }
            }

            if (script == null) return spans;

            if (current != null)
            {
                foreach (TableRef table in visitor.Tables)
                {
                    if (!InStatement(statementStart, statementEnd, table.NameStart)) continue;
                    if (!Matches(current, table.Name) && !Matches(current, table.Alias)) continue;
                    foreach (TableRef occurrence in visitor.Tables.Where(t =>
                        InStatement(statementStart, statementEnd, t.NameStart)
                        && (string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Alias, table.Alias, StringComparison.OrdinalIgnoreCase))))
                    {
                        Add(spans, occurrence.NameStart, occurrence.NameLength);
                        if (occurrence.AliasLength > 0) Add(spans, occurrence.AliasStart, occurrence.AliasLength);
                    }

                    foreach (ColumnRef column in visitor.Columns)
                        if (InStatement(statementStart, statementEnd, column.Start)
                            && (string.Equals(column.Qualifier, table.Alias, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(column.Qualifier, table.Name, StringComparison.OrdinalIgnoreCase)))
                            Add(spans, column.Start, column.Length);
                }

                foreach (ColumnRef column in visitor.Columns)
                {
                    if (!InStatement(statementStart, statementEnd, column.Start)) continue;
                    if (!Matches(current, column.Name)) continue;
                    if (qualifiedColumn
                        && !string.Equals(column.Qualifier, currentColumn.Qualifier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (ColumnRef occurrence in visitor.Columns.Where(c =>
                        InStatement(statementStart, statementEnd, c.Start)
                        && string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)
                        && (!qualifiedColumn
                            || string.Equals(c.Qualifier, currentColumn.Qualifier, StringComparison.OrdinalIgnoreCase))))
                        Add(spans, occurrence.Start, occurrence.Length);
                }
            }

            foreach (ColumnPair pair in visitor.Pairs)
            {
                if (!InStatement(statementStart, statementEnd, pair.TargetStart)) continue;
                bool caretOnPair = Contains(pair.TargetStart, pair.TargetLength, caretOffset)
                    || Contains(pair.SourceStart, pair.SourceLength, caretOffset)
                    || (current != null && (Matches(current, pair.TargetName) || Matches(current, pair.SourceName)));
                if (!caretOnPair) continue;
                Add(spans, pair.TargetStart, pair.TargetLength);
                Add(spans, pair.SourceStart, pair.SourceLength);
            }

            string tokenText = current?.Text ?? "<literal>";
            string pairText = string.Join("; ", visitor.Pairs.Select(p => p.TargetName + "<-" + p.SourceName));
            MSSQLToolPackage._logger?.Debug(
                $"SQL-HIGHLIGHT token='{tokenText}' qualifier='{currentColumn?.Qualifier}' caret={caretOffset} " +
                $"statement=[{statementStart},{statementEnd}] tables={visitor.Tables.Count} columns={visitor.Columns.Count} " +
                $"pairs={visitor.Pairs.Count} spans={spans.Count} pairs=[{pairText}]");

            return spans;
        }

        private static bool Matches(Token token, string value)
            => !string.IsNullOrWhiteSpace(value) && string.Equals(token.Text, value, StringComparison.OrdinalIgnoreCase);

        private static bool Matches(Token token, Identifier value)
            => value != null && Matches(token, value.Value);

        private static bool Contains(int start, int length, int offset)
            => start <= offset && offset <= start + length;

        private static bool InStatement(int start, int end, int offset)
            => offset >= start && offset <= end;

        private static void Add(List<HighlightSpan> spans, int start, int length)
        {
            if (length <= 0) return;
            if (spans.Any(s => s.Start == start && s.Length == length)) return;
            spans.Add(new HighlightSpan { Start = start, Length = length });
        }

        private static bool IsKeyword(string value)
        {
            return KeywordSet.Contains(value);
        }

        private sealed class Token
        {
            public int Start;
            public int Length;
            public string Text;
        }

        private sealed class TableRef
        {
            public string Name;
            public string Alias;
            public int NameStart;
            public int NameLength;
            public int AliasStart;
            public int AliasLength;
        }

        private sealed class ColumnRef
        {
            public string Name;
            public string Qualifier;
            public int Start;
            public int Length;
        }

        private sealed class ColumnPair
        {
            public string TargetName;
            public string SourceName;
            public int TargetStart;
            public int TargetLength;
            public int SourceStart;
            public int SourceLength;
        }

        private sealed class SemanticVisitor : TSqlFragmentVisitor
        {
            public List<TableRef> Tables { get; } = new List<TableRef>();
            public List<ColumnRef> Columns { get; } = new List<ColumnRef>();
            public List<ColumnPair> Pairs { get; } = new List<ColumnPair>();

            public override void Visit(NamedTableReference node)
            {
                Identifier table = node.SchemaObject?.BaseIdentifier;
                if (table == null) return;
                Tables.Add(new TableRef
                {
                    Name = table.Value,
                    Alias = node.Alias?.Value,
                    NameStart = table.StartOffset,
                    NameLength = table.FragmentLength,
                    AliasStart = node.Alias?.StartOffset ?? 0,
                    AliasLength = node.Alias?.FragmentLength ?? 0
                });
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var parts = node.MultiPartIdentifier?.Identifiers;
                if (parts == null || parts.Count == 0) return;
                Identifier column = parts[parts.Count - 1];
                Columns.Add(new ColumnRef
                {
                    Name = column.Value,
                    Qualifier = parts.Count > 1 ? parts[parts.Count - 2].Value : null,
                    Start = column.StartOffset,
                    Length = column.FragmentLength
                });
            }

            public override void Visit(InsertStatement node)
            {
                InsertSpecification spec = node.InsertSpecification;
                if (spec?.Columns == null || spec.Columns.Count == 0) return;

                if (spec.InsertSource is SelectInsertSource selectSource)
                {
                    QuerySpecification query = FindQuery(selectSource.Select);
                    if (query?.SelectElements == null) return;
                    for (int i = 0; i < spec.Columns.Count && i < query.SelectElements.Count; i++)
                    {
                        var scalar = query.SelectElements[i] as SelectScalarExpression;
                        if (scalar?.Expression == null) continue;
                        AddPair(spec.Columns[i], scalar.Expression);
                    }
                    return;
                }

                if (spec.InsertSource is ValuesInsertSource valuesSource)
                {
                    RowValue row = valuesSource.RowValues?.FirstOrDefault();
                    if (row?.ColumnValues == null) return;
                    for (int i = 0; i < spec.Columns.Count && i < row.ColumnValues.Count; i++)
                        AddPair(spec.Columns[i], row.ColumnValues[i]);
                }
            }

            private void AddPair(ColumnReferenceExpression targetRef, ScalarExpression sourceExpression)
            {
                Identifier target = targetRef?.MultiPartIdentifier?.Identifiers.LastOrDefault();
                if (target == null || sourceExpression == null) return;

                var sourceColumn = sourceExpression as ColumnReferenceExpression;
                Identifier sourceIdentifier = sourceColumn?.MultiPartIdentifier?.Identifiers.LastOrDefault();
                int sourceStart = sourceIdentifier?.StartOffset ?? sourceExpression.StartOffset;
                int sourceLength = sourceIdentifier?.FragmentLength ?? sourceExpression.FragmentLength;
                if (sourceLength <= 0) return;

                if (Pairs.Any(p => p.TargetStart == target.StartOffset && p.SourceStart == sourceStart)) return;
                Pairs.Add(new ColumnPair
                {
                    TargetName = target.Value,
                    TargetStart = target.StartOffset,
                    TargetLength = target.FragmentLength,
                    SourceName = sourceIdentifier?.Value,
                    SourceStart = sourceStart,
                    SourceLength = sourceLength
                });
            }

            private static QuerySpecification FindQuery(QueryExpression expression)
            {
                if (expression is QuerySpecification query) return query;
                if (expression is QueryParenthesisExpression parenthesis) return FindQuery(parenthesis.QueryExpression);
                if (expression is BinaryQueryExpression binary) return FindQuery(binary.FirstQueryExpression);
                return null;
            }
        }

        private static readonly HashSet<string> KeywordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON",
            "GROUP", "BY", "ORDER", "HAVING", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE",
            "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "FUNCTION", "PROCEDURE", "AS", "AND", "OR", "NOT",
            "NULL", "IS", "IN", "LIKE", "BETWEEN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END",
            "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "WITH", "UNION", "ALL", "DISTINCT", "TOP", "INTO",
            "IF", "WHILE", "EXEC", "EXECUTE", "RETURN", "GO", "USE", "PRINT", "MERGE", "USING", "OUTPUT"
        };
    }
}
