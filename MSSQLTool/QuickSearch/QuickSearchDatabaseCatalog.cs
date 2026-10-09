using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using Microsoft.Data.SqlClient;

namespace MSSQLTool
{
    /// <summary>
    /// One database of the connected server as shown in the Quick Search database picker.
    /// </summary>
    internal sealed class QuickSearchDatabaseInfo
    {
        public string Name { get; set; }
        public string StateDescription { get; set; }
        public string UserAccessDescription { get; set; }
        public bool IsOnline { get; set; }
        public bool IsAccessible { get; set; }
        public bool IsSystemDatabase { get; set; }

        /// <summary>
        /// Databases that are offline, restricted or not reachable are listed but cannot be
        /// selected, and are never pre-selected.
        /// </summary>
        public bool CanBeSearched => IsOnline && IsAccessible;

        public string Annotation
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(StateDescription) && !IsOnline) parts.Add(StateDescription.ToLowerInvariant());
                if (!IsAccessible) parts.Add(LocalizationManager.T("no access"));
                if (IsSystemDatabase) parts.Add(LocalizationManager.T("system"));
                if (!string.IsNullOrEmpty(UserAccessDescription)
                    && !string.Equals(UserAccessDescription, "MULTI_USER", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(UserAccessDescription.ToLowerInvariant().Replace('_', ' '));
                }

                return parts.Count == 0 ? string.Empty : LocalizationManager.Format("({0})", string.Join(", ", parts));
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// Reads the database list of a server (name + state) so the tool window can offer an
    /// explicit database selection. All access is read-only and cancellation aware.
    /// </summary>
    internal static class QuickSearchDatabaseCatalog
    {
        private const string CatalogSql = @"
SELECT d.[name],
       d.[state_desc],
       d.[user_access_desc],
       CASE WHEN HAS_DBACCESS(d.[name]) = 1 THEN 1 ELSE 0 END AS IsAccessible,
       CASE WHEN d.[database_id] <= 4 THEN 1 ELSE 0 END AS IsSystemDatabase
FROM sys.databases d
WHERE d.[name] <> 'tempdb'
ORDER BY d.[name];";

        private const string SearchableDatabasesSql = @"
SELECT d.[name]
FROM sys.databases d
WHERE d.[name] <> 'tempdb'
  AND d.[state] = 0          -- ONLINE
  AND d.[user_access] = 0    -- MULTI_USER
  AND HAS_DBACCESS(d.[name]) = 1
ORDER BY d.[name];";

        /// <summary>
        /// Database names that the "all user databases" mode searches. Kept identical to the
        /// historical behaviour so existing users see the same result set. Runs on the caller's
        /// (background) thread and is used by the search engine.
        /// </summary>
        public static List<string> GetSearchableDatabaseNames(
            ScriptFactoryAccess.ConnectionInfo connection,
            CancellationToken cancellationToken)
        {
            var names = new List<string>();
            if (connection == null) return names;

            using (SqlConnection sqlConnection = QuickSearchConnectionFactory.Create(connection, "master"))
            {
                using (cancellationToken.Register(() => TryClose(sqlConnection)))
                {
                    sqlConnection.Open();

                    using (var command = new SqlCommand(SearchableDatabasesSql, sqlConnection))
                    {
                        command.CommandTimeout = 60;
                        using (cancellationToken.Register(() => TryCancel(command)))
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (!reader.IsDBNull(0)) names.Add(reader.GetString(0));
                            }
                        }
                    }
                }
            }

            return names;
        }

        private static void TryClose(SqlConnection connection)
        {
            try
            {
                connection.Close();
            }
            catch (Exception)
            {
                // Cancellation is best effort.
            }
        }

        private static void TryCancel(SqlCommand command)
        {
            try
            {
                command.Cancel();
            }
            catch (Exception)
            {
                // Cancellation is best effort.
            }
        }

        public static async Task<List<QuickSearchDatabaseInfo>> LoadAsync(
            ScriptFactoryAccess.ConnectionInfo connection,
            CancellationToken cancellationToken)
        {
            var databases = new List<QuickSearchDatabaseInfo>();
            if (connection == null) return databases;

            using (SqlConnection sqlConnection = QuickSearchConnectionFactory.Create(connection, "master"))
            {
                await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var command = new SqlCommand(CatalogSql, sqlConnection))
                {
                    command.CommandTimeout = 60;
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            string state = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            databases.Add(new QuickSearchDatabaseInfo
                            {
                                Name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                StateDescription = state,
                                UserAccessDescription = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                IsAccessible = !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
                                IsSystemDatabase = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
                                IsOnline = string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
            }

            return databases;
        }
    }

    /// <summary>
    /// Creates a connection for a specific database of the selected target. Uses the helper on
    /// ConnectionInfo (which carries Azure AD tokens/credentials that never appear in the
    /// connection string) and falls back to the plain connection string for safety.
    /// </summary>
    internal static class QuickSearchConnectionFactory
    {
        public static SqlConnection Create(ScriptFactoryAccess.ConnectionInfo connection, string database)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            try
            {
                return connection.CreateSqlConnection(database);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Quick Search", "Falling back to the raw connection string", ex);

                var builder = new SqlConnectionStringBuilder(connection.FullConnectionString ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(database)) builder.InitialCatalog = database;
                return new SqlConnection(builder.ConnectionString);
            }
        }
    }
}
