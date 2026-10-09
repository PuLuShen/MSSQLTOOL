using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MSSQLTool.Completion
{
    internal sealed class SqlSymbolResolution
    {
        public string Symbol { get; set; }
        public string ObjectName { get; set; }
        public int LocalDefinitionOffset { get; set; } = -1;
    }

    internal static class SqlSymbolResolver
    {
        public static SqlSymbolResolution Resolve(string sql, int caretOffset, MetadataSnapshot metadata)
        {
            string symbol = ReadMultipartIdentifier(sql, caretOffset);
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            return ResolveFromSymbol(sql, caretOffset, symbol, metadata);
        }

        /// <summary>
        /// Second phase of symbol resolution, once the identifier text under the
        /// caret is known. F12 runs this on a background thread: it may parse
        /// the whole document again (semantic model), which must not run on the
        /// UI thread while the keyboard hook is dispatching.
        /// </summary>
        public static SqlSymbolResolution ResolveFromSymbol(string sql, int caretOffset, string symbol, MetadataSnapshot metadata)
        {
            string clean = Clean(symbol);
            string last = clean.Split('.').LastOrDefault();
            int local = FindLocalDefinition(sql, last);
            if (local >= 0) return new SqlSymbolResolution { Symbol = symbol, ObjectName = clean, LocalDefinitionOffset = local };

            SqlSemanticModel semantic = SqlSemanticModel.Create(sql, caretOffset);
            string[] parts = clean.Split('.');
            if (parts.Length >= 2 && semantic.Aliases.TryGetValue(parts[parts.Length - 2], out string aliased))
                return new SqlSymbolResolution { Symbol = symbol, ObjectName = aliased };
            if (semantic.Aliases.TryGetValue(last, out string direct))
                return new SqlSymbolResolution { Symbol = symbol, ObjectName = direct };

            metadata = metadata ?? MetadataSnapshot.Empty;
            List<DatabaseObjectMetadata> directMatches = metadata.Objects.Where(o => Matches(o.QualifiedName, clean) || Matches(o.Name, clean)).ToList();
            if (directMatches.Count == 1) return new SqlSymbolResolution { Symbol = symbol, ObjectName = directMatches[0].QualifiedName };

            List<DatabaseObjectMetadata> columnOwners = new List<DatabaseObjectMetadata>();
            foreach (string source in semantic.Aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DatabaseObjectMetadata item = metadata.Objects.FirstOrDefault(o => Matches(o.QualifiedName, source) || Matches(o.Name, source));
                if (item != null && item.Columns.Any(c => string.Equals(c.Name, last, StringComparison.OrdinalIgnoreCase))) columnOwners.Add(item);
            }
            if (columnOwners.Count == 1) return new SqlSymbolResolution { Symbol = symbol, ObjectName = columnOwners[0].QualifiedName };
            return new SqlSymbolResolution { Symbol = symbol, ObjectName = clean };
        }

        internal static string ReadMultipartIdentifier(string sql, int caretOffset)
        {
            if (string.IsNullOrEmpty(sql)) return string.Empty;
            try
            {
                var parser = new TSql170Parser(true);
                IList<TSqlParserToken> tokens = parser.GetTokenStream(new StringReader(sql), out IList<ParseError> _);
                int index = -1;
                for (int i = 0; i < tokens.Count; i++)
                {
                    int end = tokens[i].Offset + (tokens[i].Text?.Length ?? 0);
                    if (tokens[i].Offset <= caretOffset && caretOffset <= end && IsIdentifier(tokens[i])) { index = i; break; }
                    if (caretOffset == tokens[i].Offset - 1 && IsIdentifier(tokens[i])) { index = i; break; }
                }
                if (index < 0) return string.Empty;
                int first = index, last = index;
                while (first >= 2 && tokens[first - 1].TokenType == TSqlTokenType.Dot && IsIdentifier(tokens[first - 2])) first -= 2;
                while (last + 2 < tokens.Count && tokens[last + 1].TokenType == TSqlTokenType.Dot && IsIdentifier(tokens[last + 2])) last += 2;
                return string.Concat(tokens.Skip(first).Take(last - first + 1).Select(t => t.Text));
            }
            catch { return string.Empty; }
        }

        private static int FindLocalDefinition(string sql, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string masked = SqlTextContext.MaskCommentsAndStrings(sql ?? string.Empty);
            string escaped = Regex.Escape(name);
            foreach (string pattern in new[]
            {
                @"(?:\bWITH|,)\s*(?<name>\[?" + escaped + @"\]?)\s+(?:\([^)]*\)\s*)?AS\s*\(",
                @"\bCREATE\s+TABLE\s+(?<name>\[?" + escaped + @"\]?)\b",
                @"\bDECLARE\s+(?<name>@" + escaped.TrimStart('@') + @")\s+TABLE\b"
            })
            {
                Match match = Regex.Match(masked, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success) return match.Groups["name"].Index;
            }
            return -1;
        }

        private static bool IsIdentifier(TSqlParserToken token) => token.TokenType == TSqlTokenType.Identifier
            || token.TokenType == TSqlTokenType.QuotedIdentifier || token.TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier;
        private static string Clean(string value) => DatabaseIdentifier.NormalizeSqlServer(value).Trim('"');
        private static bool Matches(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Clean(a).Split('.').LastOrDefault(), Clean(b).Split('.').LastOrDefault(), StringComparison.OrdinalIgnoreCase);
    }
}
