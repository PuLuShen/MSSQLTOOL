namespace MSSQLTool
{
    using DocumentFormat.OpenXml.Packaging;
    using DocumentFormat.OpenXml.Spreadsheet;
    using Microsoft.VisualStudio.Shell;
    using Microsoft.Win32;
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using Microsoft.Data.SqlClient;
    using System.Data;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;

    /// <summary>
    /// Interaction logic for the Data Import tool window.
    /// </summary>
    public partial class DataImportWindowControl : UserControl
    {
        private const string SkipMappingItem = "(skip)";
        private const int PreviewRowCount = 20;

        private ScriptFactoryAccess.ConnectionInfo targetConnection;
        private string selectedExcelPath;
        private bool isImporting;
        private CancellationTokenSource importCancellation;
        private ExcelImport.WorksheetAnalysis previewAnalysis;
        private List<string> targetColumns;
        private int previewGeneration;
        private int targetColumnsGeneration;
        private readonly System.Windows.Threading.DispatcherTimer previewDebounce;

        public DataImportWindowControl()
        {
            InitializeComponent();

            // Wire the editable worksheet ComboBox text changes to the preview refresh.
            ComboBox_Worksheet.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(ComboBox_Worksheet_TextChanged));

            previewDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            previewDebounce.Tick += (s, e) =>
            {
                previewDebounce.Stop();
                RunPreviewAsync();
            };

            LocalizationManager.Apply(this);

            UpdateStatus(LocalizationManager.T("Choose an Excel file to get started."));
        }

        private async void ButtonBrowse_OnClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                selectedExcelPath = dialog.FileName;
                TextBox_ExcelPath.Text = selectedExcelPath;

                if (string.IsNullOrWhiteSpace(TextBox_TargetTable.Text))
                {
                    TextBox_TargetTable.Text = Path.GetFileNameWithoutExtension(selectedExcelPath);
                }

                UpdateStatus($"Loaded {Path.GetFileName(selectedExcelPath)}. Select the target database.");
                UpdateImportButtonState();

                await LoadWorksheetNamesAsync();
            }
        }

        private async Task LoadWorksheetNamesAsync()
        {
            string path = selectedExcelPath;
            previewAnalysis = null;
            PreviewGrid.ItemsSource = null;
            ComboBox_Worksheet.ItemsSource = null;
            ComboBox_Worksheet.Text = string.Empty;

            try
            {
                List<string> names = await Task.Run(() => GetWorksheetNames(path));
                ComboBox_Worksheet.ItemsSource = names;
                if (names.Count > 0)
                {
                    // Selecting the first sheet triggers the preview refresh.
                    ComboBox_Worksheet.SelectedIndex = 0;
                }
                else
                {
                    SetPreviewStatus(LocalizationManager.T("The workbook does not contain any worksheets."));
                }
            }
            catch (Exception ex)
            {
                SetPreviewStatus(LocalizationManager.T("Could not read the workbook: ") + ex.Message);
            }
        }

        private static List<string> GetWorksheetNames(string filePath)
        {
            var names = new List<string>();
            using (SpreadsheetDocument document = SpreadsheetDocument.Open(filePath, false))
            {
                WorkbookPart workbookPart = document.WorkbookPart;
                if (workbookPart?.Workbook?.Sheets == null)
                {
                    return names;
                }

                foreach (Sheet sheet in workbookPart.Workbook.Sheets.Elements<Sheet>())
                {
                    string name = sheet.Name?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names;
        }

        private void ComboBox_Worksheet_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QueuePreview();
        }

        private void ComboBox_Worksheet_TextChanged(object sender, TextChangedEventArgs e)
        {
            QueuePreview();
        }

        private void CheckBox_FirstRowHeaders_Changed(object sender, RoutedEventArgs e)
        {
            QueuePreview();
        }

        private void CheckBox_CreateTable_Changed(object sender, RoutedEventArgs e)
        {
            UpdateMappingArea();
        }

        private void QueuePreview()
        {
            if (previewDebounce == null || isImporting)
            {
                return;
            }

            previewDebounce.Stop();
            previewDebounce.Start();
        }

        private async void RunPreviewAsync()
        {
            string path = selectedExcelPath;
            string sheet = string.IsNullOrWhiteSpace(ComboBox_Worksheet.Text) ? null : ComboBox_Worksheet.Text.Trim();
            bool firstRowHeaders = CheckBox_FirstRowHeaders.IsChecked == true;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || string.IsNullOrWhiteSpace(sheet))
            {
                previewAnalysis = null;
                PreviewGrid.ItemsSource = null;
                UpdateMappingArea();
                return;
            }

            int generation = ++previewGeneration;
            SetPreviewStatus(LocalizationManager.T("Previewing..."));
            try
            {
                ExcelImport.WorksheetAnalysis analysis = await Task.Run(() =>
                    ExcelImport.AnalyzeWorksheet(path, sheet, firstRowHeaders, CancellationToken.None));

                DataTable previewTable = await Task.Run(() =>
                    ExcelImport.ReadBatches(path, sheet, firstRowHeaders, analysis, PreviewRowCount, CancellationToken.None).FirstOrDefault());

                if (generation != previewGeneration)
                {
                    return;
                }

                previewAnalysis = analysis;
                PreviewGrid.ItemsSource = previewTable != null ? previewTable.DefaultView : null;
                SetPreviewStatus(previewTable == null
                    ? LocalizationManager.T("The worksheet does not contain any data rows.")
                    : LocalizationManager.Format("Showing first {0} of {1:#,0} rows.", previewTable.Rows.Count, analysis.RowCount));
            }
            catch (Exception ex)
            {
                if (generation != previewGeneration)
                {
                    return;
                }

                previewAnalysis = null;
                PreviewGrid.ItemsSource = null;
                SetPreviewStatus(LocalizationManager.T("Preview failed: ") + ex.Message);
            }

            UpdateMappingArea();
        }

        private void SetPreviewStatus(string message)
        {
            TextBlock_PreviewStatus.Text = message;
        }

        private void ButtonSelectTarget_OnClick(object sender, RoutedEventArgs e)
        {
            var connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            if (connectionInfo == null)
            {
                LocalizedMessageBox.Show("Please select a database in Object Explorer first.", "Data Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            targetConnection = connectionInfo;
            TextBox_Target.Text = targetConnection.DisplayName;

            UpdateStatus($"Target ready: {targetConnection.DisplayName}.");
            UpdateImportButtonState();

            _ = LoadTargetColumnsAsync();
        }

        private async void ButtonImport_OnClick(object sender, RoutedEventArgs e)
        {
            if (!EnsureSelections())
            {
                return;
            }

            if (!int.TryParse(TextBox_Timeout.Text, out int bulkCopyTimeout) || bulkCopyTimeout < 0)
            {
                LocalizedMessageBox.Show("Enter the bulk copy timeout in seconds (0 or a positive number).",
                    "Data Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string worksheet = string.IsNullOrWhiteSpace(ComboBox_Worksheet.Text) ? null : ComboBox_Worksheet.Text.Trim();
            bool firstRowHeaders = CheckBox_FirstRowHeaders.IsChecked == true;
            bool createTable = CheckBox_CreateTable.IsChecked == true;
            bool truncateTable = CheckBox_Truncate.IsChecked == true;
            bool checkConstraints = CheckBox_CheckConstraints.IsChecked == true;
            string destinationTable = TextBox_TargetTable.Text.Trim();
            var connectionInfo = targetConnection;
            List<KeyValuePair<string, string>> columnMappings = GetSelectedColumnMappings();

            SetBusyState(true);
            importCancellation = new CancellationTokenSource();

            try
            {
                await PerformImportAsync(worksheet, firstRowHeaders, createTable, truncateTable, checkConstraints, destinationTable, bulkCopyTimeout, columnMappings, connectionInfo, importCancellation.Token);
            }
            catch (OperationCanceledException) { UpdateStatus(LocalizationManager.T("Import cancelled; destination changes were rolled back.")); }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                LocalizedMessageBox.Show($"Import failed: {ex.Message}", "Data Import", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus(LocalizationManager.T("Import failed. Review the error and try again."));
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetBusyState(false);
                importCancellation?.Dispose();
                importCancellation = null;
            }
        }

        private bool EnsureSelections()
        {
            if (string.IsNullOrWhiteSpace(selectedExcelPath))
            {
                LocalizedMessageBox.Show("Select an Excel workbook first.", "Data Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (targetConnection == null)
            {
                LocalizedMessageBox.Show("Select a target database from Object Explorer.", "Data Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(TextBox_TargetTable.Text))
            {
                LocalizedMessageBox.Show("Provide a destination table name.", "Data Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void ButtonClear_OnClick(object sender, RoutedEventArgs e)
        {
            selectedExcelPath = null;
            targetConnection = null;
            previewAnalysis = null;
            targetColumns = null;
            TextBox_ExcelPath.Text = string.Empty;
            ComboBox_Worksheet.ItemsSource = null;
            ComboBox_Worksheet.Text = string.Empty;
            TextBox_Target.Text = string.Empty;
            TextBox_TargetTable.Text = string.Empty;
            TextBox_Timeout.Text = "0";
            CheckBox_CreateTable.IsChecked = true;
            CheckBox_Truncate.IsChecked = false;
            CheckBox_FirstRowHeaders.IsChecked = true;
            CheckBox_CheckConstraints.IsChecked = true;
            PreviewGrid.ItemsSource = null;
            ItemsControl_ColumnMapping.ItemsSource = null;

            SetPreviewStatus(LocalizationManager.T("Choose an Excel file and worksheet to see a preview."));
            UpdateMappingArea();
            UpdateStatus(LocalizationManager.T("Choose an Excel file to get started."));
            UpdateImportButtonState();
        }

        private void ButtonCancel_OnClick(object sender, RoutedEventArgs e)
        {
            importCancellation?.Cancel();
            UpdateStatus(LocalizationManager.T("Cancelling import..."));
        }

        private void TextBox_TargetTable_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateImportButtonState();
            _ = LoadTargetColumnsAsync();
        }

        private async Task LoadTargetColumnsAsync()
        {
            targetColumns = null;

            var connectionInfo = targetConnection;
            string table = TextBox_TargetTable.Text.Trim();

            if (connectionInfo == null || string.IsNullOrWhiteSpace(table))
            {
                UpdateMappingArea();
                return;
            }

            int generation = ++targetColumnsGeneration;

            try
            {
                string quotedTable = SqlIdentifierHelper.QuoteQualifiedName(table);
                List<string> columns = await Task.Run(() =>
                {
                    using (SqlConnection connection = new SqlConnection(connectionInfo.FullConnectionString))
                    {
                        connection.Open();
                        using (SqlCommand command = new SqlCommand("SELECT TOP 0 * FROM " + quotedTable, connection) { CommandTimeout = 15 })
                        using (SqlDataReader reader = command.ExecuteReader())
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

                if (generation != targetColumnsGeneration)
                {
                    return;
                }

                targetColumns = columns;
            }
            catch
            {
                if (generation != targetColumnsGeneration)
                {
                    return;
                }

                targetColumns = null;
            }

            UpdateMappingArea();
        }

        private void UpdateMappingArea()
        {
            if (TextBlock_MappingInfo == null)
            {
                return;
            }

            if (previewAnalysis == null)
            {
                TextBlock_MappingInfo.Text = LocalizationManager.T("Choose an Excel file and worksheet, then select an existing destination table to map its columns.");
                ItemsControl_ColumnMapping.ItemsSource = null;
                return;
            }

            if (CheckBox_CreateTable.IsChecked == true)
            {
                TextBlock_MappingInfo.Text = LocalizationManager.T("The destination table will be created automatically with the Excel column names; column mapping is not required.");
                ItemsControl_ColumnMapping.ItemsSource = null;
                return;
            }

            if (targetColumns == null)
            {
                TextBlock_MappingInfo.Text = LocalizationManager.T("The destination table must exist (with 'Automatically create the table' cleared) to configure column mapping.");
                ItemsControl_ColumnMapping.ItemsSource = null;
                return;
            }

            var options = new ObservableCollection<string> { SkipMappingItem };
            foreach (string column in targetColumns)
            {
                options.Add(column);
            }

            var rows = new List<ColumnMappingRow>();
            foreach (ExcelImport.ExcelColumnMetadata column in previewAnalysis.Columns)
            {
                string matched = targetColumns.FirstOrDefault(t => string.Equals(t, column.Name, StringComparison.OrdinalIgnoreCase));
                rows.Add(new ColumnMappingRow
                {
                    ExcelColumn = column.Name,
                    TargetColumns = options,
                    SelectedTarget = matched ?? SkipMappingItem
                });
            }

            ItemsControl_ColumnMapping.ItemsSource = rows;
            TextBlock_MappingInfo.Text = LocalizationManager.Format("Map each Excel column to a destination column; choose '{0}' to skip a column.", SkipMappingItem);
        }

        private List<KeyValuePair<string, string>> GetSelectedColumnMappings()
        {
            if (!(ItemsControl_ColumnMapping.ItemsSource is System.Collections.IEnumerable rows))
            {
                return null;
            }

            var mappings = new List<KeyValuePair<string, string>>();
            foreach (ColumnMappingRow row in rows.OfType<ColumnMappingRow>())
            {
                if (!string.Equals(row.SelectedTarget, SkipMappingItem, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(row.SelectedTarget))
                {
                    mappings.Add(new KeyValuePair<string, string>(row.ExcelColumn, row.SelectedTarget));
                }
            }

            return mappings.Count > 0 ? mappings : null;
        }

        private sealed class ColumnMappingRow
        {
            public string ExcelColumn { get; set; }
            public ObservableCollection<string> TargetColumns { get; set; }
            public string SelectedTarget { get; set; }
        }

        private void UpdateImportButtonState()
        {
            Button_Import.IsEnabled =
                !isImporting &&
                !string.IsNullOrWhiteSpace(selectedExcelPath) &&
                targetConnection != null &&
                !string.IsNullOrWhiteSpace(TextBox_TargetTable.Text);
        }

        private void UpdateStatus(string message)
        {
            TextBlock_Status.Text = message;
        }

        private void SetBusyState(bool importing)
        {
            isImporting = importing;

            Button_Browse.IsEnabled = !importing;
            Button_SelectTarget.IsEnabled = !importing;
            Button_Clear.IsEnabled = !importing;
            ComboBox_Worksheet.IsEnabled = !importing;
            TextBox_TargetTable.IsEnabled = !importing;
            TextBox_Timeout.IsEnabled = !importing;
            CheckBox_CreateTable.IsEnabled = !importing;
            CheckBox_Truncate.IsEnabled = !importing;
            CheckBox_FirstRowHeaders.IsEnabled = !importing;
            CheckBox_CheckConstraints.IsEnabled = !importing;
            Button_Cancel.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;

            if (importing)
            {
                previewDebounce.Stop();
            }

            UpdateImportButtonState();
        }

        private async Task PerformImportAsync(string worksheet, bool firstRowHeaders, bool createTable, bool truncateTable, bool checkConstraints,
            string destinationTable, int bulkCopyTimeout, List<KeyValuePair<string, string>> columnMappings,
            ScriptFactoryAccess.ConnectionInfo connectionInfo, CancellationToken token)
        {
            await UpdateStatusAsync(LocalizationManager.T("Reading Excel file..."));

            ExcelImport.WorksheetAnalysis worksheetData = await Task.Run(() =>
                ExcelImport.AnalyzeWorksheet(selectedExcelPath, worksheet, firstRowHeaders, token), token);
            token.ThrowIfCancellationRequested();

            await UpdateStatusAsync(LocalizationManager.Format("Scanned '{0}' with {1} rows. Preparing destination table...",
                worksheetData.WorksheetName, worksheetData.RowCount.ToString("#,0")));

            long imported = await ImportIntoSqlAsync(worksheetData, worksheet, firstRowHeaders, destinationTable, createTable, truncateTable, checkConstraints, bulkCopyTimeout, columnMappings, connectionInfo, token);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            UpdateStatus(LocalizationManager.Format("Imported {0} rows into {1} on {2}.",
                imported.ToString("#,0"), destinationTable, connectionInfo.DisplayName));
            LocalizedMessageBox.Show(
                LocalizationManager.Format("Successfully imported {0} rows from {1} into {2} ({3}).",
                    imported.ToString("#,0"), Path.GetFileName(selectedExcelPath), connectionInfo.DisplayName, destinationTable),
                "Data Import",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async Task<long> ImportIntoSqlAsync(ExcelImport.WorksheetAnalysis worksheetData, string worksheet, bool firstRowHeaders,
            string destinationTable, bool createTable, bool truncateTable, bool checkConstraints, int bulkCopyTimeout,
            List<KeyValuePair<string, string>> columnMappings, ScriptFactoryAccess.ConnectionInfo connectionInfo, CancellationToken token)
        {
            string quotedTableName = SqlIdentifierHelper.QuoteQualifiedName(destinationTable);
            string tableLiteral = EscapeForSqlLiteral(quotedTableName);

            using (SqlConnection connection = new SqlConnection(connectionInfo.FullConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqlTransaction transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
                {

                await UpdateStatusAsync("Ensuring destination table exists...").ConfigureAwait(false);

                if (createTable)
                {
                    string createScript = BuildCreateTableScript(tableLiteral, quotedTableName, worksheetData.Columns);
                    using (SqlCommand command = new SqlCommand(createScript, connection, transaction))
                    {
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                else
                {
                    string existsScript =
                        $"IF OBJECT_ID(N'{tableLiteral}', 'U') IS NULL BEGIN THROW 50000, 'Destination table was not found.', 1; END";

                    using (SqlCommand command = new SqlCommand(existsScript, connection, transaction))
                    {
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                if (truncateTable)
                {
                    await UpdateStatusAsync("Truncating destination table...").ConfigureAwait(false);
                    using (SqlCommand command = new SqlCommand($"TRUNCATE TABLE {quotedTableName};", connection, transaction))
                    {
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                await UpdateStatusAsync("Streaming rows into SQL Server...").ConfigureAwait(false);

                long imported = 0;
                SqlBulkCopyOptions bulkOptions = SqlBulkCopyOptions.TableLock;
                if (checkConstraints) bulkOptions |= SqlBulkCopyOptions.CheckConstraints;
                using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection, bulkOptions, transaction))
                {
                    bulkCopy.DestinationTableName = quotedTableName;
                    bulkCopy.BatchSize = 5000;
                    bulkCopy.BulkCopyTimeout = bulkCopyTimeout;

                    if (columnMappings != null && columnMappings.Count > 0)
                    {
                        foreach (KeyValuePair<string, string> mapping in columnMappings)
                        {
                            bulkCopy.ColumnMappings.Add(mapping.Key, mapping.Value);
                        }
                    }
                    else
                    {
                        foreach (ExcelImport.ExcelColumnMetadata column in worksheetData.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(column.Name, column.Name);
                        }
                    }

                    foreach (System.Data.DataTable batch in ExcelImport.ReadBatches(selectedExcelPath, worksheet, firstRowHeaders, worksheetData, 5000, token))
                    {
                        token.ThrowIfCancellationRequested();
                        await bulkCopy.WriteToServerAsync(batch, token).ConfigureAwait(false);
                        imported += batch.Rows.Count;
                        await UpdateStatusAsync(LocalizationManager.Format("Imported {0} of {1} rows...", imported.ToString("#,0"), worksheetData.RowCount.ToString("#,0"))).ConfigureAwait(false);
                    }
                }
                if (imported != worksheetData.RowCount)
                    throw new InvalidOperationException($"Import verification failed: Excel contained {worksheetData.RowCount:#,0} data rows but SQL Server accepted {imported:#,0} rows.");

                transaction.Commit();
                return imported;
                }
            }
        }

        private static string BuildCreateTableScript(string tableLiteral, string quotedTableName, IReadOnlyList<ExcelImport.ExcelColumnMetadata> columns)
        {
            string columnDefinitions = string.Join(
                ",\n        ",
                columns.Select(column => $"{SqlIdentifierHelper.QuoteIdentifier(column.Name)} {column.SqlType} NULL"));

            return
                $"IF OBJECT_ID(N'{tableLiteral}', 'U') IS NULL\n" +
                "BEGIN\n" +
                $"    CREATE TABLE {quotedTableName} (\n" +
                $"        {columnDefinitions}\n" +
                "    );\n" +
                "END";
        }

        private static string EscapeForSqlLiteral(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("'", "''");
        }

        private async Task UpdateStatusAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            UpdateStatus(message);
        }

        private static class SqlIdentifierHelper
        {
            public static string QuoteQualifiedName(string input)
            {
                var parts = GetNameParts(input);
                if (parts.Count == 0 || parts.Count > 2)
                {
                    throw new InvalidOperationException("Destination table must be table or schema.table in the selected SQL Server database.");
                }

                return string.Join(".", parts.Select(QuoteIdentifier));
            }

            public static string QuoteIdentifier(string identifier)
            {
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    throw new InvalidOperationException("Column names cannot be empty.");
                }

                string sanitized = identifier.Replace("]", "]]");
                return $"[{sanitized}]";
            }

            private static List<string> GetNameParts(string input)
            {
                var parts = new List<string>();
                if (string.IsNullOrWhiteSpace(input))
                {
                    return parts;
                }

                StringBuilder current = new StringBuilder();
                bool insideBrackets = false;

                foreach (char ch in input)
                {
                    if (ch == '[')
                    {
                        insideBrackets = true;
                        continue;
                    }

                    if (ch == ']')
                    {
                        insideBrackets = false;
                        continue;
                    }

                    if (ch == '.' && !insideBrackets)
                    {
                        AddPart(current, parts);
                        continue;
                    }

                    current.Append(ch);
                }

                AddPart(current, parts);

                if (parts.Count == 0)
                {
                    parts.Add(input.Trim());
                }

                return parts;
            }

            private static void AddPart(StringBuilder builder, List<string> parts)
            {
                if (builder.Length == 0)
                {
                    return;
                }

                string value = builder.ToString().Trim();
                builder.Clear();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }
            }
        }
    }
}
