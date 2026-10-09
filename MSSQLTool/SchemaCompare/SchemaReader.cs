// Schema Compare - reads a whole database from the catalog views.
// Every catalog is read with a single set based query (never per object) so a
// snapshot of a large database is a handful of round trips instead of thousands.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace MSSQLTool.SchemaCompare
{
    /// <summary>
    /// Removes comments and collapses whitespace so that two definitions can be
    /// compared ignoring formatting. The original text is always kept in the model
    /// for display and for script generation.
    /// </summary>
    public static class SchemaDefinitionNormalizer
    {
        private static readonly Regex WhitespaceRun = new Regex("[\\s]+", RegexOptions.Compiled);

        public static string Normalize(string definition)
        {
            if (string.IsNullOrEmpty(definition))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(definition.Length);
            bool pendingSpace = false;
            int index = 0;
            int length = definition.Length;

            while (index < length)
            {
                char current = definition[index];

                // Quoted content is copied verbatim. Whitespace inside a literal is
                // significant, so it must survive the collapse.
                if (current == '\'' || current == '"' || current == '[')
                {
                    if (pendingSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }
                    pendingSpace = false;
                    index = CopyQuoted(definition, index, builder);
                    continue;
                }

                if (current == '-' && index + 1 < length && definition[index + 1] == '-')
                {
                    index += 2;
                    while (index < length && definition[index] != '\n' && definition[index] != '\r')
                    {
                        index++;
                    }

                    pendingSpace = true;
                    continue;
                }

                if (current == '/' && index + 1 < length && definition[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < length && !(definition[index] == '*' && definition[index + 1] == '/'))
                    {
                        index++;
                    }

                    index = Math.Min(index + 2, length);
                    pendingSpace = true;
                    continue;
                }

                if (char.IsWhiteSpace(current))
                {
                    index++;
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(current);
                index++;
            }

            return builder.ToString().Trim();
        }

        private static int CopyQuoted(string text, int start, StringBuilder builder)
        {
            char open = text[start];
            char close = open == '[' ? ']' : open;
            int index = start + 1;

            while (index < text.Length)
            {
                char current = text[index];
                if (current == close)
                {
                    // ]] / '' / "" escape the closing character.
                    if (index + 1 < text.Length && text[index + 1] == close)
                    {
                        index += 2;
                        continue;
                    }

                    builder.Append(text, start, index - start + 1);
                    return index + 1;
                }

                index++;
            }

            // Unbalanced quoting: keep whatever was scanned so the text is not lost.
            builder.Append(text, start, text.Length - start);
            return text.Length;
        }

        /// <summary>True when the two texts differ only by comments/whitespace.</summary>
        public static bool AreEquivalent(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
        }

        /// <summary>Collapses whitespace without touching comments (used for short values).</summary>
        public static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return WhitespaceRun.Replace(value, " ").Trim();
        }
    }

    public static class SchemaReader
    {
        private const string TableQuery = @"
SELECT s.[name] AS SchemaName, t.[name] AS TableName
FROM sys.tables AS t
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE (@includeSystem = 1 OR t.[is_ms_shipped] = 0)
ORDER BY s.[name], t.[name];";

        private const string ColumnQuery = @"
SELECT s.[name] AS SchemaName,
       t.[name] AS TableName,
       c.[name] AS ColumnName,
       c.[column_id] AS Ordinal,
       ty.[name] AS DataTypeName,
       TYPE_NAME(c.[system_type_id]) AS SystemTypeName,
       c.[max_length] AS MaxLength,
       c.[precision] AS Precision,
       c.[scale] AS Scale,
       c.[is_nullable] AS IsNullable,
       c.[is_identity] AS IsIdentity,
       c.[is_computed] AS IsComputed,
       c.[collation_name] AS CollationName,
       dc.[name] AS DefaultConstraintName,
       dc.[definition] AS DefaultDefinition,
       cc.[definition] AS ComputedDefinition,
       cc.[is_persisted] AS IsPersisted,
       ic.[seed] AS IdentitySeed,
       ic.[increment] AS IdentityIncrement
FROM sys.columns AS c
INNER JOIN sys.tables AS t ON t.[object_id] = c.[object_id]
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
LEFT JOIN sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
LEFT JOIN sys.default_constraints AS dc ON dc.[parent_object_id] = c.[object_id] AND dc.[parent_column_id] = c.[column_id]
LEFT JOIN sys.computed_columns AS cc ON cc.[object_id] = c.[object_id] AND cc.[column_id] = c.[column_id]
LEFT JOIN sys.identity_columns AS ic ON ic.[object_id] = c.[object_id] AND ic.[column_id] = c.[column_id]
WHERE (@includeSystem = 1 OR t.[is_ms_shipped] = 0)
ORDER BY s.[name], t.[name], c.[column_id];";

        private const string IndexQuery = @"
SELECT s.[name] AS SchemaName,
       t.[name] AS TableName,
       i.[object_id] AS ObjectId,
       i.[index_id] AS IndexId,
       i.[name] AS IndexName,
       i.[type_desc] AS TypeDescription,
       i.[is_unique] AS IsUnique,
       i.[is_primary_key] AS IsPrimaryKey,
       i.[is_unique_constraint] AS IsUniqueConstraint,
       i.[is_disabled] AS IsDisabled,
       i.[has_filter] AS HasFilter,
       i.[filter_definition] AS FilterDefinition
FROM sys.indexes AS i
INNER JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE i.[name] IS NOT NULL
  AND i.[is_hypothetical] = 0
  AND (@includeSystem = 1 OR t.[is_ms_shipped] = 0)
ORDER BY s.[name], t.[name], i.[index_id];";

        private const string IndexColumnQuery = @"
SELECT ic.[object_id] AS ObjectId,
       ic.[index_id] AS IndexId,
       ic.[key_ordinal] AS KeyOrdinal,
       ic.[is_descending_key] AS IsDescending,
       ic.[is_included_column] AS IsIncluded,
       ic.[index_column_id] AS IndexColumnId,
       c.[name] AS ColumnName
FROM sys.index_columns AS ic
INNER JOIN sys.indexes AS i ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id]
INNER JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
INNER JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
WHERE i.[name] IS NOT NULL
  AND i.[is_hypothetical] = 0
  AND (@includeSystem = 1 OR t.[is_ms_shipped] = 0)
ORDER BY ic.[object_id], ic.[index_id], ic.[is_included_column], ic.[key_ordinal], ic.[index_column_id];";

        private const string KeyConstraintQuery = @"
SELECT s.[name] AS SchemaName,
       t.[name] AS TableName,
       kc.[name] AS ConstraintName,
       kc.[type] AS ConstraintType,
       kc.[parent_object_id] AS ObjectId,
       kc.[unique_index_id] AS UniqueIndexId,
       kc.[is_system_named] AS IsSystemNamed,
       i.[type_desc] AS IndexTypeDescription
FROM sys.key_constraints AS kc
INNER JOIN sys.tables AS t ON t.[object_id] = kc.[parent_object_id]
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
INNER JOIN sys.indexes AS i ON i.[object_id] = kc.[parent_object_id] AND i.[index_id] = kc.[unique_index_id]
WHERE (@includeSystem = 1 OR t.[is_ms_shipped] = 0);";

        private const string KeyConstraintColumnQuery = @"
SELECT kc.[parent_object_id] AS ObjectId,
       kc.[unique_index_id] AS IndexId,
       ic.[key_ordinal] AS KeyOrdinal,
       c.[name] AS ColumnName
FROM sys.key_constraints AS kc
INNER JOIN sys.index_columns AS ic ON ic.[object_id] = kc.[parent_object_id]
                                   AND ic.[index_id] = kc.[unique_index_id]
                                   AND ic.[is_included_column] = 0
INNER JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
INNER JOIN sys.tables AS t ON t.[object_id] = kc.[parent_object_id]
WHERE (@includeSystem = 1 OR t.[is_ms_shipped] = 0)
ORDER BY kc.[parent_object_id], kc.[unique_index_id], ic.[key_ordinal];";

        private const string ForeignKeyQuery = @"SELECT s.[name] AS SchemaName,
       t.[name] AS TableName,
       fk.[name] AS ConstraintName,
       fk.[parent_object_id] AS ObjectId,
       rs.[name] AS ReferencedSchema,
       rt.[name] AS ReferencedTable,
       fk.[delete_referential_action_desc] AS DeleteAction,
       fk.[update_referential_action_desc] AS UpdateAction,
       fk.[is_not_trusted] AS IsNotTrusted,
       fk.[is_disabled] AS IsDisabled,
       fk.[is_not_for_replication] AS IsNotForReplication
FROM sys.foreign_keys AS fk
INNER JOIN sys.tables AS t ON t.[object_id] = fk.[parent_object_id]
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
INNER JOIN sys.tables AS rt ON rt.[object_id] = fk.[referenced_object_id]
INNER JOIN sys.schemas AS rs ON rs.[schema_id] = rt.[schema_id]
WHERE (@includeSystem = 1 OR t.[is_ms_shipped] = 0);";

        private const string ForeignKeyColumnQuery = @"
SELECT fkc.[constraint_object_id] AS ObjectId,
       fkc.[constraint_column_id] AS Ordinal,
       pc.[name] AS ParentColumn,
       rc.[name] AS ReferencedColumn
FROM sys.foreign_key_columns AS fkc
INNER JOIN sys.columns AS pc ON pc.[object_id] = fkc.[parent_object_id] AND pc.[column_id] = fkc.[parent_column_id]
INNER JOIN sys.columns AS rc ON rc.[object_id] = fkc.[referenced_object_id] AND rc.[column_id] = fkc.[referenced_column_id]
ORDER BY fkc.[constraint_object_id], fkc.[constraint_column_id];";

        private const string CheckConstraintQuery = @"
SELECT s.[name] AS SchemaName,
       t.[name] AS TableName,
       cc.[name] AS ConstraintName,
       cc.[parent_object_id] AS ObjectId,
       cc.[definition] AS CheckDefinition,
       cc.[is_not_trusted] AS IsNotTrusted,
       cc.[is_disabled] AS IsDisabled,
       cc.[is_system_named] AS IsSystemNamed,
       cc.[parent_column_id] AS ParentColumnId
FROM sys.check_constraints AS cc
INNER JOIN sys.tables AS t ON t.[object_id] = cc.[parent_object_id]
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE (@includeSystem = 1 OR t.[is_ms_shipped] = 0);";

        private const string ModuleQuery = @"
SELECT s.[name] AS SchemaName,
       o.[name] AS ObjectName,
       o.[type] AS ObjectType,
       m.[definition] AS Definition,
       m.[is_schema_bound] AS IsSchemaBound,
       m.[uses_ansi_nulls] AS UsesAnsiNulls,
       m.[uses_quoted_identifier] AS UsesQuotedIdentifier,
       ps.[name] AS ParentSchema,
       po.[name] AS ParentName,
       CASE WHEN t.[is_disabled] = 1 OR tr.[is_disabled] = 1 THEN 1 ELSE 0 END AS IsDisabled
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.[schema_id] = o.[schema_id]
LEFT JOIN sys.sql_modules AS m ON m.[object_id] = o.[object_id]
LEFT JOIN sys.objects AS po ON po.[object_id] = o.[parent_object_id]
LEFT JOIN sys.schemas AS ps ON ps.[schema_id] = po.[schema_id]
LEFT JOIN sys.triggers AS tr ON tr.[object_id] = o.[object_id]
LEFT JOIN sys.tables AS t ON t.[object_id] = o.[object_id]
WHERE (@includeSystem = 1 OR o.[is_ms_shipped] = 0)
  AND o.[type] IN ('P','PC','FN','FS','IF','TF','FT','AF','TR','TA')
ORDER BY s.[name], o.[name];";

        private const string ViewQuery = @"
SELECT s.[name] AS SchemaName,
       v.[name] AS ObjectName,
       m.[definition] AS Definition,
       m.[is_schema_bound] AS IsSchemaBound,
       m.[uses_ansi_nulls] AS UsesAnsiNulls,
       m.[uses_quoted_identifier] AS UsesQuotedIdentifier
FROM sys.views AS v
INNER JOIN sys.schemas AS s ON s.[schema_id] = v.[schema_id]
LEFT JOIN sys.sql_modules AS m ON m.[object_id] = v.[object_id]
WHERE (@includeSystem = 1 OR v.[is_ms_shipped] = 0)
ORDER BY s.[name], v.[name];";

        private const string RowCountQuery = @"
SELECT t.[name] AS TableName, s.[name] AS SchemaName, SUM(p.[rows]) AS RowCount
FROM sys.tables AS t
INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
INNER JOIN sys.partitions AS p ON p.[object_id] = t.[object_id] AND p.[index_id] IN (0, 1)
GROUP BY s.[name], t.[name];";

        private const string DatabaseListQuery = @"
SELECT d.[name] AS DatabaseName, d.[collation_name] AS CollationName
FROM sys.databases AS d
WHERE d.[state] = 0 AND HAS_DBACCESS(d.[name]) = 1
ORDER BY d.[name];";

        /// <summary>The catalog views need a fixed, well known database, so the query runs on master.</summary>
        private const string DatabasePropertiesQuery = @"
SELECT DB_NAME() AS DatabaseName, @@SERVERNAME AS ServerName, @db AS RequestedDatabase,
       (SELECT d.[collation_name] FROM sys.databases AS d WHERE d.[name] = @db) AS CollationName,
       CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS ProductVersion;";

        public static async Task<IList<string>> GetDatabaseNamesAsync(string connectionString, int commandTimeoutSeconds, CancellationToken token)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return names;
            }

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = new SqlCommand(DatabaseListQuery, connection))
                {
                    command.CommandTimeout = commandTimeoutSeconds > 0 ? commandTimeoutSeconds : 30;
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            names.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                        }
                    }
                }
            }

            return names;
        }

        public static Task<IList<string>> GetDatabaseNamesAsync(ScriptFactoryAccess.ConnectionInfo connection, string serverName, int commandTimeoutSeconds, CancellationToken token)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            var masterConnection = SchemaCompareConnectionFactory.Create(connection, serverName, "master");
            return GetDatabaseNamesAsync(masterConnection.FullConnectionString, commandTimeoutSeconds, token);
        }

        /// <summary>Reads a snapshot using a plain connection string.</summary>
        public static Task<SchemaSnapshot> ReadAsync(string connectionString, string databaseName, SchemaReadOptions options, IProgress<string> progress, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A connection string is required.", nameof(connectionString));
            }

            var effectiveOptions = options ?? new SchemaReadOptions();
            string target = string.IsNullOrWhiteSpace(databaseName) ? "master" : databaseName;
            return ReadCoreAsync(() => new SqlConnection(BuildConnectionString(connectionString, target)), target, effectiveOptions, progress, token);
        }

        /// <summary>
        /// Reads a snapshot with the credentials of an SSMS connection (keeps Entra
        /// tokens and impersonation working, which a rebuilt connection string cannot).
        /// </summary>
        public static Task<SchemaSnapshot> ReadAsync(ScriptFactoryAccess.ConnectionInfo connection, string databaseName, SchemaReadOptions options, IProgress<string> progress, CancellationToken token)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            var effectiveOptions = options ?? new SchemaReadOptions();
            string target = string.IsNullOrWhiteSpace(databaseName) ? (connection.Database ?? "master") : databaseName;
            return ReadCoreAsync(() => connection.CreateSqlConnection(target), target, effectiveOptions, progress, token);
        }

        public static string BuildConnectionString(string connectionString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                builder.InitialCatalog = databaseName;
            }

            return builder.ConnectionString;
        }

        /// <summary>
        /// Everything a read step needs. Passing it around keeps the database name,
        /// the options and the cancellation token consistent for every catalog query.
        /// </summary>
        private sealed class ReadContext
        {
            public SqlConnection Connection { get; set; }
            public SchemaSnapshot Snapshot { get; set; }
            public SchemaReadOptions Options { get; set; }
            public string DatabaseName { get; set; }
            public CancellationToken Token { get; set; }
        }

        private static async Task<SchemaSnapshot> ReadCoreAsync(Func<SqlConnection> connectionFactory, string databaseName, SchemaReadOptions options, IProgress<string> progress, CancellationToken token)
        {
            var snapshot = new SchemaSnapshot
            {
                DatabaseName = databaseName,
                CapturedUtc = DateTime.UtcNow
            };

            using (var connection = connectionFactory())
            {
                token.ThrowIfCancellationRequested();
                Report(progress, "Connecting to " + databaseName + "...");
                await connection.OpenAsync(token).ConfigureAwait(false);

                var context = new ReadContext
                {
                    Connection = connection,
                    Snapshot = snapshot,
                    Options = options,
                    DatabaseName = databaseName,
                    Token = token
                };

                await ReadDatabasePropertiesAsync(context).ConfigureAwait(false);
                Report(progress, "Connected to " + snapshot.DisplayName + ".");

                var tables = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
                var tableList = new List<SchemaTable>();

                if (options.ReadTables)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading tables...");
                    await ReadTablesAsync(context, tableList, tables).ConfigureAwait(false);
                }

                if (options.ReadColumns)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading columns (" + tableList.Count.ToString(CultureInfo.InvariantCulture) + " tables)...");
                    await ReadColumnsAsync(context, tables).ConfigureAwait(false);
                }

                if (options.ReadIndexes)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading indexes...");
                    await ReadIndexesAsync(context, tables).ConfigureAwait(false);
                }

                if (options.ReadConstraints)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading constraints...");
                    await ReadConstraintsAsync(context, tables).ConfigureAwait(false);
                }

                if (options.ReadRowCounts)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading row counts...");
                    await ReadRowCountsAsync(context, tables).ConfigureAwait(false);
                }

                if (options.ReadViews)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading views...");
                    await ReadViewsAsync(context).ConfigureAwait(false);
                }

                if (options.ReadRoutines)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, "Reading procedures, functions and triggers...");
                    await ReadRoutinesAsync(context).ConfigureAwait(false);
                }

                snapshot.Tables.AddRange(tableList);
            }

            Report(progress, "Read " + snapshot.ObjectCount.ToString(CultureInfo.InvariantCulture) + " objects from " + snapshot.DatabaseName + ".");
            return snapshot;
        }

        private static async Task ReadDatabasePropertiesAsync(ReadContext context)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            CancellationToken token = context.Token;

            try
            {
                using (var command = CreateCommand(connection, DatabasePropertiesQuery, options, null))
                {
                    command.Parameters.Add(new SqlParameter("@db", snapshot.DatabaseName ?? string.Empty));
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            snapshot.DatabaseName = reader.IsDBNull(0) ? snapshot.DatabaseName : reader.GetString(0);
                            snapshot.ServerName = reader.IsDBNull(1) ? null : reader.GetString(1);
                            snapshot.DatabaseCollation = reader.IsDBNull(3) ? null : reader.GetString(3);
                            snapshot.ProductVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot.Notes.Add("Database properties could not be read: " + ex.Message);
            }
        }

        private static async Task ReadTablesAsync(ReadContext context, List<SchemaTable> tableList, Dictionary<string, SchemaTable> tables)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "tables", async () =>
            {
                using (var command = CreateCommand(connection, TableQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var table = new SchemaTable
                            {
                                Schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1)
                            };
                            tableList.Add(table);
                            if (!tables.ContainsKey(table.Key))
                            {
                                tables.Add(table.Key, table);
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task ReadColumnsAsync(ReadContext context, Dictionary<string, SchemaTable> tables)
        {
            if (tables.Count == 0)
            {
                return;
            }

            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "columns", async () =>
            {
                using (var command = CreateCommand(connection, ColumnQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            string table = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            SchemaTable owner;
                            if (!tables.TryGetValue(schema + "." + table, out owner))
                            {
                                continue;
                            }

                            var column = new SchemaColumn
                            {
                                Schema = schema,
                                Table = table,
                                Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                Ordinal = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                DataType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                SystemTypeName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                                MaxLength = reader.IsDBNull(6) ? (short)0 : reader.GetInt16(6),
                                Precision = reader.IsDBNull(7) ? (byte)0 : reader.GetByte(7),
                                Scale = reader.IsDBNull(8) ? (byte)0 : reader.GetByte(8),
                                IsNullable = !reader.IsDBNull(9) && reader.GetBoolean(9),
                                IsIdentity = !reader.IsDBNull(10) && reader.GetBoolean(10),
                                IsComputed = !reader.IsDBNull(11) && reader.GetBoolean(11),
                                Collation = reader.IsDBNull(12) ? null : reader.GetString(12),
                                DefaultConstraintName = reader.IsDBNull(13) ? null : reader.GetString(13),
                                DefaultDefinition = reader.IsDBNull(14) ? null : reader.GetString(14),
                                ComputedDefinition = reader.IsDBNull(15) ? null : reader.GetString(15),
                                IsPersisted = !reader.IsDBNull(16) && reader.GetBoolean(16)
                            };

                            column.IdentitySeed = reader.IsDBNull(17) ? (decimal?)null : Convert.ToDecimal(reader.GetValue(17), CultureInfo.InvariantCulture);
                            column.IdentityIncrement = reader.IsDBNull(18) ? (decimal?)null : Convert.ToDecimal(reader.GetValue(18), CultureInfo.InvariantCulture);

                            owner.Columns.Add(column);
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task ReadIndexesAsync(ReadContext context, Dictionary<string, SchemaTable> tables)
        {
            if (tables.Count == 0)
            {
                return;
            }

            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            var indexByObject = new Dictionary<string, SchemaIndex>(StringComparer.Ordinal);

            await GuardedAsync(snapshot, "indexes", async () =>
            {
                using (var command = CreateCommand(connection, IndexQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            string table = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            SchemaTable owner;
                            if (!tables.TryGetValue(schema + "." + table, out owner))
                            {
                                continue;
                            }

                            string typeDescription = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                            var index = new SchemaIndex
                            {
                                Name = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                Type = typeDescription,
                                IsUnique = !reader.IsDBNull(6) && reader.GetBoolean(6),
                                IsPrimaryKey = !reader.IsDBNull(7) && reader.GetBoolean(7),
                                IsUniqueConstraint = !reader.IsDBNull(8) && reader.GetBoolean(8),
                                IsDisabled = !reader.IsDBNull(9) && reader.GetBoolean(9),
                                FilterDefinition = reader.IsDBNull(11) ? null : reader.GetString(11),
                                IsClustered = typeDescription.IndexOf("CLUSTERED", StringComparison.OrdinalIgnoreCase) == 0
                            };

                            string objectId = reader.IsDBNull(2) ? string.Empty : reader.GetInt32(2).ToString(CultureInfo.InvariantCulture);
                            string indexId = reader.IsDBNull(3) ? string.Empty : reader.GetInt32(3).ToString(CultureInfo.InvariantCulture);
                            indexByObject[objectId + "|" + indexId] = index;
                            owner.Indexes.Add(index);
                        }
                    }
                }
            }).ConfigureAwait(false);

            if (indexByObject.Count == 0)
            {
                return;
            }

            var indexColumnBuffer = new Dictionary<string, List<SchemaIndexColumn>>(StringComparer.Ordinal);
            var includedBuffer = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            await GuardedAsync(snapshot, "index columns", async () =>
            {
                using (var command = CreateCommand(connection, IndexColumnQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string objectId = reader.IsDBNull(0) ? string.Empty : reader.GetInt32(0).ToString(CultureInfo.InvariantCulture);
                            string indexId = reader.IsDBNull(1) ? string.Empty : reader.GetInt32(1).ToString(CultureInfo.InvariantCulture);
                            string key = objectId + "|" + indexId;
                            if (!indexByObject.ContainsKey(key))
                            {
                                continue;
                            }

                            bool isIncluded = !reader.IsDBNull(4) && reader.GetBoolean(4);
                            string columnName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);

                            if (isIncluded)
                            {
                                List<string> included;
                                if (!includedBuffer.TryGetValue(key, out included))
                                {
                                    included = new List<string>();
                                    includedBuffer[key] = included;
                                }

                                included.Add(columnName);
                            }
                            else
                            {
                                List<SchemaIndexColumn> keyColumns;
                                if (!indexColumnBuffer.TryGetValue(key, out keyColumns))
                                {
                                    keyColumns = new List<SchemaIndexColumn>();
                                    indexColumnBuffer[key] = keyColumns;
                                }

                                keyColumns.Add(new SchemaIndexColumn
                                {
                                    Name = columnName,
                                    IsDescending = !reader.IsDBNull(3) && reader.GetBoolean(3),
                                    IsIncluded = false,
                                    Ordinal = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)
                                });
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);

            foreach (var pair in indexByObject)
            {
                List<SchemaIndexColumn> keyColumns;
                if (indexColumnBuffer.TryGetValue(pair.Key, out keyColumns))
                {
                    pair.Value.Columns = keyColumns;
                }

                List<string> included;
                if (includedBuffer.TryGetValue(pair.Key, out included))
                {
                    pair.Value.IncludedColumns = included;
                }
            }
        }

        private static async Task ReadConstraintsAsync(ReadContext context, Dictionary<string, SchemaTable> tables)
        {
            if (tables.Count == 0)
            {
                return;
            }

            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            var keyConstraintIndex = new Dictionary<string, SchemaConstraint>(StringComparer.Ordinal);

            await GuardedAsync(snapshot, "key constraints", async () =>
            {
                using (var command = CreateCommand(connection, KeyConstraintQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            string table = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            SchemaTable owner;
                            if (!tables.TryGetValue(schema + "." + table, out owner))
                            {
                                continue;
                            }

                            string constraintType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                            string indexType = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
                            var constraint = new SchemaConstraint
                            {
                                Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                Kind = string.Equals(constraintType, "PK", StringComparison.OrdinalIgnoreCase)
                                    ? SchemaConstraintKind.PrimaryKey
                                    : SchemaConstraintKind.Unique,
                                IsSystemNamed = !reader.IsDBNull(6) && reader.GetBoolean(6),
                                IsClustered = indexType.IndexOf("CLUSTERED", StringComparison.OrdinalIgnoreCase) == 0
                            };

                            // The key columns are resolved in a dedicated pass (ReadKeyConstraintColumns)
                            // through (parent_object_id, unique_index_id).
                            string constraintObjectId = reader.IsDBNull(4) ? string.Empty : reader.GetInt32(4).ToString(CultureInfo.InvariantCulture);
                            string uniqueIndexId = reader.IsDBNull(5) ? string.Empty : reader.GetInt32(5).ToString(CultureInfo.InvariantCulture);

                            owner.Constraints.Add(constraint);
                            keyConstraintIndex[constraintObjectId + "|" + uniqueIndexId] = constraint;
                        }
                    }
                }
            }).ConfigureAwait(false);

            if (keyConstraintIndex.Count > 0)
            {
                await ReadKeyConstraintColumnsAsync(context, keyConstraintIndex).ConfigureAwait(false);
            }

            var foreignKeyTargets = new Dictionary<string, SchemaConstraint>(StringComparer.Ordinal);

            await GuardedAsync(snapshot, "foreign keys", async () =>
            {
                using (var command = CreateCommand(connection, ForeignKeyQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            string table = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            SchemaTable owner;
                            if (!tables.TryGetValue(schema + "." + table, out owner))
                            {
                                continue;
                            }

                            string name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                            string parentObjectId = reader.IsDBNull(3) ? string.Empty : reader.GetInt32(3).ToString(CultureInfo.InvariantCulture);
                            var constraint = new SchemaConstraint
                            {
                                Name = name,
                                Kind = SchemaConstraintKind.ForeignKey,
                                ReferencedSchema = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                ReferencedTable = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                                DeleteAction = reader.IsDBNull(6) ? null : reader.GetString(6),
                                UpdateAction = reader.IsDBNull(7) ? null : reader.GetString(7),
                                IsNotTrusted = !reader.IsDBNull(8) && reader.GetBoolean(8),
                                IsDisabled = !reader.IsDBNull(9) && reader.GetBoolean(9)
                            };

                            owner.Constraints.Add(constraint);
                            foreignKeyTargets[parentObjectId] = constraint;
                        }
                    }
                }
            }).ConfigureAwait(false);

            if (foreignKeyTargets.Count > 0)
            {
                await ReadForeignKeyColumnsAsync(context, foreignKeyTargets).ConfigureAwait(false);
            }

            await GuardedAsync(snapshot, "check constraints", async () =>
            {
                using (var command = CreateCommand(connection, CheckConstraintQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            string table = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            SchemaTable owner;
                            if (!tables.TryGetValue(schema + "." + table, out owner))
                            {
                                continue;
                            }

                            var constraint = new SchemaConstraint
                            {
                                Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                Kind = SchemaConstraintKind.Check,
                                Definition = reader.IsDBNull(4) ? null : reader.GetString(4),
                                IsNotTrusted = !reader.IsDBNull(5) && reader.GetBoolean(5),
                                IsDisabled = !reader.IsDBNull(6) && reader.GetBoolean(6),
                                IsSystemNamed = !reader.IsDBNull(7) && reader.GetBoolean(7)
                            };

                            if (!reader.IsDBNull(8) && reader.GetInt32(8) > 0)
                            {
                                var column = owner.Columns.FirstOrDefault(c => c.Ordinal == reader.GetInt32(8));
                                if (column != null)
                                {
                                    constraint.Columns.Add(column.Name);
                                }
                            }

                            owner.Constraints.Add(constraint);
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task ReadViewsAsync(ReadContext context)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "views", async () =>
            {
                using (var command = CreateCommand(connection, ViewQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string definition = reader.IsDBNull(2) ? null : reader.GetString(2);
                            snapshot.Views.Add(new SchemaView
                            {
                                Schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                Definition = definition,
                                NormalizedDefinition = SchemaDefinitionNormalizer.Normalize(definition),
                                IsEncrypted = string.IsNullOrWhiteSpace(definition),
                                IsSchemaBound = !reader.IsDBNull(3) && reader.GetBoolean(3),
                                UsesAnsiNulls = !reader.IsDBNull(4) && reader.GetBoolean(4),
                                UsesQuotedIdentifier = !reader.IsDBNull(5) && reader.GetBoolean(5)
                            });
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task ReadRoutinesAsync(ReadContext context)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "routines", async () =>
            {
                using (var command = CreateCommand(connection, ModuleQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string objectType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                            SchemaRoutineKind kind = MapRoutineKind(objectType);
                            if (kind == SchemaRoutineKind.Trigger && !options.ReadTriggers)
                            {
                                continue;
                            }

                            string definition = reader.IsDBNull(3) ? null : reader.GetString(3);
                            snapshot.Routines.Add(new SchemaRoutine
                            {
                                Schema = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                Kind = kind,
                                Definition = definition,
                                NormalizedDefinition = SchemaDefinitionNormalizer.Normalize(definition),
                                IsSchemaBound = !reader.IsDBNull(4) && reader.GetBoolean(4),
                                UsesAnsiNulls = !reader.IsDBNull(5) && reader.GetBoolean(5),
                                UsesQuotedIdentifier = !reader.IsDBNull(6) && reader.GetBoolean(6),
                                ParentSchema = reader.IsDBNull(7) ? null : reader.GetString(7),
                                ParentName = reader.IsDBNull(8) ? null : reader.GetString(8),
                                IsDisabled = !reader.IsDBNull(9) && reader.GetBoolean(9),
                                IsEncrypted = string.IsNullOrWhiteSpace(definition) && IsSqlModuleType(objectType)
                            });
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static SchemaRoutineKind MapRoutineKind(string objectType)
        {
            switch (objectType == null ? string.Empty : objectType.Trim().ToUpperInvariant())
            {
                case "P":
                case "PC":
                    return SchemaRoutineKind.Procedure;
                case "FN":
                case "FS":
                    return SchemaRoutineKind.ScalarFunction;
                case "IF":
                    return SchemaRoutineKind.InlineTableValuedFunction;
                case "TF":
                case "FT":
                    return SchemaRoutineKind.TableValuedFunction;
                case "AF":
                    return SchemaRoutineKind.Aggregate;
                case "TR":
                case "TA":
                    return SchemaRoutineKind.Trigger;
                default:
                    return SchemaRoutineKind.Procedure;
            }
        }

        private static bool IsSqlModuleType(string objectType)
        {
            switch (objectType == null ? string.Empty : objectType.Trim().ToUpperInvariant())
            {
                case "PC":
                case "FS":
                case "FT":
                case "TA":
                    return false;
                default:
                    return true;
            }
        }

        private static async Task ReadRowCountsAsync(ReadContext context, Dictionary<string, SchemaTable> tables)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "row counts", async () =>
            {
                using (var command = CreateCommand(connection, RowCountQuery, options, databaseName))
                {
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string schema = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            string table = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            SchemaTable owner;
                            if (!tables.TryGetValue(schema + "." + table, out owner))
                            {
                                continue;
                            }

                            owner.RowCount = reader.IsDBNull(2) ? (long?)null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the key columns of PK/UNIQUE constraints through the backing unique index.
        /// The rows arrive ordered by key_ordinal so the column order is preserved.
        /// </summary>
        private static async Task ReadKeyConstraintColumnsAsync(ReadContext context, Dictionary<string, SchemaConstraint> constraints)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "key constraint columns", async () =>
            {
                using (var command = CreateCommand(connection, KeyConstraintColumnQuery, options, databaseName))
                {
                    AddSystemObjectParameter(command, options);
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(3))
                            {
                                continue;
                            }

                            string key = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture) + "|" +
                                         reader.GetInt32(1).ToString(CultureInfo.InvariantCulture);
                            SchemaConstraint constraint;
                            if (constraints.TryGetValue(key, out constraint))
                            {
                                constraint.Columns.Add(reader.GetString(3));
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task ReadForeignKeyColumnsAsync(ReadContext context, Dictionary<string, SchemaConstraint> constraints)
        {
            SqlConnection connection = context.Connection;
            SchemaSnapshot snapshot = context.Snapshot;
            SchemaReadOptions options = context.Options;
            string databaseName = context.DatabaseName;
            CancellationToken token = context.Token;

            await GuardedAsync(snapshot, "foreign key columns", async () =>
            {
                using (var command = CreateCommand(connection, ForeignKeyColumnQuery, options, databaseName))
                {
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (reader.IsDBNull(0) || reader.IsDBNull(2) || reader.IsDBNull(3))
                            {
                                continue;
                            }

                            string key = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture);
                            SchemaConstraint constraint;
                            if (constraints.TryGetValue(key, out constraint))
                            {
                                constraint.Columns.Add(reader.GetString(2));
                                constraint.ReferencedColumns.Add(reader.GetString(3));
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static SqlCommand CreateCommand(SqlConnection connection, string commandText, SchemaReadOptions options, string databaseName)
        {
            var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(databaseName)
                ? commandText
                : "USE " + DatabaseIdentifier.SqlServerPart(databaseName) + ";" + Environment.NewLine + commandText;
            command.CommandTimeout = options != null && options.CommandTimeoutSeconds > 0 ? options.CommandTimeoutSeconds : 120;
            return command;
        }

        private static void AddSystemObjectParameter(SqlCommand command, SchemaReadOptions options)
        {
            command.Parameters.Add(new SqlParameter("@includeSystem", options != null && options.IncludeSystemObjects ? 1 : 0));
        }

        private async static Task GuardedAsync(SchemaSnapshot snapshot, string area, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string message = area + " could not be read completely: " + ex.Message;
                if (snapshot != null)
                {
                    snapshot.Notes.Add(message);
                }

                FeatureDiagnostics.Report("Schema Compare", message, ex);
            }
        }

        private static void Report(IProgress<string> progress, string message)
        {
            if (progress != null)
            {
                progress.Report(message);
            }
        }
    }
}
