using System;
using System.Collections.Generic;

namespace MSSQLTool.Model
{
    /// <summary>
    /// What the caret position inside a WHERE / ON / HAVING predicate expects next.  Clause
    /// detection alone cannot answer this: after "WHERE a = 1" the useful tokens are AND/OR and
    /// the next clause, while after "WHERE a" they are comparison operators.
    /// </summary>
    internal enum SqlPredicateState
    {
        Unknown,
        OperandExpected,
        OperatorExpected,
        LogicalExpected,
        BetweenLowerBound
    }

    /// <summary>
    /// Classifies the tail of a predicate from its token stream.  Pure text in, state out: no
    /// editor types, no metadata, so the completion item table and its tests share one source of
    /// truth.  The clause anchor is found by scanning backwards at the caret's parenthesis depth,
    /// which keeps nested subqueries from hijacking the outer predicate's state.
    /// </summary>
    internal static class SqlPredicateStateClassifier
    {
        private static readonly HashSet<string> OperandKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NULL", "END", "CURRENT_TIMESTAMP", "CURRENT_USER", "CURRENT_DATE", "CURRENT_TIME", "DEFAULT"
        };

        private static readonly HashSet<string> FunctionKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CAST", "CONVERT", "TRY_CONVERT", "COALESCE", "NULLIF", "IIF", "CHOOSE"
        };

        private static readonly HashSet<string> ComparisonOperators = new HashSet<string>(StringComparer.Ordinal)
        {
            "=", "<>", "!=", ">", ">=", "<", "<=", "!<", "!>"
        };

        public static SqlPredicateState Classify(string sql) => Classify(sql, out _);

        public static SqlPredicateState Classify(string sql, out int depth)
        {
            depth = 0;
            if (string.IsNullOrWhiteSpace(sql)) return SqlPredicateState.Unknown;

            List<SqlToken> tokens = SqlTokenizer.Tokenize(sql);
            int anchor = FindClauseAnchor(tokens);
            if (anchor < 0) return SqlPredicateState.Unknown;

            bool comparisonSeen = false;
            bool expectOperand = true;
            bool betweenPending = false;
            bool betweenAwaitingAnd = false;
            var frames = new Stack<Frame>();
            SqlToken previous = null;

            for (int i = anchor + 1; i < tokens.Count; i++)
            {
                SqlToken token = tokens[i];
                if (token.IsComment) continue;

                if (token.Text == "(")
                {
                    frames.Push(new Frame
                    {
                        ComparisonSeen = comparisonSeen,
                        Kind = ClassifyParen(previous, betweenPending)
                    });
                    expectOperand = true;
                    previous = token;
                    continue;
                }

                if (token.Text == ")")
                {
                    Frame frame = frames.Count > 0 ? frames.Pop() : Frame.None;
                    expectOperand = false;
                    betweenPending = false;
                    betweenAwaitingAnd = false;
                    comparisonSeen = frame.Kind == ParenKind.Group || frame.Kind == ParenKind.Exists
                        ? true
                        : frame.ComparisonSeen;
                    previous = token;
                    continue;
                }

                if (token.Is("AND"))
                {
                    // The AND of "BETWEEN low AND high" completes a range; every other AND starts
                    // a new predicate inside the current logical expression.
                    if (betweenAwaitingAnd)
                    {
                        betweenAwaitingAnd = false;
                        expectOperand = true;
                    }
                    else
                    {
                        comparisonSeen = false;
                        expectOperand = true;
                    }

                    previous = token;
                    continue;
                }

                if (token.Is("OR"))
                {
                    comparisonSeen = false;
                    expectOperand = true;
                    previous = token;
                    continue;
                }

                if (token.Is("BETWEEN"))
                {
                    comparisonSeen = true;
                    expectOperand = true;
                    betweenPending = true;
                    previous = token;
                    continue;
                }

                if (token.Is("NOT") || token.Is("EXISTS"))
                {
                    expectOperand = true;
                    previous = token;
                    continue;
                }

                if (token.Is("LIKE") || token.Is("IN") || token.Is("IS"))
                {
                    comparisonSeen = true;
                    expectOperand = true;
                    previous = token;
                    continue;
                }

                if (token.Text == ",")
                {
                    expectOperand = true;
                    previous = token;
                    continue;
                }

                if (token.Text == ".")
                {
                    previous = token;
                    continue;
                }

                if (token.Kind == SqlTokenKind.Operator)
                {
                    if (ComparisonOperators.Contains(token.Text))
                    {
                        comparisonSeen = true;
                        expectOperand = true;
                    }
                    else if (token.Text == "*")
                    {
                        // COUNT(*) and SELECT * are complete operands; arithmetic operators keep
                        // waiting for their right-hand side.
                        expectOperand = false;
                    }
                    else
                    {
                        expectOperand = true;
                    }

                    previous = token;
                    continue;
                }

                if (token.Kind == SqlTokenKind.Identifier
                    || token.Kind == SqlTokenKind.Number
                    || token.Kind == SqlTokenKind.String
                    || OperandKeywords.Contains(token.Text))
                {
                    expectOperand = false;
                    if (betweenPending)
                    {
                        betweenPending = false;
                        betweenAwaitingAnd = true;
                    }

                    previous = token;
                    continue;
                }

                // Unrecognised keywords (SELECT of a subquery, WHEN/THEN of a CASE, ASC, ...) do
                // not by themselves change whether an operand or an operator comes next.
                previous = token;
            }

            depth = frames.Count;
            if (betweenAwaitingAnd && !expectOperand) return SqlPredicateState.BetweenLowerBound;
            if (expectOperand) return SqlPredicateState.OperandExpected;
            if (!comparisonSeen) return SqlPredicateState.OperatorExpected;
            return SqlPredicateState.LogicalExpected;
        }

        private static int FindClauseAnchor(List<SqlToken> tokens)
        {
            int depth = 0;
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                SqlToken token = tokens[i];
                if (token.IsComment) continue;
                if (token.Text == ")") { depth++; continue; }
                if (token.Text == "(")
                {
                    if (depth > 0) depth--;
                    continue;
                }

                if (depth == 0 && (token.Is("WHERE") || token.Is("HAVING") || token.Is("ON"))) return i;
            }

            return -1;
        }

        private static ParenKind ClassifyParen(SqlToken previous, bool betweenPending)
        {
            if (previous == null) return ParenKind.Group;
            if (previous.Is("IN")) return ParenKind.InList;
            if (previous.Is("EXISTS") || betweenPending) return ParenKind.Exists;
            if (previous.Kind == SqlTokenKind.Identifier || FunctionKeywords.Contains(previous.Text))
                return ParenKind.FunctionCall;
            return ParenKind.Group;
        }

        private enum ParenKind
        {
            Group,
            FunctionCall,
            InList,
            Exists
        }

        private struct Frame
        {
            public static readonly Frame None = new Frame { ComparisonSeen = false, Kind = ParenKind.Group };

            public bool ComparisonSeen;
            public ParenKind Kind;
        }
    }
}
