using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MSSQLTool.Completion;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace MSSQLTool
{
    public static class AsteriskExpansionService
    {
        private static readonly ConcurrentDictionary<IntPtr, ExpansionRequest> PendingExpansions = new ConcurrentDictionary<IntPtr, ExpansionRequest>();
        private sealed class ExpansionRequest
        {
            public IVsTextView View;
            public IVsTextLines Buffer;
            public string DocumentText;
            public int Line;
            public int CaretColumn;
            public int StarColumn;
            public int StarOffset;
            public string Qualifier;
            public SelectInfo Select;
            public string TempTableSetup;
            public ScriptFactoryAccess.ConnectionInfo Connection;
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
        }
        private sealed class SelectInfo
        {
            public string MetadataStatementText { get; set; }
            public int StatementStartOffset { get; set; }
            public Dictionary<string, string> TableQualifiersByName { get; set; }
            public List<TableInfo> Tables { get; set; }
            public Dictionary<string, List<string>> LocalColumnsByName { get; set; }
            public Dictionary<string, QueryExpression> LocalQueriesByName { get; set; }
            public bool AllowMetadataFallback { get; set; }
        }

        private sealed class TableInfo
        {
            public string ServerName { get; set; }
            public string DatabaseName { get; set; }
            public string SchemaName { get; set; }
            public string TableName { get; set; }
            public string Qualifier { get; set; }
            public QueryExpression QueryExpression { get; set; }
        }

        private sealed class ColumnInfo
        {
            public string Name { get; set; }
            public string SourceTableName { get; set; }
            public string SourceQualifier { get; set; }
            public string Qualifier { get; set; } = string.Empty;
        }

        private sealed class TempTableSetup
        {
            public int Index;
            public List<string> Statements { get; } = new List<string>();
        }

        public static bool TryExpand(IVsTextView textView)
        {
            try
            {
                if (textView == null || textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                    return false;

                textView.GetCaretPos(out int line, out int column);
                if (!TryGetAsteriskAtCaret(textLines, line, column, out int starColumn, out string qualifier))
                    return false;

                if (!TryGetFullText(textLines, out string fullText))
                    return false;

                int starOffset = GetAbsoluteOffset(fullText, line, starColumn);
                if (!TryGetCurrentSelectStatement(fullText, line + 1, column + 1, starOffset, out SelectInfo selectInfo))
                    return false;

                string tempTableSetup = GetPriorTempTableCreateStatements(fullText, selectInfo.StatementStartOffset);
                List<ColumnInfo> columns = GetResultColumns(selectInfo, tempTableSetup, qualifier);
                if (columns.Count == 0)
                    return QueueExpansionAfterMetadataLoad(textView, textLines, fullText, line, column, starColumn, starOffset, qualifier, selectInfo, tempTableSetup);

                string replacement = BuildColumnList(columns, starColumn, qualifier);
                ReplaceText(textLines, line, starColumn, column, replacement);
                SetCaretPosition(textView, line, starColumn, replacement, replacement.Length);
                return true;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Asterisk Expansion", "Expansion failed", ex);
                return false;
            }
        }

        private static bool QueueExpansionAfterMetadataLoad(IVsTextView textView, IVsTextLines buffer, string documentText,
            int line, int caretColumn, int starColumn, int starOffset, string qualifier, SelectInfo selectInfo, string tempTableSetup)
        {
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connection == null || string.IsNullOrWhiteSpace(connection.FullConnectionString)) return false;
            IntPtr key = textView.GetWindowHandle();
            var request = new ExpansionRequest
            {
                View = textView, Buffer = buffer, DocumentText = documentText, Line = line, CaretColumn = caretColumn,
                StarColumn = starColumn, StarOffset = starOffset, Qualifier = qualifier, Select = selectInfo,
                TempTableSetup = tempTableSetup, Connection = connection
            };
            if (PendingExpansions.TryGetValue(key, out ExpansionRequest previous)) previous.Cancellation.Cancel();
            PendingExpansions[key] = request;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    MetadataSnapshot metadata = await SqlMetadataCache.GetAsync(connection, request.Cancellation.Token);
                    request.Cancellation.Token.ThrowIfCancellationRequested();
                    ApplyCachedColumns(request.Select, metadata);
                    List<ColumnInfo> resolved = GetColumnsFromTableReferences(null, request.Select.Tables, request.Qualifier,
                        request.Select.LocalColumnsByName, request.Select.LocalQueriesByName);
                    if (resolved.Count == 0)
                        resolved = await Task.Run(() => ResolveFromServer(request), request.Cancellation.Token);
                    request.Cancellation.Token.ThrowIfCancellationRequested();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!IsRequestCurrent(request))
                    {
                        ReportExpansionNotice("SELECT * expansion skipped — the text changed while metadata was loading.");
                        return;
                    }
                    if (resolved.Count == 0)
                    {
                        ReportExpansionNotice("Could not expand SELECT * — the columns of the referenced tables could not be resolved.");
                        return;
                    }
                    string replacement = BuildColumnList(resolved, request.StarColumn, request.Qualifier);
                    ReplaceText(request.Buffer, request.Line, request.StarColumn, request.CaretColumn, replacement);
                    SetCaretPosition(request.View, request.Line, request.StarColumn, replacement, replacement.Length);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { FeatureDiagnostics.Report("Asterisk Expansion", "Deferred expansion failed", ex); }
                finally
                {
                    if (PendingExpansions.TryGetValue(key, out ExpansionRequest current) && ReferenceEquals(current, request))
                        PendingExpansions.TryRemove(key, out ExpansionRequest _);
                    request.Cancellation.Dispose();
                }
            });
            return true;
        }

        /// <summary>Deferred expansions previously failed silently; surface why the pending Tab did nothing.</summary>
        private static void ReportExpansionNotice(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText(LocalizationManager.T(message));
        }

        private static bool IsRequestCurrent(ExpansionRequest request)
        {
            if (request.Cancellation.IsCancellationRequested || request.View == null || request.Buffer == null) return false;
            if (request.View.GetBuffer(out IVsTextLines currentBuffer) != VSConstants.S_OK || !ReferenceEquals(currentBuffer, request.Buffer)) return false;
            if (!TryGetFullText(currentBuffer, out string currentText)) return false;
            request.View.GetCaretPos(out int line, out int column);
            if (!TryGetAsteriskAtCaret(currentBuffer, line, column, out int starColumn, out string qualifier)) return false;
            return IsExpansionSnapshotCurrent(request.DocumentText, currentText, request.Line, request.CaretColumn,
                request.StarColumn, request.StarOffset, request.Qualifier, line, column, starColumn,
                GetAbsoluteOffset(currentText, line, starColumn), qualifier);
        }

        internal static bool IsExpansionSnapshotCurrent(string originalText, string currentText,
            int originalLine, int originalCaretColumn, int originalStarColumn, int originalStarOffset, string originalQualifier,
            int currentLine, int currentCaretColumn, int currentStarColumn, int currentStarOffset, string currentQualifier)
        {
            return string.Equals(originalText, currentText, StringComparison.Ordinal)
                && originalLine == currentLine && originalCaretColumn == currentCaretColumn
                && originalStarColumn == currentStarColumn && originalStarOffset == currentStarOffset
                && string.Equals(originalQualifier, currentQualifier, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyCachedColumns(SelectInfo selectInfo, MetadataSnapshot cached)
        {
            if (cached == null) return;
            foreach (TableInfo table in selectInfo.Tables)
            {
                if (selectInfo.LocalColumnsByName.ContainsKey(table.TableName)) continue;
                DatabaseObjectMetadata match = cached.Objects.Find(o => string.Equals(o.Name, table.TableName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(table.ServerName) || string.Equals(o.Server, table.ServerName, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(table.DatabaseName) || string.Equals(o.Database, table.DatabaseName, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(table.SchemaName) || string.Equals(o.Schema, table.SchemaName, StringComparison.OrdinalIgnoreCase)));
                if (match != null) selectInfo.LocalColumnsByName[table.TableName] = match.Columns.ConvertAll(c => c.Name);
            }
        }

        private static List<ColumnInfo> ResolveFromServer(ExpansionRequest request)
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            using (var connection = new SqlConnection(request.Connection.FullConnectionString))
            {
                connection.Open();
                if (!string.IsNullOrWhiteSpace(request.TempTableSetup))
                {
                    using (var setup = connection.CreateCommand())
                    {
                        setup.CommandText = request.TempTableSetup;
                        setup.CommandTimeout = 10;
                        setup.ExecuteNonQuery();
                    }
                }
                List<ColumnInfo> columns = GetColumnsFromTableReferences(connection, request.Select.Tables, request.Qualifier,
                    request.Select.LocalColumnsByName, request.Select.LocalQueriesByName);
                if (columns.Count > 0) return columns;
                return request.Select.AllowMetadataFallback
                    ? GetResultColumnsFromFmtOnly(connection, request.Select.MetadataStatementText, request.Select.TableQualifiersByName,
                        string.IsNullOrWhiteSpace(request.Qualifier) && request.Select.Tables != null && request.Select.Tables.Count > 1)
                    : columns;
            }
        }

        private static string BuildColumnList(IList<ColumnInfo> columns, int starColumn, string qualifier)
        {
            var escaped = new List<string>();
            foreach (ColumnInfo column in columns)
            {
                if (!string.IsNullOrWhiteSpace(column.Name))
                    escaped.Add(column.Qualifier + "[" + column.Name.Replace("]", "]]") + "]");
            }

            if (escaped.Count == 0)
                return string.Empty;

            string indent = new string(' ', Math.Max(0, starColumn - qualifier.Length));
            string separator = "," + Environment.NewLine + indent + qualifier;
            return string.Join(separator, escaped);
        }

        private static bool TryGetAsteriskAtCaret(IVsTextLines textLines, int line, int column, out int starColumn, out string qualifier)
        {
            starColumn = -1;
            qualifier = string.Empty;

            if (column <= 0)
                return false;

            textLines.GetLengthOfLine(line, out int lineLength);
            if (column > lineLength)
                return false;

            textLines.GetLineText(line, 0, line, lineLength, out string lineText);
            starColumn = column - 1;
            if (string.IsNullOrEmpty(lineText) || starColumn >= lineText.Length || lineText[starColumn] != '*')
                return false;

            qualifier = GetQualifierBeforeAsterisk(lineText, starColumn);
            return true;
        }

        private static string GetQualifierBeforeAsterisk(string lineText, int starColumn)
        {
            if (starColumn < 2 || lineText[starColumn - 1] != '.')
                return string.Empty;

            int start = starColumn - 2;
            if (lineText[start] == ']')
            {
                while (start >= 0 && lineText[start] != '[')
                    start--;

                if (start < 0)
                    return string.Empty;
            }
            else
            {
                while (start >= 0 && (char.IsLetterOrDigit(lineText[start]) || lineText[start] == '_' || lineText[start] == '#'))
                    start--;

                start++;
            }

            return lineText.Substring(start, starColumn - start);
        }

        private static bool TryGetFullText(IVsTextLines textLines, out string fullText)
        {
            fullText = null;

            if (textLines.GetLastLineIndex(out int lastLine, out int lastIndex) != VSConstants.S_OK)
                return false;

            return textLines.GetLineText(0, 0, lastLine, lastIndex, out fullText) == VSConstants.S_OK
                && !string.IsNullOrWhiteSpace(fullText);
        }

        private static bool TryGetCurrentSelectStatement(string fullText, int cursorLine, int cursorColumn, int starOffset, out SelectInfo selectInfo)
        {
            selectInfo = null;

            var parser = new TSql170Parser(false);
            TSqlFragment fragment = parser.Parse(new StringReader(fullText), out IList<ParseError> _);
            if (!(fragment is TSqlScript script) || fragment.ScriptTokenStream == null)
                return false;

            foreach (TSqlBatch batch in script.Batches)
            {
                foreach (TSqlStatement statement in batch.Statements)
                {
                    SelectStatement selectStatement = GetSelectStatement(statement);
                    TSqlFragment selectFragment = selectStatement != null && selectStatement.FragmentLength > 0 ? (TSqlFragment)selectStatement : selectStatement?.QueryExpression;
                    if (selectStatement == null || selectFragment == null || !ContainsCursor(fragment, selectFragment, cursorLine, cursorColumn))
                        continue;

                    if (selectFragment == null || selectFragment.StartOffset < 0 || selectFragment.FragmentLength <= 0 || selectFragment.StartOffset + selectFragment.FragmentLength > fullText.Length)
                        return false;

                    return TryBuildSelectInfo(script, fullText, selectStatement, starOffset, out selectInfo);
                }
            }

            return false;
        }

        private static SelectStatement GetSelectStatement(TSqlStatement statement)
        {
            if (statement is SelectStatement select) return select;
            if (statement is InsertStatement insert && insert.InsertSpecification?.InsertSource is SelectInsertSource source)
                return new SelectStatement { QueryExpression = source.Select, WithCtesAndXmlNamespaces = insert.WithCtesAndXmlNamespaces };
            if (statement is CreateViewStatement createView) return createView.SelectStatement;
            if (statement is AlterViewStatement alterView) return alterView.SelectStatement;
            if (statement is CreateOrAlterViewStatement createOrAlterView) return createOrAlterView.SelectStatement;
            return null;
        }

        internal static bool CanAnalyzeAsteriskContext(string fullText, int starOffset)
        {
            var parser = new TSql170Parser(false);
            TSqlFragment parsed = parser.Parse(new StringReader(fullText ?? string.Empty), out IList<ParseError> errors);
            var script = parsed as TSqlScript;
            if (script == null || (errors != null && errors.Count > 0)) return false;
            foreach (TSqlBatch batch in script.Batches)
                foreach (TSqlStatement statement in batch.Statements)
                {
                    SelectStatement select = GetSelectStatement(statement);
                    if (select != null && TryBuildSelectInfo(script, fullText, select, starOffset, out SelectInfo _)) return true;
                }
            return false;
        }

        private static bool TryBuildSelectInfo(TSqlScript script, string fullText, SelectStatement statement, int starOffset, out SelectInfo selectInfo)
        {
            selectInfo = null;

            if (!TryFindQueryWithStar(statement, starOffset, out QuerySpecification query, out SelectElement selectedStar))
                return false;

            SelectElement firstElement = query.SelectElements[0];
            SelectElement lastElement = query.SelectElements[query.SelectElements.Count - 1];
            int selectListStart = firstElement.StartOffset;
            int selectListEnd = lastElement.StartOffset + lastElement.FragmentLength;

            if (selectListStart < 0 || selectListEnd <= selectListStart || selectListEnd > fullText.Length)
                return false;

            int statementStart = statement.StartOffset;
            int statementLength = statement.FragmentLength;
            if ((statementLength <= 0 || statementStart + statementLength > fullText.Length) && statement.QueryExpression != null)
            {
                statementStart = statement.QueryExpression.StartOffset;
                statementLength = statement.QueryExpression.FragmentLength;
            }
            if (statementStart < 0 || statementLength <= 0 || statementStart + statementLength > fullText.Length) return false;
            string statementText = fullText.Substring(statementStart, statementLength);
            string starText = fullText.Substring(selectedStar.StartOffset, selectedStar.FragmentLength);
            int relativeSelectListStart = selectListStart - statementStart;
            int relativeSelectListEnd = selectListEnd - statementStart;
            Dictionary<string, QueryExpression> localQueriesByName;
            Dictionary<string, List<string>> localColumnsByName = GetLocalColumns(script, statement, statementStart, out localQueriesByName);

            selectInfo = new SelectInfo
            {
                StatementStartOffset = statementStart,
                MetadataStatementText = statementText.Substring(0, relativeSelectListStart)
                    + starText
                    + statementText.Substring(relativeSelectListEnd),
                TableQualifiersByName = GetTableQualifiers(query.FromClause),
                Tables = GetTables(query.FromClause),
                LocalColumnsByName = localColumnsByName,
                LocalQueriesByName = localQueriesByName,
                AllowMetadataFallback = ReferenceEquals(query, statement.QueryExpression)
            };
            return true;
        }

        private static bool TryFindQueryWithStar(
            SelectStatement statement,
            int starOffset,
            out QuerySpecification query,
            out SelectElement selectedStar)
        {
            if (TryFindQueryWithStar(statement.QueryExpression, starOffset, out query, out selectedStar))
                return true;

            if (statement.WithCtesAndXmlNamespaces?.CommonTableExpressions == null)
                return false;

            foreach (CommonTableExpression cte in statement.WithCtesAndXmlNamespaces.CommonTableExpressions)
            {
                if (TryFindQueryWithStar(cte.QueryExpression, starOffset, out query, out selectedStar))
                    return true;
            }

            return false;
        }

        private static bool TryFindQueryWithStar(
            QueryExpression queryExpression,
            int starOffset,
            out QuerySpecification query,
            out SelectElement selectedStar)
        {
            query = queryExpression as QuerySpecification;
            selectedStar = null;

            if (query == null || query.SelectElements == null || query.SelectElements.Count == 0)
                return false;

            foreach (SelectElement element in query.SelectElements)
            {
                if (element is SelectStarExpression && ContainsOffset(element, starOffset))
                {
                    selectedStar = element;
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsOffset(TSqlFragment fragment, int offset)
        {
            return fragment.StartOffset <= offset && offset < fragment.StartOffset + fragment.FragmentLength;
        }

        private static bool ContainsCursor(TSqlFragment fragment, TSqlFragment statement, int cursorLine, int cursorColumn)
        {
            TSqlParserToken firstToken = fragment.ScriptTokenStream[statement.FirstTokenIndex];
            TSqlParserToken lastToken = fragment.ScriptTokenStream[statement.LastTokenIndex];
            int endColumn = lastToken.Column + lastToken.Text.Length;

            if (cursorLine < firstToken.Line || (cursorLine == firstToken.Line && cursorColumn < firstToken.Column))
                return false;

            if (cursorLine > lastToken.Line || (cursorLine == lastToken.Line && cursorColumn > endColumn))
                return false;

            return true;
        }

        internal static string GetPriorTempTableCreateStatements(string fullText, int beforeOffset)
        {
            if (string.IsNullOrWhiteSpace(fullText) || beforeOffset <= 0)
                return string.Empty;

            var parser = new TSql170Parser(false);
            TSqlFragment fragment = parser.Parse(new StringReader(fullText), out IList<ParseError> _);
            if (!(fragment is TSqlScript script))
                return string.Empty;

            var active = new Dictionary<string, TempTableSetup>(StringComparer.OrdinalIgnoreCase);
            foreach (TSqlBatch batch in script.Batches)
            {
                foreach (TSqlStatement statement in batch.Statements)
                {
                    if (statement.StartOffset >= beforeOffset)
                        continue;

                    if (statement.StartOffset < 0 || statement.FragmentLength <= 0 || statement.StartOffset + statement.FragmentLength > fullText.Length)
                        continue;

                    string statementText = fullText.Substring(statement.StartOffset, statement.FragmentLength);

                    if (statement is CreateTableStatement createTable
                        && IsTempTable(createTable.SchemaObjectName)
                        && createTable.SchemaObjectName?.BaseIdentifier != null)
                    {
                        var setup = new TempTableSetup { Index = statement.StartOffset };
                        setup.Statements.Add(statementText);
                        active[createTable.SchemaObjectName.BaseIdentifier.Value] = setup;
                        continue;
                    }

                    Match drop = Regex.Match(statementText, @"^\s*DROP\s+TABLE(?:\s+IF\s+EXISTS)?\s+(?<name>\#\#?\w+)", RegexOptions.IgnoreCase);
                    if (drop.Success) { active.Remove(drop.Groups["name"].Value); continue; }
                    Match alter = Regex.Match(statementText, @"^\s*ALTER\s+TABLE\s+(?<name>\#\#?\w+)\b", RegexOptions.IgnoreCase);
                    if (alter.Success && active.TryGetValue(alter.Groups["name"].Value, out TempTableSetup setupForAlter))
                        setupForAlter.Statements.Add(statementText);
                }
            }

            return string.Join(Environment.NewLine, active.Values.OrderBy(x => x.Index).SelectMany(x => x.Statements));
        }

        private static bool IsTempTable(SchemaObjectName name)
        {
            if (name == null || name.BaseIdentifier == null)
                return false;

            string value = name.BaseIdentifier.Value;
            return !string.IsNullOrWhiteSpace(value) && value.StartsWith("#", StringComparison.Ordinal);
        }

        private static List<ColumnInfo> GetResultColumns(SelectInfo selectInfo, string tempTableSetup, string typedQualifier)
        {
            var columns = GetColumnsFromTableReferences(null, selectInfo.Tables, typedQualifier, selectInfo.LocalColumnsByName, selectInfo.LocalQueriesByName);
            if (columns.Count > 0)
                return columns;

            var connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.FullConnectionString))
                return columns;

            if (SqlMetadataCache.TryGetCached(connectionInfo, out MetadataSnapshot cached))
            {
                ApplyCachedColumns(selectInfo, cached);
                columns = GetColumnsFromTableReferences(null, selectInfo.Tables, typedQualifier, selectInfo.LocalColumnsByName, selectInfo.LocalQueriesByName);
                if (columns.Count > 0) return columns;
            }

            // Editor command filters run on the SSMS UI thread. Never open a database
            // connection here: warm the shared cache asynchronously and let the next
            // invocation use it.
            return columns;
        }

        private static List<ColumnInfo> GetColumnsFromTableReferences(
            SqlConnection connection,
            List<TableInfo> tables,
            string typedQualifier,
            Dictionary<string, List<string>> localColumnsByName,
            Dictionary<string, QueryExpression> localQueriesByName)
        {
            var result = new List<ColumnInfo>();
            if (tables == null || tables.Count == 0)
                return result;

            List<TableInfo> targetTables = tables;
            if (!string.IsNullOrWhiteSpace(typedQualifier))
            {
                string normalizedQualifier = typedQualifier.TrimEnd('.');
                targetTables = tables.FindAll(t => string.Equals(t.Qualifier.TrimEnd('.'), normalizedQualifier, StringComparison.OrdinalIgnoreCase));
                if (targetTables.Count == 0)
                    return result;
            }

            foreach (TableInfo table in targetTables)
            {
                List<string> tableColumns = GetLocalColumnNames(table, localColumnsByName);
                if (tableColumns.Count == 0)
                    tableColumns = GetLocalQueryColumnNames(connection, table, localColumnsByName, localQueriesByName);

                if (tableColumns.Count == 0)
                {
                    if (connection == null)
                        return new List<ColumnInfo>();

                    tableColumns = GetColumnNamesForTable(connection, table);
                }

                if (tableColumns.Count == 0)
                    return new List<ColumnInfo>();

                foreach (string columnName in tableColumns)
                {
                    result.Add(new ColumnInfo
                    {
                        Name = columnName,
                        SourceTableName = table.TableName,
                        SourceQualifier = table.Qualifier,
                        Qualifier = string.Empty
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(typedQualifier))
                ApplyQualifiers(result, BuildQualifierMap(targetTables), targetTables.Count > 1);

            return result;
        }

        private static List<string> GetLocalQueryColumnNames(
            SqlConnection connection,
            TableInfo table,
            Dictionary<string, List<string>> localColumnsByName,
            Dictionary<string, QueryExpression> localQueriesByName)
        {
            if (table == null || string.IsNullOrWhiteSpace(table.TableName))
                return new List<string>();

            QueryExpression queryExpression = table.QueryExpression;
            if (queryExpression == null)
            {
                if (localQueriesByName == null || !localQueriesByName.TryGetValue(table.TableName, out queryExpression))
                    return new List<string>();
            }

            List<string> columns = ResolveQueryColumns(connection, queryExpression, localColumnsByName, localQueriesByName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (columns.Count > 0 && localColumnsByName != null)
                localColumnsByName[table.TableName] = columns;

            return columns;
        }

        private static List<string> GetLocalColumnNames(TableInfo table, Dictionary<string, List<string>> localColumnsByName)
        {
            if (table == null || localColumnsByName == null || string.IsNullOrWhiteSpace(table.TableName))
                return new List<string>();

            return localColumnsByName.TryGetValue(table.TableName, out List<string> columns)
                ? columns
                : new List<string>();
        }

        private static List<string> GetColumnNamesForTable(SqlConnection connection, TableInfo table)
        {
            var result = new List<string>();
            if (table == null || string.IsNullOrWhiteSpace(table.TableName))
                return result;

            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = 5;
                command.Parameters.AddWithValue("@tableName", table.TableName);
                command.Parameters.AddWithValue("@schemaName", string.IsNullOrWhiteSpace(table.SchemaName) ? (object)DBNull.Value : table.SchemaName);
                if (table.TableName.StartsWith("#", StringComparison.Ordinal))
                {
                    command.Parameters.AddWithValue("@objectName", GetObjectNameForLookup(table));
                    command.CommandText = "SELECT c.[name] FROM tempdb.sys.columns c WHERE c.[object_id] = OBJECT_ID(@objectName) ORDER BY c.column_id;";
                }
                else
                {
                    string catalog = string.Empty;
                    if (!string.IsNullOrWhiteSpace(table.ServerName))
                    {
                        if (string.IsNullOrWhiteSpace(table.DatabaseName)) return result;
                        catalog = DatabaseIdentifier.SqlServerPart(table.ServerName) + "." + DatabaseIdentifier.SqlServerPart(table.DatabaseName) + ".";
                    }
                    else if (!string.IsNullOrWhiteSpace(table.DatabaseName))
                    {
                        catalog = DatabaseIdentifier.SqlServerPart(table.DatabaseName) + ".";
                    }
                    command.CommandText = "SELECT c.[name] FROM " + catalog + "sys.columns c "
                        + "JOIN " + catalog + "sys.objects o ON o.object_id=c.object_id "
                        + "JOIN " + catalog + "sys.schemas s ON s.schema_id=o.schema_id "
                        + "WHERE o.[name]=@tableName AND (@schemaName IS NULL OR s.[name]=@schemaName) ORDER BY c.column_id;";
                }

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                            result.Add(reader.GetString(0));
                    }
                }
            }

            return result;
        }

        private static string GetObjectNameForLookup(TableInfo table)
        {
            if (table.TableName.StartsWith("#", StringComparison.Ordinal))
                return "tempdb.." + table.TableName;

            return string.IsNullOrWhiteSpace(table.SchemaName)
                ? table.TableName
                : table.SchemaName + "." + table.TableName;
        }

        private static Dictionary<string, string> BuildQualifierMap(List<TableInfo> tables)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TableInfo table in tables)
            {
                if (!string.IsNullOrWhiteSpace(table.TableName))
                    result[table.TableName] = table.Qualifier;
            }

            return result;
        }

        private static List<ColumnInfo> GetResultColumnsFromFmtOnly(SqlConnection connection, string statementText, Dictionary<string, string> tableQualifiersByName, bool qualifyAllColumns)
        {
            var columns = new List<ColumnInfo>();
            try
            {
                using (var command = new SqlCommand("EXEC sys.sp_describe_first_result_set @tsql=@sql, @params=NULL, @browse_information_mode=1;", connection))
                {
                    command.CommandTimeout = 10;
                    command.Parameters.AddWithValue("@sql", statementText);
                    using (var reader = command.ExecuteReader())
                    {
                        int nameOrdinal = reader.GetOrdinal("name");
                        int sourceTableOrdinal = -1;
                        try { sourceTableOrdinal = reader.GetOrdinal("source_table"); } catch { }
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(nameOrdinal)) continue;
                            columns.Add(new ColumnInfo { Name = reader.GetString(nameOrdinal), SourceTableName = sourceTableOrdinal >= 0 && !reader.IsDBNull(sourceTableOrdinal) ? reader.GetString(sourceTableOrdinal) : null });
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                FeatureDiagnostics.Report("Asterisk Expansion", "sp_describe_first_result_set could not resolve the query; using temp-table fallback", ex);
            }
            if (columns.Count > 0)
            {
                ApplyQualifiers(columns, tableQualifiersByName, qualifyAllColumns);
                return columns;
            }

            // Legacy fallback is restricted to the isolated background connection;
            // it is retained for session temp tables that the describe procedure rejects.
            if (statementText.IndexOf('#') < 0)
                return columns;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SET FMTONLY ON;" + Environment.NewLine + statementText + Environment.NewLine + "SET FMTONLY OFF;";
                command.CommandTimeout = 5;
                using (var reader = command.ExecuteReader())
                {
                    do
                    {
                        DataTable schema = reader.GetSchemaTable();
                        if (schema == null)
                            continue;

                        foreach (DataRow row in schema.Rows)
                        {
                            string name = row["ColumnName"] as string;
                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            columns.Add(new ColumnInfo
                            {
                                Name = name,
                                SourceTableName = GetSchemaString(row, "BaseTableName")
                            });
                        }

                        if (columns.Count > 0)
                        {
                            ApplyQualifiers(columns, tableQualifiersByName, qualifyAllColumns);
                            return columns;
                        }
                    }
                    while (reader.NextResult());
                }
            }

            return columns;
        }

        private static string GetSchemaString(DataRow row, string columnName)
        {
            return row.Table.Columns.Contains(columnName) ? row[columnName] as string : null;
        }

        private static void ApplyQualifiers(List<ColumnInfo> columns, Dictionary<string, string> tableQualifiersByName, bool qualifyAllColumns)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ColumnInfo column in columns)
            {
                if (!counts.ContainsKey(column.Name))
                    counts[column.Name] = 0;

                counts[column.Name]++;
            }

            foreach (ColumnInfo column in columns)
            {
                if (!qualifyAllColumns && counts[column.Name] <= 1)
                    continue;

                column.Qualifier = !string.IsNullOrWhiteSpace(column.SourceQualifier)
                    ? column.SourceQualifier
                    : GetQualifier(column.SourceTableName, tableQualifiersByName);
            }
        }

        private static string GetQualifier(string sourceTableName, Dictionary<string, string> tableQualifiersByName)
        {
            if (string.IsNullOrWhiteSpace(sourceTableName))
                return string.Empty;

            if (tableQualifiersByName != null && tableQualifiersByName.TryGetValue(sourceTableName, out string qualifier))
                return qualifier;

            if (tableQualifiersByName != null)
            {
                foreach (var pair in tableQualifiersByName)
                {
                    if (pair.Key.StartsWith("#", StringComparison.Ordinal)
                        && sourceTableName.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }
            }

            return EscapeIdentifier(sourceTableName) + ".";
        }

        private static string EscapeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return "[" + value.Replace("]", "]]") + "]";
        }

        private static Dictionary<string, string> GetTableQualifiers(FromClause fromClause)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fromClause == null || fromClause.TableReferences == null)
                return result;

            foreach (TableReference tableReference in fromClause.TableReferences)
            {
                AddTableQualifiers(tableReference, result);
            }

            return result;
        }

        private static List<TableInfo> GetTables(FromClause fromClause)
        {
            var result = new List<TableInfo>();
            if (fromClause == null || fromClause.TableReferences == null)
                return result;

            foreach (TableReference tableReference in fromClause.TableReferences)
            {
                AddTables(tableReference, result);
            }

            return result;
        }

        private static void AddTables(TableReference tableReference, List<TableInfo> result)
        {
            if (tableReference == null)
                return;

            if (tableReference is NamedTableReference namedTable)
            {
                string tableName = namedTable.SchemaObject?.BaseIdentifier?.Value;
                if (string.IsNullOrWhiteSpace(tableName))
                    return;

                string alias = namedTable.Alias?.Value;
                IList<Identifier> identifiers = namedTable.SchemaObject?.Identifiers;
                int count = identifiers?.Count ?? 0;
                result.Add(new TableInfo
                {
                    ServerName = count >= 4 ? identifiers[count - 4].Value : null,
                    DatabaseName = count >= 3 ? identifiers[count - 3].Value : null,
                    SchemaName = count >= 2 ? identifiers[count - 2].Value : null,
                    TableName = tableName,
                    Qualifier = !string.IsNullOrWhiteSpace(alias) ? alias + "." : EscapeIdentifier(tableName) + "."
                });
                return;
            }

            if (tableReference is VariableTableReference variableTable)
            {
                string tableName = variableTable.Variable?.Name;
                if (string.IsNullOrWhiteSpace(tableName))
                    return;

                string alias = variableTable.Alias?.Value;
                result.Add(new TableInfo
                {
                    TableName = tableName,
                    Qualifier = !string.IsNullOrWhiteSpace(alias) ? alias + "." : EscapeIdentifier(tableName) + "."
                });
                return;
            }

            if (tableReference is QueryDerivedTable derivedTable)
            {
                string alias = derivedTable.Alias?.Value;
                if (string.IsNullOrWhiteSpace(alias))
                    return;

                result.Add(new TableInfo
                {
                    TableName = alias,
                    Qualifier = alias + ".",
                    QueryExpression = derivedTable.QueryExpression
                });
                return;
            }

            if (tableReference is QualifiedJoin qualifiedJoin)
            {
                AddTables(qualifiedJoin.FirstTableReference, result);
                AddTables(qualifiedJoin.SecondTableReference, result);
                return;
            }

            if (tableReference is UnqualifiedJoin unqualifiedJoin)
            {
                AddTables(unqualifiedJoin.FirstTableReference, result);
                AddTables(unqualifiedJoin.SecondTableReference, result);
                return;
            }

            if (tableReference is JoinParenthesisTableReference parenthesizedJoin)
            {
                AddTables(parenthesizedJoin.Join, result);
            }
        }

        private static void AddTableQualifiers(TableReference tableReference, Dictionary<string, string> result)
        {
            if (tableReference == null)
                return;

            if (tableReference is NamedTableReference namedTable)
            {
                string tableName = namedTable.SchemaObject?.BaseIdentifier?.Value;
                if (string.IsNullOrWhiteSpace(tableName))
                    return;

                string alias = namedTable.Alias?.Value;
                result[tableName] = !string.IsNullOrWhiteSpace(alias) ? alias + "." : EscapeIdentifier(tableName) + ".";
                return;
            }

            if (tableReference is VariableTableReference variableTable)
            {
                string tableName = variableTable.Variable?.Name;
                if (string.IsNullOrWhiteSpace(tableName))
                    return;

                string alias = variableTable.Alias?.Value;
                result[tableName] = !string.IsNullOrWhiteSpace(alias) ? alias + "." : EscapeIdentifier(tableName) + ".";
                return;
            }

            if (tableReference is QueryDerivedTable derivedTable)
            {
                string alias = derivedTable.Alias?.Value;
                if (string.IsNullOrWhiteSpace(alias))
                    return;

                result[alias] = alias + ".";
                return;
            }

            if (tableReference is QualifiedJoin qualifiedJoin)
            {
                AddTableQualifiers(qualifiedJoin.FirstTableReference, result);
                AddTableQualifiers(qualifiedJoin.SecondTableReference, result);
                return;
            }

            if (tableReference is UnqualifiedJoin unqualifiedJoin)
            {
                AddTableQualifiers(unqualifiedJoin.FirstTableReference, result);
                AddTableQualifiers(unqualifiedJoin.SecondTableReference, result);
                return;
            }

            if (tableReference is JoinParenthesisTableReference parenthesizedJoin)
            {
                AddTableQualifiers(parenthesizedJoin.Join, result);
            }
        }

        private static Dictionary<string, List<string>> GetLocalColumns(
            TSqlScript script,
            SelectStatement statement,
            int statementStart,
            out Dictionary<string, QueryExpression> localQueriesByName)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            localQueriesByName = new Dictionary<string, QueryExpression>(StringComparer.OrdinalIgnoreCase);
            AddPriorTableVariables(script, statementStart, result);
            AddPriorSelectIntoTempTables(script, statementStart, result);
            AddCommonTableExpressions(statement.WithCtesAndXmlNamespaces, result, localQueriesByName);
            return result;
        }

        private static void AddPriorTableVariables(TSqlScript script, int targetOffset, Dictionary<string, List<string>> result)
        {
            if (script == null || result == null)
                return;

            foreach (TSqlBatch batch in script.Batches)
            {
                if (!BatchContainsOffset(batch, targetOffset))
                    continue;

                foreach (TSqlStatement statement in batch.Statements)
                {
                    if (statement.StartOffset >= targetOffset)
                        continue;

                    if (!(statement is DeclareTableVariableStatement declareTable) || declareTable.Body == null)
                        continue;

                    string variableName = declareTable.Body.VariableName?.Value;
                    if (string.IsNullOrWhiteSpace(variableName) || declareTable.Body.Definition == null)
                        continue;

                    var columns = new List<string>();
                    foreach (ColumnDefinition column in declareTable.Body.Definition.ColumnDefinitions)
                    {
                        string columnName = column.ColumnIdentifier?.Value;
                        if (!string.IsNullOrWhiteSpace(columnName))
                            columns.Add(columnName);
                    }

                    if (columns.Count > 0)
                        result[variableName] = columns;
                }
            }
        }

        private static void AddPriorSelectIntoTempTables(TSqlScript script, int targetOffset, Dictionary<string, List<string>> result)
        {
            if (script == null || result == null)
                return;

            foreach (TSqlBatch batch in script.Batches)
            {
                foreach (TSqlStatement statement in batch.Statements)
                {
                    if (statement.StartOffset >= targetOffset)
                        continue;

                    if (!(statement is SelectStatement selectInto) || !IsTempTable(selectInto.Into))
                        continue;

                    string tableName = selectInto.Into.BaseIdentifier?.Value;
                    if (string.IsNullOrWhiteSpace(tableName))
                        continue;

                    List<string> columns = InferColumns(selectInto.QueryExpression, result);
                    if (columns.Count > 0)
                        result[tableName] = columns;
                }
            }
        }

        private static bool BatchContainsOffset(TSqlBatch batch, int targetOffset)
        {
            return batch != null && batch.StartOffset <= targetOffset
                && targetOffset <= batch.StartOffset + batch.FragmentLength;
        }

        private static void AddCommonTableExpressions(
            WithCtesAndXmlNamespaces withClause,
            Dictionary<string, List<string>> result,
            Dictionary<string, QueryExpression> localQueriesByName)
        {
            if (withClause?.CommonTableExpressions == null || result == null)
                return;

            foreach (CommonTableExpression cte in withClause.CommonTableExpressions)
            {
                string cteName = cte.ExpressionName?.Value;
                if (string.IsNullOrWhiteSpace(cteName))
                    continue;

                if (cte.QueryExpression != null && localQueriesByName != null)
                    localQueriesByName[cteName] = cte.QueryExpression;

                List<string> columns = GetExplicitCteColumns(cte);
                if (columns.Count == 0)
                    columns = InferColumns(cte.QueryExpression, result);

                if (columns.Count > 0)
                    result[cteName] = columns;
            }
        }

        private static List<string> GetExplicitCteColumns(CommonTableExpression cte)
        {
            var result = new List<string>();
            if (cte?.Columns == null)
                return result;

            foreach (Identifier column in cte.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column?.Value))
                    result.Add(column.Value);
            }

            return result;
        }

        private static List<string> ResolveQueryColumns(
            SqlConnection connection,
            QueryExpression queryExpression,
            Dictionary<string, List<string>> localColumnsByName,
            Dictionary<string, QueryExpression> localQueriesByName,
            HashSet<string> resolving)
        {
            var query = queryExpression as QuerySpecification;
            var result = new List<string>();
            if (query == null || query.SelectElements == null)
                return result;

            foreach (SelectElement element in query.SelectElements)
            {
                if (element is SelectScalarExpression scalar)
                {
                    string columnName = GetOutputColumnName(scalar);
                    if (!string.IsNullOrWhiteSpace(columnName))
                        result.Add(columnName);
                    continue;
                }

                if (element is SelectStarExpression star)
                {
                    List<string> starColumns = ResolveStarColumns(connection, star, query.FromClause, localColumnsByName, localQueriesByName, resolving);
                    if (starColumns.Count == 0)
                        return new List<string>();

                    result.AddRange(starColumns);
                }
            }

            return result;
        }

        private static List<string> ResolveStarColumns(
            SqlConnection connection,
            SelectStarExpression star,
            FromClause fromClause,
            Dictionary<string, List<string>> localColumnsByName,
            Dictionary<string, QueryExpression> localQueriesByName,
            HashSet<string> resolving)
        {
            var result = new List<string>();
            string qualifier = star.Qualifier?.Identifiers.Count > 0
                ? star.Qualifier.Identifiers[star.Qualifier.Identifiers.Count - 1].Value
                : null;

            foreach (TableInfo table in GetTables(fromClause))
            {
                if (!TableMatchesQualifier(table, qualifier))
                    continue;

                List<string> tableColumns = GetLocalColumnNames(table, localColumnsByName);
                if (tableColumns.Count == 0)
                    tableColumns = ResolveLocalQueryTableColumns(connection, table, localColumnsByName, localQueriesByName, resolving);

                if (tableColumns.Count == 0 && connection != null)
                    tableColumns = GetColumnNamesForTable(connection, table);

                if (tableColumns.Count == 0)
                    return new List<string>();

                result.AddRange(tableColumns);
            }

            return result;
        }

        private static List<string> ResolveLocalQueryTableColumns(
            SqlConnection connection,
            TableInfo table,
            Dictionary<string, List<string>> localColumnsByName,
            Dictionary<string, QueryExpression> localQueriesByName,
            HashSet<string> resolving)
        {
            if (table == null || string.IsNullOrWhiteSpace(table.TableName))
                return new List<string>();

            QueryExpression queryExpression = table.QueryExpression;
            if (queryExpression == null)
            {
                if (localQueriesByName == null || !localQueriesByName.TryGetValue(table.TableName, out queryExpression))
                    return new List<string>();
            }

            if (resolving == null || resolving.Contains(table.TableName))
                return new List<string>();

            resolving.Add(table.TableName);
            List<string> columns = ResolveQueryColumns(connection, queryExpression, localColumnsByName, localQueriesByName, resolving);
            resolving.Remove(table.TableName);

            if (columns.Count > 0 && localColumnsByName != null)
                localColumnsByName[table.TableName] = columns;

            return columns;
        }

        private static List<string> InferColumns(QueryExpression queryExpression, Dictionary<string, List<string>> localColumnsByName)
        {
            var query = queryExpression as QuerySpecification;
            var result = new List<string>();
            if (query == null || query.SelectElements == null)
                return result;

            foreach (SelectElement element in query.SelectElements)
            {
                if (element is SelectScalarExpression scalar)
                {
                    string columnName = GetOutputColumnName(scalar);
                    if (!string.IsNullOrWhiteSpace(columnName))
                        result.Add(columnName);
                    continue;
                }

                if (element is SelectStarExpression star)
                {
                    foreach (string columnName in GetStarColumns(star, query.FromClause, localColumnsByName))
                    {
                        if (!string.IsNullOrWhiteSpace(columnName))
                            result.Add(columnName);
                    }
                }
            }

            return result;
        }

        private static string GetOutputColumnName(SelectScalarExpression scalar)
        {
            if (scalar == null)
                return null;

            string alias = scalar.ColumnName?.Identifier?.Value ?? scalar.ColumnName?.Value;
            if (!string.IsNullOrWhiteSpace(alias))
                return alias;

            var column = scalar.Expression as ColumnReferenceExpression;
            return column?.MultiPartIdentifier?.Identifiers.Count > 0
                ? column.MultiPartIdentifier.Identifiers[column.MultiPartIdentifier.Identifiers.Count - 1].Value
                : null;
        }

        private static List<string> GetStarColumns(
            SelectStarExpression star,
            FromClause fromClause,
            Dictionary<string, List<string>> localColumnsByName)
        {
            var result = new List<string>();
            if (localColumnsByName == null)
                return result;

            string qualifier = star.Qualifier?.Identifiers.Count > 0
                ? star.Qualifier.Identifiers[star.Qualifier.Identifiers.Count - 1].Value
                : null;

            foreach (TableInfo table in GetTables(fromClause))
            {
                if (!TableMatchesQualifier(table, qualifier))
                    continue;

                foreach (string columnName in GetLocalColumnNames(table, localColumnsByName))
                    result.Add(columnName);
            }

            return result;
        }

        private static bool TableMatchesQualifier(TableInfo table, string qualifier)
        {
            if (table == null)
                return false;

            return string.IsNullOrWhiteSpace(qualifier)
                || string.Equals(table.Qualifier.TrimEnd('.'), qualifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(table.TableName, qualifier, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetAbsoluteOffset(string text, int targetLine, int targetColumn)
        {
            int line = 0;
            int column = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (line == targetLine && column == targetColumn)
                    return i;

                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;

                    line++;
                    column = 0;
                }
                else if (text[i] == '\n')
                {
                    line++;
                    column = 0;
                }
                else
                {
                    column++;
                }
            }

            return text.Length;
        }

        private static void ReplaceText(IVsTextLines textLines, int line, int startColumn, int endColumn, string replacement)
        {
            IntPtr pNewText = Marshal.StringToHGlobalUni(replacement);
            try
            {
                using (var edit = EditorEditTransaction.Begin(textLines, "Expand SELECT asterisk"))
                {
                    TextSpan[] changedSpan = new TextSpan[1];
                    textLines.ReplaceLines(line, startColumn, line, endColumn, pNewText, replacement.Length, changedSpan);
                    edit.Complete();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pNewText);
            }
        }

        private static void SetCaretPosition(IVsTextView textView, int startLine, int startColumn, string text, int offset)
        {
            int targetLine = startLine;
            int targetColumn = startColumn;

            for (int i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    targetLine++;
                    targetColumn = 0;
                    i++;
                }
                else if (text[i] == '\n')
                {
                    targetLine++;
                    targetColumn = 0;
                }
                else
                {
                    targetColumn++;
                }
            }

            textView.SetCaretPos(targetLine, targetColumn);
        }
    }
}
