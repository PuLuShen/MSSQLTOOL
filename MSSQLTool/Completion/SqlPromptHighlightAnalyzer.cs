using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MSSQLTool.Model;

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
        public static List<HighlightSpan> Analyze(string sql, int caretOffset)
        {
            var spans = new List<HighlightSpan>();
            if (string.IsNullOrEmpty(sql) || caretOffset < 0 || caretOffset > sql.Length) return spans;

            // A caret on a space, a comma, a parenthesis, an operator or a line break has no
            // related token: leave before the tokenizer and the parser run.  Without this guard
            // every such keystroke paid a whole-document tokenize plus a ScriptDom parse for a
            // list that is always empty (measured: ~0.4 ms at 10 KB, ~18 ms at 200 KB just for
            // the tokenizer, plus 8 ms / 700 ms for the parse).
            if (!CaretIsOnIdentifier(sql, caretOffset)) return spans;

            // The document model is shared with analysis and completion, so highlighting no longer
            // re-lexes the script with a regular expression and it inherits the cache.  Get()
            // reuses the model of an unchanged document; Create() re-tokenized it on every caret
            // move, so arrowing through a large script also paid the full document cost.
            SqlDocumentModel model = SqlDocumentModel.Get(sql);
            var tokens = new List<Token>();
            foreach (SqlToken token in model.Tokens)
            {
                if (token.Kind != SqlTokenKind.Identifier) continue;
                string text = SqlTokenizer.Unquote(token);
                if (string.IsNullOrEmpty(text) || IsKeyword(text)) continue;
                tokens.Add(new Token { Start = token.Start, Length = token.Length, Text = text });
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

            // Always fall back to the cached statement index.  Before this, a script the parser
            // rejected (SQLCMD directives, half-typed batches) highlighted every same-named word
            // in the whole document instead of the statement under the caret.
            int statementStart;
            int statementEnd;
            if (statement != null)
            {
                statementStart = statement.StartOffset;
                statementEnd = statement.StartOffset + statement.FragmentLength;
            }
            else
            {
                SqlStatementRange range = model.StatementAt(caretOffset);
                statementStart = range != null ? range.Start : 0;
                statementEnd = range != null ? range.Start + range.Length : sql.Length;
            }

            // @variables live at batch scope: the declaration, the SET and every use are one object,
            // so highlighting (and renaming) only inside the caret statement would light up nothing
            // but the token under the caret.
            if (current != null && current.Text.StartsWith("@", StringComparison.Ordinal))
            {
                SqlStatementRange batch = model.Statements.InBatch(caretOffset);
                int batchStart = batch != null ? batch.Start : 0;
                int batchEnd = batch != null ? batch.Start + batch.Length : sql.Length;

                // Parameter names in EXEC calls and assignment targets in a SELECT list are not
                // variable references, even though they are spelled like one.
                HashSet<int> targets = AssignmentTargetPositions(model, statementStart, statementEnd);

                foreach (Token token in tokens)
                {
                    if (token.Start < batchStart || token.Start + token.Length > batchEnd) continue;
                    if (!string.Equals(token.Text, current.Text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (targets.Contains(token.Start)) continue;
                    Add(spans, token.Start, token.Length);
                }

                return spans;
            }

            // A CREATE VIEW ... UNION ALL ... is one statement, so statement bounds would light up
            // the same column in every branch.  Narrow the scope to the innermost SELECT block
            // that contains the caret (SQL Prompt highlights inside the current query block).
            int scopeStart = statementStart;
            int scopeEnd = statementEnd;
            if (script != null)
            {
                var scopeVisitor = new QueryScopeVisitor(caretOffset);
                script.Accept(scopeVisitor);
                QuerySpecification scope = scopeVisitor.Best;
                if (scope != null)
                {
                    scopeStart = scope.StartOffset;
                    scopeEnd = scope.StartOffset + scope.FragmentLength;
                }
            }

            var visitor = new SemanticVisitor();
            script?.Accept(visitor);

            ColumnRef currentColumn = current == null
                ? null
                : visitor.Columns.FirstOrDefault(c => c.Start == current.Start);
            bool qualifiedColumn = currentColumn != null && !string.IsNullOrWhiteSpace(currentColumn.Qualifier);
            TableRef currentTable = current == null
                ? null
                : visitor.Tables.FirstOrDefault(t => InStatement(scopeStart, scopeEnd, t.NameStart)
                    && (IsSameToken(current, t.NameStart, t.NameLength)
                        || IsSameToken(current, t.AliasStart, t.AliasLength)));

            // When the parser resolved the token to a column or a table, the semantic pass below
            // knows the real scope.  Only unresolved identifiers fall back to plain name matching,
            // which keeps a sibling subquery's alias from lighting up.
            bool semanticallyResolved = currentColumn != null || currentTable != null;

            // @name on the left of '=' inside an EXEC argument list is a parameter name, and in a
            // SELECT list it is an assignment target; neither is a variable reference, so they must
            // not light up together with the real usages.
            HashSet<int> assignmentTargets = AssignmentTargetPositions(model, scopeStart, scopeEnd);

            if (current != null && !qualifiedColumn && !semanticallyResolved)
            {
                foreach (Token token in tokens)
                {
                    if (token.Start < scopeStart || token.Start + token.Length > scopeEnd) continue;
                    if (!string.Equals(token.Text, current.Text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (assignmentTargets.Contains(token.Start)) continue;
                    Add(spans, token.Start, token.Length);
                }
            }

            if (script == null) return spans;

            // Database objects (queues, services, message types, tables, procedures, types) share one
            // identity per name, independent of the statement they are written in.  The visitor below
            // records every object-name position, so the caret on any of them highlights all the
            // references to that object across the whole script.  Column and alias relations above
            // stay scoped: the same column name in an unrelated statement is a different column.
            if (current != null)
            {
                ObjectRef caretObject = visitor.Objects.FirstOrDefault(o => o.Start == current.Start);
                if (caretObject != null)
                {
                    foreach (ObjectRef other in visitor.Objects)
                    {
                        if (!string.Equals(other.Name, caretObject.Name, StringComparison.OrdinalIgnoreCase)) continue;

                        // Same name under a different schema is a different object; an unqualified
                        // reference cannot be told apart, so it always counts as the same one.
                        if (!string.IsNullOrEmpty(caretObject.Schema) && !string.IsNullOrEmpty(other.Schema)
                            && !string.Equals(other.Schema, caretObject.Schema, StringComparison.OrdinalIgnoreCase))
                            continue;

                        Add(spans, other.Start, other.Length);
                    }
                }
            }

            if (current != null)
            {
                foreach (TableRef table in visitor.Tables)
                {
                    if (!InStatement(scopeStart, scopeEnd, table.NameStart)) continue;
                    if (!Matches(current, table.Name) && !Matches(current, table.Alias)) continue;
                    foreach (TableRef occurrence in visitor.Tables.Where(t =>
                        InStatement(scopeStart, scopeEnd, t.NameStart)
                        && (string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase)
                            // Never compare aliases when the caret table has none: null == null would
                            // make every unaliased table in the script look like the same object.
                            || (!string.IsNullOrEmpty(table.Alias)
                                && string.Equals(t.Alias, table.Alias, StringComparison.OrdinalIgnoreCase)))))
                    {
                        Add(spans, occurrence.NameStart, occurrence.NameLength);
                        if (occurrence.AliasLength > 0) Add(spans, occurrence.AliasStart, occurrence.AliasLength);
                    }

                    foreach (ColumnRef column in visitor.Columns)
                    {
                        if (!InStatement(scopeStart, scopeEnd, column.Start)) continue;
                        if (string.IsNullOrEmpty(column.Qualifier)) continue;
                        if (!string.Equals(column.Qualifier, table.Alias, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(column.Qualifier, table.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        Add(spans, column.Start, column.Length);
                    }
                }

                foreach (ColumnRef column in visitor.Columns)
                {
                    if (!InStatement(scopeStart, scopeEnd, column.Start)) continue;
                    if (!Matches(current, column.Name)) continue;
                    if (qualifiedColumn
                        && !string.Equals(column.Qualifier, currentColumn.Qualifier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (ColumnRef occurrence in visitor.Columns.Where(c =>
                        InStatement(scopeStart, scopeEnd, c.Start)
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
                $"statement=[{statementStart},{statementEnd}] scope=[{scopeStart},{scopeEnd}] " +
                $"tables={visitor.Tables.Count} columns={visitor.Columns.Count} objects={visitor.Objects.Count} " +
                $"pairs={visitor.Pairs.Count} spans={spans.Count} pairs=[{pairText}]");

            return spans;
        }

        /// <summary>
        /// Offsets of @name tokens that name an EXEC argument rather than reference a variable, for
        /// example the @CODE in <c>EXEC dbo.Proc @CODE = @value</c>.
        ///
        /// A SELECT-list assignment (<c>SELECT @v = expr</c>) is deliberately NOT included: there the
        /// target is the variable itself, so it must highlight and rename together with its DECLARE
        /// and its other uses.  Excluding it made the caret sit on a token that never lit up while
        /// the DECLARE lit up alone.
        /// </summary>
        private static HashSet<int> AssignmentTargetPositions(SqlDocumentModel model, int start, int end)
        {
            var result = new HashSet<int>();
            List<SqlToken> tokens = model.Tokens;
            int first = 0;
            while (first < tokens.Count && tokens[first].Start < start) first++;

            bool exec = false;
            int execIndex = -1;
            for (int i = first; i < tokens.Count && tokens[i].Start < end; i++)
            {
                SqlToken token = tokens[i];
                if (token.Kind != SqlTokenKind.Keyword) continue;
                if (IsWord(token, "EXEC") || IsWord(token, "EXECUTE"))
                {
                    exec = true;
                    execIndex = i;
                    break;
                }
            }

            if (!exec) return result;

            for (int i = execIndex + 1; i < tokens.Count && tokens[i].Start < end; i++)
            {
                SqlToken token = tokens[i];
                if (token.Kind != SqlTokenKind.Identifier) continue;
                if (!token.Text.StartsWith("@", StringComparison.Ordinal)) continue;

                int next = i + 1;
                while (next < tokens.Count && tokens[next].Kind == SqlTokenKind.Comment) next++;
                if (next >= tokens.Count || tokens[next].Text != "=") continue;

                // EXEC @rc = proc assigns the return code to a variable, so a token is only a
                // parameter name when a procedure name precedes it.
                bool hasProcedureName = false;
                for (int j = execIndex + 1; j < i; j++)
                {
                    SqlToken between = tokens[j];
                    if (between.Kind != SqlTokenKind.Identifier) continue;
                    if (between.Text.StartsWith("@", StringComparison.Ordinal)) continue;
                    hasProcedureName = true;
                    break;
                }

                if (hasProcedureName) result.Add(token.Start);
            }

            return result;
        }

        private static bool IsWord(SqlToken token, string value)
            => string.Equals(token.Text, value, StringComparison.OrdinalIgnoreCase);

        private static bool Matches(Token token, string value)
            => !string.IsNullOrWhiteSpace(value) && string.Equals(token.Text, value, StringComparison.OrdinalIgnoreCase);

        private static bool Matches(Token token, Identifier value)
            => value != null && Matches(token, value.Value);

        /// <summary>True when the caret token is exactly the token at this position.</summary>
        private static bool IsSameToken(Token token, int start, int length)
            => length > 0 && token.Start == start;

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

        /// <summary>
        /// True when the caret touches a plain identifier or a quoted/bracketed name.  Keywords are
        /// excluded: the token list drops them, so a caret on one can never produce a related span.
        /// </summary>
        private static bool CaretIsOnIdentifier(string sql, int caretOffset)
        {
            int caret = Math.Max(0, Math.Min(caretOffset, sql.Length));
            bool touchesIdentifier = IsWordAt(sql, caret - 1) || IsWordAt(sql, caret)
                || (caret > 0 && IsNameQuote(sql[caret - 1]))
                || (caret < sql.Length && IsNameQuote(sql[caret]));
            if (!touchesIdentifier) return false;

            int start = caret;
            while (start > 0 && IsWordChar(sql[start - 1])) start--;
            int end = caret;
            while (end < sql.Length && IsWordChar(sql[end])) end++;
            if (end == start) return true;
            return !IsKeyword(sql.Substring(start, end - start));
        }

        private static bool IsWordAt(string sql, int index)
            => index >= 0 && index < sql.Length && IsWordChar(sql[index]);

        private static bool IsNameQuote(char value) => value == '[' || value == ']' || value == '"';

        private static bool IsWordChar(char value)
            => char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$';

        private sealed class Token
        {
            public int Start;
            public int Length;
            public string Text;
        }

        private sealed class ObjectRef
        {
            public string Name;
            public string Schema;
            public int Start;
            public int Length;
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

        private sealed class QueryScopeVisitor : TSqlFragmentVisitor
        {
            private readonly int caret;

            public QueryScopeVisitor(int caret)
            {
                this.caret = caret;
            }

            /// <summary>Smallest query block containing the caret.</summary>
            public QuerySpecification Best { get; private set; }

            public override void Visit(QuerySpecification node)
            {
                if (node.StartOffset > caret) return;
                if (caret > node.StartOffset + node.FragmentLength) return;
                if (Best == null || node.FragmentLength < Best.FragmentLength) Best = node;
            }
        }

        private sealed class SemanticVisitor : TSqlFragmentVisitor
        {
            public List<TableRef> Tables { get; } = new List<TableRef>();
            public List<ColumnRef> Columns { get; } = new List<ColumnRef>();
            public List<ColumnPair> Pairs { get; } = new List<ColumnPair>();
            public List<ObjectRef> Objects { get; } = new List<ObjectRef>();

            public override void Visit(SchemaObjectName node)
            {
                var identifiers = node?.Identifiers;
                if (identifiers == null || identifiers.Count == 0) return;

                Identifier name = identifiers[identifiers.Count - 1];
                string schema = identifiers.Count > 1 ? identifiers[identifiers.Count - 2].Value : null;
                AddObject(name, schema);
            }

            // Service Broker names are plain Identifier nodes, not SchemaObjectName, so a script that
            // only creates services, contracts and message types would otherwise contribute no
            // object at all.
            public override void Visit(MessageTypeStatementBase node) => AddObject(node?.Name, null);

            public override void Visit(AlterCreateServiceStatementBase node)
            {
                AddObject(node?.Name, null);
                if (node?.QueueName != null) Visit(node.QueueName);
            }

            public override void Visit(CreateContractStatement node) => AddObject(node?.Name, null);

            public override void Visit(ContractMessage node) => AddObject(node?.Name, null);

            public override void Visit(ServiceContract node) => AddObject(node?.Name, null);

            public override void Visit(SendStatement node) => AddObject(node?.MessageTypeName?.Identifier, null);

            public override void Visit(BeginDialogStatement node)
            {
                AddObject(node?.InitiatorServiceName?.Identifier, null);
                AddObject(node?.ContractName?.Identifier, null);
            }

            private void AddObject(Identifier identifier, string schema)
            {
                if (identifier == null || string.IsNullOrEmpty(identifier.Value)) return;
                if (Objects.Any(o => o.Start == identifier.StartOffset)) return;

                Objects.Add(new ObjectRef
                {
                    Name = identifier.Value,
                    Schema = string.IsNullOrEmpty(schema) ? null : schema,
                    Start = identifier.StartOffset,
                    Length = identifier.FragmentLength
                });
            }

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
