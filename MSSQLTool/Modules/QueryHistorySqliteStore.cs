using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SQLitePCL;

namespace MSSQLTool
{
    /// <summary>
    /// A query history row as stored in the local SQLite database.
    /// </summary>
    public sealed class QueryHistoryStoreEntry
    {
        public long QueryId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime FinishTime { get; set; }
        public string ElapsedTime { get; set; }
        public long TotalRowsReturned { get; set; }
        public string ExecResult { get; set; }
        public string QueryText { get; set; }
        public string DataSource { get; set; }
        public string DatabaseName { get; set; }
        public string LoginName { get; set; }
        public string WorkstationId { get; set; }
    }

    /// <summary>Filter and paging arguments for a history query.</summary>
    public sealed class QueryHistoryStoreFilter
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Login { get; set; }
        public string[] QueryTerms { get; set; }
        /// <summary>"All", "Succeeded", "Failed" or "Cancelled".</summary>
        public string ResultKind { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 100;
    }

    /// <summary>
    /// Query history persistence in a standalone SQLite database that lives under the user's
    /// local application data.  Nothing is ever written to the SQL Server instance the user is
    /// connected to, and the history is therefore kept even when no server is reachable.
    ///
    /// The SQLite engine ships with SSMS (PublicAssemblies\SQLite).  Its native library is
    /// resolved at runtime and the matching SQLitePCLRaw provider is initialized once, so the
    /// extension needs no additional deployment of its own.
    /// </summary>
    public static class QueryHistorySqliteStore
    {
        private static readonly object SyncRoot = new object();
        private static bool initialized;
        private static string initializationError;
        private static string engineVersion;
        private static bool nativeLibraryLocated;

        /// <summary>Name of the history database file.</summary>
        public const string DatabaseFileName = "query-history.db";

        /// <summary>Where the history database is kept.</summary>
        public static string DatabasePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MSSQLTool",
                    "QueryHistory",
                    DatabaseFileName);
            }
        }

        /// <summary>Non-null when the engine could not be initialized.</summary>
        public static string InitializationError => initializationError;

        public static string EngineVersion => engineVersion ?? string.Empty;

        /// <summary>
        /// Initializes the SQLite engine and creates the schema if needed.  Safe to call from any
        /// thread and cheap after the first call.
        /// </summary>
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
                    LoadNativeEngine();
                    Batteries_V2.Init();
                    engineVersion = raw.sqlite3_libversion().utf8_to_string();
                    CreateSchema();
                    initializationError = null;
                }
                catch (Exception ex)
                {
                    initializationError = ex.Message;
                    FeatureDiagnostics.Report("QueryHistory", "The local SQLite history store could not be initialized", ex);
                }

                initialized = true;
                error = initializationError;
                return error == null;
            }
        }

        /// <summary>
        /// Loads the native SQLite library.  Loading it by full path first makes the module
        /// available to the provider, which resolves it purely by name.
        /// </summary>
        private static void LoadNativeEngine()
        {
            if (nativeLibraryLocated) return;

            foreach (string candidate in FindNativeCandidates())
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                if (LoadLibrary(candidate) != IntPtr.Zero)
                {
                    nativeLibraryLocated = true;
                    return;
                }
            }

            // Fall back to the default search order; the provider reports a clear error if this
            // also fails, which is surfaced through InitializationError.
            nativeLibraryLocated = true;
        }

        private static IList<string> FindNativeCandidates()
        {
            var candidates = new List<string>();

            try
            {
                string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(assemblyFolder))
                {
                    candidates.Add(Path.Combine(assemblyFolder, NativeLibraryName));

                    // The extension is deployed as <SSMS>\Common7\IDE\Extensions\MSSQLTool,
                    // so the SQLite folder that ships with SSMS is two levels up.
                    string ideFolder = Path.GetFullPath(Path.Combine(assemblyFolder, "..", ".."));
                    candidates.Add(Path.Combine(ideFolder, "PublicAssemblies", "SQLite", NativeLibraryName));
                    candidates.Add(Path.Combine(ideFolder, "CommonExtensions", "Microsoft", "VBCSharp", "LanguageServices", NativeLibraryName));
                }
            }
            catch { }

            try
            {
                string processFolder = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                if (!string.IsNullOrEmpty(processFolder))
                {
                    candidates.Add(Path.Combine(processFolder, "PublicAssemblies", "SQLite", NativeLibraryName));
                    candidates.Add(Path.Combine(processFolder, "CommonExtensions", "Microsoft", "VBCSharp", "LanguageServices", NativeLibraryName));
                }
            }
            catch { }

            return candidates;
        }

        private const string NativeLibraryName = "e_sqlite3.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string libraryPath);

        private static void CreateSchema()
        {
            using (SqliteDatabase database = SqliteDatabase.Open(DatabasePath))
            {
                database.Execute(@"
                    CREATE TABLE IF NOT EXISTS QueryHistory (
                        QueryId             INTEGER PRIMARY KEY AUTOINCREMENT,
                        StartTime           TEXT    NOT NULL,
                        FinishTime          TEXT    NOT NULL,
                        ElapsedTime         TEXT    NOT NULL DEFAULT '',
                        TotalRowsReturned   INTEGER NOT NULL DEFAULT 0,
                        ExecResult          TEXT    NOT NULL DEFAULT '',
                        QueryText           TEXT    NOT NULL DEFAULT '',
                        DataSource          TEXT    NOT NULL DEFAULT '',
                        DatabaseName        TEXT    NOT NULL DEFAULT '',
                        LoginName           TEXT    NOT NULL DEFAULT '',
                        WorkstationId       TEXT    NOT NULL DEFAULT '',
                        ClientExecutionId   TEXT    NULL
                    );");
                database.Execute("CREATE INDEX IF NOT EXISTS IX_QueryHistory_StartTime ON QueryHistory(StartTime DESC);");
                database.Execute("CREATE INDEX IF NOT EXISTS IX_QueryHistory_Server_Database ON QueryHistory(DataSource, DatabaseName);");
                database.Execute("CREATE UNIQUE INDEX IF NOT EXISTS UX_QueryHistory_ClientExecutionId ON QueryHistory(ClientExecutionId);");
            }
        }

        /// <summary>
        /// Appends one execution to the history.  Re-running the same client execution is
        /// idempotent thanks to the unique index on the client id.
        /// </summary>
        public static bool TryInsert(QueryHistoryEntry entry, out string error)
        {
            return TryInsert(entry, out bool _, out error);
        }

        /// <summary>
        /// Appends one execution and reports whether a row was actually written.  A record whose
        /// client execution id is already stored is skipped, which is how a repeated save (or a
        /// repeated text file import) stays a no-op.
        /// </summary>
        public static bool TryInsert(QueryHistoryEntry entry, out bool inserted, out string error)
        {
            inserted = false;
            error = null;
            if (entry == null) return true;
            if (!EnsureInitialized(out error)) return false;

            try
            {
                lock (SyncRoot)
                {
                    using (SqliteDatabase database = SqliteDatabase.Open(DatabasePath))
                    using (SqliteStatement statement = database.Prepare(@"
                        INSERT OR IGNORE INTO QueryHistory
                            (StartTime, FinishTime, ElapsedTime, TotalRowsReturned, ExecResult,
                             QueryText, DataSource, DatabaseName, LoginName, WorkstationId, ClientExecutionId)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);"))
                    {
                        statement.BindText(1, ToStorageTime(entry.StartTime));
                        statement.BindText(2, ToStorageTime(entry.FinishTime));
                        statement.BindText(3, entry.ElapsedTime ?? string.Empty);
                        statement.BindInt64(4, entry.TotalRowsReturned);
                        statement.BindText(5, entry.ExecResult ?? string.Empty);
                        statement.BindText(6, entry.QueryText ?? string.Empty);
                        statement.BindText(7, entry.DataSource ?? string.Empty);
                        statement.BindText(8, entry.DatabaseName ?? string.Empty);
                        statement.BindText(9, entry.LoginName ?? string.Empty);
                        statement.BindText(10, entry.WorkstationId ?? string.Empty);
                        statement.BindText(11, entry.ClientExecutionId == Guid.Empty ? null : entry.ClientExecutionId.ToString("D"));
                        statement.StepToCompletion();
                        inserted = raw.sqlite3_changes(database.Handle) > 0;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryHistory", "Writing to the local SQLite history store failed", ex);
                return false;
            }
        }

        /// <summary>Reads one page of history together with the total number of matches.</summary>
        public static List<QueryHistoryStoreEntry> Query(QueryHistoryStoreFilter filter, out int total, out string error)
        {
            total = 0;
            error = null;
            var records = new List<QueryHistoryStoreEntry>();
            if (filter == null) return records;
            if (!EnsureInitialized(out error)) return records;

            try
            {
                var clauses = new List<string>();
                var parameters = new List<object>();

                if (filter.FromDate.HasValue)
                {
                    clauses.Add("StartTime >= ?");
                    parameters.Add(ToStorageTime(filter.FromDate.Value.Date));
                }
                if (filter.ToDate.HasValue)
                {
                    clauses.Add("StartTime < ?");
                    parameters.Add(ToStorageTime(filter.ToDate.Value.Date.AddDays(1)));
                }
                AddLike(clauses, parameters, "DataSource", filter.Server);
                AddLike(clauses, parameters, "DatabaseName", filter.Database);
                AddLike(clauses, parameters, "LoginName", filter.Login);

                if (filter.QueryTerms != null)
                {
                    foreach (string term in filter.QueryTerms.Where(t => !string.IsNullOrWhiteSpace(t)))
                        AddLike(clauses, parameters, "QueryText", term);
                }

                string resultClause = ResultClause(filter.ResultKind);
                if (resultClause != null) clauses.Add(resultClause);

                string where = clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
                int pageSize = Math.Max(1, filter.PageSize);
                int pageNumber = Math.Max(1, filter.PageNumber);

                lock (SyncRoot)
                {
                    using (SqliteDatabase database = SqliteDatabase.Open(DatabasePath))
                    {
                        using (SqliteStatement count = database.Prepare("SELECT COUNT(1) FROM QueryHistory" + where + ";"))
                        {
                            count.BindAll(parameters);
                            if (count.Step()) total = (int)Math.Min(int.MaxValue, count.ColumnInt64(0));
                        }

                        string sql = @"SELECT QueryId, StartTime, FinishTime, ElapsedTime, TotalRowsReturned,
                                              ExecResult, QueryText, DataSource, DatabaseName, LoginName, WorkstationId
                                       FROM QueryHistory" + where + @"
                                       ORDER BY StartTime DESC, QueryId DESC
                                       LIMIT " + pageSize + " OFFSET " + ((pageNumber - 1) * pageSize) + ";";

                        using (SqliteStatement statement = database.Prepare(sql))
                        {
                            statement.BindAll(parameters);
                            while (statement.Step())
                            {
                                records.Add(new QueryHistoryStoreEntry
                                {
                                    QueryId = statement.ColumnInt64(0),
                                    StartTime = FromStorageTime(statement.ColumnText(1)),
                                    FinishTime = FromStorageTime(statement.ColumnText(2)),
                                    ElapsedTime = statement.ColumnText(3) ?? string.Empty,
                                    TotalRowsReturned = statement.ColumnInt64(4),
                                    ExecResult = statement.ColumnText(5) ?? string.Empty,
                                    QueryText = statement.ColumnText(6) ?? string.Empty,
                                    DataSource = statement.ColumnText(7) ?? string.Empty,
                                    DatabaseName = statement.ColumnText(8) ?? string.Empty,
                                    LoginName = statement.ColumnText(9) ?? string.Empty,
                                    WorkstationId = statement.ColumnText(10) ?? string.Empty
                                });
                            }
                        }
                    }
                }

                return records;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryHistory", "Reading the local SQLite history store failed", ex);
                return records;
            }
        }

        private static void AddLike(List<string> clauses, List<object> parameters, string column, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            clauses.Add(column + " LIKE ? ESCAPE '!'");
            parameters.Add("%" + EscapeLike(value.Trim()) + "%");
        }

        /// <summary>Escapes LIKE wildcards so a filter matches them literally.</summary>
        private static string EscapeLike(string value)
        {
            return value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
        }

        private static string ResultClause(string resultKind)
        {
            if (string.IsNullOrWhiteSpace(resultKind) || string.Equals(resultKind, "All", StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.Equals(resultKind, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return "(ExecResult LIKE '%success%' OR ExecResult LIKE '%succeed%')";
            if (string.Equals(resultKind, "Cancelled", StringComparison.OrdinalIgnoreCase))
                return "ExecResult LIKE '%cancel%'";

            return "(ExecResult LIKE '%fail%' OR ExecResult LIKE '%error%')";
        }

        /// <summary>Deletes rows older than the retention window; 0 or less keeps everything.</summary>
        public static int DeleteOlderThan(int retentionDays, out string error)
        {
            error = null;
            if (retentionDays <= 0) return 0;
            if (!EnsureInitialized(out error)) return 0;

            try
            {
                lock (SyncRoot)
                {
                    using (SqliteDatabase database = SqliteDatabase.Open(DatabasePath))
                    using (SqliteStatement statement = database.Prepare("DELETE FROM QueryHistory WHERE StartTime < ?;"))
                    {
                        statement.BindText(1, ToStorageTime(DateTime.Now.AddDays(-retentionDays)));
                        statement.StepToCompletion();
                        return raw.sqlite3_changes(database.Handle);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryHistory", "Retention cleanup of the local SQLite history store failed", ex);
                return 0;
            }
        }

        /// <summary>Number of stored rows.</summary>
        public static long Count(out string error)
        {
            error = null;
            if (!EnsureInitialized(out error)) return 0;

            try
            {
                lock (SyncRoot)
                {
                    using (SqliteDatabase database = SqliteDatabase.Open(DatabasePath))
                    using (SqliteStatement statement = database.Prepare("SELECT COUNT(1) FROM QueryHistory;"))
                    {
                        return statement.Step() ? statement.ColumnInt64(0) : 0;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return 0;
            }
        }

        /// <summary>
        /// Imports the JSONL files written by older versions of the extension, so switching to the
        /// SQLite store does not hide the history a user already has.  Both the plain day files and
        /// the "recovery" files (written when a save could not be completed, and wrapped in an
        /// envelope with the reason and capture time) are read.  Duplicate client execution ids are
        /// ignored, which makes the import repeatable.
        /// </summary>
        public static int ImportFromJsonLineFiles(string folder, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
            if (!EnsureInitialized(out error)) return 0;

            // The recovery folder holds the records that a previous configuration failed to save;
            // they are the only copy of that history, so they are imported as well.
            var folders = new List<string> { folder };
            string recoveryFolder = Path.Combine(folder, "recovery");
            if (Directory.Exists(recoveryFolder)) folders.Add(recoveryFolder);

            int imported = 0;
            try
            {
                foreach (string source in folders)
                foreach (string file in Directory.GetFiles(source, "*.jsonl"))
                {
                    foreach (string line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        QueryHistoryEntry entry = TryReadEntry(line);
                        if (entry == null || entry.StartTime == default(DateTime)) continue;

                        if (!TryInsert(entry, out bool wasInserted, out string insertError))
                        {
                            if (error == null) error = insertError;
                        }
                        else if (wasInserted)
                        {
                            imported++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FeatureDiagnostics.Report("QueryHistory", "Importing the text file history failed", ex);
            }

            return imported;
        }

        /// <summary>
        /// Reads one JSONL line.  Day files store a bare entry; recovery files wrap it together
        /// with the reason the save failed, so the nested "Entry" object is unwrapped when present.
        /// </summary>
        private static QueryHistoryEntry TryReadEntry(string line)
        {
            try
            {
                Newtonsoft.Json.Linq.JObject root = Newtonsoft.Json.Linq.JObject.Parse(line);
                Newtonsoft.Json.Linq.JToken payload = root["Entry"] ?? root;
                return payload.ToObject<QueryHistoryEntry>();
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Round-trippable storage format; sorts correctly because it is fixed width.</summary>
        private static string ToStorageTime(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }

        private static DateTime FromStorageTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return default(DateTime);
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : default(DateTime);
        }

        /// <summary>Minimal wrapper over a SQLite handle: statement lifetime, binding and stepping.</summary>
        private sealed class SqliteDatabase : IDisposable
        {
            private sqlite3 handle;

            private SqliteDatabase(sqlite3 handle) { this.handle = handle; }

            public sqlite3 Handle => handle;

            public static SqliteDatabase Open(string path)
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                sqlite3 database;
                int result = raw.sqlite3_open_v2(path, out database, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null);
                if (result != raw.SQLITE_OK || database == null)
                {
                    string message = database != null ? raw.sqlite3_errmsg(database).utf8_to_string() : "unknown error";
                    if (database != null) raw.sqlite3_close_v2(database);
                    throw new InvalidOperationException("Could not open the query history database: " + message);
                }

                // A short busy timeout keeps a concurrent writer from failing immediately.
                raw.sqlite3_busy_timeout(database, 4000);
                return new SqliteDatabase(database);
            }

            public void Execute(string sql)
            {
                // Preparing only compiles the statement; it has to be stepped to take effect.
                using (SqliteStatement statement = Prepare(sql))
                {
                    statement.StepToCompletion();
                }
            }

            public SqliteStatement Prepare(string sql)
            {
                sqlite3_stmt statement;
                int result = raw.sqlite3_prepare_v2(handle, sql, out statement, out string _);
                if (result != raw.SQLITE_OK)
                    throw new InvalidOperationException("Could not prepare a query history statement: " + raw.sqlite3_errmsg(handle).utf8_to_string());
                return new SqliteStatement(handle, statement);
            }

            public void Dispose()
            {
                if (handle == null) return;
                raw.sqlite3_close_v2(handle);
                handle = null;
            }
        }

        private sealed class SqliteStatement : IDisposable
        {
            private readonly sqlite3 database;
            private sqlite3_stmt statement;

            public SqliteStatement(sqlite3 database, sqlite3_stmt statement)
            {
                this.database = database;
                this.statement = statement;
            }

            public void BindText(int index, string value)
            {
                if (value == null) raw.sqlite3_bind_null(statement, index);
                else raw.sqlite3_bind_text(statement, index, value);
            }

            public void BindInt64(int index, long value) => raw.sqlite3_bind_int64(statement, index, value);

            public void BindAll(IEnumerable<object> values)
            {
                int index = 1;
                foreach (object value in values ?? Enumerable.Empty<object>())
                {
                    if (value is long longValue) BindInt64(index, longValue);
                    else BindText(index, Convert.ToString(value, CultureInfo.InvariantCulture));
                    index++;
                }
            }

            /// <summary>Advances the statement, returning true while a row is available.</summary>
            public bool Step()
            {
                int result = raw.sqlite3_step(statement);
                if (result == raw.SQLITE_ROW) return true;
                if (result == raw.SQLITE_DONE) return false;
                throw new InvalidOperationException("A query history statement failed: " + raw.sqlite3_errmsg(database).utf8_to_string());
            }

            public void StepToCompletion()
            {
                while (Step()) { }
            }

            public string ColumnText(int index)
            {
                return raw.sqlite3_column_text(statement, index).utf8_to_string();
            }

            public long ColumnInt64(int index)
            {
                return raw.sqlite3_column_int64(statement, index);
            }

            public void Dispose()
            {
                if (statement == null) return;
                statement.Dispose();
                statement = null;
            }
        }
    }
}
