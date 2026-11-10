using System;
using System.IO;

namespace MSSQLTool
{
    /// <summary>
    /// Renames the history databases that shipped under the old names.  The execution history used
    /// to be query-history.db and now is execution-history.db; the query text history used to be
    /// query-text-history.db and now owns query-history.db.  The execution file is moved first so
    /// the second rename can never read the execution data as query text.
    /// </summary>
    internal static class HistoryDatabaseMigration
    {
        private static readonly object SyncRoot = new object();
        private static bool completed;

        internal static void EnsureMigrated()
        {
            lock (SyncRoot)
            {
                if (completed) return;
                completed = true;

                try
                {
                    string folder = AppPaths.QueryHistoryFolder;
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

                    MoveIfMissing(Path.Combine(folder, "query-history.db"), Path.Combine(folder, "execution-history.db"));
                    MoveIfMissing(Path.Combine(folder, "query-text-history.db"), Path.Combine(folder, "query-history.db"));
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not migrate the history database file names.");
                }
            }
        }

        private static void MoveIfMissing(string oldPath, string newPath)
        {
            if (!File.Exists(oldPath) || File.Exists(newPath)) return;

            File.Move(oldPath, newPath);

            // SQLite keeps a write-ahead log next to the database; move it too so the renamed file
            // is not left behind with a stale log.
            MoveSidecar(oldPath, newPath, "-wal");
            MoveSidecar(oldPath, newPath, "-shm");
        }

        private static void MoveSidecar(string oldPath, string newPath, string suffix)
        {
            try
            {
                if (File.Exists(oldPath + suffix) && !File.Exists(newPath + suffix))
                    File.Move(oldPath + suffix, newPath + suffix);
            }
            catch (Exception)
            {
            }
        }
    }
}
