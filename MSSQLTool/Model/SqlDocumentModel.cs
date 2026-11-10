using System;
using System.Collections.Generic;
using System.Linq;

namespace MSSQLTool.Model
{
    /// <summary>
    /// Immutable per-document index shared by completion, highlight and code analysis:
    /// tokens, statement ranges and scope trees.  The full AST is never retained (a 300 KB
    /// document would cost about 20 MB), only these compact arrays.
    /// </summary>
    internal sealed class SqlDocumentModel
    {
        private const int CacheCapacity = 6;
        private static readonly object CacheGate = new object();
        private static readonly List<CacheEntry> Cache = new List<CacheEntry>();

        private readonly Dictionary<SqlStatementRange, SqlScopeTree> scopes = new Dictionary<SqlStatementRange, SqlScopeTree>();

        public string Text { get; private set; } = string.Empty;
        public int Version { get; private set; }
        public List<SqlToken> Tokens { get; private set; } = new List<SqlToken>();
        public SqlStatementIndex Statements { get; private set; } = new SqlStatementIndex();

        public static SqlDocumentModel Create(string text)
        {
            string value = text ?? string.Empty;
            var model = new SqlDocumentModel
            {
                Text = value,
                Version = Hash(value),
                Tokens = SqlTokenizer.Tokenize(value)
            };
            model.Statements = SqlStatementIndex.Build(value, model.Tokens);
            return model;
        }

        /// <summary>Returns a cached model when the text is unchanged; otherwise builds one.</summary>
        public static SqlDocumentModel Get(string text)
        {
            string value = text ?? string.Empty;
            int hash = Hash(value);
            lock (CacheGate)
            {
                for (int i = Cache.Count - 1; i >= 0; i--)
                {
                    CacheEntry entry = Cache[i];
                    if (entry.Version == hash && string.Equals(entry.Model.Text, value, StringComparison.Ordinal))
                    {
                        if (i != Cache.Count - 1)
                        {
                            Cache.RemoveAt(i);
                            Cache.Add(entry);
                        }
                        return entry.Model;
                    }
                }
            }

            SqlDocumentModel model = Create(value);
            lock (CacheGate)
            {
                Cache.Add(new CacheEntry { Version = model.Version, Model = model });
                while (Cache.Count > CacheCapacity) Cache.RemoveAt(0);
            }
            return model;
        }

        public SqlScopeTree ScopeOf(SqlStatementRange statement)
        {
            if (statement == null) return SqlScopeTree.Build(Text, Tokens, null);
            lock (scopes)
            {
                if (scopes.TryGetValue(statement, out SqlScopeTree scope)) return scope;
                scope = SqlScopeTree.Build(Text, Tokens, statement);
                scopes[statement] = scope;
                return scope;
            }
        }

        public SqlStatementRange StatementAt(int offset) => Statements.At(offset);

        public static int Hash(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < value.Length; i++)
                    hash = (hash ^ value[i]) * 16777619;
                return hash;
            }
        }

        private sealed class CacheEntry
        {
            public int Version;
            public SqlDocumentModel Model;
        }
    }

    internal static class TextPositions
    {
        public static void ToLineColumn(string text, int offset, out int line, out int column)
        {
            line = 0;
            column = 0;
            if (string.IsNullOrEmpty(text)) return;
            int limit = Math.Max(0, Math.Min(offset, text.Length));
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n') { line++; column = 0; }
                else if (text[i] != '\r') column++;
            }
        }

        public static int ToOffset(string text, int line, int column)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int current = 0, offset = 0;
            while (current < line && offset < text.Length)
            {
                int newline = text.IndexOf('\n', offset);
                if (newline < 0) return text.Length;
                offset = newline + 1;
                current++;
            }
            return Math.Min(text.Length, offset + Math.Max(0, column));
        }
    }
}
