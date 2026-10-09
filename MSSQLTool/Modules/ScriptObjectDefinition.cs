using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;
using static MSSQLTool.ScriptFactoryAccess;

namespace MSSQLTool
{
    public sealed class ScriptObjectSelectionItem
    {
        public ScriptObjectSelectionItem(string typeDesc, string schemaName, string objectName, int objectId, string databaseName, int parentObjectId, string parentObjectName)
        {
            TypeDesc = typeDesc;
            SchemaName = schemaName;
            ObjectName = objectName;
            ObjectId = objectId;
            DatabaseName = databaseName;
            ParentObjectId = parentObjectId;
            ParentObjectName = parentObjectName;
        }

        public string TypeDesc { get; }

        public string SchemaName { get; }

        public string ObjectName { get; }

        public int ObjectId { get; }

        public string DatabaseName { get; }

        public int ParentObjectId { get; }

        public string ParentObjectName { get; }

        public string DisplayName =>
            string.IsNullOrEmpty(ParentObjectName)
                ? $"{DatabaseName}.{SchemaName}.{ObjectName} ({TypeDesc})"
                : $"{DatabaseName}.{SchemaName}.{ParentObjectName}.{ObjectName} ({TypeDesc})";
    }

    public static class ScriptObjectDefinition
    {

        public static string GetText(AsyncPackage package, string selectedObjectName)
            => GetText(package, selectedObjectName, ScriptFactoryAccess.GetCurrentConnectionInfo());

        public static string GetText(AsyncPackage package, string selectedObjectName, ScriptFactoryAccess.ConnectionInfo connectionInfo)
        {
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.FullConnectionString))
                throw new InvalidOperationException("No active SQL Server connection is available.");

            ScriptObjectSelectionItem selectedObject = null;

            // Parse the (possibly bracket-quoted) multi-part name locally
            // instead of a PARSENAME roundtrip to the server: right-most part
            // is the object name, the parts before it may carry schema,
            // database and linked server qualifiers.
            string databaseOverride = string.Empty;
            string linkedServer = string.Empty;

            if (!DatabaseIdentifier.TrySplitSqlServer(selectedObjectName, out List<string> nameParts)
                || nameParts.Count == 0
                || nameParts[nameParts.Count - 1].Length == 0)
            {
                throw new Exception($"Failed to extract a table name from the provided object string: {selectedObjectName}");
            }

            ParsedObjectName parsedObjectName = new ParsedObjectName(
                nameParts.Count >= 2 ? nameParts[nameParts.Count - 2] : string.Empty,
                nameParts[nameParts.Count - 1]);
            if (nameParts.Count == 4)
            {
                linkedServer = nameParts[0];
                databaseOverride = nameParts[1];
            }
            else if (nameParts.Count == 3)
            {
                databaseOverride = nameParts[0];
            }

            using (SqlConnection currentServerConnection = new SqlConnection(connectionInfo.FullConnectionString))
            {
                currentServerConnection.Open();

                if (!string.IsNullOrWhiteSpace(linkedServer))
                {
                    return QueryLinkedModuleDefinition(currentServerConnection, linkedServer, databaseOverride,
                        parsedObjectName.SchemaName, parsedObjectName.ObjectName, selectedObjectName);
                }

                // Search only the database the object belongs to: the one
                // written in the name, or the editor window's current one.
                // Probing other databases (the old master fallback) produced
                // ambiguous matches and popped up the database picker even
                // when the current database had an unambiguous hit.
                string searchDatabase = !string.IsNullOrWhiteSpace(databaseOverride)
                    ? databaseOverride
                    : (!string.IsNullOrWhiteSpace(connectionInfo.Database)
                        ? connectionInfo.Database
                        : currentServerConnection.Database);

                // Modules (procedures, views, functions) are read live from the
                // server with one cheap query, so F12 always shows the current
                // definition even when someone altered the object remotely
                // after the completion metadata cache loaded. This also skips
                // the heavier SMO scripting path for the most common target.
                string moduleScript = QueryModuleDefinition(currentServerConnection, searchDatabase,
                    parsedObjectName.SchemaName, parsedObjectName.ObjectName);
                if (moduleScript != null) return moduleScript;

                List<ScriptObjectSelectionItem> matches = QueryObjects(
                    currentServerConnection,
                    searchDatabase,
                    parsedObjectName.ObjectName,
                    parsedObjectName.SchemaName);

                matches = DeduplicateMatches(matches);

                if (matches.Count == 0)
                {
                    throw new Exception($"The specified object was not found: '{selectedObjectName}'.");
                }

                if (matches.Count == 1)
                {
                    selectedObject = matches[0];
                }
                else
                {
                    ScriptObjectSelectionItem picked = null;
                    ThreadHelper.JoinableTaskFactory.Run(async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var dialog = new ScriptObjectPickerDialog(matches);
                        var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                        if (uiShell != null && uiShell.GetDialogOwnerHwnd(out var hwnd) == 0 && hwnd != IntPtr.Zero)
                            new WindowInteropHelper(dialog).Owner = hwnd;
                        if (dialog.ShowDialog() == true) picked = dialog.SelectedObject;
                    });
                    selectedObject = picked;
                    if (selectedObject == null)
                    {
                        return string.Empty;
                    }
                }

                // Reuse the already-authenticated connection for SMO instead
                // of opening a second one; scripting runs on the same server
                // and database the lookup just used.
                ServerConnection smoConnection = new ServerConnection(currentServerConnection);
                Server server = new Server(smoConnection);

                Scripter scripter = new Scripter(server) { Options = new ScriptingOptions() };

                if (selectedObject.TypeDesc == "USER_TABLE")
                {
                    scripter.Options.ScriptData = false;
                    scripter.Options.DriAllKeys = true;

                    scripter.Options.Indexes = true;
                    scripter.Options.Triggers = true;
                    scripter.Options.Default = true;
                    scripter.Options.DriAll = true;

                    scripter.Options.ScriptDataCompression = true;
                    scripter.Options.NoCollation = true;
                }
                else
                {
                    scripter.Options.ScriptForCreateOrAlter = true;
                    scripter.Options.EnforceScriptingOptions = true;
                }
                // scripter.Options.ScriptBatchTerminator = true; -> this doesn't work for some reason..

                Database db = server.Databases[selectedObject.DatabaseName];
                SqlSmoObject dbObject = FindSmoObject(db, selectedObject);

                string fullScriptResult = String.Empty;

                if (dbObject != null)
                {
                    System.Collections.Specialized.StringCollection sc = scripter.Script(new Urn[] { dbObject.Urn });

                    StringBuilder sb = new StringBuilder();
                    foreach (string line in sc)
                    {
                        sb.AppendLine(line);
                        sb.AppendLine("GO");
                    }
                    fullScriptResult = sb.ToString();

                    // additional format to make it pretty
                    if (selectedObject.TypeDesc == "USER_TABLE")
                    {
                        TSql170Parser sqlParser = new TSql170Parser(false);
                        IList<ParseError> parseErrors = new List<ParseError>();
                        TSqlFragment result = sqlParser.Parse(new StringReader(fullScriptResult), out parseErrors);

                        // leave it as is if for some reason we can't format it
                        if (parseErrors.Count == 0)
                        {
                            Sql170ScriptGenerator gen = new Sql170ScriptGenerator();
                            gen.Options.AlignClauseBodies = false;
                            gen.Options.IncludeSemicolons = false;
                            gen.GenerateScript(result, out fullScriptResult);
                        }
                    }

                }
                else
                {
                    throw new Exception($"The specified object was not found: '{selectedObjectName}'.");
                }

                return fullScriptResult;
            }
        }

        // Reads the live CREATE text of a stored procedure, view or function
        // with a single query. Returns null when the name does not resolve to
        // exactly one readable module, letting the caller fall back to the
        // generic object search (tables, synonyms, triggers, constraints, ...).
        private static string QueryModuleDefinition(SqlConnection connection, string databaseName,
            string schemaName, string objectName)
        {
            string quotedDatabase = DatabaseIdentifier.SqlServerPart(databaseName);
            string sql = $@"
SELECT s.name, OBJECT_DEFINITION(o.object_id)
FROM {quotedDatabase}.sys.objects o
JOIN {quotedDatabase}.sys.schemas s ON s.schema_id = o.schema_id
WHERE o.name = @objectName
  AND (@schemaName IS NULL OR s.name = @schemaName)
  AND o.type IN ('V','P','PC','FN','IF','TF','FS','FT');";

            int matches = 0;
            string definition = null;

            using (SqlCommand command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 15;
                command.Parameters.Add(new SqlParameter("objectName", objectName));
                command.Parameters.Add(new SqlParameter("schemaName", string.IsNullOrWhiteSpace(schemaName) ? (object)DBNull.Value : schemaName));
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        matches++;
                        if (!reader.IsDBNull(1)) definition = reader.GetString(1);
                    }
                }
            }

            // Ambiguous across schemas, or not a readable module (encrypted
            // definitions come back NULL): let the generic path handle it.
            if (matches != 1 || definition == null) return null;

            // Mirror the SMO scripting output, which wraps modules in the
            // standard SET batches so re-running the script is unambiguous.
            return "SET ANSI_NULLS ON" + Environment.NewLine + "GO" + Environment.NewLine
                 + "SET QUOTED_IDENTIFIER ON" + Environment.NewLine + "GO" + Environment.NewLine
                 + definition.TrimEnd() + Environment.NewLine + "GO" + Environment.NewLine;
        }

        // Resolves the SMO object using direct filtered indexers instead of
        // Contains(). Contains forces SMO to load and scan the whole
        // collection (all tables, all procedures, ...) which dominated the
        // F12 latency on large databases; the two-argument name indexer only
        // fetches the requested object.
        private static SqlSmoObject FindSmoObject(Database db, ScriptObjectSelectionItem selectedObject)
        {
            string schema = selectedObject.SchemaName;
            string name = selectedObject.ObjectName;
            string typeDesc = selectedObject.TypeDesc ?? string.Empty;

            switch (typeDesc)
            {
                case "SQL_TRIGGER":
                    return FindTableTrigger(db, selectedObject.ParentObjectName, name, schema);
                case "INDEX":
                case "PRIMARY_KEY_CONSTRAINT":
                case "UNIQUE_CONSTRAINT":
                    return FindTableIndex(db, selectedObject.ParentObjectName, name, schema);
                case "FOREIGN_KEY_CONSTRAINT":
                    return FindTableForeignKey(db, selectedObject.ParentObjectName, name, schema);
                case "CHECK_CONSTRAINT":
                    return FindTableCheck(db, selectedObject.ParentObjectName, name, schema);
                case "DEFAULT_CONSTRAINT":
                    return FindDefaultConstraint(db, selectedObject.ParentObjectName, name, schema);
            }

            SqlSmoObject found = ResolveFromCollections(db, typeDesc, name, schema);
            return found;
        }

        private static SqlSmoObject ResolveFromCollections(Database db, string typeDesc, string name, string schema)
        {
            // The two-argument name indexer on SMO collections issues a
            // filtered query and returns null when missing; each probe is
            // cheap. The collection matching typeDesc is probed first so the
            // common path needs exactly one roundtrip.
            if (typeDesc.Contains("PROCEDURE"))
                return FirstNonNull(
                    () => db.StoredProcedures[name, schema],
                    () => db.Tables[name, schema]);
            if (typeDesc.Contains("FUNCTION"))
                return FirstNonNull(
                    () => db.UserDefinedFunctions[name, schema],
                    () => db.Tables[name, schema]);
            if (typeDesc.Contains("VIEW"))
                return FirstNonNull(
                    () => db.Views[name, schema],
                    () => db.UserDefinedFunctions[name, schema]);
            if (typeDesc == "SYNONYM")
                return FirstNonNull(
                    () => db.Synonyms[name, schema],
                    () => db.Tables[name, schema]);
            if (typeDesc == "TYPE_TABLE")
                return FirstNonNull(
                    () => db.UserDefinedTableTypes[name, schema],
                    () => db.Tables[name, schema]);
            if (typeDesc == "CLR_TYPE" || typeDesc == "USER_TYPE")
                return FirstNonNull(
                    () => db.UserDefinedTypes[name, schema],
                    () => db.Tables[name, schema]);
            return FirstNonNull(
                () => db.Tables[name, schema],
                () => db.StoredProcedures[name, schema],
                () => db.UserDefinedFunctions[name, schema],
                () => db.Views[name, schema],
                () => db.Synonyms[name, schema],
                () => db.UserDefinedTableTypes[name, schema],
                () => db.UserDefinedTypes[name, schema]);
        }

        private static SqlSmoObject FirstNonNull(params Func<SqlSmoObject>[] lookups)
        {
            foreach (Func<SqlSmoObject> lookup in lookups)
            {
                SqlSmoObject candidate = lookup();
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static string QueryLinkedModuleDefinition(SqlConnection connection, string serverName, string databaseName,
            string schemaName, string objectName, string selectedObjectName)
        {
            string prefix = DatabaseIdentifier.SqlServerPart(serverName) + "." + DatabaseIdentifier.SqlServerPart(databaseName);
            string sql = $@"
SELECT sm.definition
FROM {prefix}.sys.sql_modules AS sm
JOIN {prefix}.sys.objects AS o ON o.object_id = sm.object_id
JOIN {prefix}.sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.name = @objectName
  AND (@schemaName IS NULL OR s.name = @schemaName);";

            var definitions = new List<string>();
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 15;
                command.Parameters.Add(new SqlParameter("objectName", objectName));
                command.Parameters.Add(new SqlParameter("schemaName", string.IsNullOrWhiteSpace(schemaName) ? (object)DBNull.Value : schemaName));
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read()) definitions.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
                }
            }

            if (definitions.Count > 1)
                throw new InvalidOperationException($"The linked-server object name is ambiguous: '{selectedObjectName}'. Include its schema.");
            if (definitions.Count == 1 && definitions[0] == null)
                throw new InvalidOperationException($"The linked-server module is encrypted and its definition cannot be read: '{selectedObjectName}'.");
            if (definitions.Count == 1)
                return definitions[0].TrimEnd() + Environment.NewLine + "GO" + Environment.NewLine;

            throw new NotSupportedException($"F12 can open stored procedures, views, functions, and triggers through a linked server, but '{selectedObjectName}' is not a readable SQL module. Table scripting requires a direct connection to that server.");
        }



        private static List<ScriptObjectSelectionItem> QueryObjects(
            SqlConnection connection,
            string databaseName,
            string objectName,
            string schemaName)
        {
            string quotedDatabase = DatabaseIdentifier.SqlServerPart(databaseName);
            string commandText = $@"USE {quotedDatabase};
            SELECT o.type_desc,
                   s.name,
                   o.name,
                   o.object_id,
                   DB_NAME(),
                   parent_object_id,
                   OBJECT_NAME(parent_object_id) as parent_object_name      
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.name = @objectName
              AND (@schemaName IS NULL OR s.name = @schemaName)";
            commandText += @"
            UNION ALL
            SELECT 'INDEX' AS type_desc,
                   s.name,
                   i.name,
                   o.object_id,
                   DB_NAME(),
                   i.object_id,
                   OBJECT_NAME(i.object_id) 
            FROM sys.indexes i
            JOIN sys.objects o ON i.object_id = o.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE i.name = @objectName
              AND (@schemaName IS NULL OR s.name = @schemaName);";

            using (SqlCommand cmd = new SqlCommand(commandText, connection))
            {
                cmd.Parameters.Add(new SqlParameter("objectName", objectName));
                cmd.Parameters.Add(new SqlParameter("schemaName", string.IsNullOrWhiteSpace(schemaName) ? (object)DBNull.Value : schemaName));

                List<ScriptObjectSelectionItem> matches = new List<ScriptObjectSelectionItem>();
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        matches.Add(new ScriptObjectSelectionItem(
                           reader.GetString(0),
                           reader.GetString(1),
                           reader.GetString(2),
                           reader.GetInt32(3),
                           reader.GetString(4),
                           reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                           reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
                       ));
                    }
                }

                return matches;
            }
        }

        private static List<ScriptObjectSelectionItem> DeduplicateMatches(List<ScriptObjectSelectionItem> matches)
        {
            Dictionary<string, ScriptObjectSelectionItem> unique = new Dictionary<string, ScriptObjectSelectionItem>(StringComparer.OrdinalIgnoreCase);

            foreach (ScriptObjectSelectionItem match in matches)
            {
                string key = $"{match.DatabaseName}|{match.SchemaName}|{match.ObjectName}|{match.TypeDesc}|{match.ObjectId}";
                if (!unique.ContainsKey(key))
                {
                    unique[key] = match;
                }
            }

            return new List<ScriptObjectSelectionItem>(unique.Values);
        }

        private static SqlSmoObject FindTableTrigger(Database db, string ParentObjectName, string triggerName, string schemaName)
        {

            Table table = db.Tables[ParentObjectName, schemaName];
            if (table.Triggers.Contains(triggerName))
            {
                return table.Triggers[triggerName];
            }

            return null;
        }

        private static SqlSmoObject FindTableIndex(Database db, string ParentObjectName, string indexName, string schemaName)
        {
            Table table = db.Tables[ParentObjectName, schemaName];

            if (table.Indexes.Contains(indexName))
            {
                return table.Indexes[indexName];
            }


            return null;
        }

        private static SqlSmoObject FindTableForeignKey(Database db, string ParentObjectName, string constraintName, string schemaName)
        {
            Table table = db.Tables[ParentObjectName, schemaName];
            if (table.ForeignKeys.Contains(constraintName))
            {
                return table.ForeignKeys[constraintName];
            }

            return null;
        }

        private static SqlSmoObject FindTableCheck(Database db, string ParentObjectName, string constraintName, string schemaName)
        {
            Table table = db.Tables[ParentObjectName, schemaName];

            if (table.Checks.Contains(constraintName))
            {
                return table.Checks[constraintName];
            }

            return null;
        }

        private static SqlSmoObject FindDefaultConstraint(Database db, string ParentObjectName, string constraintName, string schemaName)
        {
            Table table = db.Tables[ParentObjectName, schemaName];

            foreach (Column column in table.Columns)
            {
                if (column.DefaultConstraint != null
                    && string.Equals(column.DefaultConstraint.Name, constraintName, StringComparison.OrdinalIgnoreCase))
                {
                    return column.DefaultConstraint;
                }
            }

            return null;
        }

        private sealed class ParsedObjectName
        {
            public ParsedObjectName(string schemaName, string objectName)
            {
                SchemaName = schemaName;
                ObjectName = objectName ?? throw new ArgumentNullException(nameof(objectName));
            }

            public string SchemaName { get; }

            public string ObjectName { get; }
        }

    }


}
