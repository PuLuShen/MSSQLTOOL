using System;
using System.Collections.Generic;
using MSSQLTool.Model;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// Bounded top-N filter for completion candidates.  The old path built an anonymous object per
    /// candidate, sorted the whole list and only then took the first page; a 20k-object database
    /// paid 20k allocations and a full sort on every keystroke.  This scans once, scores in place,
    /// and keeps only the best page in a min-heap of the page size.
    /// </summary>
    internal static class CompletionFilter
    {
        public static List<CompletionItem> Filter(IReadOnlyList<CompletionItem> candidates, string prefix, int maximumItems)
        {
            string normalizedPrefix = SqlCompletionAnalyzer.Clean(prefix ?? string.Empty).TrimStart('@');
            uint prefixMask = PrefixMask(normalizedPrefix);
            int capacity = Math.Max(1, maximumItems);
            var settings = SettingsManager.GetSqlCompletionSettings();
            Dictionary<string, int> boosts = settings.preferRecentlyUsed ? CompletionUsageStore.BoostMap() : null;
            // Fuzzy edit-distance is reserved for small lists (columns, keywords, routines).
            // Against 20k tables it was 45 ms for one typo-sized prefix and almost never won.
            bool allowFuzzy = candidates.Count <= 4000;
            TopNHeap heap = Scan(candidates, normalizedPrefix, prefixMask, capacity, boosts, allowFuzzy);
            if (heap.Count == 0 && !allowFuzzy && normalizedPrefix.Length >= 3)
                heap = Scan(candidates, normalizedPrefix, 0, capacity, boosts, true);
            return heap.ToSortedList();
        }

        private static TopNHeap Scan(IReadOnlyList<CompletionItem> candidates, string prefix, uint prefixMask,
            int capacity, Dictionary<string, int> boosts, bool allowFuzzy)
        {
            var heap = new TopNHeap(capacity);
            for (int i = 0; i < candidates.Count; i++)
            {
                CompletionItem item = candidates[i];
                if (item == null) continue;
                // One bit test rejects rows that cannot contain the typed letters.  This is the
                // difference between a 20k-object schema feeling instant and spending ~13 ms
                // walking every matcher channel for every table.
                if (prefixMask != 0 && (item.MatchMask & prefixMask) != prefixMask) continue;
                int match = SqlCompletionAnalyzer.MatchItem(item, prefix, allowFuzzy);
                if (match < 0) continue;
                int score = item.Score + match;
                if (boosts != null && boosts.Count > 0) score += CompletionUsageStore.Boost(boosts, item);
                heap.Add(item, score, i);
            }
            return heap;
        }

        private static uint PrefixMask(string prefix)
        {
            uint mask = 0;
            if (string.IsNullOrEmpty(prefix)) return mask;
            for (int i = 0; i < prefix.Length; i++)
            {
                char c = prefix[i];
                if (c >= 'a' && c <= 'z') mask |= 1u << (c - 'a');
                else if (c >= 'A' && c <= 'Z') mask |= 1u << (c - 'A');
                else if (char.IsLetter(c)) return 0; // non-ASCII identifier: do not prefilter
            }
            return mask;
        }

        private sealed class TopNHeap
        {
            private readonly int capacity;
            private readonly CompletionItem[] items;
            private readonly int[] scores;
            private readonly int[] order;
            private int count;

            public TopNHeap(int capacity)
            {
                this.capacity = capacity;
                items = new CompletionItem[capacity];
                scores = new int[capacity];
                order = new int[capacity];
            }

            public int Count => count;

            public void Add(CompletionItem item, int score, int index)
            {
                if (count < capacity)
                {
                    items[count] = item;
                    scores[count] = score;
                    order[count] = index;
                    count++;
                    SiftUp(count - 1);
                    return;
                }

                if (!IsBetter(score, index, item, scores[0], order[0], items[0])) return;
                items[0] = item;
                scores[0] = score;
                order[0] = index;
                SiftDown(0);
            }

            public List<CompletionItem> ToSortedList()
            {
                var result = new List<CompletionItem>(count);
                int[] indices = new int[count];
                for (int i = 0; i < count; i++) indices[i] = i;
                Array.Sort(indices, (left, right) =>
                {
                    int compare = Compare(items[left], scores[left], order[left], items[right], scores[right], order[right]);
                    return compare;
                });
                foreach (int index in indices) result.Add(items[index]);
                return result;
            }

            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    // The root must be the worst kept row, so a better child bubbles up.
                    if (Compare(items[parent], scores[parent], order[parent], items[index], scores[index], order[index]) >= 0) break;
                    Swap(parent, index);
                    index = parent;
                }
            }

            private void SiftDown(int index)
            {
                while (true)
                {
                    int left = index * 2 + 1;
                    if (left >= count) break;
                    int right = left + 1;
                    int worst = left;
                    if (right < count && Compare(items[right], scores[right], order[right], items[left], scores[left], order[left]) > 0)
                        worst = right;
                    if (Compare(items[index], scores[index], order[index], items[worst], scores[worst], order[worst]) >= 0) break;
                    Swap(index, worst);
                    index = worst;
                }
            }

            private void Swap(int left, int right)
            {
                CompletionItem item = items[left]; items[left] = items[right]; items[right] = item;
                int score = scores[left]; scores[left] = scores[right]; scores[right] = score;
                int index = order[left]; order[left] = order[right]; order[right] = index;
            }

            /// <summary>True when the candidate should rank before the current worst kept row.</summary>
            private static bool IsBetter(int score, int index, CompletionItem item, int otherScore, int otherIndex, CompletionItem other)
                => Compare(item, score, index, other, otherScore, otherIndex) < 0;

            /// <summary>Negative when left ranks before right; the same order as score desc, name asc.</summary>
            private static int Compare(CompletionItem left, int leftScore, int leftIndex, CompletionItem right, int rightScore, int rightIndex)
            {
                if (leftScore != rightScore) return leftScore > rightScore ? -1 : 1;
                int name = string.Compare((left?.DisplayText ?? string.Empty), (right?.DisplayText ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase);
                if (name != 0) return name;
                return leftIndex.CompareTo(rightIndex);
            }
        }
    }
}
