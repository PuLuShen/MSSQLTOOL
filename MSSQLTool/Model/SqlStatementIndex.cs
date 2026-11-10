using System;
using System.Collections.Generic;

namespace MSSQLTool.Model
{
    internal sealed class SqlStatementRange
    {
        public int Start;
        public int End;
        public int BatchIndex;
        public int FirstToken;
        public int LastToken;
        public bool Terminated;

        public int Length => Math.Max(0, End - Start);

        public bool Contains(int offset) => offset >= Start && offset <= End;
    }

    /// <summary>
    /// Statement and batch boundaries derived from tokens rather than a full parse.  A boundary
    /// is a semicolon at parenthesis depth zero or a GO batch separator at the start of a line,
    /// so incomplete statements (which is what the editor usually holds) still get a usable
    /// range.  The index is immutable and cheap: one linear pass over the token list.
    /// </summary>
    internal sealed class SqlStatementIndex
    {
        public List<SqlStatementRange> Statements { get; } = new List<SqlStatementRange>();
        public List<int> BatchStarts { get; } = new List<int>();

        public static SqlStatementIndex Build(string text, IList<SqlToken> tokens)
        {
            var index = new SqlStatementIndex();
            if (tokens == null || tokens.Count == 0)
            {
                index.Statements.Add(new SqlStatementRange { Start = 0, End = (text ?? string.Empty).Length, BatchIndex = 0 });
                index.BatchStarts.Add(0);
                return index;
            }

            string source = text ?? string.Empty;
            int batch = 0;
            int depth = 0;
            int statementStartToken = -1;
            index.BatchStarts.Add(tokens[0].Start);

            for (int i = 0; i < tokens.Count; i++)
            {
                SqlToken token = tokens[i];
                if (token.IsComment) continue;

                if (statementStartToken < 0) statementStartToken = i;

                if (token.Kind == SqlTokenKind.Punctuation)
                {
                    if (token.Text == "(") depth++;
                    else if (token.Text == ")") depth = Math.Max(0, depth - 1);
                    else if (token.Text == ";" && depth == 0)
                    {
                        Add(index, source, tokens, statementStartToken, i, batch, true);
                        statementStartToken = -1;
                    }
                    continue;
                }

                if (IsGoSeparator(source, token))
                {
                    if (statementStartToken >= 0 && statementStartToken < i)
                        Add(index, source, tokens, statementStartToken, i - 1, batch, false);
                    statementStartToken = -1;
                    batch++;
                    int next = i + 1 < tokens.Count ? tokens[i + 1].Start : source.Length;
                    index.BatchStarts.Add(next);
                    depth = 0;
                }
            }

            if (statementStartToken >= 0 && statementStartToken < tokens.Count)
                Add(index, source, tokens, statementStartToken, tokens.Count - 1, batch, false);

            if (index.Statements.Count == 0)
                index.Statements.Add(new SqlStatementRange { Start = 0, End = source.Length, BatchIndex = 0 });

            return index;
        }

        public SqlStatementRange At(int offset)
        {
            if (Statements.Count == 0) return null;
            int low = 0, high = Statements.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                SqlStatementRange statement = Statements[mid];
                if (offset < statement.Start) high = mid - 1;
                else if (offset > statement.End) low = mid + 1;
                else return statement;
            }

            // The caret sits in the whitespace between statements: use the statement that ends
            // closest to it, so "SELECT ... |" still resolves the query being typed.
            int insertion = Math.Min(low, Statements.Count - 1);
            int before = Math.Max(0, insertion - 1);
            SqlStatementRange left = Statements[before];
            SqlStatementRange right = Statements[insertion];
            return Math.Abs(offset - left.End) <= Math.Abs(right.Start - offset) ? left : right;
        }

        public SqlStatementRange InBatch(int offset)
        {
            SqlStatementRange statement = At(offset);
            if (statement == null) return null;

            // A batch is every statement that shares the BatchIndex (Statements is ordered by
            // position), so the whole DECLARE/SET/SELECT group is covered.
            int start = statement.Start;
            int end = statement.End;
            foreach (SqlStatementRange candidate in Statements)
            {
                if (candidate.BatchIndex != statement.BatchIndex) continue;
                if (candidate.Start < start) start = candidate.Start;
                if (candidate.End > end) end = candidate.End;
            }

            return new SqlStatementRange
            {
                Start = start,
                End = end,
                BatchIndex = statement.BatchIndex,
                Terminated = statement.Terminated
            };
        }

        private static void Add(SqlStatementIndex index, string text, IList<SqlToken> tokens, int firstToken, int lastToken, int batch, bool terminated)
        {
            if (firstToken < 0 || lastToken < firstToken || firstToken >= tokens.Count) return;
            int start = tokens[firstToken].Start;
            int end = tokens[Math.Min(lastToken, tokens.Count - 1)].End;
            if (end <= start) return;

            if (index.Statements.Count > 0)
            {
                SqlStatementRange previous = index.Statements[index.Statements.Count - 1];
                if (start <= previous.Start && end <= previous.End) return;
            }

            index.Statements.Add(new SqlStatementRange
            {
                Start = start,
                End = end,
                BatchIndex = batch,
                FirstToken = firstToken,
                LastToken = Math.Min(lastToken, tokens.Count - 1),
                Terminated = terminated
            });
        }

        private static bool IsGoSeparator(string text, SqlToken token)
        {
            if (!token.IsWordLike || !token.Is("GO")) return false;
            int index = token.Start - 1;
            while (index >= 0 && (text[index] == ' ' || text[index] == '\t')) index--;
            return index < 0 || text[index] == '\n' || text[index] == '\r';
        }
    }
}
