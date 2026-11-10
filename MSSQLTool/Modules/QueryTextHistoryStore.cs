using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MSSQLTool
{
    /// <summary>
    /// Storage for query <em>text</em> history (what SQL Prompt calls query/tab history): every
    /// captured editor state, whether or not it was executed.  It shares the SQLite engine that
    /// ships with SSMS through <see cref="QueryHistorySqliteStore"/>, and writes to its own
    /// database file so the execution history stays untouched.
    /// </summary>
    public static class QueryTextHistoryStore
    {
        private static readonly object SyncRoot = new object();
        private static bool initialized;
        private static string initializationError;

        public const string DatabaseFileName = "query-history.db";

        public static string DatabasePath => Path.Combine(AppPaths.QueryHistoryFolder, DatabaseFileName);

        public static string InitializationError => initializationError;

        /// <summary>Call after the data folder changes so the next access uses the new file.</summary>
        internal static void ResetForDataFolderChange()
        {
            lock (SyncRoot)
            {
                initialized = false;
                initializationError = null;
            }
        }

        public static bool EnsureInitialized(out string error)
        {
            if (initialized)
            {
                error = initializationError;
                return error == null;
            }

            lock (SyncRoot)
            {
                if (initialized)
                {
                    error = initializationError;
                    return error == null;
                }

                try
                {
                    // The execution-history store owns engine loading and native resolution.
                    if (!QueryHistorySqliteStore.EnsureInitialized(out error))
                    {
                        initializationError = error;
                        return false;
                    }

                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    {
                        database.Execute(@"
                            CREATE TABLE IF NOT EXISTS QueryTextHistory (
                                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                                ContentHash    TEXT    NOT NULL,
                                CapturedAt     TEXT    NOT NULL,
                                LastSeenAt     TEXT    NOT NULL,
                                Title          TEXT    NOT NULL DEFAULT '',
                                FilePath       TEXT    NOT NULL DEFAULT '',
                                ServerName     TEXT    NOT NULL DEFAULT '',
                                DatabaseName   TEXT    NOT NULL DEFAULT '',
                                Source         TEXT    NOT NULL DEFAULT 'MSSQLTool',
                                QueryText      TEXT    NOT NULL DEFAULT '',
                                CaptureCount   INTEGER NOT NULL DEFAULT 1,
                                GroupKey       TEXT    NOT NULL DEFAULT ''
                            );");
                        // GroupKey arrived later: older databases get the column, then the rows are
                        // grouped once so the list can show one row per query instead of one per text.
                        if (!ColumnsOf(database, "QueryTextHistory").Contains("GroupKey"))
                            database.Execute("ALTER TABLE QueryTextHistory ADD COLUMN GroupKey TEXT NOT NULL DEFAULT '';");

                        BackfillGroupKeys(database);
                        MigrateGrouping(database);

                        database.Execute("CREATE INDEX IF NOT EXISTS IX_QueryTextHistory_LastSeen ON QueryTextHistory(LastSeenAt DESC);");
                        database.Execute("CREATE INDEX IF NOT EXISTS IX_QueryTextHistory_Group ON QueryTextHistory(GroupKey, LastSeenAt DESC);");
                    }

                    initializationError = null;
                }
                catch (Exception ex)
                {
                    initializationError = ex.Message;
                    FeatureDiagnostics.Report("QueryTextHistory", "The query text history store could not be initialized", ex);
                }

                initialized = true;
                error = initializationError;
                return error == null;
            }
        }

        /// <summary>
        /// Stores a snapshot unless the same query already holds this exact text; repeating text only
        /// refreshes the last-seen timestamp and the touch counter.  Identity is the pair
        /// (GroupKey, ContentHash): two tabs that happen to contain the same SQL are two versions of
        /// their own query, not one row that jumps between groups.
        /// </summary>
        public static bool TryInsert(QueryTextHistoryEntry entry, out bool inserted, out string error)
        {
            inserted = false;
            error = null;
            if (entry == null || string.IsNullOrWhiteSpace(entry.QueryText)) return true;
            if (!EnsureInitialized(out error)) return false;

            try
            {
                entry.ContentHash = string.IsNullOrEmpty(entry.ContentHash) ? ComputeHash(entry.QueryText) : entry.ContentHash;
                if (entry.CapturedAt == default(DateTime)) entry.CapturedAt = DateTime.Now;
                if (entry.LastSeenAt == default(DateTime)) entry.LastSeenAt = entry.CapturedAt;

                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    {
                        inserted = InsertWithDatabase(database, entry);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryTextHistory", "Writing a query text snapshot failed", ex);
                return false;
            }
        }

        private static bool InsertWithDatabase(QueryHistorySqliteStore.SqliteDatabase database, QueryTextHistoryEntry entry)
        {
            if (string.IsNullOrEmpty(entry.GroupKey))
                entry.GroupKey = BuildGroupKey(entry.Title, entry.FilePath, entry.ServerName, entry.DatabaseName);

            using (var existing = database.Prepare(
                "SELECT Id FROM QueryTextHistory WHERE GroupKey = ? AND ContentHash = ? LIMIT 1;"))
            {
                existing.BindText(1, entry.GroupKey);
                existing.BindText(2, entry.ContentHash);
                if (existing.Step())
                {
                    long id = existing.ColumnInt64(0);
                    using (var touch = database.Prepare(@"UPDATE QueryTextHistory
                        SET LastSeenAt = ?, CaptureCount = CaptureCount + 1,
                            Title = CASE WHEN ? <> '' THEN ? ELSE Title END,
                            FilePath = CASE WHEN ? <> '' THEN ? ELSE FilePath END,
                            ServerName = CASE WHEN ? <> '' THEN ? ELSE ServerName END,
                            DatabaseName = CASE WHEN ? <> '' THEN ? ELSE DatabaseName END
                        WHERE Id = ?;"))
                    {
                        string lastSeen = ToStorageTime(entry.LastSeenAt);
                        touch.BindText(1, lastSeen);
                        touch.BindText(2, entry.Title ?? string.Empty);
                        touch.BindText(3, entry.Title ?? string.Empty);
                        touch.BindText(4, entry.FilePath ?? string.Empty);
                        touch.BindText(5, entry.FilePath ?? string.Empty);
                        touch.BindText(6, entry.ServerName ?? string.Empty);
                        touch.BindText(7, entry.ServerName ?? string.Empty);
                        touch.BindText(8, entry.DatabaseName ?? string.Empty);
                        touch.BindText(9, entry.DatabaseName ?? string.Empty);
                        touch.BindInt64(10, id);
                        touch.StepToCompletion();
                    }

                    return false;
                }
            }

            using (var insert = database.Prepare(@"INSERT INTO QueryTextHistory
                (ContentHash, CapturedAt, LastSeenAt, Title, FilePath, ServerName, DatabaseName, Source, QueryText, CaptureCount, GroupKey)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?);"))
            {
                insert.BindText(1, entry.ContentHash);
                insert.BindText(2, ToStorageTime(entry.CapturedAt));
                insert.BindText(3, ToStorageTime(entry.LastSeenAt));
                insert.BindText(4, entry.Title ?? string.Empty);
                insert.BindText(5, entry.FilePath ?? string.Empty);
                insert.BindText(6, entry.ServerName ?? string.Empty);
                insert.BindText(7, entry.DatabaseName ?? string.Empty);
                insert.BindText(8, string.IsNullOrEmpty(entry.Source) ? "MSSQLTool" : entry.Source);
                insert.BindText(9, entry.QueryText ?? string.Empty);
                insert.BindText(10, entry.GroupKey ?? string.Empty);
                insert.StepToCompletion();
            }

            return true;
        }

        /// <summary>
        /// Identity of the query a snapshot belongs to.  A saved file is the query; one open tab is the
        /// query when the caller knows its instance (<paramref name="tabKey"/>).  A temporary SSMS
        /// document must never be treated as a file: its path lives in %TEMP% and SSMS reuses those
        /// names, so the temporary name is only a fallback identity and is always qualified by the tab
        /// title.  Keeping this in one place means the importer, the capture service and the group
        /// query always agree on what "the same query" means.
        /// </summary>
        internal static string BuildGroupKey(string title, string filePath, string server, string database,
            string tabKey = null)
        {
            string tab = (tabKey ?? string.Empty).Trim();
            if (tab.Length > 0) return "tab:" + tab.ToLowerInvariant();

            string path = (filePath ?? string.Empty).Trim();
            bool rooted = false;
            if (path.Length > 0)
            {
                try { rooted = Path.IsPathRooted(path); }
                catch (Exception) { rooted = false; }
            }

            string name = (title ?? string.Empty).Trim();
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) name = "SQL";

            // SSMS names a new query SQLQuery1.sql and auto-recovered tabs ~vsXXXX.sql inside %TEMP%.
            // The same path is handed to a later tab, so it identifies a document only together with
            // the title it had while it was captured.
            if (rooted && IsTemporaryQueryPath(path))
                return "temp:" + path.ToLowerInvariant() + "|" + name.ToLowerInvariant();

            if (rooted) return "file:" + path.ToLowerInvariant();

            return "tab:" + name.ToLowerInvariant()
                + "|" + (server ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + (database ?? string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>
        /// True for the throw-away documents SSMS creates for a query tab: %TEMP%\SQLQueryN.sql and
        /// the auto-recovery files %TEMP%\~vsXXXX.sql (and their .sql1/.sql2 snapshots).
        /// </summary>
        internal static bool IsTemporaryQueryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory)) return false;

                string temp = (Path.GetTempPath() ?? string.Empty).TrimEnd('\\', '/');
                if (temp.Length == 0) return false;
                if (!string.Equals(directory.TrimEnd('\\', '/'), temp, StringComparison.OrdinalIgnoreCase))
                    return false;

                string name = Path.GetFileName(path) ?? string.Empty;
                return name.StartsWith("SQLQuery", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("~vs", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Brings an existing database to the current grouping rules.  Version 2 stops treating the
        /// temporary files of unsaved SSMS tabs as saved files, and moves the unique index from the
        /// text alone to (query, text) so the same SQL in two tabs can stay in both histories.
        /// </summary>
        private static void MigrateGrouping(QueryHistorySqliteStore.SqliteDatabase database)
        {
            if (UserVersion(database) >= 2) return;

            var updates = new List<KeyValuePair<long, string>>();
            using (var read = database.Prepare(
                "SELECT Id, Title, FilePath, ServerName, DatabaseName, GroupKey FROM QueryTextHistory;"))
            {
                while (read.Step())
                {
                    string key = BuildGroupKey(read.ColumnText(1), read.ColumnText(2), read.ColumnText(3), read.ColumnText(4));
                    if (!string.Equals(key, read.ColumnText(5) ?? string.Empty, StringComparison.Ordinal))
                        updates.Add(new KeyValuePair<long, string>(read.ColumnInt64(0), key));
                }
            }

            database.Execute("DROP INDEX IF EXISTS UX_QueryTextHistory_Hash;");

            if (updates.Count > 0)
            {
                database.Execute("BEGIN;");
                try
                {
                    using (var update = database.Prepare("UPDATE QueryTextHistory SET GroupKey = ? WHERE Id = ?;"))
                    {
                        foreach (KeyValuePair<long, string> pair in updates)
                        {
                            update.BindText(1, pair.Value);
                            update.BindInt64(2, pair.Key);
                            update.StepToCompletion();
                        }
                    }

                    database.Execute("COMMIT;");
                }
                catch
                {
                    try { database.Execute("ROLLBACK;"); } catch (Exception) { }
                    throw;
                }
            }

            // Old rows were unique by text; keep that guarantee inside a query so the new index can
            // always be created, even for a database written by a partially migrated build.
            database.Execute(@"DELETE FROM QueryTextHistory WHERE Id NOT IN
                (SELECT MAX(Id) FROM QueryTextHistory GROUP BY GroupKey, ContentHash);");
            database.Execute("CREATE UNIQUE INDEX IF NOT EXISTS UX_QueryTextHistory_GroupHash ON QueryTextHistory(GroupKey, ContentHash);");
            database.Execute("PRAGMA user_version = 2;");
        }

        private static long UserVersion(QueryHistorySqliteStore.SqliteDatabase database)
        {
            using (var statement = database.Prepare("PRAGMA user_version;"))
            {
                return statement.Step() ? statement.ColumnInt64(0) : 0;
            }
        }

        /// <summary>
        /// Moves the versions of an unsaved tab onto the file it was just saved as, so Save As keeps
        /// the history with the query instead of leaving an orphan "unsaved tab" entry behind.
        /// </summary>
        public static bool MoveGroup(string fromGroupKey, string toGroupKey, string filePath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(fromGroupKey) || string.IsNullOrEmpty(toGroupKey)) return true;
            if (string.Equals(fromGroupKey, toGroupKey, StringComparison.Ordinal)) return true;
            if (!EnsureInitialized(out error)) return false;

            try
            {
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    {
                        database.Execute("BEGIN;");
                        try
                        {
                            string path = filePath ?? string.Empty;
                            using (var move = database.Prepare(@"UPDATE OR IGNORE QueryTextHistory
                                SET GroupKey = ?, FilePath = CASE WHEN ? <> '' THEN ? ELSE FilePath END
                                WHERE GroupKey = ?;"))
                            {
                                move.BindText(1, toGroupKey);
                                move.BindText(2, path);
                                move.BindText(3, path);
                                move.BindText(4, fromGroupKey);
                                move.StepToCompletion();
                            }

                            // A text the file's history already contains stays there; the tab copy was
                            // the duplicate, so it is dropped.
                            using (var duplicates = database.Prepare(@"DELETE FROM QueryTextHistory
                                WHERE GroupKey = ? AND ContentHash IN
                                      (SELECT ContentHash FROM QueryTextHistory WHERE GroupKey = ?);"))
                            {
                                duplicates.BindText(1, fromGroupKey);
                                duplicates.BindText(2, toGroupKey);
                                duplicates.StepToCompletion();
                            }

                            database.Execute("COMMIT;");
                        }
                        catch
                        {
                            try { database.Execute("ROLLBACK;"); } catch (Exception) { }
                            throw;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryTextHistory", "Moving a query group failed", ex);
                return false;
            }
        }

        /// <summary>Fills GroupKey for rows written before the column existed.</summary>
        private static void BackfillGroupKeys(QueryHistorySqliteStore.SqliteDatabase database)
        {
            List<long> ids = new List<long>();
            List<string> keys = new List<string>();

            using (var read = database.Prepare(
                "SELECT Id, Title, FilePath, ServerName, DatabaseName FROM QueryTextHistory WHERE GroupKey = '';"))
            {
                while (read.Step())
                {
                    ids.Add(read.ColumnInt64(0));
                    keys.Add(BuildGroupKey(read.ColumnText(1), read.ColumnText(2), read.ColumnText(3), read.ColumnText(4)));
                }
            }

            if (ids.Count == 0) return;

            database.Execute("BEGIN;");
            try
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    using (var update = database.Prepare("UPDATE QueryTextHistory SET GroupKey = ? WHERE Id = ?;"))
                    {
                        update.BindText(1, keys[i]);
                        update.BindInt64(2, ids[i]);
                        update.StepToCompletion();
                    }
                }

                database.Execute("COMMIT;");
            }
            catch
            {
                try { database.Execute("ROLLBACK;"); } catch (Exception) { }
                throw;
            }
        }

        public static List<QueryTextHistoryEntry> Search(string term, int limit, out string error)
        {
            var results = new List<QueryTextHistoryEntry>();
            error = null;
            if (!EnsureInitialized(out error)) return results;

            try
            {
                string where = string.Empty;
                var parameters = new List<object>();
                foreach (string word in (term ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    where += (where.Length == 0 ? " WHERE " : " AND ");
                    where += "(QueryText LIKE ? OR Title LIKE ? OR FilePath LIKE ? OR ServerName LIKE ? OR DatabaseName LIKE ?)";
                    string pattern = "%" + word + "%";
                    parameters.AddRange(new object[] { pattern, pattern, pattern, pattern, pattern });
                }

                int max = Math.Max(1, Math.Min(5000, limit));

                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare(@"SELECT Id, ContentHash, CapturedAt, LastSeenAt, Title, FilePath,
                                ServerName, DatabaseName, Source, QueryText, CaptureCount
                            FROM QueryTextHistory" + where + @"
                            ORDER BY LastSeenAt DESC, Id DESC
                            LIMIT " + max + ";"))
                    {
                        statement.BindAll(parameters);
                        while (statement.Step())
                        {
                            results.Add(new QueryTextHistoryEntry
                            {
                                Id = statement.ColumnInt64(0),
                                ContentHash = statement.ColumnText(1) ?? string.Empty,
                                CapturedAt = FromStorageTime(statement.ColumnText(2)),
                                LastSeenAt = FromStorageTime(statement.ColumnText(3)),
                                Title = statement.ColumnText(4) ?? string.Empty,
                                FilePath = statement.ColumnText(5) ?? string.Empty,
                                ServerName = statement.ColumnText(6) ?? string.Empty,
                                DatabaseName = statement.ColumnText(7) ?? string.Empty,
                                Source = statement.ColumnText(8) ?? string.Empty,
                                QueryText = statement.ColumnText(9) ?? string.Empty,
                                CaptureCount = (int)statement.ColumnInt64(10)
                            });
                        }
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryTextHistory", "Reading the query text history failed", ex);
                return results;
            }
        }

        public static bool Delete(long id, out string error)
        {
            error = null;
            if (!EnsureInitialized(out error)) return false;

            try
            {
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare("DELETE FROM QueryTextHistory WHERE Id = ?;"))
                    {
                        statement.BindInt64(1, id);
                        statement.StepToCompletion();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool DeleteGroup(string groupKey, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(groupKey)) return true;
            if (!EnsureInitialized(out error)) return false;

            try
            {
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare("DELETE FROM QueryTextHistory WHERE GroupKey = ?;"))
                    {
                        statement.BindText(1, groupKey);
                        statement.StepToCompletion();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static int Count(out string error)
        {
            error = null;
            if (!EnsureInitialized(out error)) return 0;

            try
            {
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare("SELECT COUNT(1) FROM QueryTextHistory;"))
                    {
                        if (statement.Step()) return (int)Math.Min(int.MaxValue, statement.ColumnInt64(0));
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return 0;
        }

        /// <summary>
        /// One row per query: the newest stored text, plus how many versions that query has.  This is
        /// what SQL Prompt's history list looks like, and it also means the list is no longer cut off
        /// at a fixed number of text snapshots.
        /// </summary>
        public static List<QueryTextHistoryGroup> SearchGroups(
            QueryTextHistoryFilter filter, int limit, out string error)
        {
            var results = new List<QueryTextHistoryGroup>();
            error = null;
            if (!EnsureInitialized(out error)) return results;

            try
            {
                var parameters = new List<object>();
                string where = BuildWhere(filter, parameters);
                int max = Math.Max(1, Math.Min(50000, limit));

                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare(@"SELECT h.Id, h.ContentHash, h.CapturedAt, h.LastSeenAt,
                                h.Title, h.FilePath, h.ServerName, h.DatabaseName, h.Source, h.QueryText,
                                h.CaptureCount, h.GroupKey,
                                (SELECT COUNT(1) FROM QueryTextHistory c WHERE c.GroupKey = h.GroupKey),
                                (SELECT MIN(c.LastSeenAt) FROM QueryTextHistory c WHERE c.GroupKey = h.GroupKey)
                            FROM QueryTextHistory h
                            JOIN (SELECT GroupKey AS gk, MAX(LastSeenAt) AS lastSeen FROM QueryTextHistory" + where + @"
                                  GROUP BY GroupKey) g
                              ON g.gk = h.GroupKey AND g.lastSeen = h.LastSeenAt
                            WHERE h.Id = (SELECT MAX(c2.Id) FROM QueryTextHistory c2
                                          WHERE c2.GroupKey = h.GroupKey AND c2.LastSeenAt = h.LastSeenAt)
                            ORDER BY h.LastSeenAt DESC, h.Id DESC
                            LIMIT " + max + ";"))
                    {
                        statement.BindAll(parameters);
                        while (statement.Step())
                        {
                            QueryTextHistoryEntry latest = ReadEntry(statement, 0);
                            results.Add(new QueryTextHistoryGroup
                            {
                                GroupKey = latest.GroupKey,
                                Latest = latest,
                                Versions = (int)Math.Max(1, statement.ColumnInt64(12)),
                                FirstSeenAt = FromStorageTime(statement.ColumnText(13))
                            });
                        }
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryTextHistory", "Reading the grouped query history failed", ex);
                return results;
            }
        }

        /// <summary>Every stored version of one query, newest first.</summary>
        public static List<QueryTextHistoryEntry> SearchVersions(string groupKey, int limit, out string error)
        {
            var results = new List<QueryTextHistoryEntry>();
            error = null;
            if (string.IsNullOrEmpty(groupKey)) return results;
            if (!EnsureInitialized(out error)) return results;

            try
            {
                int max = Math.Max(1, Math.Min(5000, limit));
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare(@"SELECT Id, ContentHash, CapturedAt, LastSeenAt, Title,
                                FilePath, ServerName, DatabaseName, Source, QueryText, CaptureCount, GroupKey
                            FROM QueryTextHistory WHERE GroupKey = ?
                            ORDER BY LastSeenAt DESC, Id DESC
                            LIMIT " + max + ";"))
                    {
                        statement.BindText(1, groupKey);
                        while (statement.Step()) results.Add(ReadEntry(statement, 0));
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryTextHistory", "Reading the versions of a query failed", ex);
                return results;
            }
        }

        public static int CountGroups(QueryTextHistoryFilter filter, out string error)
        {
            error = null;
            if (!EnsureInitialized(out error)) return 0;

            try
            {
                var parameters = new List<object>();
                string where = BuildWhere(filter, parameters);
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    using (var statement = database.Prepare(
                        "SELECT COUNT(DISTINCT GroupKey) FROM QueryTextHistory" + where + ";"))
                    {
                        statement.BindAll(parameters);
                        if (statement.Step()) return (int)Math.Min(int.MaxValue, statement.ColumnInt64(0));
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return 0;
        }

        /// <summary>Servers and databases that actually appear in the history, for the filter pickers.</summary>
        public static void GetFacets(out List<string> servers, out List<string> databases, out string error)
        {
            servers = new List<string>();
            databases = new List<string>();
            error = null;
            if (!EnsureInitialized(out error)) return;

            try
            {
                lock (SyncRoot)
                {
                    using (var database = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    {
                        using (var statement = database.Prepare(
                            "SELECT DISTINCT ServerName FROM QueryTextHistory WHERE ServerName <> '' ORDER BY ServerName;"))
                        {
                            while (statement.Step()) servers.Add(statement.ColumnText(0) ?? string.Empty);
                        }

                        using (var statement = database.Prepare(
                            "SELECT DISTINCT DatabaseName FROM QueryTextHistory WHERE DatabaseName <> '' ORDER BY DatabaseName;"))
                        {
                            while (statement.Step()) databases.Add(statement.ColumnText(0) ?? string.Empty);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        private static QueryTextHistoryEntry ReadEntry(QueryHistorySqliteStore.SqliteStatement statement, int index)
        {
            return new QueryTextHistoryEntry
            {
                Id = statement.ColumnInt64(index),
                ContentHash = statement.ColumnText(index + 1) ?? string.Empty,
                CapturedAt = FromStorageTime(statement.ColumnText(index + 2)),
                LastSeenAt = FromStorageTime(statement.ColumnText(index + 3)),
                Title = statement.ColumnText(index + 4) ?? string.Empty,
                FilePath = statement.ColumnText(index + 5) ?? string.Empty,
                ServerName = statement.ColumnText(index + 6) ?? string.Empty,
                DatabaseName = statement.ColumnText(index + 7) ?? string.Empty,
                Source = statement.ColumnText(index + 8) ?? string.Empty,
                QueryText = statement.ColumnText(index + 9) ?? string.Empty,
                CaptureCount = (int)statement.ColumnInt64(index + 10),
                GroupKey = statement.ColumnText(index + 11) ?? string.Empty
            };
        }

        /// <summary>
        /// WHERE clause for the filters.  The search box keeps its old behaviour (every word must
        /// appear somewhere), and the other filters narrow it further instead of replacing it.
        /// </summary>
        internal static string BuildWhere(QueryTextHistoryFilter filter, List<object> parameters)
        {
            var parts = new List<string>();

            foreach (string word in (filter?.Text ?? string.Empty).Split(
                new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                parts.Add("(QueryText LIKE ? OR Title LIKE ? OR FilePath LIKE ? OR ServerName LIKE ? OR DatabaseName LIKE ?)");
                string pattern = "%" + word + "%";
                parameters.AddRange(new object[] { pattern, pattern, pattern, pattern, pattern });
            }

            if (filter != null && filter.From.HasValue)
            {
                parts.Add("LastSeenAt >= ?");
                parameters.Add(ToStorageTime(filter.From.Value.Date));
            }

            if (filter != null && filter.To.HasValue)
            {
                parts.Add("LastSeenAt <= ?");
                parameters.Add(ToStorageTime(filter.To.Value.Date.AddDays(1).AddSeconds(-1)));
            }

            if (filter != null && !string.IsNullOrWhiteSpace(filter.Server))
            {
                parts.Add("ServerName = ?");
                parameters.Add(filter.Server.Trim());
            }

            if (filter != null && !string.IsNullOrWhiteSpace(filter.Database))
            {
                parts.Add("DatabaseName = ?");
                parameters.Add(filter.Database.Trim());
            }

            if (filter != null && string.Equals(filter.Source, "editor", StringComparison.OrdinalIgnoreCase))
                parts.Add("Source LIKE 'editor:%'");
            else if (filter != null && string.Equals(filter.Source, "SQL Prompt", StringComparison.OrdinalIgnoreCase))
                parts.Add("Source = 'SQL Prompt'");

            return parts.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", parts);
        }

        internal static string ComputeHash(string text)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string ToStorageTime(DateTime value)
            => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private static DateTime FromStorageTime(string value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : default(DateTime);
        }

        /// <summary>Progress reported while a SQL Prompt history file is imported.</summary>
        public sealed class QueryTextImportProgress
        {
            public string Table = string.Empty;
            public int TablesDone;
            public int TablesTotal;
            public int RowsScanned;
            public int Imported;
            public int Skipped;

            public int Percent => TablesTotal == 0 ? 0 : Math.Min(100, (int)(TablesDone * 100L / TablesTotal));
        }

        /// <summary>
        /// Imports the query history of SQL Prompt.  SQL Prompt keeps a SQLite database whose schema
        /// changes between versions, so the importer inspects it instead of assuming column names:
        /// every table with a long text column is treated as a source of query text, and a
        /// timestamp/connection column is used when one is present.
        /// </summary>
        public static bool ImportSqlPrompt(string path, out int imported, out int skipped, out string error)
        {
            return ImportSqlPrompt(path, null, System.Threading.CancellationToken.None, out imported, out skipped, out error);
        }

        /// <summary>Import with progress reporting and cancellation; runs safely on a worker thread.</summary>
        public static bool ImportSqlPrompt(
            string path,
            IProgress<QueryTextImportProgress> progress,
            System.Threading.CancellationToken cancellationToken,
            out int imported,
            out int skipped,
            out string error)
        {
            imported = 0;
            skipped = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "The selected file does not exist.";
                return false;
            }

            if (!EnsureInitialized(out error)) return false;

            try
            {
                lock (SyncRoot)
                {
                    using (var source = QueryHistorySqliteStore.SqliteDatabase.OpenReadOnly(path))
                    using (var destination = QueryHistorySqliteStore.SqliteDatabase.Open(DatabasePath))
                    {
                        // SQL Prompt's own schema is known, so use it directly instead of guessing
                        // which table holds the text: Queries -> QueryVersions -> QueryVersionProperties.
                        if (HasSqlPromptSchema(source))
                        {
                            var schemaState = new QueryTextImportProgress { TablesTotal = 1, Table = "QueryVersions" };
                            destination.Execute("BEGIN;");
                            try
                            {
                                ImportSqlPromptQueryVersions(source, destination, ref imported, ref skipped,
                                    schemaState, progress, cancellationToken);
                                destination.Execute("COMMIT;");
                            }
                            catch
                            {
                                try { destination.Execute("ROLLBACK;"); } catch (Exception) { }
                                throw;
                            }

                            schemaState.TablesDone = 1;
                            schemaState.Imported = imported;
                            schemaState.Skipped = skipped;
                            Report(progress, schemaState);
                            return true;
                        }

                        List<string> tables = ListTables(source);

                        // Prefer tables that look like a history/tab store; only fall back to the
                        // whole database when the schema uses another naming scheme.
                        List<string> preferred = tables
                            .Where(table => LooksLikeHistoryTable(table))
                            .ToList();
                        if (preferred.Count == 0) preferred = tables;

                        var state = new QueryTextImportProgress { TablesTotal = preferred.Count };
                        destination.Execute("BEGIN;");
                        try
                        {
                            foreach (string table in preferred)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                state.Table = table;
                                Report(progress, state);

                                ImportTable(source, destination, table, ref imported, ref skipped,
                                    state, progress, cancellationToken);

                                state.TablesDone++;
                                state.Imported = imported;
                                state.Skipped = skipped;
                                Report(progress, state);
                            }

                            destination.Execute("COMMIT;");
                        }
                        catch
                        {
                            try { destination.Execute("ROLLBACK;"); } catch (Exception) { }
                            throw;
                        }

                        state.Imported = imported;
                        state.Skipped = skipped;
                        Report(progress, state);
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                error = "The import was cancelled.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryTextHistory", "Importing the SQL Prompt history failed", ex);
                return false;
            }
        }

        private static void Report(IProgress<QueryTextImportProgress> progress, QueryTextImportProgress state)
        {
            try { progress?.Report(state); } catch (Exception) { }
        }

        private static bool HasSqlPromptSchema(QueryHistorySqliteStore.SqliteDatabase database)
        {
            int found = 0;
            using (var statement = database.Prepare(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Queries', 'QueryVersions', 'QueryVersionProperties');"))
            {
                while (statement.Step())
                {
                    string name = statement.ColumnText(0);
                    // QueryVersions is the only table the import actually reads; the other two make it
                    // nicer (tab name, server) but SQL Prompt versions differ in what they carry.
                    if (string.Equals(name, "QueryVersions", StringComparison.OrdinalIgnoreCase)) found += 2;
                    else if (!string.IsNullOrEmpty(name)) found++;
                }
            }

            return found >= 2;
        }

        /// <summary>Column names of a table, used because SQL Prompt's schema differs between versions.</summary>
        private static HashSet<string> ColumnsOf(QueryHistorySqliteStore.SqliteDatabase database, string table)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var statement = database.Prepare("PRAGMA table_info(" + table + ");"))
                {
                    while (statement.Step())
                    {
                        string name = statement.ColumnText(1);
                        if (!string.IsNullOrEmpty(name)) columns.Add(name.Trim());
                    }
                }
            }
            catch (Exception)
            {
            }

            return columns;
        }

        /// <summary>
        /// Column of QueryVersions that points at QueryVersionProperties.  SQL Prompt names it
        /// QueryVersionPropertiesIdentity in some builds and QueryPropertiesIdentity in others, so the
        /// widest match that still contains "PropertiesIdentity" is used first.
        /// </summary>
        private static string FindPropertiesLink(HashSet<string> versionColumns)
        {
            string exact = FirstPresent(versionColumns,
                "QueryVersionPropertiesIdentity", "QueryPropertiesIdentity", "PropertiesIdentity",
                "QueryVersionPropertiesId", "QueryPropertiesId");
            if (exact != null) return exact;

            foreach (string column in versionColumns)
            {
                if (column.IndexOf("PropertiesIdentity", StringComparison.OrdinalIgnoreCase) >= 0) return column;
            }

            foreach (string column in versionColumns)
            {
                if (column.IndexOf("Properties", StringComparison.OrdinalIgnoreCase) >= 0
                    && column.IndexOf("Identity", StringComparison.OrdinalIgnoreCase) >= 0) return column;
            }

            return null;
        }

        private static string FirstPresent(HashSet<string> columns, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (columns.Contains(candidate)) return candidate;
            }

            return null;
        }

        internal sealed class SqlPromptImportShape
        {
            public string ContentsColumn;
            public string Sql;
        }

        /// <summary>
        /// Builds the QueryVersions statement from the columns the file actually has, because SQL
        /// Prompt versions differ: SQL Prompt 10 links through QueryPropertiesIdentity while other
        /// builds use QueryVersionPropertiesIdentity, and the text column is not always called
        /// Contents.  A unknown column name used to make the whole import fail with
        /// "no such column", so every optional column is resolved by lookup first.
        /// </summary>
        internal static SqlPromptImportShape BuildSqlPromptImportShape(
            HashSet<string> versionColumns,
            HashSet<string> propertyColumns)
        {
            string contentsColumn = FirstPresent(versionColumns, "Contents", "QueryText", "Query", "Text");
            if (contentsColumn == null) return null;

            string dateColumn = FirstPresent(versionColumns, "DateSaved", "LastSaved", "SavedAt", "Created");
            string linkColumn = FindPropertiesLink(versionColumns);

            bool joinProperties = linkColumn != null
                && propertyColumns.Contains("Identity")
                && (propertyColumns.Contains("Name") || propertyColumns.Contains("Path")
                    || propertyColumns.Contains("Server") || propertyColumns.Contains("Database"));

            string sql = "SELECT v." + contentsColumn + ", "
                + (dateColumn == null ? "''" : "v." + dateColumn) + ", "
                + (joinProperties && propertyColumns.Contains("Name") ? "COALESCE(p.Name, '')" : "''") + ", "
                + (joinProperties && propertyColumns.Contains("Path") ? "COALESCE(p.Path, '')" : "''") + ", "
                + (joinProperties && propertyColumns.Contains("Server") ? "COALESCE(p.Server, '')" : "''") + ", "
                + (joinProperties && propertyColumns.Contains("Database") ? "COALESCE(p.Database, '')" : "''")
                + " FROM QueryVersions v"
                + (joinProperties ? " LEFT JOIN QueryVersionProperties p ON p.Identity = v." + linkColumn : string.Empty)
                + " WHERE v." + contentsColumn + " IS NOT NULL AND length(trim(v." + contentsColumn + ")) > 0"
                // Oldest first: the newest version of a query must end up with the highest row id,
                // because the grouped list shows the row with the highest id as the current text.
                + (dateColumn == null ? string.Empty : " ORDER BY v." + dateColumn)
                + ";";

            return new SqlPromptImportShape { ContentsColumn = contentsColumn, Sql = sql };
        }

        /// <summary>
        /// Imports SQL Prompt's query versions.  Every row in QueryVersions holds one saved text of a
        /// tab; QueryVersionProperties carries the tab name, file path, server and database, and
        /// DateSaved is the save stamp.
        /// </summary>
        private static void ImportSqlPromptQueryVersions(
            QueryHistorySqliteStore.SqliteDatabase source,
            QueryHistorySqliteStore.SqliteDatabase destination,
            ref int imported,
            ref int skipped,
            QueryTextImportProgress state,
            IProgress<QueryTextImportProgress> progress,
            System.Threading.CancellationToken cancellationToken)
        {
            SqlPromptImportShape shape = BuildSqlPromptImportShape(
                ColumnsOf(source, "QueryVersions"),
                ColumnsOf(source, "QueryVersionProperties"));

            if (shape == null)
            {
                state.Imported = imported;
                state.Skipped = skipped;
                Report(progress, state);
                return;
            }

            string sql = shape.Sql;

            Report(progress, state);
            using (var statement = source.Prepare(sql))
            {
                while (statement.Step())
                {
                    if ((state.RowsScanned & 31) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        state.Imported = imported;
                        state.Skipped = skipped;
                        Report(progress, state);
                    }

                    state.RowsScanned++;

                    string text = statement.ColumnText(0);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    DateTime captured = FromSavedStamp(statement.ColumnText(1));
                    var entry = new QueryTextHistoryEntry
                    {
                        QueryText = text,
                        CapturedAt = captured,
                        LastSeenAt = captured,
                        Title = statement.ColumnText(2) ?? string.Empty,
                        FilePath = statement.ColumnText(3) ?? string.Empty,
                        ServerName = statement.ColumnText(4) ?? string.Empty,
                        DatabaseName = statement.ColumnText(5) ?? string.Empty,
                        Source = "SQL Prompt",
                        ContentHash = ComputeHash(text)
                    };

                    if (InsertWithDatabase(destination, entry)) imported++;
                    else skipped++;
                }
            }

            state.Imported = imported;
            state.Skipped = skipped;
            Report(progress, state);
        }

        /// <summary>
        /// DateSaved is unix seconds, unix milliseconds, .NET ticks or a Windows FILETIME depending on
        /// the SQL Prompt release; SQL Prompt 10 stores FILETIME (100ns since 1601-01-01 UTC).
        /// </summary>
        internal static DateTime FromSavedStamp(string raw)
        {
            if (!long.TryParse((raw ?? string.Empty).Trim(), out long value) || value <= 0) return DateTime.Now;

            try
            {
                // .NET ticks are an order of magnitude larger than FILETIME for the same date.
                if (value >= 500000000000000000L) return new DateTime(value, DateTimeKind.Utc).ToLocalTime();
                if (value >= 10000000000000000L) return DateTime.FromFileTimeUtc(value).ToLocalTime();
                if (value > 100000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
                return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
            }
            catch (Exception)
            {
                return DateTime.Now;
            }
        }

        private static bool LooksLikeHistoryTable(string name)
        {
            string value = (name ?? string.Empty).ToLowerInvariant();
            return value.Contains("histor") || value.Contains("query") || value.Contains("tab") ||
                   value.Contains("script") || value.Contains("snapshot");
        }

        private static List<string> ListTables(QueryHistorySqliteStore.SqliteDatabase database)
        {
            var tables = new List<string>();
            using (var statement = database.Prepare(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"))
            {
                while (statement.Step())
                {
                    string name = statement.ColumnText(0);
                    if (!string.IsNullOrEmpty(name)) tables.Add(name);
                }
            }

            return tables;
        }

        private sealed class ColumnInfo
        {
            public string Name;
            public string Type;
        }

        private static List<ColumnInfo> ReadColumns(QueryHistorySqliteStore.SqliteDatabase database, string table)
        {
            var columns = new List<ColumnInfo>();
            string quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            using (var statement = database.Prepare("PRAGMA table_info(" + quoted + ");"))
            {
                while (statement.Step())
                {
                    columns.Add(new ColumnInfo
                    {
                        Name = statement.ColumnText(1) ?? string.Empty,
                        Type = statement.ColumnText(2) ?? string.Empty
                    });
                }
            }

            return columns;
        }

        private static void ImportTable(
            QueryHistorySqliteStore.SqliteDatabase source,
            QueryHistorySqliteStore.SqliteDatabase destination,
            string table,
            ref int imported,
            ref int skipped,
            QueryTextImportProgress state,
            IProgress<QueryTextImportProgress> progress,
            System.Threading.CancellationToken cancellationToken)
        {
            List<ColumnInfo> columns = ReadColumns(source, table);
            if (columns.Count == 0) return;

            int textIndex = FindTextColumn(columns);
            if (textIndex < 0) return;

            int timeIndex = FindColumn(columns, "time", "date", "created", "modified", "stamp");
            int titleIndex = FindColumn(columns, "title", "name", "tab", "caption");
            int serverIndex = FindColumn(columns, "server", "instance", "datasource", "data_source");
            int databaseIndex = FindColumn(columns, "database", "catalog", "dbname", "db_name");
            int pathIndex = FindColumn(columns, "path", "file");

            string quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            using (var statement = source.Prepare("SELECT * FROM " + quoted + ";"))
            {
                while (statement.Step())
                {
                    if ((state.RowsScanned & 31) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        state.Imported = imported;
                        state.Skipped = skipped;
                        Report(progress, state);
                    }

                    state.RowsScanned++;

                    string text = statement.ColumnText(textIndex);
                    if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 8) continue;

                    DateTime captured = DateTime.Now;
                    if (timeIndex >= 0)
                    {
                        string raw = statement.ColumnText(timeIndex);
                        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
                        {
                            captured = parsed;
                        }
                    }

                    var entry = new QueryTextHistoryEntry
                    {
                        QueryText = text,
                        CapturedAt = captured,
                        LastSeenAt = captured,
                        Title = titleIndex >= 0 ? (statement.ColumnText(titleIndex) ?? string.Empty) : string.Empty,
                        FilePath = pathIndex >= 0 ? (statement.ColumnText(pathIndex) ?? string.Empty) : string.Empty,
                        ServerName = serverIndex >= 0 ? (statement.ColumnText(serverIndex) ?? string.Empty) : string.Empty,
                        DatabaseName = databaseIndex >= 0 ? (statement.ColumnText(databaseIndex) ?? string.Empty) : string.Empty,
                        Source = "SQL Prompt",
                        ContentHash = ComputeHash(text)
                    };

                    if (InsertWithDatabase(destination, entry)) imported++;
                    else skipped++;
                }
            }
        }

        /// <summary>Longest text-looking column, preferring a name that reads like query text.</summary>
        private static int FindTextColumn(List<ColumnInfo> columns)
        {
            int named = -1;
            for (int index = 0; index < columns.Count; index++)
            {
                string name = columns[index].Name.ToLowerInvariant();
                if (name.Contains("sql") || name.Contains("query") || name.Contains("script") ||
                    name.Contains("text") || name.Contains("body") || name.Contains("content") ||
                    name.Contains("statement"))
                {
                    named = index;
                    break;
                }
            }

            if (named >= 0) return named;

            for (int index = 0; index < columns.Count; index++)
            {
                string type = (columns[index].Type ?? string.Empty).ToUpperInvariant();
                if (type.Contains("CHAR") || type.Contains("TEXT") || type.Contains("CLOB") || type.Length == 0)
                    return index;
            }

            return -1;
        }

        private static int FindColumn(List<ColumnInfo> columns, params string[] hints)
        {
            for (int index = 0; index < columns.Count; index++)
            {
                string name = columns[index].Name.ToLowerInvariant();
                foreach (string hint in hints)
                {
                    if (name.Contains(hint)) return index;
                }
            }

            return -1;
        }
    }
}
