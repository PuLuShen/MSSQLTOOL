using System;
using System.Collections.Generic;
using System.Linq;
using MSSQLTool.Model;

namespace MSSQLTool.Analysis
{
    /// <summary>
    /// Token-level implementations of the catalogue.  Each rule is deliberately small and
    /// independent: a failure or timeout in one rule cannot stop the others, and every rule
    /// works on incomplete SQL (a half-typed WHERE clause is the normal editing state).
    /// </summary>
    internal static class AnalysisRules
    {
        public static void Run(string ruleId, RuleContext context)
        {
            switch (ruleId)
            {
                case "delete-without-where": DeleteWithoutWhere(context); break;
                case "update-without-where": UpdateWithoutWhere(context); break;
                case "equals-null": EqualsNull(context); break;
                case "not-in-null": NotInNull(context); break;
                case "join-without-on": JoinWithoutOn(context); break;
                case "select-star": SelectStar(context); break;
                case "non-sargable-predicate": NonSargablePredicate(context); break;
                case "leading-wildcard-like": LeadingWildcardLike(context); break;
                case "implicit-conversion": ImplicitConversion(context); break;
                case "dynamic-sql-concatenation": DynamicSqlConcatenation(context); break;
                case "transaction-without-try-catch": TransactionWithoutTryCatch(context); break;
                case "unbalanced-transaction": UnbalancedTransaction(context); break;
                case "nolock-hint": NoLockHint(context); break;
                case "cursor-usage": CursorUsage(context); break;
                case "merge-without-terminator": MergeWithoutTerminator(context); break;
                case "meaningless-order-by": MeaninglessOrderBy(context); break;
                case "duplicate-alias": DuplicateAlias(context); break;
                case "implicit-cross-join": ImplicitCrossJoin(context); break;
                case "missing-schema-prefix": MissingSchemaPrefix(context); break;
                case "unused-variable": UnusedVariable(context); break;
                case "unused-cte": UnusedCte(context); break;
                case "temp-table-not-dropped": TempTableNotDropped(context); break;
                case "select-into": SelectInto(context); break;
                case "sp-prefix": SpPrefix(context); break;
                case "count-one": CountOne(context); break;
                case "missing-as-alias": MissingAsAlias(context); break;
                case "redundant-distinct-in-exists": RedundantDistinctInExists(context); break;
                case "group-by-missing-column": GroupByMissingColumn(context); break;
                case "having-without-group-by": HavingWithoutGroupBy(context); break;
                case "varchar-without-length": VarcharWithoutLength(context); break;
            }
        }

        private static RuleDefinition Rule(string id) => RuleCatalog.Find(id);

        private static bool StartsWith(RuleContext context, string word)
            => context.First >= 0 && context.First < context.Tokens.Count && context.IsWord(context.First, word);

        private static int FindWhere(RuleContext context)
        {
            for (int i = context.First; i <= context.Last; i++)
                if (context.IsWord(i, "WHERE")) return i;
            return -1;
        }

        private static void DeleteWithoutWhere(RuleContext context)
        {
            if (!StartsWith(context, "DELETE")) return;
            if (context.IndexOfWord("WHERE") >= 0) return;
            RuleDefinition rule = Rule("delete-without-where");
            SqlToken token = context.Tokens[context.First];
            context.Add(rule, token.Start, token.Length);
        }

        private static void UpdateWithoutWhere(RuleContext context)
        {
            if (!StartsWith(context, "UPDATE")) return;
            if (context.IndexOfWord("WHERE") >= 0) return;
            RuleDefinition rule = Rule("update-without-where");
            SqlToken token = context.Tokens[context.First];
            context.Add(rule, token.Start, token.Length);
        }

        private static void EqualsNull(RuleContext context)
        {
            RuleDefinition rule = Rule("equals-null");
            for (int i = context.First; i <= context.Last; i++)
            {
                SqlToken token = context.Tokens[i];
                if (token.Text != "=" && token.Text != "<>" && token.Text != "!=") continue;
                int next = context.Next(i);
                if (next < 0 || !context.IsWord(next, "NULL")) continue;
                context.Add(rule, token.Start, context.Tokens[next].End - token.Start);
            }
        }

        private static void NotInNull(RuleContext context)
        {
            RuleDefinition rule = Rule("not-in-null");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "NOT")) continue;
                int inIndex = context.Next(i);
                if (inIndex < 0 || !context.IsWord(inIndex, "IN")) continue;
                int open = context.Next(inIndex);
                if (open < 0 || !context.IsSymbol(open, "(")) continue;
                SqlToken token = context.Tokens[i];
                context.Add(rule, token.Start, context.Tokens[inIndex].End - token.Start);
            }
        }

        private static void JoinWithoutOn(RuleContext context)
        {
            RuleDefinition rule = Rule("join-without-on");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "JOIN")) continue;
                int previous = context.Previous(i);
                if (previous >= 0 && (context.IsWord(previous, "CROSS") || context.IsWord(previous, "APPLY"))) continue;
                if (previous >= 0 && context.IsWord(previous, "OUTER") && context.IsWord(context.Previous(previous), "CROSS")) continue;

                bool hasOn = false;
                for (int j = context.Next(i); j >= 0 && j <= context.Last; j = context.Next(j))
                {
                    if (context.IsWord(j, "ON")) { hasOn = true; break; }
                    if (context.IsWord(j, "JOIN") || context.IsWord(j, "WHERE") || context.IsWord(j, "GROUP")
                        || context.IsWord(j, "ORDER") || context.IsWord(j, "HAVING") || context.IsWord(j, "UNION")
                        || context.IsWord(j, "OPTION")) break;
                }
                if (hasOn) continue;

                SqlToken token = context.Tokens[i];
                context.Add(rule, token.Start, token.Length);
            }
        }

        private static void SelectStar(RuleContext context)
        {
            RuleDefinition rule = Rule("select-star");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsSymbol(i, "*")) continue;
                int previous = context.Previous(i);
                if (previous < 0) continue;
                SqlToken star = context.Tokens[i];
                if (context.IsWord(previous, "SELECT"))
                {
                    context.Add(rule, star.Start, star.Length);
                    continue;
                }
                if (context.IsSymbol(previous, "."))
                {
                    int qualifier = context.Previous(previous);
                    if (qualifier >= 0 && (context.Tokens[qualifier].Kind == SqlTokenKind.Identifier || context.Tokens[qualifier].Kind == SqlTokenKind.Keyword))
                        context.Add(rule, context.Tokens[qualifier].Start, star.End - context.Tokens[qualifier].Start);
                }
            }
        }

        private static void NonSargablePredicate(RuleContext context)
        {
            RuleDefinition rule = Rule("non-sargable-predicate");
            bool inPredicate = false;
            for (int i = context.First; i <= context.Last; i++)
            {
                if (context.IsWord(i, "WHERE") || context.IsWord(i, "ON") || context.IsWord(i, "HAVING")) { inPredicate = true; continue; }
                if (context.IsWord(i, "GROUP") || context.IsWord(i, "ORDER") || context.IsWord(i, "UNION")) inPredicate = false;
                if (!inPredicate) continue;
                if (context.Tokens[i].Kind != SqlTokenKind.Identifier && context.Tokens[i].Kind != SqlTokenKind.Keyword) continue;
                if (IsAggregate(context.Word(i))) continue;
                int open = context.Next(i);
                if (open < 0 || !context.IsSymbol(open, "(")) continue;
                int close = FindClosing(context, open);
                if (close < 0) continue;
                int after = context.Next(close);
                if (after < 0) continue;
                bool compared = context.IsSymbol(after, "=") || context.IsSymbol(after, ">") || context.IsSymbol(after, "<")
                    || context.IsSymbol(after, ">=") || context.IsSymbol(after, "<=") || context.IsWord(after, "LIKE")
                    || context.IsWord(after, "IN") || context.IsWord(after, "BETWEEN");
                if (!compared) continue;
                SqlToken token = context.Tokens[i];
                context.Add(rule, token.Start, token.Length);
            }
        }

        private static void LeadingWildcardLike(RuleContext context)
        {
            RuleDefinition rule = Rule("leading-wildcard-like");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "LIKE")) continue;
                int next = context.Next(i);
                if (next < 0 || context.Tokens[next].Kind != SqlTokenKind.String) continue;
                string value = context.Tokens[next].Text;
                if (value.Length < 3) continue;
                char first = value.Length > 1 ? value[1] : '\0';
                if (first != '%' && first != '_') continue;
                SqlToken token = context.Tokens[next];
                context.Add(rule, token.Start, token.Length);
            }
        }

        private static void ImplicitConversion(RuleContext context)
        {
            RuleDefinition rule = Rule("implicit-conversion");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsSymbol(i, "=") && !context.IsSymbol(i, ">") && !context.IsSymbol(i, "<")
                    && !context.IsSymbol(i, ">=") && !context.IsSymbol(i, "<=")) continue;
                int left = context.Previous(i);
                int right = context.Next(i);
                if (left < 0 || right < 0) continue;
                if (context.Tokens[left].Kind != SqlTokenKind.Identifier) continue;
                if (context.Tokens[right].Kind != SqlTokenKind.String) continue;
                string literal = context.Tokens[right].Text.Trim('\'', 'N', 'n');
                if (literal.Length == 0 || !literal.All(c => char.IsDigit(c) || c == '.' || c == '-')) continue;
                context.Add(rule, context.Tokens[right].Start, context.Tokens[right].Length);
            }
        }

        private static void DynamicSqlConcatenation(RuleContext context)
        {
            RuleDefinition rule = Rule("dynamic-sql-concatenation");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "EXEC") && !context.IsWord(i, "EXECUTE")) continue;
                int next = context.Next(i);
                if (next < 0) continue;
                if (context.IsSymbol(next, "("))
                {
                    int afterOpen = context.Next(next);
                    if (afterOpen < 0) continue;
                    int plus = context.Next(afterOpen);
                    if (plus >= 0 && context.IsSymbol(plus, "+"))
                    {
                        SqlToken token = context.Tokens[i];
                        context.Add(rule, token.Start, token.Length);
                    }
                }
                else if (context.Tokens[next].Kind == SqlTokenKind.Identifier)
                {
                    int plus = context.Next(next);
                    if (plus >= 0 && context.IsSymbol(plus, "+"))
                    {
                        SqlToken token = context.Tokens[i];
                        context.Add(rule, token.Start, token.Length);
                    }
                }
            }
        }

        private static void TransactionWithoutTryCatch(RuleContext context)
        {
            int begin = FindBeginTransaction(context);
            if (begin < 0) return;
            for (int i = context.First; i <= context.Last; i++)
                if (context.IsWord(i, "TRY")) return;
            RuleDefinition rule = Rule("transaction-without-try-catch");
            SqlToken token = context.Tokens[begin];
            context.Add(rule, token.Start, token.Length);
        }

        private static void UnbalancedTransaction(RuleContext context)
        {
            int begin = FindBeginTransaction(context);
            if (begin < 0) return;

            int begins = 0, endings = 0;
            IEnumerable<SqlToken> tokens = context.Tokens;
            int first = context.First, last = context.Last;
            if (context.Model != null && context.Statement != null)
            {
                var batchStatements = context.Model.Statements.Statements
                    .Where(s => s.BatchIndex == context.Statement.BatchIndex).ToList();
                if (batchStatements.Count > 0)
                {
                    first = Math.Max(0, batchStatements.Min(s => s.FirstToken));
                    last = Math.Min(context.Tokens.Count - 1, batchStatements.Max(s => s.LastToken));
                }
            }

            for (int i = first; i <= last; i++)
            {
                if (context.IsWord(i, "BEGIN") && i + 1 < context.Tokens.Count && context.IsWord(i + 1, "TRAN")) { begins++; continue; }
                if (context.IsWord(i, "BEGIN") && i + 1 < context.Tokens.Count && context.IsWord(i + 1, "TRANSACTION")) { begins++; continue; }
                if (context.IsWord(i, "COMMIT")) { endings++; continue; }
                if (context.IsWord(i, "ROLLBACK") && !context.IsWord(i + 1, "TRAN")) endings++;
            }
            if (begins == 0 || begins == endings) return;
            RuleDefinition rule = Rule("unbalanced-transaction");
            SqlToken beginToken = context.Tokens[begin];
            context.Add(rule, beginToken.Start, beginToken.Length);
        }

        private static int FindBeginTransaction(RuleContext context)
        {
            for (int i = context.First; i <= context.Last; i++)
                if (context.IsWord(i, "BEGIN") && context.Next(i) >= 0
                    && (context.IsWord(context.Next(i), "TRAN") || context.IsWord(context.Next(i), "TRANSACTION")))
                    return i;
            return -1;
        }

        private static void NoLockHint(RuleContext context)
        {
            RuleDefinition rule = Rule("nolock-hint");
            for (int i = context.First; i <= context.Last; i++)
                if (context.IsWord(i, "NOLOCK"))
                {
                    SqlToken token = context.Tokens[i];
                    context.Add(rule, token.Start, token.Length);
                }
        }

        private static void CursorUsage(RuleContext context)
        {
            int index = context.IndexOfWord("CURSOR");
            if (index < 0) return;
            RuleDefinition rule = Rule("cursor-usage");
            SqlToken token = context.Tokens[index];
            context.Add(rule, token.Start, token.Length);
        }

        private static void MergeWithoutTerminator(RuleContext context)
        {
            if (!StartsWith(context, "MERGE")) return;
            if (context.Statement == null || context.Statement.Terminated) return;
            RuleDefinition rule = Rule("merge-without-terminator");
            SqlToken token = context.Tokens[context.First];
            context.Add(rule, token.Start, token.Length);
        }

        private static void MeaninglessOrderBy(RuleContext context)
        {
            RuleDefinition rule = Rule("meaningless-order-by");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "ORDER")) continue;
                int by = context.Next(i);
                if (by < 0 || !context.IsWord(by, "BY")) continue;
                if (context.DepthAt(i) == 0) continue;
                int top = FindEnclosingTop(context, i);
                if (top >= 0) continue;
                SqlToken token = context.Tokens[i];
                context.Add(rule, token.Start, context.Tokens[by].End - token.Start);
            }
        }

        /// <summary>True when the ORDER BY belongs to a TOP/OFFSET construct and therefore matters.</summary>
        private static int FindEnclosingTop(RuleContext context, int index)
        {
            int depth = 0;
            for (int i = index - 1; i >= context.First; i--)
            {
                if (context.IsSymbol(i, ")")) { depth++; continue; }
                if (context.IsSymbol(i, "("))
                {
                    if (depth == 0) return -1;
                    depth--;
                    continue;
                }
                if (depth > 0) continue;
                if (context.IsWord(i, "TOP") || context.IsWord(i, "OFFSET")) return i;
                if (context.IsWord(i, "SELECT") || context.IsSymbol(i, ";")) return -1;
            }
            return -1;
        }

        private static void DuplicateAlias(RuleContext context)
        {
            RuleDefinition rule = Rule("duplicate-alias");

            // Two sources only collide when they are in the same query block, at the same parenthesis
            // depth and use the same alias.  Without the block the rule fired across unrelated
            // statements: a batch like "SELECT ... FROM T\nSELECT ... FROM dbo.T" (no semicolons)
            // is one statement range, and both sources expose the implicit alias "T".
            var seen = new Dictionary<string, SqlSource>(StringComparer.OrdinalIgnoreCase);
            foreach (SqlSource source in context.Scope.Sources)
            {
                if (string.IsNullOrEmpty(source.Alias)) continue;
                string key = source.Block + "|" + source.Depth + "|" + source.Alias;
                if (!seen.TryGetValue(key, out SqlSource existing))
                {
                    seen[key] = source;
                    continue;
                }
                if (string.Equals(existing.ObjectName, source.ObjectName, StringComparison.OrdinalIgnoreCase)) continue;
                int start = source.AliasStart > 0 ? source.AliasStart : source.Start;
                int length = source.AliasLength > 0 ? source.AliasLength : source.Alias.Length;
                context.Add(rule, start, length);
            }
        }

        private static void ImplicitCrossJoin(RuleContext context)
        {
            int from = context.IndexOfWord("FROM");
            if (from < 0) return;
            int depth = context.DepthAt(from);
            bool sawComma = false;
            for (int i = context.Next(from); i >= 0 && i <= context.Last; i = context.Next(i))
            {
                if (context.IsSymbol(i, ",") && context.DepthAt(i) == depth) { sawComma = true; break; }
                if (context.IsWord(i, "WHERE") || context.IsWord(i, "GROUP") || context.IsWord(i, "ORDER")
                    || context.IsWord(i, "UNION") || context.IsWord(i, ";")) break;
            }
            if (!sawComma) return;
            for (int i = from; i <= context.Last; i++)
                if (context.IsWord(i, "JOIN")) return;
            RuleDefinition rule = Rule("implicit-cross-join");
            SqlToken token = context.Tokens[from];
            context.Add(rule, token.Start, token.Length);
        }

        private static void MissingSchemaPrefix(RuleContext context)
        {
            RuleDefinition rule = Rule("missing-schema-prefix");
            foreach (SqlSource source in context.Scope.Sources)
            {
                if (string.IsNullOrEmpty(source.ObjectName)) continue;
                if (source.ObjectName.IndexOf('.') >= 0) continue;
                if (source.ObjectName.StartsWith("#", StringComparison.Ordinal) || source.ObjectName.StartsWith("@", StringComparison.Ordinal)) continue;
                if (context.Scope.CteNames.Any(c => string.Equals(c, source.ObjectName, StringComparison.OrdinalIgnoreCase))) continue;
                context.Add(rule, source.Start, source.ObjectName.Length);
            }
        }

        private static void UnusedVariable(RuleContext context)
        {
            if (!StartsWith(context, "DECLARE")) return;
            for (int i = context.First; i <= context.Last; i++)
            {
                SqlToken token = context.Tokens[i];
                if (token.Kind != SqlTokenKind.Identifier || !token.Text.StartsWith("@", StringComparison.Ordinal)) continue;
                int uses = context.Tokens.Count(t => !t.IsComment && string.Equals(t.Text, token.Text, StringComparison.OrdinalIgnoreCase));
                if (uses != 1) continue;
                RuleDefinition rule = Rule("unused-variable");
                context.Add(rule, token.Start, token.Length);
            }
        }

        private static void UnusedCte(RuleContext context)
        {
            RuleDefinition rule = Rule("unused-cte");
            foreach (string name in context.Scope.CteNames)
            {
                int uses = context.Tokens.Count(t => !t.IsComment && t.Kind == SqlTokenKind.Identifier
                    && string.Equals(SqlTokenizer.Unquote(t), name, StringComparison.OrdinalIgnoreCase));
                if (uses > 1) continue;
                SqlToken token = context.Tokens.FirstOrDefault(t => t.Kind == SqlTokenKind.Identifier
                    && string.Equals(SqlTokenizer.Unquote(t), name, StringComparison.OrdinalIgnoreCase));
                if (token == null) continue;
                context.Add(rule, token.Start, token.Length);
            }
        }

        private static void TempTableNotDropped(RuleContext context)
        {
            var created = new List<SqlToken>();
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "CREATE")) continue;
                int table = context.Next(i);
                if (table < 0 || !context.IsWord(table, "TABLE")) continue;
                int name = context.Next(table);
                if (name < 0) continue;
                string value = SqlTokenizer.Unquote(context.Tokens[name]);
                if (value.StartsWith("#", StringComparison.Ordinal)) created.Add(context.Tokens[name]);
            }
            if (context.Model != null)
            {
                foreach (SelectIntoReference reference in SqlSelectIntoScanner.Find(context.Text))
                {
                    if (reference.Index < context.Statement.Start || reference.Index > context.Statement.End) continue;
                    SqlToken token = context.Tokens.FirstOrDefault(t => t.Start == reference.Index);
                    if (token != null) created.Add(token);
                }
            }

            RuleDefinition rule = Rule("temp-table-not-dropped");
            foreach (SqlToken token in created)
            {
                string name = SqlTokenizer.Unquote(token);
                bool dropped = false;
                for (int i = context.First; i <= context.Last; i++)
                {
                    if (!context.IsWord(i, "DROP")) continue;
                    int table = context.Next(i);
                    if (table < 0 || !context.IsWord(table, "TABLE")) continue;
                    int droppedName = context.Next(table);
                    if (droppedName >= 0 && context.IsWord(droppedName, "IF")) droppedName = context.Next(context.Next(droppedName));
                    if (droppedName >= 0 && string.Equals(SqlTokenizer.Unquote(context.Tokens[droppedName]), name, StringComparison.OrdinalIgnoreCase))
                    {
                        dropped = true;
                        break;
                    }
                }
                if (!dropped) context.Add(rule, token.Start, token.Length);
            }
        }

        private static void SelectInto(RuleContext context)
        {
            RuleDefinition rule = Rule("select-into");
            foreach (SelectIntoReference reference in SqlSelectIntoScanner.Find(context.Text))
            {
                if (reference.Index < context.Statement.Start || reference.Index > context.Statement.End) continue;
                int into = context.Text.IndexOf("INTO", reference.Index, StringComparison.OrdinalIgnoreCase);
                if (into < 0) continue;
                context.Add(rule, into, 4);
            }
        }

        private static void SpPrefix(RuleContext context)
        {
            RuleDefinition rule = Rule("sp-prefix");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "EXEC") && !context.IsWord(i, "EXECUTE")) continue;
                int target = context.Next(i);
                if (target < 0) continue;
                string name = SqlTokenizer.Unquote(context.Tokens[target]);
                if (!name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)) continue;
                context.Add(rule, context.Tokens[target].Start, context.Tokens[target].Length);
            }
        }

        private static void CountOne(RuleContext context)
        {
            RuleDefinition rule = Rule("count-one");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "COUNT")) continue;
                int open = context.Next(i);
                if (open < 0 || !context.IsSymbol(open, "(")) continue;
                int value = context.Next(open);
                if (value < 0 || context.Tokens[value].Kind != SqlTokenKind.Number || context.Tokens[value].Text != "1") continue;
                int close = context.Next(value);
                if (close < 0 || !context.IsSymbol(close, ")")) continue;
                context.Add(rule, context.Tokens[i].Start, context.Tokens[close].End - context.Tokens[i].Start);
            }
        }

        private static void MissingAsAlias(RuleContext context)
        {
            RuleDefinition rule = Rule("missing-as-alias");
            int select = context.IndexOfWord("SELECT");
            if (select < 0) return;
            int from = -1;
            int depth = context.DepthAt(select);
            for (int i = context.Next(select); i >= 0 && i <= context.Last; i = context.Next(i))
                if (context.IsWord(i, "FROM") && context.DepthAt(i) == depth) { from = i; break; }
            if (from < 0) return;
            for (int i = context.Next(select); i >= 0 && i < from; i = context.Next(i))
            {
                if (context.IsWord(i, "AS")) continue;
                if (context.Tokens[i].Kind != SqlTokenKind.Identifier) continue;
                int next = context.Next(i);
                if (next < 0 || next >= from) continue;
                if (context.Tokens[next].Kind != SqlTokenKind.Identifier) continue;
                context.Add(rule, context.Tokens[i].Start, context.Tokens[i].Length);
            }
        }

        private static void RedundantDistinctInExists(RuleContext context)
        {
            RuleDefinition rule = Rule("redundant-distinct-in-exists");
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "DISTINCT")) continue;
                int select = context.Previous(i);
                if (select < 0 || !context.IsWord(select, "SELECT")) continue;
                int open = context.Previous(select);
                if (open < 0 || !context.IsSymbol(open, "(")) continue;
                int exists = context.Previous(open);
                if (exists < 0 || !context.IsWord(exists, "EXISTS")) continue;
                context.Add(rule, context.Tokens[i].Start, context.Tokens[i].Length);
            }
        }

        private static void GroupByMissingColumn(RuleContext context)
        {
            int group = -1, by = -1;
            for (int i = context.First; i <= context.Last; i++)
            {
                if (!context.IsWord(i, "GROUP")) continue;
                int next = context.Next(i);
                if (next >= 0 && context.IsWord(next, "BY")) { group = i; by = next; break; }
            }
            if (group < 0) return;

            var groupColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = context.Next(by); i >= 0 && i <= context.Last; i = context.Next(i))
            {
                if (context.IsWord(i, "ORDER") || context.IsWord(i, "HAVING") || context.IsSymbol(i, ";")) break;
                if (context.Tokens[i].Kind == SqlTokenKind.Identifier) groupColumns.Add(SqlTokenizer.Unquote(context.Tokens[i]));
            }

            int select = context.IndexOfWord("SELECT");
            if (select < 0) return;
            int from = -1;
            for (int i = context.Next(select); i >= 0 && i <= context.Last; i = context.Next(i))
                if (context.IsWord(i, "FROM")) { from = i; break; }
            if (from < 0) return;

            RuleDefinition rule = Rule("group-by-missing-column");
            int depth = context.DepthAt(select);
            for (int i = context.Next(select); i >= 0 && i < from; i = context.Next(i))
            {
                if (context.Tokens[i].Kind != SqlTokenKind.Identifier) continue;
                if (context.DepthAt(i) != depth) continue;
                int next = context.Next(i);
                if (next >= 0 && context.IsSymbol(next, ".")) continue;
                string column = SqlTokenizer.Unquote(context.Tokens[i]);
                if (groupColumns.Contains(column)) continue;
                if (context.Scope.SelectAliases.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
                int previous = context.Previous(i);
                if (previous >= 0 && context.IsWord(previous, "AS")) continue;
                context.Add(rule, context.Tokens[i].Start, context.Tokens[i].Length);
            }
        }

        private static void HavingWithoutGroupBy(RuleContext context)
        {
            int having = context.IndexOfWord("HAVING");
            if (having < 0) return;
            for (int i = context.First; i <= context.Last; i++)
                if (context.IsWord(i, "GROUP") && context.Next(i) >= 0 && context.IsWord(context.Next(i), "BY")) return;
            RuleDefinition rule = Rule("having-without-group-by");
            context.Add(rule, context.Tokens[having].Start, context.Tokens[having].Length);
        }

        private static void VarcharWithoutLength(RuleContext context)
        {
            RuleDefinition rule = Rule("varchar-without-length");
            for (int i = context.First; i <= context.Last; i++)
            {
                string word = context.Word(i);
                if (!string.Equals(word, "VARCHAR", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(word, "NVARCHAR", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(word, "CHAR", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(word, "NCHAR", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(word, "VARBINARY", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(word, "BINARY", StringComparison.OrdinalIgnoreCase)) continue;
                int next = context.Next(i);
                if (next < 0) continue;
                if (context.IsSymbol(next, "(")) continue;
                if (!context.IsSymbol(next, ")") && !context.IsSymbol(next, ",")) continue;
                context.Add(rule, context.Tokens[i].Start, context.Tokens[i].Length);
            }
        }

        private static int FindClosing(RuleContext context, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i <= context.Last; i++)
            {
                if (context.IsSymbol(i, "(")) depth++;
                else if (context.IsSymbol(i, ")"))
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static bool IsAggregate(string word)
            => string.Equals(word, "COUNT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "SUM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "AVG", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "MIN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "MAX", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "STRING_AGG", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "STDEV", StringComparison.OrdinalIgnoreCase);
    }
}
