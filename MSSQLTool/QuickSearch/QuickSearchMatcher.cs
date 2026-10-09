using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MSSQLTool
{
    /// <summary>
    /// Quality of a single text hit, ordered from best to worst.
    /// </summary>
    internal enum QuickSearchMatchKind
    {
        None = 0,
        Exact = 1,
        Prefix = 2,
        Contains = 3,
        Fuzzy = 4
    }

    /// <summary>
    /// A single hit inside a text. <see cref="Accepted"/> is false when the range is
    /// only a "pre-filter" range (the database LIKE matched the anchor but the client
    /// side matcher did not accept the word); such ranges are still useful so the
    /// preview and the editor highlight can point at something.
    /// </summary>
    internal struct QuickSearchTextMatch
    {
        public QuickSearchMatchKind Kind;
        public int Index;
        public int Length;
        public bool Accepted;

        public bool HasRange => Length > 0 && Index >= 0;

        public static QuickSearchTextMatch Create(QuickSearchMatchKind kind, int index, int length, bool accepted)
        {
            return new QuickSearchTextMatch { Kind = kind, Index = index, Length = length, Accepted = accepted };
        }
    }

    /// <summary>
    /// Token (identifier word) with its offset inside the scanned text.
    /// </summary>
    internal struct QuickSearchToken
    {
        public int Start;
        public int Length;
        public string Text;
    }

    /// <summary>
    /// One search term plus the matching rules selected by the user. The object is built
    /// once per search and reused for every row, so all pattern/regex work is done here.
    ///
    /// Matching modes:
    ///  - wildcards: raw SQL LIKE pattern semantics (%, _), mutually exclusive with fuzzy.
    ///  - whole words without fuzzy: legacy behaviour, the term must not touch other
    ///    identifier characters (regex word boundary over identifier characters).
    ///  - fuzzy: case/separator insensitive comparison with bounded edit distance,
    ///    sub-sequence matching and identifier-token matching, e.g. "uspGetOrd" finds
    ///    "usp_GetOrder".
    ///  - whole words with fuzzy: identifier-token matching, so "get order" finds
    ///    "usp_GetOrder" but "getord" does not.
    /// </summary>
    internal sealed class QuickSearchSearchOptions
    {
        private const int MaxWordLength = 120;
        private const int MaxEditDistance = 3;
        private const int MaxLevenshteinInput = 200;

        private static readonly RegexOptions MatchOptions = RegexOptions.IgnoreCase;

        private readonly Regex wildcardRegex;
        private readonly Regex wholeWordRegex;
        private readonly Regex plainRegex;
        private readonly Regex anchorRegex;
        private readonly int fuzzyMaxDistance;

        public string Term { get; }
        public string NormalizedTerm { get; }
        public string Anchor { get; }
        public string AnchorNormalized { get; }
        public bool WholeWord { get; }
        public bool UseWildcards { get; }
        public bool Fuzzy { get; }

        public bool HasTerm => Term.Length > 0;

        public QuickSearchSearchOptions(string term, bool wholeWord, bool useWildcards, bool fuzzy)
        {
            Term = (term ?? string.Empty).Trim();
            WholeWord = wholeWord;
            UseWildcards = useWildcards;
            // Wildcards describe an explicit pattern, so fuzzy cannot be combined with them.
            Fuzzy = fuzzy && !useWildcards;

            NormalizedTerm = Normalize(Term);
            Anchor = BuildAnchor(Term);
            AnchorNormalized = Normalize(Anchor);

            int length = NormalizedTerm.Length;
            fuzzyMaxDistance = length <= 3 ? 1 : Math.Min(MaxEditDistance, Math.Max(1, length / 3));

            if (HasTerm)
            {
                plainRegex = new Regex(Regex.Escape(Term), MatchOptions);
                anchorRegex = Anchor.Length > 0 ? new Regex(Regex.Escape(Anchor), MatchOptions) : null;
                wholeWordRegex = new Regex(
                    @"(?<![\p{L}\p{N}_])" + Regex.Escape(Term) + @"(?![\p{L}\p{N}_])",
                    MatchOptions);
            }

            if (UseWildcards && HasTerm)
            {
                wildcardRegex = new Regex(LikeToRegex(Term), MatchOptions);
            }
        }

        /// <summary>
        /// LIKE pattern used to pre-filter rows on the server. In fuzzy mode the anchor
        /// (the longest identifier word of the term) is used instead of the whole term,
        /// which keeps the server side cheap while still returning every candidate the
        /// client side matcher can accept, because every accepted match contains the anchor.
        /// </summary>
        public string BuildLikePattern()
        {
            if (!HasTerm)
            {
                return "%";
            }

            if (UseWildcards)
            {
                return EscapeLike(Term, true);
            }

            string anchor = Fuzzy && Anchor.Length > 0 ? Anchor : Term;
            return "%" + EscapeLike(anchor, false) + "%";
        }

        /// <summary>
        /// All hits of the term inside <paramref name="text"/>, ordered by offset.
        /// </summary>
        public List<QuickSearchTextMatch> Scan(string text, int maxMatches)
        {
            var matches = new List<QuickSearchTextMatch>();
            if (string.IsNullOrEmpty(text) || !HasTerm || maxMatches <= 0)
            {
                return matches;
            }

            if (UseWildcards)
            {
                foreach (Match match in wildcardRegex.Matches(text))
                {
                    if (match.Length <= 0) continue;
                    matches.Add(QuickSearchTextMatch.Create(
                        ClassifyRange(text, match.Index, match.Length), match.Index, match.Length, true));
                    if (matches.Count >= maxMatches) break;
                }

                return matches;
            }

            Regex directRegex = WholeWord && wholeWordRegex != null ? wholeWordRegex : plainRegex;
            foreach (Match match in directRegex.Matches(text))
            {
                if (match.Length <= 0) continue;
                matches.Add(QuickSearchTextMatch.Create(
                    ClassifyRange(text, match.Index, match.Length), match.Index, match.Length, true));
                if (matches.Count >= maxMatches) break;
            }

            if (!Fuzzy)
            {
                return matches;
            }

            // Fuzzy: also look at identifier words that contain the anchor. Ranges already
            // reported by the direct pass are skipped so highlights never double up.
            foreach (int occurrence in AnchorOccurrences(text, maxMatches))
            {
                if (IsCovered(matches, occurrence)) continue;

                int wordStart = occurrence;
                int wordEnd = occurrence + Anchor.Length;
                ExpandWord(text, ref wordStart, ref wordEnd);

                int wordLength = wordEnd - wordStart;
                if (wordLength <= 0 || IsRangeCovered(matches, wordStart, wordEnd)) continue;

                string word = text.Substring(wordStart, wordLength);
                int innerIndex;
                int innerLength;
                QuickSearchMatchKind kind = MatchWord(word, out innerIndex, out innerLength);
                if (kind == QuickSearchMatchKind.None)
                {
                    // Range only: the server pre-filter matched the anchor but the client
                    // matcher rejected the word. Callers decide whether that is enough.
                    matches.Add(QuickSearchTextMatch.Create(QuickSearchMatchKind.None, wordStart, wordLength, false));
                }
                else
                {
                    matches.Add(QuickSearchTextMatch.Create(kind, wordStart + innerIndex, innerLength, true));
                }

                if (matches.Count >= maxMatches) break;
            }

            return matches;
        }

        /// <summary>
        /// Best match for a single value (object name, column name, parameter name, ...).
        /// </summary>
        public QuickSearchTextMatch MatchValue(string value, bool requireAcceptance = true)
        {
            QuickSearchTextMatch best = default(QuickSearchTextMatch);
            QuickSearchTextMatch fallback = default(QuickSearchTextMatch);
            bool hasFallback = false;

            foreach (QuickSearchTextMatch match in Scan(value, 64))
            {
                if (!match.Accepted)
                {
                    if (!hasFallback) { fallback = match; hasFallback = true; }
                    continue;
                }

                if (!best.Accepted || match.Kind < best.Kind)
                {
                    best = match;
                }
            }

            if (best.Accepted) return best;
            return requireAcceptance || !hasFallback ? default(QuickSearchTextMatch) : fallback;
        }

        public static int TierOf(QuickSearchMatchKind kind)
        {
            switch (kind)
            {
                case QuickSearchMatchKind.Exact: return 0;
                case QuickSearchMatchKind.Prefix: return 1;
                case QuickSearchMatchKind.Contains: return 2;
                case QuickSearchMatchKind.Fuzzy: return 3;
                default: return int.MaxValue;
            }
        }

        private static bool IsCovered(List<QuickSearchTextMatch> matches, int index)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i].Index <= index && index < matches[i].Index + matches[i].Length)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRangeCovered(List<QuickSearchTextMatch> matches, int start, int end)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                int matchEnd = matches[i].Index + matches[i].Length;
                if (matches[i].Index <= start && end <= matchEnd)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsIdentifierChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$';
        }

        private void ExpandWord(string text, ref int start, ref int end)
        {
            while (start > 0 && IsIdentifierChar(text[start - 1])) start--;
            while (end < text.Length && IsIdentifierChar(text[end])) end++;

            // Guard against pathological single-character "words" (for example long base64
            // blobs inside a module definition) so highlighting stays cheap.
            if (end - start > MaxWordLength)
            {
                int centre = start + (end - start) / 2;
                start = Math.Max(0, centre - MaxWordLength / 2);
                end = Math.Min(text.Length, start + MaxWordLength);
            }
        }

        private List<int> AnchorOccurrences(string text, int maxOccurrences)
        {
            var occurrences = new List<int>();
            if (anchorRegex == null || Anchor.Length == 0) return occurrences;

            foreach (Match match in anchorRegex.Matches(text))
            {
                if (match.Length <= 0) continue;
                occurrences.Add(match.Index);
                if (occurrences.Count >= Math.Max(1, maxOccurrences)) break;
            }

            return occurrences;
        }

        /// <summary>
        /// Follows the object name ladder for one identifier word.
        /// </summary>
        private QuickSearchMatchKind MatchWord(string word, out int index, out int length)
        {
            index = 0;
            length = word.Length;

            if (string.IsNullOrEmpty(word)) return QuickSearchMatchKind.None;

            if (WholeWord)
            {
                // Token matching keeps "get order" -> "usp_GetOrder" working while rejecting
                // partial tokens such as "getord" -> "GetOrder".
                if (NormalizedTerm.Length > 0 && string.Equals(Normalize(word), NormalizedTerm, StringComparison.Ordinal))
                {
                    return QuickSearchMatchKind.Exact;
                }

                if (TryTokenMatch(word, true, out index, out length))
                {
                    return ClassifyRange(word, index, length);
                }

                index = 0;
                length = word.Length;
                return QuickSearchMatchKind.None;
            }

            string normalized = Normalize(word, out int[] rawMap);
            if (NormalizedTerm.Length == 0 || normalized.Length == 0) return QuickSearchMatchKind.None;

            if (string.Equals(normalized, NormalizedTerm, StringComparison.Ordinal))
            {
                index = 0;
                length = word.Length;
                return QuickSearchMatchKind.Exact;
            }

            if (normalized.StartsWith(NormalizedTerm, StringComparison.Ordinal))
            {
                index = rawMap[0];
                length = RawEnd(rawMap, NormalizedTerm.Length) - index;
                return QuickSearchMatchKind.Prefix;
            }

            int found = normalized.IndexOf(NormalizedTerm, StringComparison.Ordinal);
            if (found >= 0)
            {
                index = rawMap[found];
                length = RawEnd(rawMap, found + NormalizedTerm.Length) - index;
                return QuickSearchMatchKind.Contains;
            }

            if (NormalizedTerm.Length >= 3)
            {
                int sequenceStart;
                int sequenceEnd;
                if (TrySubsequence(normalized, NormalizedTerm, NormalizedTerm.Length * 3 + 2, out sequenceStart, out sequenceEnd))
                {
                    index = rawMap[sequenceStart];
                    length = rawMap[sequenceEnd] + 1 - index;
                    return QuickSearchMatchKind.Fuzzy;
                }
            }

            if (word.Length <= MaxLevenshteinInput && fuzzyMaxDistance > 0)
            {
                if (BoundedLevenshtein(normalized, NormalizedTerm, fuzzyMaxDistance) >= 0)
                {
                    index = 0;
                    length = word.Length;
                    return QuickSearchMatchKind.Fuzzy;
                }
            }

            if (TryTokenMatch(word, false, out index, out length))
            {
                return ClassifyRange(word, index, length);
            }

            index = 0;
            length = word.Length;
            return QuickSearchMatchKind.None;
        }

        /// <summary>
        /// Matches the term's identifier tokens against the candidate's tokens, in order.
        /// </summary>
        private bool TryTokenMatch(string value, bool exactTokens, out int index, out int length)
        {
            index = 0;
            length = 0;

            List<QuickSearchToken> needleTokens = Tokenize(Term);
            if (needleTokens.Count == 0) return false;

            List<QuickSearchToken> candidateTokens = Tokenize(value);
            if (candidateTokens.Count < needleTokens.Count) return false;

            int candidateIndex = 0;
            int firstStart = -1;
            int lastEnd = -1;

            for (int needleIndex = 0; needleIndex < needleTokens.Count; needleIndex++)
            {
                string needle = needleTokens[needleIndex].Text;
                bool matched = false;

                while (candidateIndex < candidateTokens.Count)
                {
                    QuickSearchToken candidate = candidateTokens[candidateIndex];
                    candidateIndex++;

                    if (TokenMatches(candidate.Text, needle, exactTokens))
                    {
                        if (firstStart < 0) firstStart = candidate.Start;
                        lastEnd = candidate.Start + candidate.Length;
                        matched = true;
                        break;
                    }
                }

                if (!matched) return false;
            }

            if (firstStart < 0 || lastEnd <= firstStart) return false;

            index = firstStart;
            length = lastEnd - firstStart;
            return true;
        }

        private bool TokenMatches(string candidateToken, string needleToken, bool exactTokens)
        {
            if (string.Equals(candidateToken, needleToken, StringComparison.Ordinal)) return true;
            if (exactTokens) return false;
            if (needleToken.Length < 2) return false;

            if (candidateToken.StartsWith(needleToken, StringComparison.Ordinal)) return true;

            int distance = needleToken.Length <= 3 ? 1 : Math.Min(MaxEditDistance, Math.Max(1, needleToken.Length / 3));
            if (BoundedLevenshtein(candidateToken, needleToken, distance) >= 0) return true;

            if (needleToken.Length >= 3)
            {
                int start;
                int end;
                return TrySubsequence(candidateToken, needleToken, needleToken.Length * 3 + 2, out start, out end);
            }

            return false;
        }

        private static QuickSearchMatchKind ClassifyRange(string text, int index, int length)
        {
            if (index == 0 && length >= text.Length) return QuickSearchMatchKind.Exact;
            if (index == 0) return QuickSearchMatchKind.Prefix;
            return QuickSearchMatchKind.Contains;
        }

        private static string EscapeLike(string text, bool preserveWildcards)
        {
            string escaped = text.Replace("!", "!!");
            if (preserveWildcards) return escaped;

            return escaped.Replace("%", "!%").Replace("_", "!_").Replace("[", "![");
        }

        private static string LikeToRegex(string pattern)
        {
            // Mirrors SQL LIKE with ESCAPE '!' so wildcard searches match exactly what the server
            // side query matched (% = any run, _ = one character, ! escapes the next character).
            var builder = new StringBuilder(pattern.Length + 8);

            for (int i = 0; i < pattern.Length; i++)
            {
                char character = pattern[i];
                if (character == '!' && i + 1 < pattern.Length)
                {
                    builder.Append(Regex.Escape(pattern[i + 1].ToString()));
                    i++;
                    continue;
                }

                switch (character)
                {
                    case '%':
                        builder.Append(".*");
                        break;
                    case '_':
                        builder.Append('.');
                        break;
                    default:
                        builder.Append(Regex.Escape(character.ToString()));
                        break;
                }
            }

            return builder.ToString();
        }

        private static int RawEnd(int[] rawMap, int normalizedCount)
        {
            int index = Math.Min(normalizedCount, rawMap.Length - 1);
            if (index <= 0) return 0;

            return rawMap[index - 1] + 1;
        }

        /// <summary>
        /// Lowercases and drops every separator so "usp_GetOrder" and "uspGetOrder" compare
        /// equal. <paramref name="rawMap"/> maps a normalized index back to the source index.
        /// </summary>
        internal static string Normalize(string value, out int[] rawMap)
        {
            if (string.IsNullOrEmpty(value))
            {
                rawMap = new int[1];
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            var map = new int[value.Length + 1];

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (!char.IsLetterOrDigit(character)) continue;

                map[builder.Length] = i;
                builder.Append(char.ToLowerInvariant(character));
            }

            map[builder.Length] = value.Length;
            rawMap = map;
            return builder.ToString();
        }

        internal static string Normalize(string value)
        {
            return Normalize(value, out int[] _);
        }

        /// <summary>
        /// Anchor used both as the server side pre-filter and to seed the fuzzy word scan.
        /// It is always a contiguous substring of the term, which guarantees that the LIKE
        /// pre-filter returns every row a literal (non fuzzy) match would return.
        ///
        /// - Multi word identifiers keep their longest word ("uspGetOrd" -> "ord"), which makes
        ///   camel case, snake case and separator insensitive matching possible.
        /// - A single long word keeps only a short prefix ("custmer" -> "cust") so the client side
        ///   edit distance / sub-sequence rules can still find the word it was meant to be.
        /// </summary>
        internal static string BuildAnchor(string term)
        {
            List<QuickSearchToken> tokens = Tokenize(term);
            string best = string.Empty;

            foreach (QuickSearchToken token in tokens)
            {
                if (token.Text.Length >= best.Length)
                {
                    best = token.Text;
                }
            }

            if (best.Length == 0)
            {
                return term ?? string.Empty;
            }

            if (tokens.Count <= 1 && best.Length > 5)
            {
                return best.Substring(0, 4);
            }

            if (best.Length < 3)
            {
                // Too short to be a useful anchor: fall back to the whole term so short searches
                // stay literal instead of matching half the database.
                return term ?? string.Empty;
            }

            return best.Length > 12 ? best.Substring(0, 12) : best;
        }

        /// <summary>
        /// Splits an identifier on separators and camel case boundaries.
        /// </summary>
        internal static List<QuickSearchToken> Tokenize(string value)
        {
            var tokens = new List<QuickSearchToken>();
            if (string.IsNullOrEmpty(value)) return tokens;

            var builder = new StringBuilder(value.Length);
            int start = -1;

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (char.IsLetterOrDigit(character))
                {
                    bool newToken = builder.Length > 0 && char.IsUpper(character) && !char.IsUpper(value[i - 1]);
                    if (newToken)
                    {
                        tokens.Add(new QuickSearchToken { Start = start, Length = builder.Length, Text = builder.ToString() });
                        builder.Clear();
                        start = -1;
                    }

                    if (builder.Length == 0) start = i;
                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (builder.Length > 0)
                {
                    tokens.Add(new QuickSearchToken { Start = start, Length = builder.Length, Text = builder.ToString() });
                    builder.Clear();
                    start = -1;
                }
            }

            if (builder.Length > 0)
            {
                tokens.Add(new QuickSearchToken { Start = start, Length = builder.Length, Text = builder.ToString() });
            }

            return tokens;
        }

        internal static bool TrySubsequence(string candidate, string needle, int maxSpan, out int start, out int end)
        {
            start = -1;
            end = -1;

            int candidateIndex = 0;
            for (int needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                char target = needle[needleIndex];
                while (candidateIndex < candidate.Length && candidate[candidateIndex] != target) candidateIndex++;
                if (candidateIndex >= candidate.Length) return false;

                if (start < 0) start = candidateIndex;
                end = candidateIndex;
                candidateIndex++;
            }

            return (end - start + 1) <= maxSpan;
        }

        /// <summary>
        /// Levenshtein distance with an early exit; returns -1 when the distance exceeds
        /// <paramref name="maxDistance"/>.
        /// </summary>
        internal static int BoundedLevenshtein(string value, string target, int maxDistance)
        {
            if (maxDistance < 0) return -1;
            if (value == null) value = string.Empty;
            if (target == null) target = string.Empty;

            int length = value.Length;
            int targetLength = target.Length;
            if (Math.Abs(length - targetLength) > maxDistance) return -1;
            if (length == 0) return targetLength <= maxDistance ? targetLength : -1;
            if (targetLength == 0) return length <= maxDistance ? length : -1;

            var previous = new int[targetLength + 1];
            var current = new int[targetLength + 1];

            for (int j = 0; j <= targetLength; j++) previous[j] = j;

            for (int i = 1; i <= length; i++)
            {
                current[0] = i;
                int rowMinimum = current[0];
                char valueChar = value[i - 1];

                for (int j = 1; j <= targetLength; j++)
                {
                    int cost = valueChar == target[j - 1] ? 0 : 1;
                    int cell = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                    current[j] = cell;
                    if (cell < rowMinimum) rowMinimum = cell;
                }

                if (rowMinimum > maxDistance) return -1;

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[targetLength] <= maxDistance ? previous[targetLength] : -1;
        }
    }
}
