// Schema Compare - tool window content.
// The window owns three things: the two connection descriptors, the comparison
// result and the generated script. All catalog reads run on a background thread and
// can be cancelled.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using MSSQLTool.SchemaCompare;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool
{
    public partial class SchemaCompareWindowControl : UserControl
    {
        /// <summary>Above this object count the status bar warns about long reads.</summary>
        private const int LargeDatabaseObjectThreshold = 5000;

        private readonly ToolWindowThemeController _themeController;
        private CancellationTokenSource _cancellation;
        private SchemaComparisonResult _result;
        private List<SchemaDifference> _visibleDifferences = new List<SchemaDifference>();
        private bool _busy;

        public SchemaCompareWindowControl()
        {
            InitializeComponent();
            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            Unloaded += OnUnloaded;

            LoadSqlHighlighting();
            RestoreEndpoints();
            FillServerSuggestions();
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelRunningOperation();
        }

        #region Connections

        /// <summary>
        /// The credentials always come from an SSMS connection: the two sides only
        /// override the server and the database on top of it.
        /// </summary>
        private ScriptFactoryAccess.ConnectionInfo GetBaseConnection()
        {
            try
            {
                var connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (connection != null && !string.IsNullOrWhiteSpace(connection.FullConnectionString))
                {
                    return connection;
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The active query window connection could not be read", ex);
            }

            try
            {
                var connection = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer(true);
                if (connection != null && !string.IsNullOrWhiteSpace(connection.FullConnectionString))
                {
                    return connection;
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The Object Explorer connection could not be read", ex);
            }

            return null;
        }

        private void FillServerSuggestions()
        {
            var connection = GetBaseConnection();
            if (connection == null || string.IsNullOrWhiteSpace(connection.ServerName))
            {
                SetStatus("Open a query window or select a node in Object Explorer to reuse its connection.", "Warning");
                return;
            }

            AddSuggestion(SourceServerBox, connection.ServerName);
            AddSuggestion(TargetServerBox, connection.ServerName);
        }

        private static void AddSuggestion(ComboBox box, string value)
        {
            if (box == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            bool exists = box.Items.Cast<object>().Any(item => string.Equals(Convert.ToString(item), value, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                box.Items.Add(value);
            }
        }

        private static void SelectSuggestion(ComboBox box, string value)
        {
            if (box == null)
            {
                return;
            }

            AddSuggestion(box, value);
            box.Text = value ?? string.Empty;
        }

        private void RestoreEndpoints()
        {
            var source = SchemaCompareSettings.GetLastSource();
            var target = SchemaCompareSettings.GetLastTarget();

            if (source != null)
            {
                SelectSuggestion(SourceServerBox, source.Server);
                SelectSuggestion(SourceDatabaseBox, source.Database);
            }

            if (target != null)
            {
                SelectSuggestion(TargetServerBox, target.Server);
                SelectSuggestion(TargetDatabaseBox, target.Database);
            }
        }

        private void PersistEndpoints()
        {
            SchemaCompareSettings.SaveLastSource(SourceServerBox.Text, SourceDatabaseBox.Text);
            SchemaCompareSettings.SaveLastTarget(TargetServerBox.Text, TargetDatabaseBox.Text);
        }

        private void UseActiveConnectionSource_Click(object sender, RoutedEventArgs e)
        {
            UseActiveConnection(SourceServerBox, SourceDatabaseBox);
        }

        private void UseActiveConnectionTarget_Click(object sender, RoutedEventArgs e)
        {
            UseActiveConnection(TargetServerBox, TargetDatabaseBox);
        }

        private void UseActiveConnection(ComboBox serverBox, ComboBox databaseBox)
        {
            if (_busy)
            {
                return;
            }

            var connection = GetBaseConnection();
            if (connection == null)
            {
                SetStatus("No active connection was found. Connect a query window or select a node in Object Explorer first.", "Error");
                return;
            }

            SelectSuggestion(serverBox, connection.ServerName);
            SelectSuggestion(databaseBox, connection.Database);
            PersistEndpoints();
            SetStatus("Connection set to " + connection.DisplayName + ".", "Success");
        }

        private void Swap_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string server = SourceServerBox.Text;
            string database = SourceDatabaseBox.Text;
            object[] sourceServerItems = SourceServerBox.Items.Cast<object>().ToArray();
            object[] sourceDatabaseItems = SourceDatabaseBox.Items.Cast<object>().ToArray();

            SourceServerBox.Items.Clear();
            SourceDatabaseBox.Items.Clear();
            foreach (object item in TargetServerBox.Items)
            {
                SourceServerBox.Items.Add(item);
            }

            foreach (object item in TargetDatabaseBox.Items)
            {
                SourceDatabaseBox.Items.Add(item);
            }

            TargetServerBox.Items.Clear();
            TargetDatabaseBox.Items.Clear();
            foreach (object item in sourceServerItems)
            {
                TargetServerBox.Items.Add(item);
            }

            foreach (object item in sourceDatabaseItems)
            {
                TargetDatabaseBox.Items.Add(item);
            }

            SourceServerBox.Text = TargetServerBox.Text;
            SourceDatabaseBox.Text = TargetDatabaseBox.Text;
            TargetServerBox.Text = server;
            TargetDatabaseBox.Text = database;

            PersistEndpoints();
            SetStatus("Source and target were exchanged. Run the comparison again.", "Warning");
        }

        private void LoadDatabasesSource_Click(object sender, RoutedEventArgs e)
        {
            LoadDatabasesAsync(SourceServerBox, SourceDatabaseBox);
        }

        private void LoadDatabasesTarget_Click(object sender, RoutedEventArgs e)
        {
            LoadDatabasesAsync(TargetServerBox, TargetDatabaseBox);
        }

        private async void LoadDatabasesAsync(ComboBox serverBox, ComboBox databaseBox)
        {
            if (_busy)
            {
                return;
            }

            var baseConnection = GetBaseConnection();
            if (baseConnection == null)
            {
                SetStatus("No active connection was found, so the database list cannot be read.", "Error");
                return;
            }

            string server = (serverBox.Text ?? string.Empty).Trim();
            string current = databaseBox.Text;

            try
            {
                SetBusy(true);
                SetStatus("Reading the database list from " + (string.IsNullOrWhiteSpace(server) ? baseConnection.ServerName : server) + "...", null);

                var connection = SchemaCompareConnectionFactory.Create(baseConnection, server, "master");
                IList<string> names = await Task.Run(() => SchemaReader.GetDatabaseNamesAsync(connection, server, 30, CancellationToken.None));

                databaseBox.Items.Clear();
                foreach (string name in names)
                {
                    databaseBox.Items.Add(name);
                }

                databaseBox.Text = current;
                SetStatus("Loaded " + names.Count + " database(s).", "Success");
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The database list could not be read", ex);
                SetStatus("The database list could not be read: " + ex.Message, "Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        #endregion

        #region Comparison

        private void Compare_Click(object sender, RoutedEventArgs e)
        {
            CompareAsync();
        }

        private async void CompareAsync()
        {
            if (_busy)
            {
                return;
            }

            string sourceServer = (SourceServerBox.Text ?? string.Empty).Trim();
            string sourceDatabase = (SourceDatabaseBox.Text ?? string.Empty).Trim();
            string targetServer = (TargetServerBox.Text ?? string.Empty).Trim();
            string targetDatabase = (TargetDatabaseBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(sourceDatabase) || string.IsNullOrWhiteSpace(targetDatabase))
            {
                SetStatus("A database name is required on both sides.", "Error");
                return;
            }

            if (string.Equals(sourceServer, targetServer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sourceDatabase, targetDatabase, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("The source and the target point to the same database; there is nothing to compare.", "Error");
                return;
            }

            var baseConnection = GetBaseConnection();
            if (baseConnection == null)
            {
                SetStatus("No active connection was found. Connect a query window or select a node in Object Explorer first.", "Error");
                return;
            }

            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;

            try
            {
                SetBusy(true);
                DifferenceGrid.ItemsSource = null;
                DetailGrid.ItemsSource = null;
                ScriptEditor.Text = string.Empty;
                _result = null;

                PersistEndpoints();

                var readOptions = BuildReadOptions();
                var compareOptions = BuildCompareOptions();
                var progress = new Progress<string>(message => SetStatus(message, null));

                var sourceConnection = SchemaCompareConnectionFactory.Create(baseConnection, sourceServer, sourceDatabase);
                var targetConnection = SchemaCompareConnectionFactory.Create(baseConnection, targetServer, targetDatabase);

                var sourceSnapshot = await Task.Run(() => SchemaReader.ReadAsync(sourceConnection, sourceDatabase, readOptions, progress, token), token);
                CheckLargeDatabase(sourceSnapshot, "source");

                var targetSnapshot = await Task.Run(() => SchemaReader.ReadAsync(targetConnection, targetDatabase, readOptions, progress, token), token);
                CheckLargeDatabase(targetSnapshot, "target");

                SetStatus("Comparing " + sourceSnapshot.DisplayName + " with " + targetSnapshot.DisplayName + "...", null);
                var result = await Task.Run(() => SchemaComparer.Compare(sourceSnapshot, targetSnapshot, compareOptions), token);

                _result = result;
                ApplyResult();
            }
            catch (OperationCanceledException)
            {
                SetStatus("The comparison was cancelled.", "Warning");
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The comparison failed", ex);
                SetStatus("The comparison failed: " + ex.Message, "Error");
            }
            finally
            {
                SetBusy(false);
                if (_cancellation != null)
                {
                    _cancellation.Dispose();
                    _cancellation = null;
                }
            }
        }

        private void CheckLargeDatabase(SchemaSnapshot snapshot, string side)
        {
            if (snapshot != null && snapshot.ObjectCount > LargeDatabaseObjectThreshold)
            {
                SetStatus("The " + side + " database contains " + snapshot.ObjectCount + " objects; the comparison can take a while.", "Warning");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRunningOperation();
        }

        private void CancelRunningOperation()
        {
            try
            {
                if (_cancellation != null)
                {
                    _cancellation.Cancel();
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The running operation could not be cancelled", ex);
            }
        }

        private SchemaReadOptions BuildReadOptions()
        {
            return new SchemaReadOptions
            {
                ReadTables = ChkTables.IsChecked == true || ChkIndexes.IsChecked == true || ChkConstraints.IsChecked == true,
                ReadColumns = ChkTables.IsChecked == true,
                ReadIndexes = ChkIndexes.IsChecked == true,
                ReadConstraints = ChkConstraints.IsChecked == true,
                ReadViews = ChkViews.IsChecked == true,
                ReadRoutines = ChkRoutines.IsChecked == true,
                ReadTriggers = ChkTriggers.IsChecked == true,
                ReadRowCounts = ChkRowCounts.IsChecked == true
            };
        }

        private SchemaCompareOptions BuildCompareOptions()
        {
            return new SchemaCompareOptions
            {
                CompareTables = ChkTables.IsChecked == true,
                CompareColumns = ChkTables.IsChecked == true,
                CompareIndexes = ChkIndexes.IsChecked == true,
                CompareConstraints = ChkConstraints.IsChecked == true,
                CompareViews = ChkViews.IsChecked == true,
                CompareRoutines = ChkRoutines.IsChecked == true,
                CompareTriggers = ChkTriggers.IsChecked == true,
                IgnoreWhitespaceDifferences = ChkIgnoreWhitespace.IsChecked == true
            };
        }

        private SchemaScriptOptions BuildScriptOptions()
        {
            return new SchemaScriptOptions
            {
                IncludeDrops = ChkScriptDrops.IsChecked == true,
                IncludeAlterColumn = ChkScriptAlter.IsChecked == true,
                IncludeIndexes = ChkScriptIndexes.IsChecked == true,
                IncludeConstraints = ChkScriptIndexes.IsChecked == true
            };
        }

        private void ApplyResult()
        {
            var result = _result;
            if (result == null)
            {
                return;
            }

            ApplyFilter();

            var notes = new List<string>();
            if (result.Source != null && result.Source.Notes.Count > 0)
            {
                notes.AddRange(result.Source.Notes);
            }

            if (result.Target != null && result.Target.Notes.Count > 0)
            {
                notes.AddRange(result.Target.Notes);
            }

            if (notes.Count > 0)
            {
                SetStatus(result.BuildSummary() + " Some parts could not be read: " + notes[0], "Warning");
            }
            else
            {
                SetStatus(result.BuildSummary(), result.IsIdentical ? "Success" : null);
            }

            RegenerateScript();
        }

        private void ApplyFilter()
        {
            if (_result == null)
            {
                _visibleDifferences = new List<SchemaDifference>();
                DifferenceGrid.ItemsSource = _visibleDifferences;
                FilterSummaryText.Text = string.Empty;
                return;
            }

            string search = (SearchBox.Text ?? string.Empty).Trim();
            int statusIndex = FilterStatusCombo.SelectedIndex;
            bool showInformational = ChkShowInformational.IsChecked == true;

            IEnumerable<SchemaDifference> items = _result.Differences;

            switch (statusIndex)
            {
                case 1:
                    items = items.Where(d => d.Status == SchemaDifferenceStatus.MissingInTarget);
                    break;
                case 2:
                    items = items.Where(d => d.Status == SchemaDifferenceStatus.MissingInSource);
                    break;
                case 3:
                    items = items.Where(d => d.Status == SchemaDifferenceStatus.Different);
                    break;
            }

            if (!showInformational)
            {
                items = items.Where(d => d.Severity != SchemaDifferenceSeverity.Information);
            }

            if (search.Length > 0)
            {
                items = items.Where(d =>
                    Contains(d.ObjectName, search) ||
                    Contains(d.Summary, search) ||
                    Contains(d.TypeText, search) ||
                    Contains(d.StatusText, search));
            }

            _visibleDifferences = items.ToList();
            DifferenceGrid.ItemsSource = _visibleDifferences;
            FilterSummaryText.Text = _visibleDifferences.Count + " of " + _result.Differences.Count + " difference(s) shown";
        }

        private static bool Contains(string value, string search)
        {
            return value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            ApplyFilter();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            ApplyFilter();
        }

        private void DifferenceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var difference = DifferenceGrid.SelectedItem as SchemaDifference;
            DetailGrid.ItemsSource = difference == null ? null : difference.Properties;
            RegenerateScript();
        }

        private void DifferenceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var difference = DifferenceGrid.SelectedItem as SchemaDifference;
            if (difference == null)
            {
                return;
            }

            if (difference.ObjectType == SchemaObjectType.Table && (difference.SourceTable != null || difference.TargetTable != null) ||
                difference.ObjectType == SchemaObjectType.View ||
                difference.ObjectType == SchemaObjectType.Routine)
            {
                OpenInNewQueryWindow(BuildSingleDifferenceScript(difference));
            }
        }

        #endregion

        #region Script

        private void RegenerateScript_Click(object sender, RoutedEventArgs e)
        {
            RegenerateScript();
        }

        private void RegenerateScript()
        {
            if (_result == null)
            {
                ScriptEditor.Text = string.Empty;
                return;
            }

            try
            {
                var difference = DifferenceGrid.SelectedItem as SchemaDifference;
                var result = SelectedOnlyCheck.IsChecked == true && difference != null
                    ? BuildSingleDifferenceResult(difference)
                    : _result;

                ScriptEditor.Text = SchemaComparer.GenerateSyncScript(result, BuildScriptOptions());
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The sync script could not be generated", ex);
                ScriptEditor.Text = "-- The script could not be generated: " + ex.Message;
            }
        }

        private SchemaComparisonResult BuildSingleDifferenceResult(SchemaDifference difference)
        {
            return new SchemaComparisonResult
            {
                Source = _result.Source,
                Target = _result.Target,
                Options = _result.Options,
                ComparedUtc = _result.ComparedUtc,
                Differences = new List<SchemaDifference> { difference }
            };
        }

        private string BuildSingleDifferenceScript(SchemaDifference difference)
        {
            if (_result == null || difference == null)
            {
                return string.Empty;
            }

            try
            {
                return SchemaComparer.GenerateSyncScript(BuildSingleDifferenceResult(difference), BuildScriptOptions());
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The single difference script could not be generated", ex);
                return string.Empty;
            }
        }

        private void CopyScript_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(ScriptEditor.Text))
                {
                    SetStatus("There is no script to copy.", "Warning");
                    return;
                }

                Clipboard.SetText(ScriptEditor.Text);
                SetStatus("The script was copied to the clipboard.", "Success");
            }
            catch (Exception ex)
            {
                SetStatus("The script could not be copied: " + ex.Message, "Error");
            }
        }

        private void SaveScript_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ScriptEditor.Text))
            {
                SetStatus("There is no script to save.", "Warning");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the synchronisation script",
                Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*",
                FileName = "SchemaCompare_"
                    + SanitizeFileName(_result == null || _result.Source == null ? null : _result.Source.DatabaseName) + "_to_"
                    + SanitizeFileName(_result == null || _result.Target == null ? null : _result.Target.DatabaseName) + ".sql"
            };

            try
            {
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                File.WriteAllText(dialog.FileName, ScriptEditor.Text, new System.Text.UTF8Encoding(true));
                SetStatus("The script was saved to " + dialog.FileName, "Success");
            }
            catch (Exception ex)
            {
                SetStatus("The script could not be saved: " + ex.Message, "Error");
            }
        }

        private static string SanitizeFileName(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "database" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(invalid, '_');
            }

            return text;
        }

        private void OpenScript_Click(object sender, RoutedEventArgs e)
        {
            OpenInNewQueryWindow(ScriptEditor.Text);
        }

        private void OpenInNewQueryWindow(string script)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(script))
            {
                SetStatus("There is no script to open.", "Warning");
                return;
            }

            try
            {
                var current = ScriptFactoryAccess.GetCurrentConnectionInfo();
                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, current == null ? null : current.ActiveConnectionInfo, null);
                var document = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                document.EndPoint.CreateEditPoint().Insert(script);
                SetStatus("The script was opened in a new query window.", "Success");
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The script could not be opened in a query window", ex);
                SetStatus("The script could not be opened in a query window: " + ex.Message, "Error");
            }
        }

        private void LoadSqlHighlighting()
        {
            try
            {
                using (Stream stream = typeof(SchemaCompareWindowControl).Assembly.GetManifestResourceStream("MSSQLTool.QuickSearch.sql.xshd"))
                using (var reader = new XmlTextReader(stream))
                {
                    ScriptEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "SQL syntax highlighting could not be loaded", ex);
            }
        }

        #endregion

        #region Status and busy state

        private void SetBusy(bool busy)
        {
            _busy = busy;
            CompareButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            CompareProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>kind: null (normal), "Error", "Warning" or "Success".</summary>
        private void SetStatus(string message, string kind)
        {
            StatusText.Text = message ?? string.Empty;
            StatusText.Tag = kind;
        }

        #endregion
    }
}
