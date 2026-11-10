using System;
using System.Collections.Generic;
using System.Linq;

namespace MSSQLTool.Model
{
    internal sealed class SqlSource
    {
        public string Alias;
        public string ObjectName;
        public bool IsDerived;
        public int Depth;
        /// <summary>
        /// Ordinal of the top-level query block that declared this source.  A batch can hold several
        /// statements without semicolons, and each UNION branch is its own block, so two sources that
        /// merely share a name (or an implicit alias) in different blocks are unrelated.
        /// </summary>
        public int Block = -1;
        public int Start;
        public int AliasStart;
        public int AliasLength;

        public string DisplayName => string.IsNullOrEmpty(Alias) ? ObjectName : Alias;

        public bool Matches(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return string.Equals(value, Alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, ObjectName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(ObjectName) && ObjectName.EndsWith("." + value, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Table sources of one statement, keyed by the parenthesis depth they were declared at.
    /// The depth is what makes sibling subqueries stop leaking aliases into each other: a
    /// source is visible only at its own depth or deeper, never in an unrelated branch.
    /// </summary>
    internal sealed class SqlScopeTree
    {
        private static readonly HashSet<string> ClauseTerminators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ON", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "APPLY", "GROUP",
            "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "FOR", "WITH", "PIVOT", "UNPIVOT",
            "SET", "VALUES", "OUTPUT", "INTO", "WHEN", "THEN", "ELSE", "END", "USING"
        };

        public List<SqlSource> Sources { get; } = new List<SqlSource>();
        public List<string> CteNames { get; } = new List<string>();
        public List<string> SelectAliases { get; } = new List<string>();
        private readonly List<int> blockStarts = new List<int>();
        public int Start { get; private set; }
        public int End { get; private set; }
        private IList<SqlToken> tokens;
        private int firstToken;

        public static SqlScopeTree Build(string text, IList<SqlToken> tokens, SqlStatementRange statement)
        {
            var tree = new SqlScopeTree();
            if (tokens == null || statement == null) return tree;
            tree.Start = statement.Start;
            tree.End = statement.End;
            tree.tokens = tokens;
            tree.firstToken = Math.Max(0, statement.FirstToken);
            tree.CollectBlockStarts();

            int first = Math.Max(0, statement.FirstToken);
            int last = Math.Min(statement.LastToken, tokens.Count - 1);

            for (int i = first; i <= last; i++)
            {
                SqlToken token = tokens[i];
                if (token.IsComment) continue;

                if (token.Is("WITH") && i == first)
                    CollectCteNames(tokens, first, last, tree);

                if (!token.IsWordLike) continue;
                if (!token.Is("FROM") && !token.Is("JOIN") && !token.Is("APPLY")) continue;

                int next = NextSignificant(tokens, i + 1, last);
                if (next < 0) continue;

                if (tokens[next].Text == "(")
                {
                    int close = FindMatchingParen(tokens, next, last);
                    if (close < 0) continue;
                    int aliasIndex = NextSignificant(tokens, close + 1, last);
                    string alias = aliasIndex >= 0 && tokens[aliasIndex].Kind == SqlTokenKind.Identifier
                        ? SqlTokenizer.Unquote(tokens[aliasIndex])
                        : null;
                    Add(tree, new SqlSource
                    {
                        Alias = alias,
                        IsDerived = true,
                        Depth = DepthAt(tokens, first, next),
                        Block = tree.BlockOf(tokens[next].Start),
                        Start = tokens[next].Start,
                        AliasStart = aliasIndex >= 0 ? tokens[aliasIndex].Start : 0,
                        AliasLength = aliasIndex >= 0 ? tokens[aliasIndex].Length : 0
                    });
                    // "FROM (SELECT ...) AS x, dbo.T t" declares t as well; keep reading the list.
                    int afterSource = aliasIndex;
                    if (aliasIndex >= 0 && tokens[aliasIndex].Is("AS"))
                        afterSource = NextSignificant(tokens, aliasIndex + 1, last);
                    if (token.Is("FROM") && afterSource >= 0)
                        CollectCommaSeparatedSources(tokens, first, last, tree, afterSource);
                    continue;
                }

                if (tokens[next].Kind != SqlTokenKind.Identifier) continue;

                string objectName = ReadQualifiedName(tokens, next, last, out int nameEnd);
                if (string.IsNullOrEmpty(objectName)) continue;

                int aliasCandidate = NextSignificant(tokens, nameEnd + 1, last);
                string sourceAlias = null;
                int aliasStart = 0, aliasLength = 0, sourceEnd = nameEnd;
                if (aliasCandidate >= 0 && tokens[aliasCandidate].Kind == SqlTokenKind.Identifier
                    && !ClauseTerminators.Contains(tokens[aliasCandidate].Text))
                {
                    sourceAlias = SqlTokenizer.Unquote(tokens[aliasCandidate]);
                    aliasStart = tokens[aliasCandidate].Start;
                    aliasLength = tokens[aliasCandidate].Length;
                    sourceEnd = aliasCandidate;
                }
                else if (aliasCandidate >= 0 && tokens[aliasCandidate].Is("AS"))
                {
                    int aliasAfterAs = NextSignificant(tokens, aliasCandidate + 1, last);
                    if (aliasAfterAs >= 0 && tokens[aliasAfterAs].Kind == SqlTokenKind.Identifier)
                    {
                        sourceAlias = SqlTokenizer.Unquote(tokens[aliasAfterAs]);
                        aliasStart = tokens[aliasAfterAs].Start;
                        aliasLength = tokens[aliasAfterAs].Length;
                        sourceEnd = aliasAfterAs;
                    }
                }

                Add(tree, new SqlSource
                {
                    Alias = string.IsNullOrEmpty(sourceAlias) ? LastPart(objectName) : sourceAlias,
                    ObjectName = objectName,
                    Depth = DepthAt(tokens, first, next),
                    Block = tree.BlockOf(tokens[next].Start),
                    Start = tokens[next].Start,
                    AliasStart = aliasStart,
                    AliasLength = aliasLength
                });
                // "FROM a AS A, b AS B" declares B too, and the loop only reads what follows
                // FROM / JOIN / APPLY itself.  The rest of the FROM list is read here.
                if (token.Is("FROM"))
                    CollectCommaSeparatedSources(tokens, first, last, tree, sourceEnd);
            }

            CollectSelectAliases(tokens, first, last, tree);
            return tree;
        }

        /// <summary>
        /// Table references that continue one FROM list after a comma.  "FROM dbo.A AS A,
        /// dbo.B AS B" declares two sources, but the walk above only reads the reference directly
        /// after FROM/JOIN/APPLY, so B was never in the scope: an unqualified column list at
        /// "... AND |" resolved against A alone and B's columns were missing, while "B." still
        /// worked through the ScriptDOM alias map.  The walk stops at the clause terminator,
        /// skips WITH (...) table hints, and leaves a derived table to its own alias.
        /// </summary>
        private static void CollectCommaSeparatedSources(IList<SqlToken> tokens, int first, int last,
            SqlScopeTree tree, int afterSource)
        {
            int cursor = afterSource;
            while (true)
            {
                int next = NextSignificant(tokens, cursor + 1, last);
                if (next < 0) return;
                while (next >= 0 && tokens[next].Is("WITH"))
                {
                    int open = NextSignificant(tokens, next + 1, last);
                    if (open < 0 || tokens[open].Text != "(") return;
                    int close = FindMatchingParen(tokens, open, last);
                    if (close < 0) return;
                    next = NextSignificant(tokens, close + 1, last);
                }
                if (next < 0 || tokens[next].Text != ",") return;

                int start = NextSignificant(tokens, next + 1, last);
                if (start < 0 || tokens[start].Kind != SqlTokenKind.Identifier) return;

                string objectName = ReadQualifiedName(tokens, start, last, out int nameEnd);
                if (string.IsNullOrEmpty(objectName)) return;

                int aliasCandidate = NextSignificant(tokens, nameEnd + 1, last);
                string alias = null;
                int aliasStart = 0, aliasLength = 0, sourceEnd = nameEnd;
                if (aliasCandidate >= 0 && tokens[aliasCandidate].Is("AS"))
                {
                    int aliasAfterAs = NextSignificant(tokens, aliasCandidate + 1, last);
                    if (aliasAfterAs >= 0 && tokens[aliasAfterAs].Kind == SqlTokenKind.Identifier)
                    {
                        alias = SqlTokenizer.Unquote(tokens[aliasAfterAs]);
                        aliasStart = tokens[aliasAfterAs].Start;
                        aliasLength = tokens[aliasAfterAs].Length;
                        sourceEnd = aliasAfterAs;
                    }
                }
                else if (aliasCandidate >= 0 && tokens[aliasCandidate].Kind == SqlTokenKind.Identifier
                         && !ClauseTerminators.Contains(tokens[aliasCandidate].Text))
                {
                    alias = SqlTokenizer.Unquote(tokens[aliasCandidate]);
                    aliasStart = tokens[aliasCandidate].Start;
                    aliasLength = tokens[aliasCandidate].Length;
                    sourceEnd = aliasCandidate;
                }

                Add(tree, new SqlSource
                {
                    Alias = string.IsNullOrEmpty(alias) ? LastPart(objectName) : alias,
                    ObjectName = objectName,
                    Depth = DepthAt(tokens, first, start),
                    Block = tree.BlockOf(tokens[start].Start),
                    Start = tokens[start].Start,
                    AliasStart = aliasStart,
                    AliasLength = aliasLength
                });
                cursor = sourceEnd;
            }
        }

        public int DepthAt(IList<SqlToken> tokens, SqlStatementRange statement, int offset)
        {
            int first = Math.Max(0, statement.FirstToken);
            return DepthAt(tokens, first, offset);
        }

        /// <summary>
        /// Records where each top-level query block starts.  A block starts at a SELECT outside any
        /// parentheses, which covers a second SELECT written without a semicolon and each UNION
        /// branch; sources declared before the first SELECT (UPDATE ... FROM) stay in block -1.
        /// </summary>
        private void CollectBlockStarts()
        {
            if (tokens == null) return;
            int depth = 0;
            for (int i = firstToken; i < tokens.Count; i++)
            {
                SqlToken token = tokens[i];
                if (token.Text == "(")
                {
                    depth++;
                    continue;
                }

                if (token.Text == ")")
                {
                    if (depth > 0) depth--;
                    continue;
                }

                if (depth == 0 && token.Is("SELECT")) blockStarts.Add(token.Start);
            }
        }

        /// <summary>Ordinal of the query block that owns this offset; -1 before the first SELECT.</summary>
        public int BlockOf(int offset)
        {
            int block = -1;
            for (int i = 0; i < blockStarts.Count; i++)
            {
                if (blockStarts[i] > offset) break;
                block = i;
            }

            return block;
        }

        public IReadOnlyList<SqlSource> VisibleAt(int offset)
        {
            int depth = DepthAtOffset(offset);
            return Sources.Where(s => s.Depth <= depth).ToList();
        }

        /// <summary>
        /// Sources declared by the query block that actually owns this offset.  VisibleAt also
        /// returns outer scopes because a correlated subquery may reference them, but a SELECT *
        /// only expands what its own FROM contributes.
        /// </summary>
        public IReadOnlyList<SqlSource> OwnSourcesAt(int offset)
        {
            int depth = DepthAtOffset(offset);
            if (depth < 0) return Sources;
            return Sources.Where(s => s.Depth == depth).ToList();
        }

        public SqlSource Resolve(string name, int offset)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            int depth = DepthAtOffset(offset);
            SqlSource best = null;
            foreach (SqlSource source in Sources)
            {
                if (source.Depth > depth) continue;
                if (!source.Matches(name)) continue;
                if (best == null || source.Depth >= best.Depth) best = source;
            }
            return best;
        }

        /// <summary>Number of visible sources a bare column could belong to (0 = unknown).</summary>
        public int AmbiguityCount(string column, IDictionary<string, List<string>> columnsBySource)
        {
            if (string.IsNullOrWhiteSpace(column) || columnsBySource == null) return 0;
            int count = 0;
            foreach (SqlSource source in Sources)
            {
                if (string.IsNullOrEmpty(source.ObjectName)) continue;
                if (!columnsBySource.TryGetValue(source.ObjectName, out List<string> columns)) continue;
                if (columns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase))) count++;
            }
            return count;
        }

        private int DepthAtOffset(int offset)
        {
            if (tokens == null) return Sources.Count == 0 ? 0 : Sources.Max(s => s.Depth);
            int depth = 0;
            for (int i = firstToken; i < tokens.Count && tokens[i].Start < offset; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")") depth = Math.Max(0, depth - 1);
            }
            return depth;
        }

        private static void Add(SqlScopeTree tree, SqlSource source)
        {
            if (string.IsNullOrEmpty(source.Alias) && string.IsNullOrEmpty(source.ObjectName)) return;
            foreach (SqlSource existing in tree.Sources)
            {
                if (existing.Depth == source.Depth
                    && string.Equals(existing.Alias, source.Alias, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.ObjectName, source.ObjectName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            tree.Sources.Add(source);
        }

        private static void CollectCteNames(IList<SqlToken> tokens, int first, int last, SqlScopeTree tree)
        {
            int index = first + 1;
            while (index <= last)
            {
                if (tokens[index].Kind != SqlTokenKind.Identifier) break;
                tree.CteNames.Add(SqlTokenizer.Unquote(tokens[index]));
                int open = NextSignificant(tokens, index + 1, last);
                if (open < 0 || tokens[open].Text == "(")
                {
                    int close = open >= 0 ? FindMatchingParen(tokens, open, last) : -1;
                    if (close < 0) break;
                    index = NextSignificant(tokens, close + 1, last);
                    if (index >= 0 && tokens[index].Text == ",") { index = NextSignificant(tokens, index + 1, last); continue; }
                }
                break;
            }
        }

        private static void CollectSelectAliases(IList<SqlToken> tokens, int first, int last, SqlScopeTree tree)
        {
            for (int i = first; i <= last; i++)
            {
                if (!tokens[i].Is("AS")) continue;
                int next = NextSignificant(tokens, i + 1, last);
                if (next < 0 || tokens[next].Kind != SqlTokenKind.Identifier) continue;
                string alias = SqlTokenizer.Unquote(tokens[next]);
                if (!tree.SelectAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) tree.SelectAliases.Add(alias);
            }
        }

        private static string ReadQualifiedName(IList<SqlToken> tokens, int index, int last, out int endIndex)
        {
            endIndex = index;
            var parts = new List<string> { SqlTokenizer.Unquote(tokens[index]) };
            int cursor = index;
            while (true)
            {
                int dot = NextSignificant(tokens, cursor + 1, last);
                if (dot < 0 || tokens[dot].Text != ".") break;
                int part = NextSignificant(tokens, dot + 1, last);
                if (part < 0 || tokens[part].Kind != SqlTokenKind.Identifier) break;
                parts.Add(SqlTokenizer.Unquote(tokens[part]));
                cursor = part;
                endIndex = part;
            }
            return string.Join(".", parts);
        }

        private static int FindMatchingParen(IList<SqlToken> tokens, int openIndex, int last)
        {
            int depth = 0;
            for (int i = openIndex; i <= last; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")")
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int NextSignificant(IList<SqlToken> tokens, int index, int last)
        {
            for (int i = index; i <= last && i < tokens.Count; i++)
                if (!tokens[i].IsComment) return i;
            return -1;
        }

        private static int DepthAt(IList<SqlToken> tokens, int first, int index)
        {
            int depth = 0;
            for (int i = first; i < index && i < tokens.Count; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")") depth = Math.Max(0, depth - 1);
            }
            return depth;
        }

        private static string LastPart(string value)
            => string.IsNullOrEmpty(value) ? value : value.Split('.').Last();
    }
}
