using System;
using System.Collections.Generic;

namespace MSSQLTool.Model
{
    internal sealed class SelectIntoReference
    {
        public int Index;
        public string Name;
        public string SelectList;
    }

    /// <summary>
    /// Linear replacement for the old <c>SELECT ... INTO #temp</c> regular expression.
    /// The regex used a lazy <c>.*?</c> between SELECT and INTO, which made the engine
    /// retry at every character: 315 ms for 22 KB and 3.2 s for 66 KB.  This scan walks
    /// the token list once, so the cost is proportional to the document size.
    /// </summary>
    internal static class SqlSelectIntoScanner
    {
        // Backward scan stops at a statement boundary.  The cap keeps a pathological
        // statement (thousands of tokens without a semicolon) from becoming quadratic.
        private const int MaxBackwardTokens = 4000;

        public static List<SelectIntoReference> Find(string maskedSql)
        {
            var result = new List<SelectIntoReference>();
            if (string.IsNullOrEmpty(maskedSql)) return result;

            List<SqlToken> tokens = SqlTokenizer.Tokenize(maskedSql);
            for (int i = 0; i < tokens.Count; i++)
            {
                SqlToken token = tokens[i];
                if (!token.IsWordLike || !token.Is("INTO")) continue;

                int nameIndex = NextSignificant(tokens, i + 1);
                if (nameIndex < 0) continue;
                string name = SqlTokenizer.Unquote(tokens[nameIndex]);
                if (!name.StartsWith("#", StringComparison.Ordinal)) continue;

                int beforeIndex = PreviousSignificant(tokens, i - 1);
                if (beforeIndex >= 0)
                {
                    SqlToken before = tokens[beforeIndex];
                    if (before.Is("INSERT") || before.Is("MERGE") || before.Is("BULK")) continue;
                }

                int selectIndex = FindOwningSelect(tokens, i);
                if (selectIndex < 0) continue;

                int listStart = tokens[selectIndex].End;
                int listLength = token.Start - listStart;
                if (listLength <= 0) continue;

                string selectList = maskedSql.Substring(listStart, listLength).Trim();
                if (selectList.Length == 0) continue;

                result.Add(new SelectIntoReference { Index = tokens[selectIndex].Start, Name = name, SelectList = selectList });
            }

            return result;
        }

        private static int FindOwningSelect(List<SqlToken> tokens, int intoIndex)
        {
            int depth = 0;
            int scanned = 0;
            for (int i = intoIndex - 1; i >= 0 && scanned < MaxBackwardTokens; i--, scanned++)
            {
                SqlToken token = tokens[i];
                if (token.IsComment) continue;
                if (token.Text == ")") { depth++; continue; }
                if (token.Text == "(")
                {
                    if (depth > 0) { depth--; continue; }
                    return -1;
                }
                if (depth > 0) continue;
                if (token.Text == ";") return -1;
                if (token.Is("GO") || token.Is("INSERT") || token.Is("UPDATE") || token.Is("DELETE")
                    || token.Is("MERGE") || token.Is("UNION") || token.Is("EXCEPT") || token.Is("INTERSECT")
                    || token.Is("CREATE") || token.Is("ALTER") || token.Is("DROP") || token.Is("DECLARE"))
                    return -1;
                if (token.Is("SELECT")) return i;
            }
            return -1;
        }

        private static int NextSignificant(List<SqlToken> tokens, int index)
        {
            for (int i = index; i < tokens.Count; i++)
                if (!tokens[i].IsComment) return i;
            return -1;
        }

        private static int PreviousSignificant(List<SqlToken> tokens, int index)
        {
            for (int i = index; i >= 0; i--)
                if (!tokens[i].IsComment) return i;
            return -1;
        }
    }
}
