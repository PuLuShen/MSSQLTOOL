using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace MSSQLTool.Completion
{
    internal static class SqlMetadataCache
    {
        private sealed class Entry
        {
            public DateTime CreatedUtc;
            public Lazy<Task<MetadataSnapshot>> Work;
            public Task<MetadataSnapshot> Task => Work.Value;
        }
        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // Snapshot lifetime is a background freshness safety net only, not a
        // user-visible reload: once expired, callers keep getting the stale
        // snapshot immediately while a replacement loads in the background
        // (stale-while-revalidate). SQL Prompt follows the same model — its
        // cache stays resident for the whole session and is refreshed by DDL
        // detection or an explicit user refresh, never by blocking reloads.
        // Schema changes executed from this plugin invalidate the entry
        // immediately (Invalidate), so the timer only covers external changes.
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        public static Task<MetadataSnapshot> GetAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString);
            Entry entry = GetOrCreate(key, () => Load(info),
                LocalizationManager.T("Loading SQL completion metadata..."));
            return AwaitWithCancellation(entry.Task, token);
        }

        public static Task<MetadataSnapshot> GetDatabaseAsync(ScriptFactoryAccess.ConnectionInfo info, string database, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString) || string.IsNullOrWhiteSpace(database)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString, database);
            Entry entry = GetOrCreate(key, () => Load(info, database),
                LocalizationManager.Format("Loading SQL completion metadata for database {0}...", database));
            return AwaitWithCancellation(entry.Task, token);
        }

        public static Task<MetadataSnapshot> GetLinkedServerAsync(ScriptFactoryAccess.ConnectionInfo info, string linkedServer, string database, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString) + "|linked|" + linkedServer + "|" + (database ?? string.Empty);
            Entry entry = GetOrCreate(key, () => LoadLinked(info, linkedServer, database),
                LocalizationManager.Format("Loading SQL completion metadata from linked server {0}...", linkedServer));
            return AwaitWithCancellation(entry.Task, token);
        }

        /// <summary>
        /// Warms the cache for a connection in the background, without the
        /// status bar animation. Used when the active connection changes so the
        /// first completion request does not pay the schema-load latency.
        /// </summary>
        public static Task PrefetchAsync(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.CompletedTask;
            Entry entry = GetOrCreate(GetKey(info.FullConnectionString), () => Load(info), null);
            return entry.Task;
        }

        private static Entry GetOrCreate(string key, Func<MetadataSnapshot> loader, string statusMessage)
        {
            var replacement = new Entry
            {
                CreatedUtc = DateTime.UtcNow,
                Work = new Lazy<Task<MetadataSnapshot>>(
                    () => LoadWithFeedbackAsync(loader, statusMessage),
                    LazyThreadSafetyMode.ExecutionAndPublication)
            };

            Entry current = Cache.GetOrAdd(key, replacement);
            if (IsFresh(current))
            {
                // Sliding expiration: as long as the snapshot keeps being used
                // (e.g. every newly opened query window), its lifetime is
                // renewed so an active connection is refreshed in the
                // background only, never synchronously per window.
                current.CreatedUtc = DateTime.UtcNow;
                return current;
            }

            if (current.Work.IsValueCreated && current.Task.IsCompleted)
            {
                // Completed snapshot (even one with errors): hand it out
                // right away and refresh in the background so completions
                // never block on a reload after the lifetime elapsed.
                if (Cache.TryUpdate(key, replacement, current))
                {
                    StartBackgroundRefresh(replacement);
                    return current;
                }
                return Cache.GetOrAdd(key, replacement);
            }

            // Not started yet, or faulted/canceled: (re)start and wait.
            if (Cache.TryUpdate(key, replacement, current))
                return replacement;
            return Cache.GetOrAdd(key, replacement);
        }

        private static async void StartBackgroundRefresh(Entry entry)
        {
            try
            {
                await entry.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Background SQL completion metadata refresh failed");
            }
        }

        private static async Task<MetadataSnapshot> LoadWithFeedbackAsync(Func<MetadataSnapshot> loader, string statusMessage)
        {
            // statusMessage == null marks a background prefetch: no status bar
            // animation, the load simply warms the cache for the next request.
            if (string.IsNullOrEmpty(statusMessage))
                return await Task.Run(loader, CancellationToken.None).ConfigureAwait(false);

            using (StatusFeedback.Begin(statusMessage))
                return await Task.Run(loader, CancellationToken.None).ConfigureAwait(false);
        }

        private static bool IsFresh(Entry entry)
        {
            TimeSpan age = DateTime.UtcNow - entry.CreatedUtc;
            if (age >= Lifetime) return false;
            if (!entry.Work.IsValueCreated) return true;
            Task<MetadataSnapshot> task = entry.Task;
            if (task.IsFaulted || task.IsCanceled) return age < TimeSpan.FromSeconds(15);
            if (task.IsCompleted && !string.IsNullOrWhiteSpace(task.Result?.ErrorMessage)) return age < TimeSpan.FromSeconds(30);
            return true;
        }

        public static void Invalidate(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return;
            var builder = new SqlConnectionStringBuilder(info.FullConnectionString);
            string prefix = builder.DataSource + "|";
            foreach (string key in Cache.Keys)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    Cache.TryRemove(key, out Entry _);
            RaiseInvalidated();
        }

        public static void InvalidateAll() => Cache.Clear();

        /// <summary>Raised after cache entries were dropped so dependent caches (e.g. merged cross-database snapshots) can be discarded.</summary>
        internal static event Action Invalidated;

        private static void RaiseInvalidated()
        {
            Action handlers = Invalidated;
            if (handlers != null) handlers();
        }

        internal static bool ShouldInvalidateAfterExecution(string sql)
        {
            string executableSql = SqlTextContext.MaskCommentsAndStrings(sql ?? string.Empty);
            return System.Text.RegularExpressions.Regex.IsMatch(executableSql,
                       @"\b(?:CREATE|ALTER|DROP|RENAME|TRUNCATE)\s+(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION|SYNONYM|TYPE|SCHEMA)\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                   || System.Text.RegularExpressions.Regex.IsMatch(executableSql, @"\bsp_rename\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static bool TryGetCached(ScriptFactoryAccess.ConnectionInfo info, out MetadataSnapshot snapshot)
        {
            snapshot = null;
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return false;
            if (!Cache.TryGetValue(GetKey(info.FullConnectionString), out Entry entry) || !entry.Work.IsValueCreated) return false;
            Task<MetadataSnapshot> task = entry.Task;
            if (!task.IsCompleted || task.IsCanceled || task.IsFaulted) return false;
            snapshot = task.Result;
            return snapshot != null;
        }

        public static async Task<MetadataSnapshot> RefreshAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return MetadataSnapshot.Empty;
            Invalidate(info);
            string key = GetKey(info.FullConnectionString);
            Entry entry = GetOrCreate(key, () => Load(info),
                LocalizationManager.T("Refreshing SQL completion metadata..."));
            return await AwaitWithCancellation(entry.Task, token).ConfigureAwait(false);
        }

        public static async Task ResolveSynonymColumnsAsync(ScriptFactoryAccess.ConnectionInfo info, MetadataSnapshot snapshot, CompletionContext context, CancellationToken token)
        {
            if (info == null || snapshot == null || context == null) return;
            string requested = (context.Qualifier ?? context.TargetObject ?? string.Empty).Replace("[", "").Replace("]", "");
            DatabaseObjectMetadata synonym = snapshot.Objects.FirstOrDefault(o => o.Kind == CompletionItemKind.Synonym && o.Columns.Count == 0
                && !string.IsNullOrWhiteSpace(o.SynonymBaseObjectName)
                && (string.Equals(o.Name, requested, StringComparison.OrdinalIgnoreCase) || requested.EndsWith("." + o.Name, StringComparison.OrdinalIgnoreCase)));
            if (synonym == null) return;
            var columns = await Task.Run(() => DescribeSynonym(info, synonym.SynonymBaseObjectName, token), token).ConfigureAwait(false);
            lock (synonym.Columns)
                if (synonym.Columns.Count == 0) synonym.Columns.AddRange(columns);
        }

        private static List<ColumnMetadata> DescribeSynonym(ScriptFactoryAccess.ConnectionInfo info, string baseObjectName, CancellationToken token)
        {
            var result = new List<ColumnMetadata>();
            using (var connection = CreateMetadataConnection(info))
            using (var command = new SqlCommand("EXEC sys.sp_describe_first_result_set @tsql=@sql, @params=NULL, @browse_information_mode=0;", connection))
            {
                command.CommandTimeout = 10;
                command.Parameters.AddWithValue("@sql", "SELECT * FROM " + baseObjectName);
                using (token.Register(() => { try { command.Cancel(); } catch { } }))
                {
                    token.ThrowIfCancellationRequested();
                connection.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (!reader.IsDBNull(2)) result.Add(new ColumnMetadata { Name = reader.GetString(2), IsNullable = !reader.IsDBNull(3) && reader.GetBoolean(3), DataType = reader.IsDBNull(5) ? "" : reader.GetString(5) });
                }
            }
            return result;
        }

        private static string GetKey(string connectionString)
        {
            var b = new SqlConnectionStringBuilder(connectionString)
            {
                TrustServerCertificate = SettingsManager.GetSqlCompletionSettings().trustServerCertificate
            };
            return GetKey(b);
        }

        private static string GetKey(SqlConnectionStringBuilder b)
        {
            return string.Join("|", b.DataSource, b.InitialCatalog, b.IntegratedSecurity, b.UserID,
                b.Authentication, b.ApplicationIntent, b.Encrypt, b.TrustServerCertificate);
        }

        private static string GetKey(string connectionString, string database)
        {
            var b = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = database,
                TrustServerCertificate = SettingsManager.GetSqlCompletionSettings().trustServerCertificate
            };
            return GetKey(b);
        }

        private static async Task<MetadataSnapshot> AwaitWithCancellation(Task<MetadataSnapshot> task, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (task != await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false)) throw new OperationCanceledException(token);
            }
            return await task.ConfigureAwait(false);
        }

        // Two-tier load: the fast tier (this method) returns as soon as the
        // object names and columns — the data every completion popup needs —
        // are available, typically within a second or two. The heavy catalog
        // queries that only enrich tooltips, JOIN hints and detail views
        // (definitions, foreign keys, indexes, constraints, descriptions,
        // row counts) continue in the background (Enrich) on the same
        // snapshot instance, so later requests find them already merged in.
        private static MetadataSnapshot Load(ScriptFactoryAccess.ConnectionInfo info, string database = null)
        {
            var builder = new SqlConnectionStringBuilder(info.FullConnectionString);
            if (!string.IsNullOrWhiteSpace(database)) builder.InitialCatalog = database;
            var result = new MetadataSnapshot { LoadedUtc = DateTime.UtcNow, Scope = builder.DataSource + "/" + builder.InitialCatalog };
            var errors = new List<string>();
            // The light, independent catalog queries (databases, linked servers,
            // parameters, synonyms/sequences/types, compatibility level) run on a
            // second connection concurrently with the object/column chain,
            // shortening the first-load latency without touching shared lists.
            var backgroundSchemas = new List<string>();
            var backgroundObjects = new List<DatabaseObjectMetadata>();
            var backgroundParameters = new List<RoutineParameterMetadata>();
            var backgroundErrors = new List<string>();
            Exception backgroundFailure = null;
            Task backgroundTask = Task.Run(() =>
            {
                try
                {
                    using (var connection = CreateMetadataConnection(info, database))
                    {
                        connection.Open();
                        TryLoadDatabases(connection, result, backgroundErrors);
                        TryLoadLinkedServers(connection, result, backgroundErrors);
                        TryLoadParameters(connection, backgroundParameters, backgroundErrors);
                        TryLoadSupplementalObjects(connection, backgroundSchemas, backgroundObjects, backgroundErrors);
                        TryLoadCompatibilityLevel(connection, result, backgroundErrors);
                    }
                }
                catch (Exception ex) { backgroundFailure = ex; }
            });
            try
            {
                using (var connection = CreateMetadataConnection(info, database))
                {
                    connection.Open();
                    TryLoadObjects(connection, result, errors);
                    // A separate command: if the column join is slow, the object list stays complete.
                    TryLoadColumns(connection, result, errors);
                }
            }
            catch (Exception ex) { RecordFailure("connection", ex, errors); }
            backgroundTask.Wait();
            result.Parameters.AddRange(backgroundParameters);
            foreach (string schema in backgroundSchemas)
                if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
            result.Objects.AddRange(backgroundObjects);
            errors.AddRange(backgroundErrors);
            if (backgroundFailure != null) RecordFailure("supplemental metadata connection", backgroundFailure, errors);
            if (errors.Count > 0)
            {
                result.ErrorMessage = string.Join("; ", errors);
                result.IsPartial = result.Objects.Count > 0 || result.Parameters.Count > 0;
            }
            if (result.Objects.Count > 0)
                _ = Task.Run(() => Enrich(info, database, result));
            return result;
        }

        // Background enrichment pass. Runs after the fast tier published the
        // snapshot, so everything it writes must be safe against concurrent
        // readers: scalar property writes are fine, and the list-shaped data
        // (foreign keys, indexes, check constraints) is built privately and
        // swapped in atomically. Failures are log-only — the snapshot is
        // already in use and stays valid without the optional details.
        private static void Enrich(ScriptFactoryAccess.ConnectionInfo info, string database, MetadataSnapshot result)
        {
            try
            {
                using (var connection = CreateMetadataConnection(info, database))
                {
                    connection.Open();
                    // Many of the queries below look objects up per row; a
                    // hash index keeps those lookups O(1) instead of scanning
                    // the whole object list for every single row.
                    var index = new SnapshotObjectIndex(result);
                    TryLoadDefinitions(connection, result, index);
                    TryLoadForeignKeys(connection, result, index);
                    TryLoadColumnDetails(connection, result, index);
                    TryLoadIndexes(connection, result, index);
                    TryLoadCheckConstraints(connection, result, index);
                    TryLoadRowCounts(connection, result, index);
                    // Tooltips rendered during the fast-tier window cached a
                    // detail string without definitions/descriptions; drop
                    // those caches so the next popup picks the enriched data.
                    foreach (DatabaseObjectMetadata item in result.Objects)
                        item.ResetDetailCache();
                }
            }
            catch (Exception ex) { RecordOptionalFailure("enrichment pass", ex); }
        }

        private static MetadataSnapshot LoadLinked(ScriptFactoryAccess.ConnectionInfo info, string linkedServer, string database)
        {
            var result = new MetadataSnapshot { LoadedUtc = DateTime.UtcNow, Scope = linkedServer + "/" + (database ?? string.Empty) };
            var errors = new List<string>();
            try
            {
                string server = DatabaseIdentifier.SqlServerPart(linkedServer);
                using (var connection = CreateMetadataConnection(info))
                {
                    connection.Open();
                    if (string.IsNullOrWhiteSpace(database))
                    {
                        using (var command = new SqlCommand("SELECT [name] FROM " + server + ".[master].[sys].[databases] WHERE [state]=0 ORDER BY [name];", connection))
                        using (var reader = command.ExecuteReader())
                        {
                            var names = new List<string>();
                            while (reader.Read()) names.Add(reader.GetString(0));
                            result.LinkedServerDatabases[linkedServer] = names;
                        }
                    }
                    else
                    {
                        string catalog = server + "." + DatabaseIdentifier.SqlServerPart(database);
                        // Objects first (no column join, no server-side sort) and then the columns: a slow
                        // or timing-out column query on a linked server must not cost the object list.
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandTimeout = 20;
                            command.CommandText = "SELECT s.name,o.name,o.type,o.is_ms_shipped "
                                + "FROM " + catalog + ".[sys].[all_objects] o JOIN " + catalog + ".[sys].[schemas] s ON s.schema_id=o.schema_id "
                                + "WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT') AND (o.is_ms_shipped=0 OR s.name='sys');";
                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2);
                                    if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                                    result.Objects.Add(new DatabaseObjectMetadata { Server = linkedServer, Database = database, Schema = schema, Name = name, Kind = ToKind(type), IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT", IsSystem = !reader.IsDBNull(3) && reader.GetBoolean(3) });
                                }
                            }
                        }

                        var index = new SnapshotObjectIndex(result);
                        try
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandTimeout = 25;
                                command.CommandText = "SELECT s.name,o.name,c.name,ty.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed,c.column_id "
                                    + "FROM " + catalog + ".[sys].[all_columns] c JOIN " + catalog + ".[sys].[all_objects] o ON o.object_id=c.object_id "
                                    + "JOIN " + catalog + ".[sys].[schemas] s ON s.schema_id=o.schema_id "
                                    + "JOIN " + catalog + ".[sys].[types] ty ON ty.user_type_id=c.user_type_id "
                                    + "WHERE o.type IN ('U','V','FN','IF','TF','FS','FT') AND o.is_ms_shipped=0;";
                                using (var reader = command.ExecuteReader())
                                {
                                    while (reader.Read())
                                    {
                                        DatabaseObjectMetadata item = index.Find(reader.GetString(0), reader.GetString(1));
                                        if (item == null) continue;
                                        lock (item.Columns)
                                            item.Columns.Add(new ColumnMetadata { Name = reader.GetString(2), DataType = reader.IsDBNull(3) ? "" : reader.GetString(3), MaxLength = reader.IsDBNull(4) ? (short)0 : reader.GetInt16(4), Precision = reader.IsDBNull(5) ? (byte)0 : reader.GetByte(5), Scale = reader.IsDBNull(6) ? (byte)0 : reader.GetByte(6), IsNullable = !reader.IsDBNull(7) && reader.GetBoolean(7), IsIdentity = !reader.IsDBNull(8) && reader.GetBoolean(8), IsComputed = !reader.IsDBNull(9) && reader.GetBoolean(9), Ordinal = reader.IsDBNull(10) ? 0 : reader.GetInt32(10) });
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { RecordOptionalFailure("linked server columns", ex); }

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandTimeout = 20;
                            command.CommandText = "SELECT s.name+'.'+o.name,p.name,ty.name,p.is_output,p.max_length,p.precision,p.scale,p.parameter_id,p.has_default_value "
                                + "FROM " + catalog + ".[sys].[all_parameters] p JOIN " + catalog + ".[sys].[all_objects] o ON o.object_id=p.object_id "
                                + "JOIN " + catalog + ".[sys].[schemas] s ON s.schema_id=o.schema_id "
                                + "JOIN " + catalog + ".[sys].[types] ty ON ty.user_type_id=p.user_type_id "
                                + "WHERE o.type IN ('P','PC','X','FN','IF','TF','FS','FT') AND p.parameter_id>0 ORDER BY o.object_id,p.parameter_id;";
                            using (var reader = command.ExecuteReader())
                                while (reader.Read()) result.Parameters.Add(new RoutineParameterMetadata
                                {
                                    ObjectName = linkedServer + "." + database + "." + reader.GetString(0),
                                    Name = reader.GetString(1),
                                    DataType = reader.GetString(2),
                                    IsOutput = reader.GetBoolean(3), MaxLength = reader.GetInt16(4), Precision = reader.GetByte(5), Scale = reader.GetByte(6), Ordinal = reader.GetInt32(7), HasDefaultValue = reader.GetBoolean(8)
                                });
                        }
                    }
                }
            }
            catch (Exception ex) { RecordFailure("linked server " + linkedServer, ex, errors); }
            if (errors.Count > 0) { result.ErrorMessage = string.Join("; ", errors); result.IsPartial = result.Objects.Count > 0; }
            return result;
        }

        private static void TryLoadDatabases(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = new SqlCommand("SELECT [name] FROM sys.databases WHERE [state]=0 AND HAS_DBACCESS([name])=1 ORDER BY [name];", connection))
                {
                    command.CommandTimeout = 10;
                    using (var reader = command.ExecuteReader()) while (reader.Read()) result.Databases.Add(reader.GetString(0));
                }
            }
            catch (Exception ex) { RecordFailure("databases", ex, errors); }
        }

        private static void TryLoadLinkedServers(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = new SqlCommand("SELECT [name] FROM sys.servers WHERE server_id > 0 AND is_linked=1 ORDER BY [name];", connection))
                using (var reader = command.ExecuteReader()) while (reader.Read()) result.LinkedServers.Add(reader.GetString(0));
            }
            catch (Exception ex) { RecordFailure("linked servers", ex, errors); }
        }

        private static void TryLoadObjects(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 20;
                    // Object names only.  Joining sys.all_columns and sorting the whole result set made
                    // this query heavy enough to time out on a busy server, and a timeout left the
                    // snapshot with just the rows read before it - a handful of objects, with no way to
                    // complete the list afterwards.
                    command.CommandText = @"
SELECT s.name, o.name, o.type, o.is_ms_shipped
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys');";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2);
                            if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                            result.Objects.Add(new DatabaseObjectMetadata
                            {
                                Database = connection.Database,
                                Schema = schema,
                                Name = name,
                                Kind = ToKind(type),
                                IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT",
                                IsSystem = !reader.IsDBNull(3) && reader.GetBoolean(3)
                            });
                        }
                    }
                }

                // Sorted in memory instead of in SQL: the server no longer sorts the whole result set
                // before it can return the first row.
                result.Objects.Sort((left, right) =>
                {
                    int bySchema = string.Compare(left.Schema, right.Schema, StringComparison.OrdinalIgnoreCase);
                    if (bySchema != 0) return bySchema;
                    int byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                    return byName != 0 ? byName : left.Kind.CompareTo(right.Kind);
                });
            }
            catch (Exception ex)
            {
                RecordFailure("objects and columns", ex, errors);
                TryLoadObjectNamesFallback(connection, result, errors);
            }
        }

        /// <summary>
        /// Columns for the objects that were just loaded.  A separate command with its own failure path,
        /// so a slow column join can never take the object list down with it.
        /// </summary>
        private static void TryLoadColumns(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            if (result.Objects.Count == 0) return;

            string schema = string.Empty, name = string.Empty;
            try
            {
                var index = new SnapshotObjectIndex(result);
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 25;
                    command.CommandText = @"
SELECT s.name, o.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, c.column_id
FROM sys.all_columns c
JOIN sys.all_objects o ON o.object_id=c.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE o.type IN ('U','V','FN','IF','TF','FS','FT')
  AND o.is_ms_shipped=0;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            schema = reader.GetString(0);
                            name = reader.GetString(1);
                            DatabaseObjectMetadata item = index.Find(schema, name);
                            if (item == null) continue;

                            var column = new ColumnMetadata
                            {
                                Name = reader.GetString(2),
                                DataType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                MaxLength = reader.IsDBNull(4) ? (short)0 : reader.GetInt16(4),
                                Precision = reader.IsDBNull(5) ? (byte)0 : reader.GetByte(5),
                                Scale = reader.IsDBNull(6) ? (byte)0 : reader.GetByte(6),
                                IsNullable = !reader.IsDBNull(7) && reader.GetBoolean(7),
                                IsIdentity = !reader.IsDBNull(8) && reader.GetBoolean(8),
                                IsComputed = !reader.IsDBNull(9) && reader.GetBoolean(9),
                                Ordinal = reader.IsDBNull(10) ? 0 : reader.GetInt32(10)
                            };
                            lock (item.Columns) item.Columns.Add(column);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Recorded (not swallowed) so the snapshot stays marked as partial and the cache retries
                // it soon; the object list itself is complete and keeps working.
                RecordFailure("object columns", ex, errors);
            }
        }

        private static void TryLoadDefinitions(SqlConnection connection, MetadataSnapshot result, SnapshotObjectIndex index)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name, o.name, OBJECT_DEFINITION(o.object_id)
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('V','P','PC','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys');";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = index.Find(reader.GetString(0), reader.GetString(1));
                            if (item != null && !reader.IsDBNull(2)) item.Definition = reader.GetString(2);
                        }
                    }
                }
            }
            catch (Exception ex) { RecordOptionalFailure("object definitions", ex); }
        }

        private static void TryLoadObjectNamesFallback(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                // The command that just failed may have left the connection closed; reopening it keeps
                // the credentials the caller already established.
                if (connection.State != System.Data.ConnectionState.Open) connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    // No ORDER BY: the names are sorted in memory, which is what lets the server answer
                    // this query while the object list is still incomplete.
                    command.CommandText = @"
SELECT s.name,o.name,o.type,o.is_ms_shipped
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys');";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string schema = reader.GetString(0);
                            string name = reader.GetString(1);
                            string type = reader.GetString(2);
                            if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                            if (result.Objects.Any(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
                                && o.Kind == ToKind(type))) continue;
                            result.Objects.Add(new DatabaseObjectMetadata
                            {
                                Database = connection.Database,
                                Schema = schema,
                                Name = name,
                                Kind = ToKind(type),
                                IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT",
                                IsSystem = !reader.IsDBNull(3) && reader.GetBoolean(3)
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { RecordFailure("object names fallback", ex, errors); }
        }

        private static void TryLoadForeignKeys(SqlConnection connection, MetadataSnapshot result, SnapshotObjectIndex index)
        {
            try
            {
                var foreignKeys = new List<ForeignKeyMetadata>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT DB_NAME()+'.'+ps.name+'.'+po.name,DB_NAME()+'.'+rs.name+'.'+ro.name,fk.object_id,pc.name,rc.name
FROM sys.foreign_keys fk JOIN sys.objects po ON po.object_id=fk.parent_object_id
JOIN sys.schemas ps ON ps.schema_id=po.schema_id JOIN sys.objects ro ON ro.object_id=fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id=ro.schema_id JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.columns pc ON pc.object_id=po.object_id AND pc.column_id=fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id=ro.object_id AND rc.column_id=fkc.referenced_column_id
ORDER BY fk.object_id,fkc.constraint_column_id;";
                    using (var reader = command.ExecuteReader())
                    {
                        ForeignKeyMetadata current = null; int id = -1;
                        while (reader.Read()) { int next = reader.GetInt32(2); if (next != id) { current = new ForeignKeyMetadata { ParentObject = reader.GetString(0), ReferencedObject = reader.GetString(1) }; foreignKeys.Add(current); id = next; } current.ParentColumns.Add(reader.GetString(3)); current.ReferencedColumns.Add(reader.GetString(4)); }
                    }
                    foreach (ForeignKeyMetadata foreignKey in foreignKeys)
                    {
                        DatabaseObjectMetadata parent = index.Find(foreignKey.ParentObject);
                        if (parent == null) continue;
                        foreach (string columnName in foreignKey.ParentColumns)
                        {
                            ColumnMetadata column = parent.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                            if (column != null) column.IsForeignKey = true;
                        }
                    }
                    result.SetForeignKeys(foreignKeys);
                }
            }
            catch (Exception ex) { RecordOptionalFailure("foreign keys", ex); }
        }

        private static void TryLoadColumnDetails(SqlConnection connection, MetadataSnapshot result, SnapshotObjectIndex index)
        {
            try
            {
                // Object-level descriptions: one row per described object instead
                // of one row per column repeating the object description.
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,o.name,CONVERT(nvarchar(4000),oep.value)
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.extended_properties oep ON oep.major_id=o.object_id AND oep.minor_id=0 AND oep.name=N'MS_Description'
WHERE o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT');";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = index.Find(reader.GetString(0), reader.GetString(1));
                            if (item != null && !reader.IsDBNull(2)) item.Description = reader.GetString(2);
                        }
                    }
                }

                // Column defaults and descriptions: only columns that actually
                // have one, instead of a row for every column in the database.
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,o.name,c.name,dc.definition,CONVERT(nvarchar(4000),cep.value)
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.all_columns c ON c.object_id=o.object_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
LEFT JOIN sys.extended_properties cep ON cep.major_id=o.object_id AND cep.minor_id=c.column_id AND cep.name=N'MS_Description'
WHERE o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT')
  AND (dc.object_id IS NOT NULL OR cep.major_id IS NOT NULL);";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = index.Find(reader.GetString(0), reader.GetString(1));
                            if (item == null) continue;
                            ColumnMetadata column = item.Columns.FirstOrDefault(c => string.Equals(c.Name, reader.GetString(2), StringComparison.OrdinalIgnoreCase));
                            if (column == null) continue;
                            if (!reader.IsDBNull(3)) column.DefaultDefinition = reader.GetString(3);
                            if (!reader.IsDBNull(4)) column.Description = reader.GetString(4);
                        }
                    }
                }
            }
            catch (Exception ex) { RecordOptionalFailure("column defaults and descriptions", ex); }
        }

        private static void TryLoadIndexes(SqlConnection connection, MetadataSnapshot result, SnapshotObjectIndex index)
        {
            try
            {
                // Built per object into private lists and swapped in at the
                // end: readers of a published snapshot never see a partially
                // populated index list.
                var byObject = new Dictionary<string, List<IndexMetadata>>(StringComparer.OrdinalIgnoreCase);
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,o.name,i.name,i.type_desc,i.is_unique,i.is_primary_key,
       ic.is_included_column,ic.key_ordinal,c.name
FROM sys.indexes i
JOIN sys.objects o ON o.object_id=i.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.index_columns ic ON ic.object_id=o.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.index_id>0 AND i.is_hypothetical=0
ORDER BY s.name,o.name,i.index_id,ic.is_included_column,ic.key_ordinal,ic.index_column_id;";
                    using (var reader = command.ExecuteReader())
                    {
                        IndexMetadata currentIndex = null;
                        string currentKey = null;
                        while (reader.Read())
                        {
                            string objectName = reader.GetString(0) + "." + reader.GetString(1);
                            string indexName = reader.GetString(2);
                            string key = objectName + "|" + indexName;
                            if (!string.Equals(key, currentKey, StringComparison.OrdinalIgnoreCase))
                            {
                                currentIndex = new IndexMetadata
                                {
                                    Name = indexName, TypeDescription = reader.GetString(3),
                                    IsUnique = reader.GetBoolean(4), IsPrimaryKey = reader.GetBoolean(5)
                                };
                                if (!byObject.TryGetValue(objectName, out List<IndexMetadata> list)) byObject[objectName] = list = new List<IndexMetadata>();
                                list.Add(currentIndex);
                                currentKey = key;
                            }
                            if (currentIndex == null) continue;
                            string columnName = reader.GetString(8);
                            if (reader.GetBoolean(6)) currentIndex.IncludedColumns.Add(columnName);
                            else currentIndex.KeyColumns.Add(columnName);
                        }
                    }
                }
                foreach (var pair in byObject)
                {
                    string[] objectParts = pair.Key.Split('.');
                    DatabaseObjectMetadata item = index.Find(objectParts.Length > 1 ? objectParts[0] : string.Empty, objectParts[objectParts.Length - 1]);
                    if (item == null) continue;
                    foreach (IndexMetadata metadata in pair.Value)
                        foreach (string columnName in metadata.KeyColumns)
                        {
                            ColumnMetadata column = item.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                            if (column != null)
                            {
                                column.IsPrimaryKey |= metadata.IsPrimaryKey;
                                column.IsUnique |= metadata.IsUnique;
                            }
                        }
                    item.SetIndexes(pair.Value);
                }
            }
            catch (Exception ex) { RecordOptionalFailure("indexes", ex); }
        }

        private static void TryLoadCheckConstraints(SqlConnection connection, MetadataSnapshot result, SnapshotObjectIndex index)
        {
            try
            {
                var byObject = new Dictionary<string, List<CheckConstraintMetadata>>(StringComparer.OrdinalIgnoreCase);
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 10;
                    command.CommandText = @"
SELECT s.name,o.name,cc.name,cc.definition
FROM sys.check_constraints cc
JOIN sys.objects o ON o.object_id=cc.parent_object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
ORDER BY s.name,o.name,cc.name;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            string objectName = reader.GetString(0) + "." + reader.GetString(1);
                            if (!byObject.TryGetValue(objectName, out List<CheckConstraintMetadata> list)) byObject[objectName] = list = new List<CheckConstraintMetadata>();
                            list.Add(new CheckConstraintMetadata { Name = reader.GetString(2), Definition = reader.GetString(3) });
                        }
                }
                foreach (var pair in byObject)
                {
                    string[] objectParts = pair.Key.Split('.');
                    index.Find(objectParts.Length > 1 ? objectParts[0] : string.Empty, objectParts[objectParts.Length - 1])?.SetCheckConstraints(pair.Value);
                }
            }
            catch (Exception ex) { RecordOptionalFailure("check constraints", ex); }
        }

        private static void TryLoadRowCounts(SqlConnection connection, MetadataSnapshot result, SnapshotObjectIndex index)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 10;
                    command.CommandText = @"
SELECT s.name,o.name,SUM(ps.row_count)
FROM sys.dm_db_partition_stats ps
JOIN sys.objects o ON o.object_id=ps.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type='U' AND ps.index_id IN (0,1)
GROUP BY s.name,o.name;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = index.Find(reader.GetString(0), reader.GetString(1));
                            if (item != null && !reader.IsDBNull(2)) item.EstimatedRowCount = reader.GetInt64(2);
                        }
                }
            }
            catch (Exception ex) { RecordOptionalFailure("estimated row counts", ex); }
        }

        private sealed class SnapshotObjectIndex
        {
            private readonly Dictionary<string, DatabaseObjectMetadata> bySchemaAndName
                = new Dictionary<string, DatabaseObjectMetadata>(StringComparer.OrdinalIgnoreCase);

            public SnapshotObjectIndex(MetadataSnapshot snapshot)
            {
                foreach (DatabaseObjectMetadata item in snapshot.Objects)
                {
                    string key = (item.Schema ?? string.Empty) + "|" + item.Name;
                    if (!bySchemaAndName.ContainsKey(key)) bySchemaAndName[key] = item;
                }
            }

            public DatabaseObjectMetadata Find(string schema, string name)
                => bySchemaAndName.TryGetValue((schema ?? string.Empty) + "|" + name, out DatabaseObjectMetadata item) ? item : null;

            public DatabaseObjectMetadata Find(string qualifiedName)
            {
                string clean = DatabaseIdentifier.NormalizeSqlServer(qualifiedName ?? string.Empty);
                string[] parts = clean.Split('.');
                return Find(parts.Length > 1 ? parts[parts.Length - 2] : string.Empty, parts[parts.Length - 1]);
            }
        }

        private static void RecordOptionalFailure(string area, Exception exception)
        {
            FeatureDiagnostics.Report("SQL Completion Metadata", "Optional metadata unavailable: " + area, exception);
            MSSQLToolPackage._logger?.Error(exception, "Optional SQL completion metadata unavailable: " + area);
        }

        private static void TryLoadParameters(SqlConnection connection, List<RoutineParameterMetadata> parameters, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
 SELECT DB_NAME()+'.'+s.name+'.'+o.name,p.name,ty.name,p.is_output,p.max_length,p.precision,p.scale,p.parameter_id,p.has_default_value
FROM sys.all_parameters p JOIN sys.all_objects o ON o.object_id=p.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.types ty ON ty.user_type_id=p.user_type_id
WHERE o.type IN ('P','PC','X','FN','IF','TF','FS','FT') AND p.parameter_id>0
ORDER BY o.object_id,p.parameter_id;";
                    using (var reader = command.ExecuteReader()) while (reader.Read()) parameters.Add(new RoutineParameterMetadata { ObjectName = reader.GetString(0), Name = reader.GetString(1), DataType = reader.GetString(2), IsOutput = reader.GetBoolean(3), MaxLength = reader.GetInt16(4), Precision = reader.GetByte(5), Scale = reader.GetByte(6), Ordinal = reader.GetInt32(7), HasDefaultValue = reader.GetBoolean(8) });
                }
            }
            catch (Exception ex) { RecordFailure("parameters", ex, errors); }
        }

        private static void RecordFailure(string area, Exception exception, List<string> errors)
        {
            errors.Add(area + ": " + exception.Message);
            FeatureDiagnostics.Report("SQL Completion Metadata", "Could not load " + area, exception);
            MSSQLToolPackage._logger?.Error(exception, "SQL completion metadata could not load " + area);
        }

        private static SqlConnection CreateMetadataConnection(ScriptFactoryAccess.ConnectionInfo info, string database = null)
        {
            bool trustServerCertificate = SettingsManager.GetSqlCompletionSettings().trustServerCertificate;
            return info.CreateSqlConnection(database, trustServerCertificate);
        }

        private static void TryLoadSupplementalObjects(SqlConnection connection, List<string> schemas, List<DatabaseObjectMetadata> objects, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,n.name,'SN',n.base_object_name FROM sys.synonyms n JOIN sys.schemas s ON s.schema_id=n.schema_id
UNION ALL SELECT s.name,q.name,'SQ',NULL FROM sys.sequences q JOIN sys.schemas s ON s.schema_id=q.schema_id
UNION ALL SELECT s.name,t.name,'TY',NULL FROM sys.types t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_user_defined=1;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2);
                            if (!schemas.Contains(schema)) schemas.Add(schema);
                            objects.Add(new DatabaseObjectMetadata { Database = connection.Database, Schema = schema, Name = name, Kind = type == "SN" ? CompletionItemKind.Synonym : type == "SQ" ? CompletionItemKind.Sequence : CompletionItemKind.Type, SynonymBaseObjectName = reader.IsDBNull(3) ? null : reader.GetString(3) });
                        }
                }
            }
            catch (Exception ex) { RecordFailure("synonyms, sequences and types", ex, errors); }
        }

        private static void TryLoadCompatibilityLevel(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = new SqlCommand("SELECT compatibility_level FROM sys.databases WHERE database_id=DB_ID();", connection))
                {
                    command.CommandTimeout = 5;
                    object value = command.ExecuteScalar();
                    if (value != null && value != DBNull.Value) result.CompatibilityLevel = Convert.ToInt32(value);
                }
            }
            catch (Exception ex) { RecordFailure("database compatibility level", ex, errors); }
        }

        private static CompletionItemKind ToKind(string type)
        {
            // sys.all_objects.type is char(2), so one-character values can be
            // returned as "U ", "V ", or "P ". Normalize them before mapping.
            type = type?.Trim();
            if (type == "U") return CompletionItemKind.Table;
            if (type == "V") return CompletionItemKind.View;
            if (type == "P" || type == "PC" || type == "X") return CompletionItemKind.Procedure;
            return CompletionItemKind.Function;
        }
    }
}
