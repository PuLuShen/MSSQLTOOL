using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// The grammar positions the completion popup cares about.  Only positions that the text
    /// shapes used to guess (and got wrong) are listed; specialised contexts such as INSERT column
    /// lists, EXEC arguments or PIVOT keep their own detection because they need more than a slot.
    /// </summary>
    internal enum SqlCaretKind
    {
        Unknown, DataSource, Join, MergeSource, TableSourceTail, JoinSourceTail, SelectList, Predicate, GroupBy, OrderBy, Member
    }

    internal sealed class SqlCaretPosition
    {
        public SqlCaretKind Kind { get; set; }
        public string SourceName { get; set; }
        public bool AliasWritten { get; set; }
        public bool TakesOn { get; set; }
        /// <summary>
        /// True when a real WHERE / HAVING / ON clause owns the caret.  A bare "=" or the word
        /// "WHERE" alone is not enough: the SET list of an UPDATE also assigns with "=".
        /// </summary>
        public bool InPredicateClause { get; set; }
        /// <summary>Qualifier of a member access: "h" in "h.|", "OtherDb.dbo" in "OtherDb.dbo.|".</summary>
        public string Qualifier { get; set; }
        /// <summary>The word being typed, which is what a replacement replaces.</summary>
        public string Prefix { get; set; }
        /// <summary>True for "dbo..Object": the schema part was left out on purpose.</summary>
        public bool OmittedSchemaQualifier { get; set; }
        /// <summary>True when the member being completed is itself a table source (FROM / JOIN ...).</summary>
        public bool IsDataSourceMember { get; set; }
        /// <summary>True when that source is being written in a JOIN clause.</summary>
        public bool IsJoinSource { get; set; }
        /// <summary>The column the predicate is comparing, read from the tokens ("h.Amount" in "WHERE h.Amount &gt; 100 AND |").</summary>
        public string ActiveColumn { get; set; }
        /// <summary>
        /// The clause that owns the predicate: WHERE, HAVING or ON.  Only an ON predicate can be
        /// followed by another JOIN, so the continuation list depends on it.
        /// </summary>
        public string PredicateOwner { get; set; }
        /// <summary>
        /// The clause that owns the caret, in T-SQL spelling (SELECT, FROM, JOIN, ON, WHERE, GROUP BY,
        /// ORDER BY, INTO, UPDATE, ...).  Published so the item lists never have to re-read the text
        /// to answer "which clause am I in".
        /// </summary>
        public string EnclosingClause { get; set; }
        /// <summary>
        /// True when the parse tree placed the caret.  False means the token scan did, which needs no
        /// tree and no completed SQL — the split is reported by the corpus so the tree's share stays
        /// visible instead of being taken on faith.
        /// </summary>
        public bool DecidedByTree { get; set; }
    }

    /// <summary>
    /// Answers "where is the caret" from the parse tree instead of from the shape of the text.
    /// The statement is parsed once for the semantic model; this resolver tokenizes it once more and
    /// looks up the last significant token before the caret in that tree.  The node that owns the
    /// token decides the position: a table reference ending at the caret is a source tail, the same
    /// reference as the second side of a join is a join tail, a WHERE / HAVING / ON clause owns a
    /// predicate.  Because aliases, columns and this position all come from one tree, they cannot
    /// disagree — the 5.464 "AND/OR after a JOIN target" bug was exactly that disagreement.
    /// </summary>
    internal static class SqlCaretPositionResolver
    {
        private class Span
        {
            public int Start;
            public int End;
        }

        private sealed class SourceSpan : Span
        {
            public string Name;
            public bool Alias;
            public bool IsJoin;
            public bool TakesOn;
        }

        private sealed class Collector : TSqlFragmentVisitor
        {
            public readonly List<SourceSpan> Sources = new List<SourceSpan>();
            public readonly List<Span> Predicates = new List<Span>();
            public readonly List<Span> SelectLists = new List<Span>();
            public readonly List<Span> GroupBys = new List<Span>();
            public readonly List<Span> OrderBys = new List<Span>();

            public override void Visit(NamedTableReference node)
            {
                string name = node.SchemaObject == null
                    ? null
                    : string.Join(".", node.SchemaObject.Identifiers.Select(i => i.Value));
                int end = node.Alias != null ? End(node.Alias) : End(node.SchemaObject);
                if (name == null || end <= node.StartOffset) return;
                Sources.Add(new SourceSpan { Start = node.StartOffset, End = end, Name = name, Alias = node.Alias != null });
            }

            public override void Visit(QueryDerivedTable node)
            {
                if (node.Alias == null) return;
                Sources.Add(new SourceSpan { Start = node.StartOffset, End = End(node.Alias), Name = node.Alias.Value, Alias = true });
            }

            public override void ExplicitVisit(QualifiedJoin node)
            {
                // Children first: the second table reference has to be collected before it can be
                // marked as the join tail.
                base.ExplicitVisit(node);
                MarkJoin(node.SecondTableReference, true);
                if (node.SearchCondition != null) Predicates.Add(SpanOf(node.SearchCondition));
            }

            public override void ExplicitVisit(UnqualifiedJoin node)
            {
                base.ExplicitVisit(node);
                // CROSS JOIN / CROSS APPLY / OUTER APPLY: a hint or the next join may follow, ON may not.
                MarkJoin(node.SecondTableReference, false);
            }

            public override void Visit(QuerySpecification node)
            {
                int end = node.FromClause != null ? node.FromClause.StartOffset : End(node);
                SelectLists.Add(new Span { Start = node.StartOffset, End = Math.Max(node.StartOffset, end) });
                if (node.GroupByClause != null) GroupBys.Add(SpanOf(node.GroupByClause));
                if (node.OrderByClause != null) OrderBys.Add(SpanOf(node.OrderByClause));
                if (node.WhereClause != null) Predicates.Add(SpanOf(node.WhereClause));
                if (node.HavingClause != null) Predicates.Add(SpanOf(node.HavingClause));
            }

            private void MarkJoin(TableReference reference, bool takesOn)
            {
                if (reference == null) return;
                int end = End(reference);
                foreach (SourceSpan source in Sources)
                {
                    if (source.Start < reference.StartOffset || source.Start >= end) continue;
                    source.IsJoin = true;
                    source.TakesOn = takesOn;
                }
            }
        }

        /// <summary>
        /// Resolves the position of the caret inside <paramref name="parseText"/>.  The caller passes
        /// a tree when it already has one (the semantic model does) so the statement is not parsed
        /// twice.
        /// </summary>
        public static SqlCaretPosition Resolve(string parseText, int caretOffset, TSqlFragment fragment = null)
        {
            var position = new SqlCaretPosition { Kind = SqlCaretKind.Unknown };
            string text = parseText ?? string.Empty;
            int caret = Math.Max(0, Math.Min(caretOffset, text.Length));
            if (caret == 0 || text.Length == 0) return position;

            // One definition of "the word being typed": a plain character walk, so a half-typed
            // "[Order" (no closing bracket yet) and a plain name read the same way.  Everything that
            // needs a prefix — the member qualifier, the replacement range, word completion — uses
            // this one string.
            int wordStart = WordStart(text, caret);
            position.Prefix = text.Substring(wordStart, caret - wordStart);
            bool typingName = position.Prefix.Length > 0;

            // Reuse the tree's own token stream when the caller already parsed the statement (the
            // semantic model does), so a keystroke never lexes the same text twice.  Inside a word
            // the tree is not asked: it would see a name, not the clause slot the caret is filling.
            TSqlFragment tree = typingName ? null : (fragment ?? Parse(text));
            List<TSqlParserToken> tokens = SignificantTokens(tree?.ScriptTokenStream) ?? Tokenize(text);

            // Member access is a lexical fact ("h.|" ends in a dot) and ScriptDOM drops the trailing
            // dot, so the tokens answer it before the tree is asked — no tree, no placeholder, no
            // second pattern for the same question.
            SqlCaretPosition scanner = Scan(tokens, text, caret, wordStart);
            if (scanner.Kind == SqlCaretKind.Member) return scanner;

            // The tree answers next, but only about constructs it could finish: ScriptDOM drops an
            // unclosed parenthesis and a join that has no ON clause yet, so the span the caret sits
            // in simply disappears.  Instead of completing the user's SQL to get a tree back, the
            // token scan below reads the same tokens — no tree, no guessed text.
            if (tree != null)
            {
                TSqlParserToken last = LastTokenBefore(tokens, caret);
                if (last != null && last.TokenType != TSqlTokenType.Dot)
                {
                    SqlCaretPosition decided = Decide(tree, tokens, last);
                    if (decided.Kind != SqlCaretKind.Unknown)
                    {
                        decided.DecidedByTree = true;
                        decided.Prefix = position.Prefix;
                        // The tree owns the predicate span, the tokens own which column it compares and
                        // which clause owns it.
                        if (decided.Kind == SqlCaretKind.Predicate)
                        {
                            decided.ActiveColumn = scanner.ActiveColumn;
                            decided.PredicateOwner = scanner.PredicateOwner;
                        }
                        return decided;
                    }
                }
            }

            return scanner;
        }

        /// <summary>
        /// Start of the word ending at the caret.  A character walk (not a regex and not a token)
        /// because the user may still be inside an unterminated bracket or quote, which the lexer
        /// cannot report but the prefix still has to replace.
        /// </summary>
        private static int WordStart(string text, int caret)
        {
            int start = caret;
            while (start > 0 && IsWordCharacter(text[start - 1])) start--;
            return start;
        }

        private static TSqlFragment Parse(string text)
        {
            try
            {
                var parser = new TSql170Parser(true);
                return parser.Parse(new StringReader(text), out IList<ParseError> _);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The one fallback path, used when the tree has nothing to say (or is not asked at all):
        /// the text is read the way a reader reads it — the innermost open level, the last clause
        /// keyword on that level, and what has been typed after it.  Nothing is guessed and nothing
        /// is appended to the user's SQL; only the tokens the caret already has behind it count.
        /// </summary>
        private static SqlCaretPosition Scan(List<TSqlParserToken> tokens, string text, int caret, int wordStart)
        {
            var position = new SqlCaretPosition { Kind = SqlCaretKind.Unknown };
            if (tokens == null || tokens.Count == 0) return position;
            position.Prefix = text.Substring(wordStart, caret - wordStart);

            // Only tokens that start before the caret are behind it; the word being typed ends at
            // the caret and counts, one that starts at or after it does not.
            int count = 0;
            while (count < tokens.Count && tokens[count].Offset < caret) count++;
            if (count == 0) return position;

            // Parenthesis depth of every token; the closing one belongs to the level it closes.
            var depths = new int[count];
            int depth = 0;
            for (int i = 0; i < count; i++)
            {
                if (tokens[i].TokenType == TSqlTokenType.RightParenthesis) depth = Math.Max(0, depth - 1);
                depths[i] = depth;
                if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis) depth++;
            }

            SqlCaretPosition member = ReadMember(tokens, depths, text, caret, wordStart, count);
            if (member != null) return member;

            // The word being typed is not part of the clause yet: "FROM Cus|" is still a source slot
            // and "WHERE Na|" is still the predicate.  Only a token that ends at the caret is dropped.
            int dropped = -1;
            bool typingWord = false;
            if (caret > 0 && !char.IsWhiteSpace(text[caret - 1])
                && tokens[count - 1].Offset + (tokens[count - 1].Text?.Length ?? 0) >= caret)
            {
                dropped = count - 1;
                count--;
                typingWord = true;
            }
            if (count == 0) return position;

            int last = count - 1;
            // "IN (|" / "WHERE (|": the caret opens an expression, so the position is the one the
            // parenthesis belongs to, not the empty level inside it.
            if (tokens[last].TokenType == TSqlTokenType.LeftParenthesis && last > 0) last--;
            int level = depths[last];

            // The last clause keyword on this level; a nested level carries a clause of its own, so
            // "WHERE EXISTS (SELECT 1 FROM dbo.T |" is the inner table and not the outer predicate.
            int clause = -1;
            for (int i = last; i >= 0; i--)
            {
                if (depths[i] != level) continue;
                if (!ClauseWords.Contains(Word(tokens[i]))) continue;
                clause = i;
                break;
            }
            if (clause < 0) return position;

            string keyword = Word(tokens[clause]);
            // The clause that owns the caret, published for the layers below: the predicate list needs
            // to know whether it follows ON (another JOIN is legal) or WHERE (it is not).
            position.EnclosingClause = keyword == "AND" || keyword == "OR"
                ? PredicateOwnerOf(tokens, depths, clause, level)
                : keyword;
            if (keyword == "BY")
            {
                string modifier = PreviousWord(tokens, depths, clause, level);
                if (modifier == "GROUP") position.Kind = SqlCaretKind.GroupBy;
                else if (modifier == "ORDER") position.Kind = SqlCaretKind.OrderBy;
                if (position.Kind != SqlCaretKind.Unknown)
                    position.EnclosingClause = modifier == "GROUP" ? "GROUP BY" : "ORDER BY";
                return position;
            }
            if (keyword == "WHERE" || keyword == "HAVING" || keyword == "ON" || keyword == "AND" || keyword == "OR")
            {
                // WHERE / HAVING / ON also prove the caret is in a predicate clause, not an assignment.
                position.Kind = SqlCaretKind.Predicate;
                position.InPredicateClause = true;
                position.ActiveColumn = PredicateColumn(tokens, depths, last, level);
                position.PredicateOwner = PredicateOwnerOf(tokens, depths, clause, level);
                return position;
            }
            if (keyword == "SELECT")
            {
                position.Kind = SqlCaretKind.SelectList;
                return position;
            }
            if (keyword == "INTO" || keyword == "UPDATE")
            {
                // A target, not a source list: what may follow is a SET list or a column list.
                position.Kind = SqlCaretKind.DataSource;
                return position;
            }
            if (keyword == "USING")
            {
                // MERGE ... USING names the source table of a merge.
                position.Kind = SqlCaretKind.MergeSource;
                return position;
            }
            bool joinSource = keyword == "JOIN" || keyword == "APPLY";
            if (keyword != "FROM" && !joinSource) return position;
            if (clause == last)
            {
                // "FROM |" and "MERGE |" take any source; "JOIN |" takes the table of the join.
                position.Kind = keyword == "JOIN" ? SqlCaretKind.Join : SqlCaretKind.DataSource;
                return position;
            }

            if (typingWord)
            {
                // The partial word sits right after the clause keyword or a comma: the slot is just
                // opening, so the popup must offer sources and never a tail hint.
                if (dropped == clause + 1 || (dropped > 0 && tokens[dropped - 1].TokenType == TSqlTokenType.Comma))
                {
                    position.Kind = keyword == "JOIN" ? SqlCaretKind.Join : SqlCaretKind.DataSource;
                    return position;
                }
                // Any other word after a finished source ("FROM dbo.T t WH|") is the next clause
                // keyword, a hint or the next join, so the position is still the tail and the typed
                // word is the prefix: the popup filters that list instead of falling back to every
                // candidate in the document.
            }
            // A tail starts past a blank; without one the caret is still inside a name.
            else if (caret <= 0 || !char.IsWhiteSpace(text[caret - 1])) return position;

            // "FROM dbo.A a, dbo.B b |": the comma opens the next entry, so only the last one counts.
            int entry = clause + 1;
            for (int i = clause + 1; i <= last; i++)
                if (depths[i] == level && tokens[i].TokenType == TSqlTokenType.Comma) entry = i + 1;
            if (entry > last)
            {
                position.Kind = SqlCaretKind.DataSource;      // "FROM dbo.A, |"
                return position;
            }

            string name = string.Empty;
            int index = entry;
            if (tokens[index].TokenType == TSqlTokenType.LeftParenthesis)
            {
                // "FROM (SELECT ...) x |": a derived table is a source known by its alias.
                index = AfterParenthesis(tokens, depths, index, last);
            }
            else
            {
                while (index <= last && depths[index] == level && IsNameToken(tokens[index]))
                {
                    if (name.Length > 0) name += ".";
                    name += Unquote(tokens[index]);
                    index++;
                    if (index <= last && depths[index] == level && tokens[index].TokenType == TSqlTokenType.Dot) index++;
                    else break;
                }
            }

            string alias = null;
            if (index <= last && depths[index] == level)
            {
                if (IsWord(tokens[index], "AS")) index++;
                if (index <= last && depths[index] == level && IsNameToken(tokens[index]) && !NonAliasWords.Contains(Word(tokens[index])))
                    alias = Unquote(tokens[index]);
            }

            if (name.Length == 0 && alias == null)
            {
                position.Kind = SqlCaretKind.DataSource;      // "FROM (|"
                return position;
            }

            position.Kind = joinSource ? SqlCaretKind.JoinSourceTail : SqlCaretKind.TableSourceTail;
            position.SourceName = name.Length > 0 ? name : alias;
            position.AliasWritten = alias != null;
            if (joinSource)
                // CROSS JOIN and APPLY take no ON clause, so ON must not be offered there.
                position.TakesOn = keyword != "APPLY" && PreviousWord(tokens, depths, clause, level) != "CROSS";
            return position;
        }

        private static string Word(TSqlParserToken token) => (token?.Text ?? string.Empty).ToUpperInvariant();

        private static bool IsWord(TSqlParserToken token, string word) => string.Equals(token?.Text, word, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// "h.|", "h.Head|", "OtherDb.dbo.|": a dot immediately before the word being typed is a
        /// member access.  The qualifier comes from the tokens (so quoting and multi-part names are
        /// read exactly once) and the prefix from the character walk, which is why a half-typed
        /// "[Ord" still replaces the right range.
        /// </summary>
        private static SqlCaretPosition ReadMember(List<TSqlParserToken> tokens, int[] depths, string text, int caret, int wordStart, int count)
        {
            if (wordStart <= 0 || text[wordStart - 1] != '.') return null;

            int dot = -1;
            for (int i = count - 1; i >= 0; i--)
            {
                if (tokens[i].Offset == wordStart - 1)
                {
                    if (tokens[i].TokenType == TSqlTokenType.Dot) dot = i;
                    break;
                }
                if (tokens[i].Offset < wordStart - 1) break;
            }
            if (dot < 0) return null;

            var parts = new List<string>();
            bool omitted = false;
            int index = dot - 1;
            while (index >= 0)
            {
                if (tokens[index].TokenType == TSqlTokenType.Dot)
                {
                    // "dbo..Object": the part left of the double dot was left out on purpose.
                    omitted = true;
                    index--;
                    continue;
                }
                if (!IsNameToken(tokens[index])) break;
                parts.Insert(0, Unquote(tokens[index]));
                index--;
                if (index >= 0 && tokens[index].TokenType == TSqlTokenType.Dot) index--;
                else break;
            }

            int chainStart = index + 1;
            int level = depths[Math.Min(chainStart, depths.Length - 1)];
            string before = ClauseBefore(tokens, depths, chainStart, level);
            string neighbour = chainStart > 0 && depths[chainStart - 1] == level ? Word(tokens[chainStart - 1]) : string.Empty;

            return new SqlCaretPosition
            {
                Kind = SqlCaretKind.Member,
                Qualifier = string.Join(".", parts),
                OmittedSchemaQualifier = omitted,
                EnclosingClause = before,
                IsDataSourceMember = before == "FROM" || before == "JOIN" || before == "APPLY"
                    || before == "INTO" || before == "UPDATE" || before == "USING" || before == "MERGE",
                IsJoinSource = neighbour == "JOIN" || neighbour == "APPLY",
                Prefix = text.Substring(wordStart, caret - wordStart)
            };
        }

        /// <summary>
        /// The column the current condition compares: the last comparison operator on this level and
        /// the dotted name right before it.  Read from the tokens, so "[h].[Amount]" and a quoted
        /// name come back unquoted and whole instead of as whatever the regex matched last.
        /// </summary>
        private static string PredicateColumn(List<TSqlParserToken> tokens, int[] depths, int last, int level)
        {
            int index = last;
            for (; index >= 0; index--)
            {
                if (depths[index] != level) { continue; }
                if (IsComparisonToken(tokens[index])) break;
            }
            if (index <= 0) return null;

            int cursor = index - 1;
            if (cursor < 0 || depths[cursor] != level || !IsNameToken(tokens[cursor])) return null;
            var parts = new List<string>();
            while (cursor >= 0 && depths[cursor] == level)
            {
                if (!IsNameToken(tokens[cursor])) break;
                parts.Insert(0, Unquote(tokens[cursor]));
                cursor--;
                if (cursor >= 0 && depths[cursor] == level && tokens[cursor].TokenType == TSqlTokenType.Dot) cursor--;
                else break;
            }
            return parts.Count == 0 ? null : string.Join(".", parts);
        }

        /// <summary>
        /// The nearest WHERE / HAVING / ON on this level; AND and OR do not change which clause owns
        /// the predicate.  "... ON a = b AND |" is still an ON predicate.
        /// </summary>
        private static string PredicateOwnerOf(List<TSqlParserToken> tokens, int[] depths, int clause, int level)
        {
            for (int i = clause; i >= 0; i--)
            {
                if (depths[i] != level) continue;
                string word = Word(tokens[i]);
                if (word == "WHERE" || word == "HAVING" || word == "ON") return word;
            }
            return null;
        }

        private static bool IsComparisonToken(TSqlParserToken token)
        {
            switch ((token?.Text ?? string.Empty).Trim())
            {
                case "=":
                case "<":
                case ">":
                case "<>":
                case "!=":
                case ">=":
                case "<=":
                case "!<":
                case "!>":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The last clause keyword on <paramref name="level"/> before <paramref name="index"/>.</summary>
        private static string ClauseBefore(List<TSqlParserToken> tokens, int[] depths, int index, int level)
        {
            for (int i = Math.Min(index, depths.Length) - 1; i >= 0; i--)
            {
                if (depths[i] != level) continue;
                string word = Word(tokens[i]);
                if (ClauseWords.Contains(word)) return word;
            }
            return string.Empty;
        }

        private static bool IsNameToken(TSqlParserToken token) =>
            token != null && (token.TokenType == TSqlTokenType.Identifier
                || token.TokenType == TSqlTokenType.QuotedIdentifier
                // "dbo" arrives as an ambiguous quoted token; with QUOTED_IDENTIFIER on it is a name.
                || token.TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier);

        private static string PreviousWord(List<TSqlParserToken> tokens, int[] depths, int index, int level)
        {
            for (int i = index - 1; i >= 0; i--)
                if (depths[i] == level) return Word(tokens[i]);
            return string.Empty;
        }

        /// <summary>Index just past the parenthesis closing the one at <paramref name="open"/>.</summary>
        private static int AfterParenthesis(List<TSqlParserToken> tokens, int[] depths, int open, int last)
        {
            for (int i = open + 1; i <= last; i++)
                if (depths[i] == depths[open] && tokens[i].TokenType == TSqlTokenType.RightParenthesis) return i + 1;
            return last + 1;
        }

        private static string Unquote(TSqlParserToken token)
        {
            string value = token?.Text ?? string.Empty;
            if (value.Length < 2) return value;
            char first = value[0];
            char final = value[value.Length - 1];
            if (first == '[' && final == ']') return value.Substring(1, value.Length - 2).Replace("]]", "]");
            if (first == '"' && final == '"') return value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
            return value;
        }

        private static readonly HashSet<string> ClauseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "JOIN", "APPLY", "WHERE", "HAVING", "ON", "GROUP", "ORDER", "BY",
            "AND", "OR", "INTO", "UPDATE", "USING", "MERGE", "SET", "VALUES", "UNION", "EXCEPT",
            "INTERSECT", "OPTION", "WHEN", "THEN"
        };

        /// <summary>Words that sit where an alias could, so they must not be read as one.</summary>
        private static readonly HashSet<string> NonAliasWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ON", "USING", "WITH", "AS", "JOIN", "APPLY", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS",
            "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "SET", "VALUES",
            "THEN", "WHEN", "FROM", "SELECT", "DELETE", "INSERT", "UPDATE", "MERGE", "PIVOT", "UNPIVOT", "FOR"
        };

        private static SqlCaretPosition Decide(TSqlFragment fragment, List<TSqlParserToken> tokens, TSqlParserToken last)
        {
            var position = new SqlCaretPosition { Kind = SqlCaretKind.Unknown };
            if (fragment == null) return position;
            var collector = new Collector();
            try
            {
                fragment.Accept(collector);
            }
            catch (Exception)
            {
                return position;
            }

            int tokenStart = last.Offset;

            // The innermost span wins: a WHERE clause encloses the whole subquery it contains, so
            // "WHERE EXISTS (SELECT 1 FROM dbo.T |" belongs to the table reference inside it and not
            // to the predicate around it.
            int bestLength = int.MaxValue;
            int bestPriority = int.MaxValue;
            SqlCaretPosition best = null;
            foreach (SourceSpan span in collector.Sources)
            {
                if (!Contains(span, tokenStart)) continue;
                Consider(new SqlCaretPosition
                {
                    Kind = span.IsJoin ? SqlCaretKind.JoinSourceTail : SqlCaretKind.TableSourceTail,
                    SourceName = span.Name,
                    AliasWritten = span.Alias,
                    TakesOn = span.IsJoin && span.TakesOn
                }, span.End - span.Start, 0, ref best, ref bestLength, ref bestPriority);
            }
            foreach (Span span in collector.Predicates)
            {
                if (!Contains(span, tokenStart)) continue;
                Consider(new SqlCaretPosition { Kind = SqlCaretKind.Predicate, InPredicateClause = true }, span.End - span.Start, 1, ref best, ref bestLength, ref bestPriority);
            }
            foreach (Span span in collector.OrderBys)
            {
                if (!Contains(span, tokenStart)) continue;
                Consider(new SqlCaretPosition { Kind = SqlCaretKind.OrderBy }, span.End - span.Start, 2, ref best, ref bestLength, ref bestPriority);
            }
            foreach (Span span in collector.GroupBys)
            {
                if (!Contains(span, tokenStart)) continue;
                Consider(new SqlCaretPosition { Kind = SqlCaretKind.GroupBy }, span.End - span.Start, 2, ref best, ref bestLength, ref bestPriority);
            }
            foreach (Span span in collector.SelectLists)
            {
                if (!Contains(span, tokenStart)) continue;
                Consider(new SqlCaretPosition { Kind = SqlCaretKind.SelectList }, span.End - span.Start, 3, ref best, ref bestLength, ref bestPriority);
            }
            if (best != null) return best;

            // No span owns the token: the tree cannot place the caret, so the token scan decides.
            // Keeping a keyword fallback here as well would be the second ladder this resolver is
            // meant to have removed.
            return position;
        }

        private static void Consider(SqlCaretPosition candidate, int length, int priority,
            ref SqlCaretPosition best, ref int bestLength, ref int bestPriority)
        {
            if (length > bestLength) return;
            if (length == bestLength && priority >= bestPriority) return;
            best = candidate;
            bestLength = length;
            bestPriority = priority;
        }

        private static bool IsWordCharacter(char value)
            => char.IsLetterOrDigit(value) || value == '_' || value == '#' || value == '@' || value == '$' || value == ']' || value == '[';

        private static bool Contains(Span span, int offset) => offset >= span.Start && offset < span.End;

        private static int End(TSqlFragment fragment)
            => fragment == null ? 0 : fragment.StartOffset + Math.Max(0, fragment.FragmentLength);

        private static Span SpanOf(TSqlFragment fragment)
            => new Span { Start = fragment.StartOffset, End = End(fragment) };

        private static List<TSqlParserToken> Tokenize(string text)
        {
            try
            {
                var parser = new TSql170Parser(true);
                IList<TSqlParserToken> all = parser.GetTokenStream(new StringReader(text), out IList<ParseError> _);
                return SignificantTokens(all) ?? new List<TSqlParserToken>();
            }
            catch (Exception)
            {
                return new List<TSqlParserToken>();
            }
        }

        /// <summary>Drops the trivia the position rules never look at, keeping the offsets intact.</summary>
        private static List<TSqlParserToken> SignificantTokens(IList<TSqlParserToken> all)
        {
            if (all == null || all.Count == 0) return null;
            var tokens = new List<TSqlParserToken>(all.Count);
            foreach (TSqlParserToken token in all)
            {
                switch (token.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                    case TSqlTokenType.EndOfFile:
                        continue;
                    default:
                        tokens.Add(token);
                        break;
                }
            }
            return tokens;
        }

        private static TSqlParserToken LastTokenBefore(List<TSqlParserToken> tokens, int caret)
        {
            TSqlParserToken last = null;
            foreach (TSqlParserToken token in tokens)
            {
                if (token.Offset >= caret) break;
                last = token;
            }
            return last;
        }
    }
}
