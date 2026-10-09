using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;

namespace MSSQLTool
{
    public partial class QueryHistoryWindowControl : UserControl
    {
        private readonly ToolWindowThemeController _themeController;

        public QueryHistoryWindowControl()
        {
            InitializeComponent();
            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            DataContext = new QueryHistoryViewModel();
            LoadSqlHighlighting();
            LoadHistoryOptions();
        }

        private void ApplyThemeBrushResources() => ToolWindowThemeResources.ApplySharedTheme(this);

        private void ToggleOptions_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            OptionsPanel.Visibility = OptionsPanel.Visibility == System.Windows.Visibility.Visible
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }

        /// <summary>Shows the stored history options in the options panel.</summary>
        private void LoadHistoryOptions()
        {
            SelectStorageMode(SettingsManager.GetQueryHistoryStorageMode());
            HistoryDatabasePath.Text = QueryHistorySqliteStore.DatabasePath;
            HistoryRetentionDays.Text = SettingsManager.GetQueryHistoryRetentionDays().ToString(System.Globalization.CultureInfo.InvariantCulture);
            HistoryRedactSensitiveText.IsChecked = SettingsManager.GetQueryHistoryRedactSensitiveText();
        }

        private void SelectStorageMode(string storageMode)
        {
            foreach (object item in HistoryStorageMode.Items)
            {
                if (item is ComboBoxItem comboItem
                    && string.Equals(comboItem.Tag as string, storageMode, StringComparison.OrdinalIgnoreCase))
                {
                    HistoryStorageMode.SelectedItem = comboItem;
                    return;
                }
            }

            HistoryStorageMode.SelectedIndex = 0;
        }

        private void HistoryStorageMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryOptionsStatus != null) HistoryOptionsStatus.Text = "Unsaved changes";
        }

        private void SaveHistoryOptions_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            string storageMode = (HistoryStorageMode.SelectedItem as ComboBoxItem)?.Tag as string
                ?? SettingsManager.QueryHistoryStorageModeSqlite;

            if (!int.TryParse(HistoryRetentionDays.Text, out int retentionDays) || retentionDays < 0)
            {
                HistoryOptionsStatus.Text = "Retention must be 0 or a positive number of days.";
                return;
            }

            SettingsManager.SaveQueryHistoryStorageMode(storageMode);
            SettingsManager.SaveQueryHistoryRetentionDays(retentionDays);
            SettingsManager.SaveQueryHistoryRedactSensitiveText(HistoryRedactSensitiveText.IsChecked == true);
            HistoryOptionsStatus.Text = "Saved";

            if (DataContext is QueryHistoryViewModel viewModel && viewModel.RefreshCommand.CanExecute(null))
                viewModel.RefreshCommand.Execute(null);
        }

        private void OpenHistoryFolder_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                string folder = Path.GetDirectoryName(QueryHistorySqliteStore.DatabasePath);
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                HistoryOptionsStatus.Text = "Could not open the folder: " + ex.Message;
            }
        }

        /// <summary>
        /// Pulls the JSONL history written by earlier versions into the SQLite database so the
        /// switch of storage does not hide records the user already had.
        /// </summary>
        private void ImportTextFiles_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            string folder = SettingsManager.GetQueryHistoryTextFileFolder();
            int imported = QueryHistorySqliteStore.ImportFromJsonLineFiles(folder, out string error);
            if (error != null)
            {
                HistoryOptionsStatus.Text = "Import failed: " + error;
                return;
            }

            HistoryOptionsStatus.Text = imported == 0
                ? "No text file history was found to import."
                : $"Imported {imported} record(s) from the text files.";

            if (DataContext is QueryHistoryViewModel viewModel && viewModel.RefreshCommand.CanExecute(null))
                viewModel.RefreshCommand.Execute(null);
        }

        private void Filter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (DataContext is QueryHistoryViewModel vm && vm.RefreshCommand.CanExecute(null)) vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }

        private void DatePreset_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int days) && DataContext is QueryHistoryViewModel vm) vm.ApplyDatePreset(days);
        }

        private void CurrentConnection_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (DataContext is QueryHistoryViewModel vm)
                    vm.ApplyConnectionFilter(connection.ServerName, connection.Database);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("Could not read the current connection: " + ex.Message, "Query History");
            }
        }

        private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SqlEditor.Text = (DataContext as QueryHistoryViewModel)?.SelectedRecord?.QueryText ?? string.Empty;
        }

        private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((DataContext as QueryHistoryViewModel)?.SelectedRecord != null) OpenSelectedQuery();
        }

        private void CopyQuery_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            string sql = (DataContext as QueryHistoryViewModel)?.SelectedRecord?.QueryText;
            if (!string.IsNullOrEmpty(sql)) System.Windows.Clipboard.SetText(sql);
        }

        private void OpenQuery_Click(object sender, System.Windows.RoutedEventArgs e) => OpenSelectedQuery();

        private void OpenSelectedQuery()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            QueryHistoryRecord record = (DataContext as QueryHistoryViewModel)?.SelectedRecord;
            if (record == null || string.IsNullOrWhiteSpace(record.QueryText)) return;
            try
            {
                var current = ScriptFactoryAccess.GetCurrentConnectionInfo();
                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, current.ActiveConnectionInfo, null);
                var document = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                document.EndPoint.CreateEditPoint().Insert($"-- Query history source: {record.DataSource} / {record.DatabaseName}{Environment.NewLine}" + record.QueryText);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("Could not open the query: " + ex.Message, "Query History");
            }
        }

        private void WrapSql_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (SqlEditor != null) SqlEditor.WordWrap = WrapSql.IsChecked == true;
        }

        private void LoadSqlHighlighting()
        {
            try
            {
                using (Stream stream = typeof(QueryHistoryWindowControl).Assembly.GetManifestResourceStream("MSSQLTool.QuickSearch.sql.xshd"))
                using (var reader = new XmlTextReader(stream))
                    SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex) { FeatureDiagnostics.Report("Query History", "SQL syntax highlighting could not be loaded", ex); }
        }
    }
}
