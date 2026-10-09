namespace MSSQLTool
{
    using Microsoft.Data.SqlClient;
    using Microsoft.VisualStudio.Shell;
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;

    /// <summary>Transactional SQL Server-to-SQL Server data transfer.</summary>
    public partial class DataTransferWindowControl : UserControl
    {
        private const string SkipMappingItem = "(skip)";

        private readonly ToolWindowThemeController themeController;
        private ScriptFactoryAccess.ConnectionInfo source;
        private ScriptFactoryAccess.ConnectionInfo target;
        private CancellationTokenSource cancellation;
        private CancellationTokenSource previewCancellation;
        private List<string> targetColumns;
        private string[] sourceColumns;
        private int mappingGeneration;
        private readonly System.Windows.Threading.DispatcherTimer mappingDebounce;

        public DataTransferWindowControl()
        {
            InitializeComponent();
            themeController = new ToolWindowThemeController(this, () => ToolWindowThemeResources.ApplySharedTheme(this));

            mappingDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            mappingDebounce.Tick += (s, e) =>
            {
                mappingDebounce.Stop();
                RefreshMappingAsync();
            };
        }

        private void Button_SelectSource_Click(object sender, RoutedEventArgs e)
        {
            source = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            Label_SourceDescription.Text = source?.DisplayName ?? LocalizationManager.T("No SQL Server source selected");
            UpdateAvailability();
            QueueMappingRefresh();
        }

        private void Button_SelectTarget_Click(object sender, RoutedEventArgs e)
        {
            target = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            Label_TargetDescription.Text = target?.DisplayName ?? LocalizationManager.T("No SQL Server target selected");
            UpdateAvailability();
            QueueMappingRefresh();
        }

        private void TextBox_TargetTable_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateAvailability();
            QueueMappingRefresh();
        }

        private void CheckBox_CreateTargetTable_Changed(object sender, RoutedEventArgs e)
        {
            UpdateMappingArea();
            QueueMappingRefresh();
        }

        private void QueueMappingRefresh()
        {
            if (mappingDebounce == null)
            {
                return;
            }

            mappingDebounce.Stop();
            mappingDebounce.Start();
        }

        private void UpdateAvailability()
        {
            if (Button_CopyData != null)
                Button_CopyData.IsEnabled = cancellation == null && source != null && target != null && !string.IsNullOrWhiteSpace(TextBox_TargetTable?.Text);
        }

        private string GetSourceSql()
        {
            return new TextRange(RichTextBox_SourceQuery.Document.ContentStart, RichTextBox_SourceQuery.Document.ContentEnd).Text.Trim();
        }

        private static string BuildPreviewQuery(string sourceSql)
        {
            // Strip trailing statement terminator(s) before wrapping the source query.
            string trimmed = sourceSql.Trim().TrimEnd(';');
            return "SELECT TOP 100 * FROM (" + trimmed + ") AS [PreviewQuery];";
        }

        private async void Button_Preview_Click(object sender, RoutedEventArgs e)
        {
            var connectionInfo = source;
            if (connectionInfo == null)
            {
                Label_PreviewStatus.Text = LocalizationManager.T("Select a source first.");
                return;
            }

            string sourceSql = GetSourceSql();
            if (string.IsNullOrWhiteSpace(sourceSql))
            {
                Label_PreviewStatus.Text = LocalizationManager.T("Source query cannot be empty.");
                return;
            }

            Button_Preview.IsEnabled = false;
            previewCancellation = new CancellationTokenSource();
            Button_Cancel.Visibility = Visibility.Visible;
            Label_PreviewStatus.Text = LocalizationManager.T("Previewing...");
            try
            {
                string previewSql = BuildPreviewQuery(sourceSql);
                DataTable table = await Task.Run(async () =>
                {
                    using (var connection = new SqlConnection(connectionInfo.FullConnectionString))
                    {
                        await connection.OpenAsync(previewCancellation.Token);
                        using (var command = new SqlCommand(previewSql, connection) { CommandTimeout = 120 })
                        using (SqlDataReader reader = await command.ExecuteReaderAsync(previewCancellation.Token))
                        {
                            var result = new DataTable();
                            result.Load(reader);
                            return result;
                        }
                    }
                });

                PreviewGrid.ItemsSource = table.DefaultView;
                sourceColumns = table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray();
                Label_PreviewStatus.Text = LocalizationManager.Format("Showing first {0} rows", table.Rows.Count);
                QueueMappingRefresh();
            }
            catch (OperationCanceledException)
            {
                Label_PreviewStatus.Text = LocalizationManager.T("Preview cancelled.");
            }
            catch (Exception ex)
            {
                if (previewCancellation != null && previewCancellation.IsCancellationRequested)
                {
                    Label_PreviewStatus.Text = LocalizationManager.T("Preview cancelled.");
                }
                else
                {
                    PreviewGrid.ItemsSource = null;
                    Label_PreviewStatus.Text = LocalizationManager.T("Preview failed") + ": " + ex.Message
                        + " " + LocalizationManager.T("Common causes: a column name does not exist, or the query syntax is invalid.");
                }
            }
            finally
            {
                previewCancellation?.Dispose();
                previewCancellation = null;
                Button_Preview.IsEnabled = true;
                Button_Cancel.Visibility = cancellation != null ? Visibility.Visible : Visibility.Collapsed;
                UpdateAvailability();
            }
        }

        private async void RefreshMappingAsync()
        {
            var sourceConnectionInfo = source;
            var targetConnectionInfo = target;
            string targetTable = TextBox_TargetTable?.Text?.Trim();
            string sourceSql = GetSourceSql();

            if (sourceConnectionInfo == null || targetConnectionInfo == null
                || string.IsNullOrWhiteSpace(targetTable) || string.IsNullOrWhiteSpace(sourceSql)
                || CheckBox_CreateTargetTable.IsChecked == true)
            {
                targetColumns = null;
                UpdateMappingArea();
                return;
            }

            int generation = ++mappingGeneration;

            try
            {
                string quotedTarget = DatabaseIdentifier.SqlServerLocalObject(targetTable);
                List<string> columns = await Task.Run(() =>
                {
                    using (var connection = new SqlConnection(targetConnectionInfo.FullConnectionString))
                    {
                        connection.Open();
                        using (var command = new SqlCommand("SELECT TOP 0 * FROM " + quotedTarget, connection) { CommandTimeout = 15 })
                        using (var reader = command.ExecuteReader())
                        {
                            var names = new List<string>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                names.Add(reader.GetName(i));
                            }

                            return names;
                        }
                    }
                });

                if (generation != mappingGeneration)
                {
                    return;
                }

                targetColumns = columns;
            }
            catch
            {
                if (generation != mappingGeneration)
                {
                    return;
                }

                targetColumns = null;
            }

            if (sourceColumns == null)
            {
                try
                {
                    List<string> names = await Task.Run(() =>
                    {
                        using (var connection = new SqlConnection(sourceConnectionInfo.FullConnectionString))
                        {
                            connection.Open();
                            using (var command = new SqlCommand(sourceSql, connection) { CommandTimeout = 30 })
                            using (var reader = command.ExecuteReader(CommandBehavior.SchemaOnly))
                            {
                                var result = new List<string>();
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    result.Add(reader.GetName(i));
                                }

                                return result;
                            }
                        }
                    });

                    if (generation != mappingGeneration)
                    {
                        return;
                    }

                    sourceColumns = names.ToArray();
                }
                catch
                {
                    if (generation != mappingGeneration)
                    {
                        return;
                    }

                    sourceColumns = null;
                }
            }

            UpdateMappingArea();
        }

        private void UpdateMappingArea()
        {
            if (Label_MappingInfo == null)
            {
                return;
            }

            if (CheckBox_CreateTargetTable.IsChecked == true)
            {
                Label_MappingInfo.Text = LocalizationManager.T("The target table will be created from the source query columns; column mapping is not applied.");
                ItemsControl_ColumnMapping.ItemsSource = null;
                return;
            }

            if (sourceColumns == null || sourceColumns.Length == 0 || targetColumns == null)
            {
                Label_MappingInfo.Text = LocalizationManager.T("Select source and target, then enter an existing target table to map columns.");
                ItemsControl_ColumnMapping.ItemsSource = null;
                return;
            }

            var options = new ObservableCollection<string> { SkipMappingItem };
            foreach (string column in targetColumns)
            {
                options.Add(column);
            }

            var rows = new List<ColumnMappingRow>();
            var takenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sourceColumn in sourceColumns)
            {
                string matched = targetColumns.FirstOrDefault(t => string.Equals(t, sourceColumn, StringComparison.OrdinalIgnoreCase));
                // One target column can only receive one source column; the mapping is filled in
                // first-come-first-served so the copy step never builds a duplicate mapping.
                if (matched != null && !takenTargets.Add(matched))
                {
                    matched = null;
                }

                rows.Add(new ColumnMappingRow
                {
                    SourceColumn = sourceColumn,
                    TargetColumns = options,
                    SelectedTarget = matched ?? SkipMappingItem
                });
            }

            ItemsControl_ColumnMapping.ItemsSource = rows;
            Label_MappingInfo.Text = LocalizationManager.Format("Map each source column to a target column; choose '{0}' to skip a column.", SkipMappingItem);
        }

        private List<KeyValuePair<string, string>> GetSelectedColumnMappings()
        {
            if (!(ItemsControl_ColumnMapping?.ItemsSource is IEnumerable rows))
            {
                return null;
            }

            var mappings = new List<KeyValuePair<string, string>>();
            foreach (ColumnMappingRow row in rows.OfType<ColumnMappingRow>())
            {
                if (!string.Equals(row.SelectedTarget, SkipMappingItem, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(row.SelectedTarget))
                {
                    mappings.Add(new KeyValuePair<string, string>(row.SourceColumn, row.SelectedTarget));
                }
            }

            return mappings.Count > 0 ? mappings : null;
        }

        private sealed class ColumnMappingRow
        {
            public string SourceColumn { get; set; }
            public ObservableCollection<string> TargetColumns { get; set; }
            public string SelectedTarget { get; set; }
        }

        private async void ButtonCopyData_Click(object sender, RoutedEventArgs e)
        {
            if (source == null || target == null || string.IsNullOrWhiteSpace(TextBox_TargetTable.Text)) return;
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                string sql = GetSourceSql();
                if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException(LocalizationManager.T("Source query cannot be empty."));
                long copied = await CopyAsync(sql, TextBox_TargetTable.Text.Trim(), cancellation.Token);
                Label_CopyProgress.Text = LocalizationManager.Format("Completed: {0} rows committed in {1} seconds.",
                    copied.ToString("#,0"), stopwatch.Elapsed.TotalSeconds.ToString("#,0.0"));
            }
            catch (OperationCanceledException) { Label_CopyProgress.Text = LocalizationManager.T("Cancelled. SQL Server rolled back the target transaction."); }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Data Transfer", "SQL Server transfer failed", ex);
                Label_CopyProgress.Text = LocalizationManager.T("Transfer failed: ") + ex.Message;
                LocalizedMessageBox.Show(ex.Message, "SQL Server Data Transfer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                stopwatch.Stop();
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false);
            }
        }

        private async Task<long> CopyAsync(string sourceSql, string targetName, CancellationToken token)
        {
            List<KeyValuePair<string, string>> columnMappings = CheckBox_CreateTargetTable.IsChecked == true ? null : GetSelectedColumnMappings();
            string quotedTarget = DatabaseIdentifier.SqlServerLocalObject(targetName);
            string literalTarget = quotedTarget.Replace("'", "''");
            using (var sourceConnection = new SqlConnection(source.FullConnectionString))
            using (var targetConnection = new SqlConnection(target.FullConnectionString))
            {
                await sourceConnection.OpenAsync(token);
                await targetConnection.OpenAsync(token);
                using (var sourceCommand = new SqlCommand(sourceSql, sourceConnection) { CommandTimeout = 120 })
                using (SqlDataReader sourceReader = await sourceCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
                using (var reader = new CountingDataReader(sourceReader))
                using (SqlTransaction transaction = targetConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    DataTable schema = reader.GetSchemaTable() ?? throw new InvalidOperationException("The source query did not return a tabular result.");
                    string[] sourceColumns = schema.Rows.Cast<DataRow>().Select(row => Convert.ToString(row["ColumnName"])).ToArray();
                    if (sourceColumns.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidOperationException("Every source expression must have a column name or alias.");
                    if (sourceColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceColumns.Length)
                        throw new InvalidOperationException("The source query returns duplicate column names. Add unique aliases before copying.");
                    if (CheckBox_CreateTargetTable.IsChecked == true)
                    {
                        string definitions = string.Join("," + Environment.NewLine, schema.Rows.Cast<DataRow>().Select(row =>
                            DatabaseIdentifier.SqlServerPart(Convert.ToString(row["ColumnName"])) + " " + GridAccess.GetColumnSqlType(row)));
                        using (var create = new SqlCommand($"IF OBJECT_ID(N'{literalTarget}','U') IS NULL CREATE TABLE {quotedTarget} ({definitions});", targetConnection, transaction))
                        {
                            create.CommandTimeout = 120;
                            await create.ExecuteNonQueryAsync(token);
                        }
                    }
                    using (var verify = new SqlCommand($"IF OBJECT_ID(N'{literalTarget}','U') IS NULL THROW 50000,'Target table does not exist.',1;", targetConnection, transaction))
                        await verify.ExecuteNonQueryAsync(token);
                    if (CheckBox_TruncateTargetTable.IsChecked == true)
                        using (var truncate = new SqlCommand($"TRUNCATE TABLE {quotedTarget};", targetConnection, transaction)) await truncate.ExecuteNonQueryAsync(token);

                    SqlBulkCopyOptions options = SqlBulkCopyOptions.TableLock | (CheckBox_KeepIdentity.IsChecked == true ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default);
                    if (CheckBox_CheckConstraints.IsChecked == true) options |= SqlBulkCopyOptions.CheckConstraints;
                    if (CheckBox_FireTriggers.IsChecked == true) options |= SqlBulkCopyOptions.FireTriggers;
                    using (var bulk = new SqlBulkCopy(targetConnection, options, transaction))
                    {
                        bulk.DestinationTableName = quotedTarget;
                        bulk.BatchSize = 5000;
                        bulk.NotifyAfter = 5000;
                        bulk.BulkCopyTimeout = 120;
                        if (columnMappings != null && columnMappings.Count > 0)
                        {
                            var schemaNames = new HashSet<string>(schema.Rows.Cast<DataRow>().Select(row => Convert.ToString(row["ColumnName"])), StringComparer.OrdinalIgnoreCase);
                            // SqlBulkCopy keys its mapping collection by destination column: two
                            // source columns aimed at one target make it throw a duplicate-key
                            // error, so the first mapping wins and the rest are reported.
                            var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var duplicates = new List<string>();
                            foreach (var mapping in columnMappings)
                            {
                                if (!schemaNames.Contains(mapping.Key)) continue;
                                if (!usedTargets.Add(mapping.Value))
                                {
                                    duplicates.Add(mapping.Key + " -> " + mapping.Value);
                                    continue;
                                }

                                bulk.ColumnMappings.Add(mapping.Key, mapping.Value);
                            }

                            if (duplicates.Count > 0)
                                throw new InvalidOperationException(LocalizationManager.Format(
                                    "More than one source column is mapped to the same target column: {0}. Give every source column its own target column.",
                                    string.Join(", ", duplicates)));
                        }
                        if (bulk.ColumnMappings.Count == 0)
                        {
                            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (DataRow row in schema.Rows)
                            {
                                string name = Convert.ToString(row["ColumnName"]);
                                // A result set may repeat a column name; SqlBulkCopy rejects the
                                // second mapping with a duplicate-key error.
                                if (string.IsNullOrWhiteSpace(name) || !mapped.Add(name)) continue;
                                bulk.ColumnMappings.Add(name, name);
                            }
                        }
                        bulk.SqlRowsCopied += (s, e) => Dispatcher.BeginInvoke(new Action(() => Label_CopyProgress.Text = $"Copied {e.RowsCopied:#,0} rows; transaction not committed yet..."));
                        await bulk.WriteToServerAsync(reader, token);
                    }
                    transaction.Commit();
                    return reader.RowsRead;
                }
            }
        }

        private sealed class CountingDataReader : DbDataReader
        {
            private readonly DbDataReader inner;
            public CountingDataReader(DbDataReader inner) { this.inner = inner ?? throw new ArgumentNullException(nameof(inner)); }
            public long RowsRead { get; private set; }
            public override bool Read() { bool value = inner.Read(); if (value) RowsRead++; return value; }
            public override async Task<bool> ReadAsync(CancellationToken cancellationToken) { bool value = await inner.ReadAsync(cancellationToken); if (value) RowsRead++; return value; }
            public override bool NextResult() => inner.NextResult();
            public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
            public override int Depth => inner.Depth;
            public override int FieldCount => inner.FieldCount;
            public override bool HasRows => inner.HasRows;
            public override bool IsClosed => inner.IsClosed;
            public override int RecordsAffected => inner.RecordsAffected;
            public override object this[int ordinal] => inner[ordinal];
            public override object this[string name] => inner[name];
            public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
            public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
            public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
            public override char GetChar(int ordinal) => inner.GetChar(ordinal);
            public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
            public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
            public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
            public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
            public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
            public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
            public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
            public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
            public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
            public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
            public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
            public override string GetName(int ordinal) => inner.GetName(ordinal);
            public override int GetOrdinal(string name) => inner.GetOrdinal(name);
            public override string GetString(int ordinal) => inner.GetString(ordinal);
            public override object GetValue(int ordinal) => inner.GetValue(ordinal);
            public override int GetValues(object[] values) => inner.GetValues(values);
            public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
            public override DataTable GetSchemaTable() => inner.GetSchemaTable();
            public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
            public override void Close() => inner.Close();
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            previewCancellation?.Cancel();
            cancellation?.Cancel();
            Label_CopyProgress.Text = "Cancelling...";
        }

        private void SetBusy(bool busy)
        {
            Button_SelectSource.IsEnabled = !busy;
            Button_SelectTarget.IsEnabled = !busy;
            TextBox_TargetTable.IsEnabled = !busy;
            Button_Preview.IsEnabled = !busy;
            if (busy)
            {
                mappingDebounce.Stop();
            }

            Button_CopyData.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            Button_Cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UpdateAvailability();
        }
    }
}
