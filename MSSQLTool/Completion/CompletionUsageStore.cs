using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace MSSQLTool.Completion
{
    internal static class CompletionUsageStore
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// Where the learned ranking is kept.  Resolved on every access so a changed data folder
        /// takes effect without restarting SSMS.
        /// </summary>
        private static string FilePath => AppPaths.CompletionUsageFile;

        private static Dictionary<string, int> counts = Load();
        private static int saveGeneration;

        public static int GetScore(CompletionItem item)
        {
            if (item == null) return 0;
            lock (Gate) return counts.TryGetValue(Key(item), out int count) ? Math.Min(40, count * 2) : 0;
        }

        /// <summary>Re-reads the ranking from the current data folder.</summary>
        internal static void Reload()
        {
            lock (Gate) counts = Load();
        }

        public static void Record(CompletionItem item)
        {
            if (item == null) return;
            Dictionary<string, int> snapshot;
            lock (Gate)
            {
                string key = Key(item);
                counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
                snapshot = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
            }
            int generation = Interlocked.Increment(ref saveGeneration);
            Task.Run(() => Save(snapshot, generation));
        }

        private static string Key(CompletionItem item) => (item.ScopeKey ?? string.Empty) + "|" + item.Kind + "|" + item.DisplayText;
        private static Dictionary<string, int> Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(FilePath))
                    ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { FeatureDiagnostics.Report("SQL Completion Usage", "Could not load local completion ranking", ex); }
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Save(Dictionary<string, int> value, int generation)
        {
            // The snapshot passed in is an immutable copy, so no lock is held
            // during file I/O — a slow disk must not block ranking lookups.
            try
            {
                if (generation != Volatile.Read(ref saveGeneration)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string temporary = FilePath + "." + generation + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(value));
                lock (Gate)
                {
                    if (generation != Volatile.Read(ref saveGeneration))
                    {
                        try { File.Delete(temporary); } catch { }
                        return;
                    }
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                    else File.Move(temporary, FilePath);
                }
            }
            catch (Exception ex) { FeatureDiagnostics.Report("SQL Completion Usage", "Could not save local completion ranking", ex); }
        }
    }
}
