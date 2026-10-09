using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Data;
using System.Threading;
using Microsoft.Data.SqlClient;

namespace MSSQLTool
{
    /// <summary>
    /// Everything the engine needs to run one search. The UI keeps the instance so the
    /// preview and the editor highlight use exactly the options that produced the results.
    /// </summary>
    internal sealed class QuickSearchRequest
    {
        public string SearchText { get; set; } = string.Empty;
        public bool WholeWord { get; set; }
        public bool UseWildcards { get; set; }
        public bool Fuzzy { get; set; }
        public bool IncludeStoredProcedures { get; set; } = true;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeFunctions { get; set; } = true;
        public bool IncludeTables { get; set; } = true;
        public bool IncludeAgentJobSteps { get; set; } = true;
        public bool AllDatabases { get; set; } = true;
        public List<string> Databases { get; set; } = new List<string>();
        public string ServerName { get; set; } = string.Empty;

        /// <summary>
        /// Built by the caller so it can be reused for highlighting; built on demand otherwise.
        /// </summary>
        public QuickSearchSearchOptions Options { get; set; }

        public QuickSearchSearchOptions EnsureOptions()
        {
            return Options ?? (Options = new QuickSearchSearchOptions(SearchText, WholeWord, UseWildcards, Fuzzy));
        }

        public bool IncludesAnyObjectType =>
            IncludeStoredProcedures || IncludeViews || IncludeFunctions || IncludeTables || IncludeAgentJobSteps;
    }

    internal sealed class QuickSearchProgress
    {
        public QuickSearchProgress(int completed, int total, string databaseName)
        {
            Completed = completed;
            Total = total;
            DatabaseName = databaseName;
        }

        public int Completed { get; }
        public int Total { get; }
        public string DatabaseName { get; }
    }

    internal sealed class QuickSearchExecutionResult
    {
        public DataTable Results { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int DatabasesSearched { get; set; }
        public List<string> FailedDatabases { get; } = new List<string>();

        public Dictionary<string, int> GetCountsByObjectType()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (Results == null) return counts;

            foreach (DataRow row in Results.Rows)
            {
                string objectType = row["ObjectType"]?.ToString() ?? string.Empty;
                int current;
                counts.TryGetValue(objectType, out current);
                counts[objectType] = current + 1;
            }

            return counts;
        }
    }

    /// <summary>
    /// Runs the cross database object search: builds the server side pre-filter, evaluates
    /// every returned row with the client side matcher (ranking + preview) and reports
    /// progress. Deliberately synchronous - the tool window calls it from Task.Run - and a
    /// failing database is skipped and reported instead of aborting the whole search.
    /// </summary>
    internal static class QuickSearchEngine
    {
        private const int CommandTimeoutSeconds = 120;
        private const int MaxBodyMatches = 400;
        private const int MaxValueMatches = 32;

        public const string DefinitionLocation = "Definition";
        public const string TableNameLocation = "Table Name";
        public const string ColumnLocation = "Column";
        public const string ParameterLocation = "Parameter";
        public const string JobStepLocation = "JobStep";

        public static QuickSearchExecutionResult Execute(
            ScriptFactoryAccess.ConnectionInfo connection,
            QuickSearchRequest request,
            IProgress<QuickSearchProgress> progress,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var stopwatch = Stopwatch.StartNew();
            var result = new QuickSearchExecutionResult { Results = BuildResultTable() };
            QuickSearchSearchOptions options = request.EnsureOptions();

            List<string> databases = ResolveDatabases(connection, request, cancellationToken);

            int total = databases.Count;
            int completed = 0;
            progress?.Report(new QuickSearchProgress(0, total, null));

            foreach (string databaseName in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    DataTable rows = SearchDatabase(connection, databaseName, request, options, cancellationToken);
                    foreach (DataRow row in rows.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AppendRow(result.Results, options, row);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.FailedDatabases.Add(databaseName);
                    FeatureDiagnostics.Report("Quick Search", "Search failed for database " + databaseName, ex);
                }

                completed++;
                progress?.Report(new QuickSearchProgress(completed, total, databaseName));
            }

            stopwatch.Stop();
            result.Elapsed = stopwatch.Elapsed;
            result.DatabasesSearched = completed;

            // Best match first, then the most useful object type, then name/database/location so
            // equally ranked rows keep a stable, readable order.
            result.Results.DefaultView.Sort = "MatchRank ASC, TypeRank ASC, ObjectName ASC, DatabaseName ASC, MatchLocation ASC";
            return result;
        }

        private static List<string> ResolveDatabases(
            ScriptFactoryAccess.ConnectionInfo connection,
            QuickSearchRequest request,
            CancellationToken cancellationToken)
        {
            var databases = new List<string>();

            if (request.AllDatabases)
            {
                databases = QuickSearchDatabaseCatalog.GetSearchableDatabaseNames(connection, cancellationToken);
            }
            else
            {
                if (request.Databases != null)
                {
                    foreach (string name in request.Databases)
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (databases.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))) continue;
                        databases.Add(name);
                    }
                }

                // "Specific databases" with nothing checked falls back to the connection default
                // instead of searching nothing at all.
                if (databases.Count == 0 && !string.IsNullOrWhiteSpace(connection?.Database))
                {
                    databases.Add(connection.Database);
                }
            }

            // SQL Agent jobs only exist in msdb; add it whenever that object type is requested so
            // the checkbox keeps working no matter which databases are selected.
            if (request.IncludeAgentJobSteps
                && !databases.Any(name => string.Equals(name, "msdb", StringComparison.OrdinalIgnoreCase)))
            {
                databases.Add("msdb");
            }

            return databases;
        }

        private static DataTable SearchDatabase(
            ScriptFactoryAccess.ConnectionInfo connection,
            string databaseName,
            QuickSearchRequest request,
            QuickSearchSearchOptions options,
            CancellationToken cancellationToken)
        {
            DataTable result = BuildResultTable();
            string pattern = options.BuildLikePattern();

            using (SqlConnection sqlConnection = QuickSearchConnectionFactory.Create(connection, databaseName))
            {
                using (cancellationToken.Register(() => TryClose(sqlConnection)))
                {
                    sqlConnection.Open();

                    if (request.IncludeStoredProcedures || request.IncludeViews || request.IncludeFunctions)
                    {
                        TryRunQuery(sqlConnection, DefinitionSql, pattern, request, result, cancellationToken);
                        TryRunQuery(sqlConnection, ParameterSql, pattern, request, result, cancellationToken);
                    }

                    if (request.IncludeTables)
                    {
                        TryRunQuery(sqlConnection, TableSql, pattern, request, result, cancellationToken);
                        TryRunQuery(sqlConnection, ColumnSql, pattern, request, result, cancellationToken);
                    }

                    if (request.IncludeAgentJobSteps
                        && string.Equals(databaseName, "msdb", StringComparison.OrdinalIgnoreCase))
                    {
                        TryRunQuery(sqlConnection, AgentJobStepSql, pattern, request, result, cancellationToken);
                    }
                }
            }

            return result;
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

        private static void TryRunQuery(
            SqlConnection connection,
            string sql,
            string pattern,
            QuickSearchRequest request,
            DataTable aggregate,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var command = new SqlCommand(sql, connection))
                {
                    command.CommandTimeout = CommandTimeoutSeconds;
                    command.Parameters.AddWithValue("@pattern", pattern);
                    command.Parameters.AddWithValue("@includeProcs", request.IncludeStoredProcedures ? 1 : 0);
                    command.Parameters.AddWithValue("@includeViews", request.IncludeViews ? 1 : 0);
                    command.Parameters.AddWithValue("@includeFunctions", request.IncludeFunctions ? 1 : 0);
                    command.Parameters.AddWithValue("@includeTables", request.IncludeTables ? 1 : 0);

                    using (cancellationToken.Register(() => TryCancel(command)))
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        if (!reader.HasRows) return;

                        var chunk = new DataTable();
                        chunk.Load(reader);
                        aggregate.Merge(chunk, true, MissingSchemaAction.Add);
                    }
                }
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                // A single failing query (permissions, a database going offline mid search)
                // must not lose the results of the other object types.
                FeatureDiagnostics.Report("Quick Search", "Query failed for database " + connection.Database, ex);
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

        /// <summary>
        /// Evaluates one raw row, computes its match rank and appends it to the result table.
        /// Rows that only survived the server side pre-filter are dropped here.
        /// </summary>
        private static void AppendRow(DataTable target, QuickSearchSearchOptions options, DataRow row)
        {
            string objectName = row["ObjectName"]?.ToString() ?? string.Empty;
            string sourceText = row["SourceText"]?.ToString() ?? string.Empty;
            string location = row["MatchLocation"]?.ToString() ?? string.Empty;
            string objectType = row["ObjectType"]?.ToString() ?? string.Empty;

            int rank;
            string preview;
            if (!TryEvaluateRow(options, objectName, sourceText, location, out rank, out preview))
            {
                return;
            }

            target.Rows.Add(
                row["DatabaseName"],
                objectType,
                row["SchemaName"],
                objectName,
                location,
                preview,
                row["ScriptDatabaseName"],
                row["ScriptSchemaName"],
                row["ScriptObjectName"],
                sourceText,
                rank,
                GetTypeRank(objectType));
        }

        internal static bool TryEvaluateRow(
            QuickSearchSearchOptions options,
            string objectName,
            string sourceText,
            string location,
            out int rank,
            out string preview)
        {
            rank = int.MaxValue;
            preview = string.Empty;

            bool isBodyLocation = IsBodyLocation(location);
            int tier = int.MaxValue;
            bool fuzzyTier = false;

            // The object name is always compared, so a module that hits in its body is still
            // ranked by how well its own name matches.
            QuickSearchTextMatch nameMatch = options.MatchValue(objectName);
            if (nameMatch.Accepted)
            {
                tier = QuickSearchSearchOptions.TierOf(nameMatch.Kind);
                fuzzyTier = nameMatch.Kind == QuickSearchMatchKind.Fuzzy;
            }

            QuickSearchTextMatch previewMatch;
            if (isBodyLocation)
            {
                List<QuickSearchTextMatch> bodyMatches = options.Scan(sourceText, MaxBodyMatches);
                previewMatch = PickPreviewMatch(bodyMatches);

                if (bodyMatches.Any(match => match.Accepted) && 4 < tier)
                {
                    tier = 4;
                    fuzzyTier = false;
                }
            }
            else
            {
                QuickSearchTextMatch valueMatch = options.MatchValue(sourceText);
                List<QuickSearchTextMatch> valueMatches = options.Scan(sourceText, MaxValueMatches);
                previewMatch = valueMatch.Accepted ? valueMatch : PickPreviewMatch(valueMatches);

                if (valueMatch.Accepted)
                {
                    // "Table Name" is the object's own name, columns and parameters are member names.
                    int valueTier = string.Equals(location, TableNameLocation, StringComparison.OrdinalIgnoreCase)
                        ? QuickSearchSearchOptions.TierOf(valueMatch.Kind)
                        : 3;

                    if (valueTier < tier)
                    {
                        tier = valueTier;
                        fuzzyTier = valueMatch.Kind == QuickSearchMatchKind.Fuzzy;
                    }
                }
            }

            if (tier == int.MaxValue) return false;

            rank = tier * 2 + (fuzzyTier ? 1 : 0);
            preview = BuildPreview(sourceText, previewMatch);
            return true;
        }

        private static QuickSearchTextMatch PickPreviewMatch(List<QuickSearchTextMatch> matches)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i].Accepted) return matches[i];
            }

            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i].HasRange) return matches[i];
            }

            return default(QuickSearchTextMatch);
        }

        internal static bool IsBodyLocation(string location)
        {
            return string.Equals(location, DefinitionLocation, StringComparison.OrdinalIgnoreCase)
                || string.Equals(location, JobStepLocation, StringComparison.OrdinalIgnoreCase);
        }

        internal static int GetTypeRank(string objectType)
        {
            if (string.Equals(objectType, "Stored Procedure", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(objectType, "Function", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(objectType, "View", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(objectType, "Table", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(objectType, "SQL Agent Job Step", StringComparison.OrdinalIgnoreCase)) return 4;
            return 5;
        }

        /// <summary>
        /// One line snippet around the match with the matched text between brackets. The
        /// snippet is whitespace collapsed so it stays readable inside a grid row.
        /// </summary>
        public static string BuildPreview(string sourceText, QuickSearchTextMatch match)
        {
            if (string.IsNullOrEmpty(sourceText)) return string.Empty;
            if (!match.HasRange) return Shorten(sourceText, 120);

            const int contextBefore = 40;
            const int contextAfter = 90;
            const char markerStart = '\u0002';
            const char markerEnd = '\u0003';

            int start = Math.Max(0, match.Index - contextBefore);
            int end = Math.Min(sourceText.Length, match.Index + match.Length + contextAfter);
            if (end <= start) return Shorten(sourceText, 120);

            int relativeIndex = match.Index - start;
            if (relativeIndex < 0 || relativeIndex > sourceText.Length - start) return Shorten(sourceText, 120);

            string snippet = sourceText.Substring(start, end - start);
            int relativeLength = Math.Max(0, Math.Min(match.Length, snippet.Length - relativeIndex));

            var builder = new StringBuilder(snippet.Length + 4);
            builder.Append(snippet.Substring(0, relativeIndex));
            builder.Append(markerStart);
            builder.Append(snippet.Substring(relativeIndex, relativeLength));
            builder.Append(markerEnd);
            builder.Append(snippet.Substring(relativeIndex + relativeLength));

            string marked = CollapseWhitespace(builder.ToString())
                .Replace(markerStart, '[')
                .Replace(markerEnd, ']');

            string prefix = start > 0 ? "... " : string.Empty;
            string suffix = end < sourceText.Length ? " ..." : string.Empty;
            return prefix + marked + suffix;
        }

        private static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var builder = new StringBuilder(text.Length);
            bool previousWhitespace = false;
            foreach (char character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (previousWhitespace) continue;
                    previousWhitespace = true;
                    builder.Append(' ');
                }
                else
                {
                    previousWhitespace = false;
                    builder.Append(character);
                }
            }

            return builder.ToString().Trim();
        }

        private static string Shorten(string text, int length)
        {
            string collapsed = CollapseWhitespace(text);
            if (collapsed.Length <= length) return collapsed;
            return collapsed.Substring(0, length) + " ...";
        }

        private static DataTable BuildResultTable()
        {
            var table = new DataTable();
            table.Columns.Add("DatabaseName", typeof(string));
            table.Columns.Add("ObjectType", typeof(string));
            table.Columns.Add("SchemaName", typeof(string));
            table.Columns.Add("ObjectName", typeof(string));
            table.Columns.Add("MatchLocation", typeof(string));
            table.Columns.Add("MatchPreview", typeof(string));
            table.Columns.Add("ScriptDatabaseName", typeof(string));
            table.Columns.Add("ScriptSchemaName", typeof(string));
            table.Columns.Add("ScriptObjectName", typeof(string));
            table.Columns.Add("SourceText", typeof(string));
            table.Columns.Add("MatchRank", typeof(int));
            table.Columns.Add("TypeRank", typeof(int));
            return table;
        }

        /// <summary>
        /// Plain text export of the result grid, used by the copy actions of the tool window.
        /// </summary>
        public static string BuildClipboardText(DataTable table, DataRowView singleRow, bool includeHeader)
        {
            var builder = new StringBuilder();

            if (includeHeader)
            {
                builder.AppendLine(string.Join("\t", new[]
                {
                    LocalizationManager.T("Database"),
                    LocalizationManager.T("Type"),
                    LocalizationManager.T("Schema"),
                    LocalizationManager.T("Object"),
                    LocalizationManager.T("Location"),
                    LocalizationManager.T("Match Preview")
                }));
            }

            if (singleRow != null)
            {
                AppendClipboardRow(builder, singleRow);
            }
            else if (table != null)
            {
                foreach (DataRowView row in table.DefaultView)
                {
                    AppendClipboardRow(builder, row);
                }
            }

            return builder.ToString();
        }

        private static void AppendClipboardRow(StringBuilder builder, DataRowView row)
        {
            builder.AppendLine(string.Join("\t", new[]
            {
                row["DatabaseName"]?.ToString() ?? string.Empty,
                row["ObjectType"]?.ToString() ?? string.Empty,
                row["SchemaName"]?.ToString() ?? string.Empty,
                row["ObjectName"]?.ToString() ?? string.Empty,
                row["MatchLocation"]?.ToString() ?? string.Empty,
                row["MatchPreview"]?.ToString() ?? string.Empty
            }));
        }

        private const string DefinitionSql = @"
SELECT
    DB_NAME() AS DatabaseName,
    CASE
        WHEN o.[type] = 'P' THEN 'Stored Procedure'
        WHEN o.[type] = 'V' THEN 'View'
        ELSE 'Function'
    END AS ObjectType,
    s.[name] AS SchemaName,
    o.[name] AS ObjectName,
    'Definition' AS MatchLocation,
    m.[definition] AS SourceText,
    DB_NAME() AS ScriptDatabaseName,
    s.[name] AS ScriptSchemaName,
    o.[name] AS ScriptObjectName
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
INNER JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE (
        (@includeProcs = 1 AND o.[type] = 'P') OR
        (@includeViews = 1 AND o.[type] = 'V') OR
        (@includeFunctions = 1 AND o.[type] IN ('FN', 'IF', 'TF'))
      )
  AND m.[definition] LIKE @pattern ESCAPE '!'
  AND o.is_ms_shipped = 0 ;";

        private const string TableSql = @"
SELECT
    DB_NAME() AS DatabaseName,
    'Table' AS ObjectType,
    s.[name] AS SchemaName,
    t.[name] AS ObjectName,
    'Table Name' AS MatchLocation,
    t.[name] AS SourceText,
    DB_NAME() AS ScriptDatabaseName,
    s.[name] AS ScriptSchemaName,
    t.[name] AS ScriptObjectName
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE @includeTables = 1
  AND t.[name] LIKE @pattern ESCAPE '!';";

        private const string ColumnSql = @"
SELECT
    DB_NAME() AS DatabaseName,
    'Table' AS ObjectType,
    s.[name] AS SchemaName,
    t.[name] AS ObjectName,
    'Column' AS MatchLocation,
    c.[name] AS SourceText,
    DB_NAME() AS ScriptDatabaseName,
    s.[name] AS ScriptSchemaName,
    t.[name] AS ScriptObjectName
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
WHERE @includeTables = 1
  AND c.[name] LIKE @pattern ESCAPE '!';";

        private const string ParameterSql = @"
SELECT
    DB_NAME() AS DatabaseName,
    CASE
        WHEN o.[type] = 'P' THEN 'Stored Procedure'
        WHEN o.[type] = 'V' THEN 'View'
        ELSE 'Function'
    END AS ObjectType,
    s.[name] AS SchemaName,
    o.[name] AS ObjectName,
    'Parameter' AS MatchLocation,
    p.[name] AS SourceText,
    DB_NAME() AS ScriptDatabaseName,
    s.[name] AS ScriptSchemaName,
    o.[name] AS ScriptObjectName
FROM sys.parameters p
INNER JOIN sys.objects o ON o.object_id = p.object_id
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE p.parameter_id > 0
  AND (
        (@includeProcs = 1 AND o.[type] = 'P') OR
        (@includeViews = 1 AND o.[type] = 'V') OR
        (@includeFunctions = 1 AND o.[type] IN ('FN', 'IF', 'TF'))
      )
  AND p.[name] LIKE @pattern ESCAPE '!';";

        private const string AgentJobStepSql = @"
SELECT
    'msdb' AS DatabaseName,
    'SQL Agent Job Step' AS ObjectType,
    'dbo' AS SchemaName,
    j.[name] + N' / Step ' + CONVERT(varchar(12), js.step_id) + N' - ' + js.step_name AS ObjectName,
    'JobStep' AS MatchLocation,
    js.[command] AS SourceText,
    N'msdb' AS ScriptDatabaseName,
    N'dbo' AS ScriptSchemaName,
    j.[name] AS ScriptObjectName
FROM dbo.sysjobs j
INNER JOIN dbo.sysjobsteps js ON js.job_id = j.job_id
WHERE js.[command] LIKE @pattern ESCAPE '!'
   OR js.step_name LIKE @pattern ESCAPE '!'
   OR j.[name] LIKE @pattern;";
    }
}
