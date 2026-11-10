using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MSSQLTool.Completion
{
    internal sealed class CompletionUsageEntry
    {
        public string Scope { get; set; }
        public string Kind { get; set; }
        public string Display { get; set; }
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }

        public string Key => (Scope ?? string.Empty) + "|" + (Kind ?? string.Empty) + "|" + (Display ?? string.Empty);

        /// <summary>Row text for the settings manager list.</summary>
        public string Summary
            => (Display ?? string.Empty) + "  x" + Count
                + (string.IsNullOrWhiteSpace(Scope) ? string.Empty : "  -  " + Scope);
    }

    /// <summary>
    /// Which completions the user actually accepts, persisted next to the settings.  The history is
    /// only a ranking hint (a bounded boost among candidates with the same match quality), so a
    /// wrong entry can never hide an item, and the list is fully manageable from the settings page.
    /// </summary>
    internal static class CompletionUsageStore
    {
        private const int MaxEntries = 400;
        private static readonly object Gate = new object();
        private static List<CompletionUsageEntry> entries;
        private static string filePathOverride;

        internal static string FilePath
            => filePathOverride ?? Path.Combine(AppPaths.Root, "completion-history.json");

        public static IReadOnlyList<CompletionUsageEntry> Snapshot()
        {
            lock (Gate)
            {
                EnsureLoaded();
                return entries.OrderByDescending(e => e.LastUsedUtc).ThenByDescending(e => e.Count).ToList();
            }
        }

        public static void Record(string scope, CompletionItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DisplayText)) return;
            lock (Gate)
            {
                EnsureLoaded();
                string kind = item.Kind.ToString();
                CompletionUsageEntry entry = entries.FirstOrDefault(e => Matches(e, scope, kind, item.DisplayText));
                if (entry == null)
                {
                    entry = new CompletionUsageEntry { Scope = scope ?? string.Empty, Kind = kind, Display = item.DisplayText };
                    entries.Add(entry);
                }

                entry.Count = Math.Min(9999, entry.Count + 1);
                entry.LastUsedUtc = DateTime.UtcNow;
                Trim();
                Save();
            }
        }

        public static bool Remove(string scope, string kind, string display)
        {
            lock (Gate)
            {
                EnsureLoaded();
                int removed = entries.RemoveAll(e => Matches(e, scope, kind, display));
                if (removed == 0) return false;
                Save();
                return true;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                entries = new List<CompletionUsageEntry>();
                Save();
            }
        }

        /// <summary>Ranking hint, 0 when the item has no history in the current scope.</summary>
        public static int Boost(CompletionItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DisplayText)) return 0;
            lock (Gate)
            {
                EnsureLoaded();
                CompletionUsageEntry entry = entries.FirstOrDefault(e => Matches(e, item.ScopeKey, item.Kind.ToString(), item.DisplayText));
                return entry == null ? 0 : ComputeBoost(entry.Count, entry.LastUsedUtc, DateTime.UtcNow);
            }
        }

        /// <summary>
        /// The same hints for a whole candidate list, looked up by key.  Boost() takes the lock and
        /// walks every remembered entry, so a 200-row select list used to pay that walk 200 times —
        /// ten times the cost of building the list itself.  One snapshot per filter pass makes it a
        /// dictionary hit per row.
        /// </summary>
        internal static Dictionary<string, int> BoostMap()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                EnsureLoaded();
                if (entries.Count == 0) return map;
                DateTime now = DateTime.UtcNow;
                foreach (CompletionUsageEntry entry in entries)
                    map[entry.Key] = ComputeBoost(entry.Count, entry.LastUsedUtc, now);
            }
            return map;
        }

        /// <summary>Boost for one item from an already built snapshot; same key rule as Matches().</summary>
        internal static int Boost(Dictionary<string, int> map, CompletionItem item)
        {
            if (map == null || map.Count == 0 || item == null || string.IsNullOrWhiteSpace(item.DisplayText)) return 0;
            int boost;
            return map.TryGetValue(item.UsageKey, out boost) ? boost : 0;
        }

        /// <summary>
        /// Bounded so history can reorder near-ties but never jump a genuinely better match:
        /// frequency contributes at most 8 points, recency at most 4.
        /// </summary>
        internal static int ComputeBoost(int count, DateTime lastUsedUtc, DateTime nowUtc)
        {
            // Cap the count before doubling so a corrupted or saturated counter cannot overflow.
            int frequency = Math.Max(0, Math.Min(4, count));
            int boost = frequency * 2;
            double ageDays = Math.Max(0, (nowUtc - lastUsedUtc).TotalDays);
            if (ageDays <= 1) boost += 4;
            else if (ageDays <= 7) boost += 3;
            else if (ageDays <= 30) boost += 2;
            else boost += 1;
            return boost;
        }

        internal static void UseFileForTests(string path)
        {
            lock (Gate)
            {
                filePathOverride = path;
                entries = null;
            }
        }

        private static bool Matches(CompletionUsageEntry entry, string scope, string kind, string display)
            => string.Equals(entry.Scope ?? string.Empty, scope ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Kind ?? string.Empty, kind ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Display ?? string.Empty, display ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        private static void Trim()
        {
            if (entries.Count <= MaxEntries) return;
            entries = entries.OrderByDescending(e => e.LastUsedUtc).Take(MaxEntries).ToList();
        }

        private static void EnsureLoaded()
        {
            if (entries != null) return;
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonConvert.DeserializeObject<List<CompletionUsageEntry>>(File.ReadAllText(FilePath));
                    if (loaded != null)
                    {
                        entries = loaded.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Display)).ToList();
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // A damaged history costs ranking hints, never the completion feature.
            }

            entries = new List<CompletionUsageEntry>();
        }

        private static void Save()
        {
            try
            {
                string path = FilePath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception)
            {
            }
        }
    }
}
