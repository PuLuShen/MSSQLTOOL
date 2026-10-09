using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Xml;
using static MSSQLTool.ScriptFactoryAccess;

namespace MSSQLTool
{
    public partial class QuickSearchWindowControl : UserControl
    {
        private const int MaxHighlightMatches = 400;
        private const int MaxAnchorHighlights = 20;

        // Highlight palette: object name hits are stronger than definition/text hits, and the
        // whole line is tinted so a long definition still shows where the match is.
        private static readonly Color NameMatchColor = Color.FromRgb(0xFF, 0xC8, 0x5C);
        private static readonly Color BodyMatchColor = Color.FromRgb(0xFF, 0xF0, 0x80);
        private static readonly Color LightLineHighlightColor = Color.FromRgb(0xFF, 0xF7, 0xDF);
        private static readonly Color DarkLineHighlightColor = Color.FromRgb(0x45, 0x40, 0x22);
        private static readonly TimeSpan DatabaseCatalogLifetime = TimeSpan.FromMinutes(10);

        private readonly ToolWindowThemeController themeController;
        private readonly ObservableCollection<DatabaseItem> databaseItems = new ObservableCollection<DatabaseItem>();
        private readonly ObservableCollection<string> recentTerms = new ObservableCollection<string>();

        private ScriptFactoryAccess.ConnectionInfo selectedConnection;
        private string selectedDatabase;
        private string selectedServer;

        private CancellationTokenSource searchCancellationTokenSource;
        private CancellationTokenSource databaseListCancellationTokenSource;
        private Task runningSearchTask;
        private TextMarkerService textMarkerService;
        private QuickSearchRequest lastRequest;
        private DataTable lastResultTable;

        private string catalogConnectionString;
        private DateTime catalogLoadedUtc = DateTime.MinValue;
        private bool databaseListLoaded;
        private bool suppressDatabaseNotifications;
        private bool suppressServerNotifications;
        private bool uiReady;

        public QuickSearchWindowControl()
        {
            this.InitializeComponent();
            themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);

            using (var stream = typeof(QuickSearchWindowControl).Assembly.GetManifestResourceStream("MSSQLTool.QuickSearch.sql.xshd"))
            using (var reader = new XmlTextReader(stream))
            {
                SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }

            if (textMarkerService == null)
            {
                textMarkerService = new TextMarkerService(SqlEditor);
            }

            ListBox_Databases.ItemsSource = databaseItems;
            PopulateDatabaseModeOptions();
            LoadRecentTerms();
            UpdateDatabaseSummary();
            BuildSearchHistoryMenu();

            // The search field is a plain TextBox now, but the tool window still has to be given the
            // keyboard focus when it appears: without that, the shell keeps the keystrokes.
            Loaded += (_, __) => OnWindowAppeared();
            IsVisibleChanged += (_, __) => { if (IsVisible) OnWindowAppeared(); };
            PreviewKeyDown += QuickSearchWindowControl_PreviewKeyDown;

            uiReady = true;
        }

        /// <summary>Diagnostics for the Quick Search window; written to the extension log.</summary>
        internal static void Log(string message)
        {
            try
            {
                MSSQLToolPackage._logger?.Info("QuickSearch: " + message);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Runs whenever the window becomes visible: refresh the server list, take the Object Explorer
        /// target when there is none yet, and put the caret in the search box.
        /// </summary>
        private async void OnWindowAppeared()
        {
            try
            {
                RefreshServerList();
                await AutoSelectTargetFromObjectExplorerAsync();
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Quick Search", "Preparing the search window failed", ex);
            }

            FocusSearchBox();
        }

        /// <summary>
        /// The target no longer needs a button: the window adopts whatever Object Explorer has
        /// selected as soon as it is shown, and the server list switches afterwards.
        /// </summary>
        private async Task AutoSelectTargetFromObjectExplorerAsync()
        {
            if (selectedConnection != null) return;

            var ci = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            if (ci == null)
            {
                Label_ConnectionDescription.Content = LocalizationManager.T("Select a server or database node in Object Explorer.");
                Log("Quick search: no Object Explorer selection to adopt yet.");
                return;
            }

            await SelectTargetAsync(ci);
            SelectServerInList(ci.ServerName);
            RefreshServerList();
            Log($"Quick search: adopted Object Explorer target {ci.ServerName} / {ci.Database}.");
        }

        /// <summary>Moves the keyboard focus into the search box.</summary>
        private void FocusSearchBox()
        {
            try
            {
                // Input priority: the focus has to be set once the shell has finished showing the pane.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!IsVisible) return;
                        TextBox_SearchText.Focus();
                        Keyboard.Focus(TextBox_SearchText);
                        Log($"Quick search: search box focus requested (focus within: {TextBox_SearchText.IsKeyboardFocusWithin}).");
                    }
                    catch (Exception)
                    {
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Last resort for keystroke routing: a plain character arriving while nothing that accepts
        /// text has the focus moves the focus into the search box, so the next keystroke lands there.
        /// </summary>
        private void QuickSearchWindowControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox || e.OriginalSource is DataGrid) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key < Key.A || e.Key > Key.Z) return;

            Log($"Quick search: a key ({e.Key}) arrived with no text box focused; moving the focus.");
            FocusSearchBox();
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void WikiLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ToolWindowNavigation.HandleRequestNavigate(e);
        }

        #region Connection and database selection

        /// <summary>
        /// Rebuilds the server picker.  It always contains at least the server currently in use, so
        /// the list is never blank even when this SSMS build does not hand out the connected-server
        /// list.
        /// </summary>
        private void RefreshServerList()
        {
            try
            {
                List<ScriptFactoryAccess.ObjectExplorerServer> servers = ScriptFactoryAccess.GetObjectExplorerServers();

                string activeServer = selectedServer;
                if (string.IsNullOrWhiteSpace(activeServer))
                    activeServer = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer()?.ServerName;

                if (!string.IsNullOrWhiteSpace(activeServer)
                    && !servers.Exists(s => string.Equals(s.ServerName, activeServer, StringComparison.OrdinalIgnoreCase)))
                {
                    servers.Insert(0, new ScriptFactoryAccess.ObjectExplorerServer
                    {
                        ServerName = activeServer,
                        DisplayName = activeServer,
                        IsConnected = true
                    });
                    Log($"Server list: Object Explorer reported nothing, so the active server '{activeServer}' is shown.");
                }

                suppressServerNotifications = true;
                try
                {
                    ComboBox_Server.ItemsSource = servers;
                    SelectServerInList(activeServer);
                    if (ComboBox_Server.SelectedItem == null && servers.Count == 1)
                        ComboBox_Server.SelectedIndex = 0;
                }
                finally
                {
                    suppressServerNotifications = false;
                }

                Log($"Server list: {servers.Count} entries, selected '{ComboBox_Server.SelectedItem}'.");
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Quick Search", "Could not fill the server list", ex);
            }
        }

        private void SelectServerInList(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return;

            bool previous = suppressServerNotifications;
            suppressServerNotifications = true;
            try
            {
                foreach (object item in ComboBox_Server.Items)
                {
                    var server = item as ScriptFactoryAccess.ObjectExplorerServer;
                    if (server != null && string.Equals(server.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                    {
                        ComboBox_Server.SelectedItem = item;
                        return;
                    }
                }
            }
            finally
            {
                suppressServerNotifications = previous;
            }
        }

        private async void ComboBox_Server_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!uiReady || suppressServerNotifications) return;

            var server = ComboBox_Server.SelectedItem as ScriptFactoryAccess.ObjectExplorerServer;
            if (server == null) return;

            var ci = ScriptFactoryAccess.GetConnectionInfoFromObjectExplorerServer(server.ServerName);
            if (ci == null)
            {
                LocalizedMessageBox.Show(
                    LocalizationManager.Format("Could not use the connection to {0}. Select a node in Object Explorer and use the button instead.", server.ServerName),
                    "Quick Search");
                return;
            }

            await SelectTargetAsync(ci);
        }

        /// <summary>Points the window at a connection and reloads the database list for it.</summary>
        private async Task SelectTargetAsync(ScriptFactoryAccess.ConnectionInfo ci)
        {
            selectedConnection = ci;
            selectedDatabase = ci.Database;
            selectedServer = ci.ServerName;
            Label_ConnectionDescription.Content = LocalizationManager.T($"Server: [{selectedServer}] / Database: [{selectedDatabase}]");
            SearchInputsGrid.IsEnabled = true;
            SearchScopeGrid.IsEnabled = true;

            // A new target server means a new database list.
            databaseListLoaded = false;
            await RefreshDatabaseListAsync(true);
        }

        private async void Button_RefreshDatabases_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDatabaseListAsync(true);
        }

        private async Task RefreshDatabaseListAsync(bool force)
        {
            if (selectedConnection == null) return;

            string connectionKey = selectedConnection.FullConnectionString ?? string.Empty;
            bool current = databaseListLoaded
                && string.Equals(catalogConnectionString, connectionKey, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - catalogLoadedUtc < DatabaseCatalogLifetime;

            if (current && !force) return;

            CancellationTokenSource previous = databaseListCancellationTokenSource;
            databaseListCancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = databaseListCancellationTokenSource.Token;
            // The previous load is only cancelled, never disposed: its token may still be inside
            // an in-flight await and disposing it would surface as an unexpected exception.
            previous?.Cancel();

            try
            {
                SetDatabasePanelBusy(true);
                TextBlock_DatabaseSummary.Text = LocalizationManager.T("Loading...");

                List<QuickSearchDatabaseInfo> databases = await QuickSearchDatabaseCatalog.LoadAsync(selectedConnection, token);
                token.ThrowIfCancellationRequested();

                catalogConnectionString = connectionKey;
                catalogLoadedUtc = DateTime.UtcNow;
                databaseListLoaded = true;
                ApplyDatabaseCatalog(databases);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Quick Search", "Could not read the database list", ex);
                databaseListLoaded = false;
                TextBlock_DatabaseSummary.Text = LocalizationManager.Format("Could not read the database list: {0}", ex.Message);
            }
            finally
            {
                SetDatabasePanelBusy(false);
            }
        }

        private void SetDatabasePanelBusy(bool busy)
        {
            Button_RefreshDatabases.IsEnabled = !busy;
            ComboBox_DatabaseMode.IsEnabled = !busy;
        }

        private void ApplyDatabaseCatalog(List<QuickSearchDatabaseInfo> databases)
        {
            QuickSearchSettings.DatabaseSelection stored = QuickSearchSettings.LoadDatabaseSelection();
            bool storedForThisServer = stored.MatchesServer(selectedServer);
            var storedNames = new HashSet<string>(
                storedForThisServer ? stored.Databases : new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            bool useStoredNames = storedForThisServer && stored.Databases.Count > 0;

            suppressDatabaseNotifications = true;
            try
            {
                databaseItems.Clear();
                foreach (QuickSearchDatabaseInfo info in databases)
                {
                    bool selected;
                    if (!info.CanBeSearched)
                    {
                        // Offline, restricted or unreachable databases are listed but never selected.
                        selected = false;
                    }
                    else if (useStoredNames)
                    {
                        selected = storedNames.Contains(info.Name);
                    }
                    else
                    {
                        selected = true;
                    }

                    databaseItems.Add(new DatabaseItem(info, selected));
                }

                bool hasSearchable = databaseItems.Any(item => item.CanBeSearched);
                SetDatabaseMode(!stored.AllDatabases && hasSearchable ? 1 : 0, false);
            }
            finally
            {
                suppressDatabaseNotifications = false;
            }

            UpdateDatabaseSummary();
            SaveDatabaseSelectionState();
        }

        private void SetDatabaseMode(int index, bool notify)
        {
            if (ComboBox_DatabaseMode.SelectedIndex == index)
            {
                if (notify) UpdateDatabaseSummary();
                return;
            }

            if (!notify) suppressDatabaseNotifications = true;
            try
            {
                ComboBox_DatabaseMode.SelectedIndex = index;
            }
            finally
            {
                if (!notify) suppressDatabaseNotifications = false;
            }

            UpdateDatabaseSummary();
        }

        private void PopulateDatabaseModeOptions()
        {
            suppressDatabaseNotifications = true;
            try
            {
                ComboBox_DatabaseMode.Items.Clear();
                ComboBox_DatabaseMode.Items.Add(LocalizationManager.T("All user databases on the server"));
                ComboBox_DatabaseMode.Items.Add(LocalizationManager.T("Specific databases"));
                ComboBox_DatabaseMode.SelectedIndex = 0;
            }
            finally
            {
                suppressDatabaseNotifications = false;
            }
        }

        private void ComboBox_DatabaseMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!uiReady || suppressDatabaseNotifications) return;

            UpdateDatabaseSummary();
            SaveDatabaseSelectionState();
        }

        private void DatabaseCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (!uiReady || suppressDatabaseNotifications) return;

            if (sender is CheckBox checkBox && checkBox.DataContext is DatabaseItem item)
            {
                // The two way binding may not have reached the model yet, so mirror the new state
                // explicitly before the selection is persisted.
                item.IsSelected = checkBox.IsChecked == true;
            }

            // Editing the list is an explicit intent to search a specific set of databases.
            if (ComboBox_DatabaseMode.SelectedIndex != 1)
            {
                suppressDatabaseNotifications = true;
                try
                {
                    ComboBox_DatabaseMode.SelectedIndex = 1;
                }
                finally
                {
                    suppressDatabaseNotifications = false;
                }
            }

            UpdateDatabaseSummary();
            SaveDatabaseSelectionState();
        }

        private void Button_CheckAllDatabases_Click(object sender, RoutedEventArgs e)
        {
            SetAllDatabaseSelections(true);
        }

        private void Button_UncheckAllDatabases_Click(object sender, RoutedEventArgs e)
        {
            SetAllDatabaseSelections(false);
        }

        private void SetAllDatabaseSelections(bool selected)
        {
            suppressDatabaseNotifications = true;
            try
            {
                foreach (DatabaseItem item in databaseItems)
                {
                    if (!item.CanBeSearched) continue;
                    item.IsSelected = selected;
                }

                ComboBox_DatabaseMode.SelectedIndex = 1;
            }
            finally
            {
                suppressDatabaseNotifications = false;
            }

            UpdateDatabaseSummary();
            SaveDatabaseSelectionState();
        }

        private void UpdateDatabaseSummary()
        {
            if (!databaseListLoaded)
            {
                TextBlock_DatabaseSummary.Text = string.Empty;
                return;
            }

            int searchable = databaseItems.Count(item => item.CanBeSearched);
            int selected = databaseItems.Count(item => item.IsSelected && item.CanBeSearched);
            int unavailable = databaseItems.Count - searchable;

            string summary = ComboBox_DatabaseMode.SelectedIndex == 1
                ? LocalizationManager.Format("{0} of {1} selected", selected, searchable)
                : LocalizationManager.Format("All databases ({0})", searchable);

            if (unavailable > 0)
            {
                summary += " | " + LocalizationManager.Format("{0} unavailable", unavailable);
            }

            TextBlock_DatabaseSummary.Text = summary;
        }

        private void SaveDatabaseSelectionState()
        {
            if (!databaseListLoaded) return;

            bool allDatabases = ComboBox_DatabaseMode.SelectedIndex != 1;
            List<string> selectedNames = databaseItems
                .Where(item => item.IsSelected && item.CanBeSearched)
                .Select(item => item.Name)
                .ToList();

            QuickSearchSettings.SaveDatabaseSelection(allDatabases, selectedServer, selectedNames);
        }

        private bool TryBuildDatabaseList(QuickSearchRequest request)
        {
            if (request.AllDatabases) return true;

            foreach (DatabaseItem item in databaseItems)
            {
                if (item.IsSelected && item.CanBeSearched) request.Databases.Add(item.Name);
            }

            if (request.Databases.Count == 0)
            {
                LocalizedMessageBox.Show("Select at least one database.", "Quick Search");
                return false;
            }

            return true;
        }

        #endregion

        #region Search

        private async void Button_Search_Click(object sender, RoutedEventArgs e)
        {
            // While a search is running the same button is the cancel button.
            runningSearchTask = RunSearchAsync(false, false);
            await runningSearchTask;
        }

        private async Task RunSearchAsync(bool forceAllDatabases, bool restartWhenRunning)
        {
            if (searchCancellationTokenSource != null)
            {
                searchCancellationTokenSource.Cancel();

                // Enter / Ctrl+Enter mean "search", so they cancel the running search and start
                // the new one as soon as the previous one released the UI.
                Task previous = runningSearchTask;
                if (!restartWhenRunning || previous == null) return;

                try
                {
                    await previous;
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("Quick Search", "Previous search ended with an error", ex);
                }

                if (searchCancellationTokenSource != null) return;
            }

            if (selectedConnection == null)
            {
                LocalizedMessageBox.Show("Select a connection from Object Explorer first.", "Quick Search");
                return;
            }

            string searchText = GetSearchText();
            if (string.IsNullOrEmpty(searchText))
            {
                LocalizedMessageBox.Show("Enter text to search.", "Quick Search");
                return;
            }

            if (!AnyTypeSelected())
            {
                LocalizedMessageBox.Show("Select at least one object type.", "Quick Search");
                return;
            }

            var request = new QuickSearchRequest
            {
                SearchText = searchText,
                WholeWord = CheckBox_WholeWord.IsChecked == true,
                UseWildcards = CheckBox_UseWildcards.IsChecked == true,
                Fuzzy = CheckBox_Fuzzy.IsChecked == true,
                IncludeStoredProcedures = CheckBox_StoredProcedures.IsChecked == true,
                IncludeViews = CheckBox_Views.IsChecked == true,
                IncludeFunctions = CheckBox_Functions.IsChecked == true,
                IncludeTables = CheckBox_Tables.IsChecked == true,
                IncludeAgentJobSteps = CheckBox_AgentJobSteps.IsChecked == true,
                ServerName = selectedServer,
                AllDatabases = forceAllDatabases || ComboBox_DatabaseMode.SelectedIndex != 1
            };
            request.Options = new QuickSearchSearchOptions(
                request.SearchText, request.WholeWord, request.UseWildcards, request.Fuzzy);

            // Refresh the database list when the target server changed since the last load, so a
            // newly attached or restored database is picked up without pressing Refresh. It runs
            // before the selection is captured so the search uses the refreshed list.
            await RefreshDatabaseListAsync(false);

            if (!TryBuildDatabaseList(request)) return;

            searchCancellationTokenSource = new CancellationTokenSource();
            CancellationToken cancellationToken = searchCancellationTokenSource.Token;

            // The options the results were produced with are reused for preview and highlight.
            lastRequest = request;
            lastResultTable = null;

            try
            {
                Button_Search.Content = LocalizationManager.T("Cancel");
                DataGrid_SearchResults.ItemsSource = null;
                SqlEditor.Text = string.Empty;
                textMarkerService.RemoveAll();
                TextBlock_ObjectTypeCounts.Text = string.Empty;
                TextBlock_Elapsed.Text = string.Empty;
                TextBlock_ResultCount.Text = LocalizationManager.T("Searching...");

                var progress = new Progress<QuickSearchProgress>(report =>
                {
                    if (report == null) return;

                    TextBlock_ResultCount.Text = string.IsNullOrEmpty(report.DatabaseName)
                        ? LocalizationManager.Format("Searching {0}/{1} databases...", report.Completed, report.Total)
                        : LocalizationManager.Format("Searching {0}/{1} databases... ({2})", report.Completed, report.Total, report.DatabaseName);
                });

                QuickSearchExecutionResult result = await Task.Run(
                    () => QuickSearchEngine.Execute(selectedConnection, request, progress, cancellationToken),
                    cancellationToken);

                lastResultTable = result.Results;
                DataGrid_SearchResults.ItemsSource = result.Results.DefaultView;

                TextBlock_ResultCount.Text = result.FailedDatabases.Count > 0
                    ? LocalizationManager.Format("{0} result(s) | {1} database(s) could not be searched", result.Results.Rows.Count, result.FailedDatabases.Count)
                    : LocalizationManager.Format("{0} result(s)", result.Results.Rows.Count);

                TextBlock_ObjectTypeCounts.Text = BuildObjectTypeCountsText(result);
                TextBlock_Elapsed.Text = LocalizationManager.Format("{0} ms", (long)result.Elapsed.TotalMilliseconds);
                UpdateRecentTerms(searchText);

                if (DataGrid_SearchResults.Items.Count > 0)
                {
                    // Populate the preview straight away so the first hit is already readable.
                    DataGrid_SearchResults.SelectedIndex = 0;
                }
            }
            catch (OperationCanceledException)
            {
                TextBlock_ResultCount.Text = LocalizationManager.T("Search canceled");
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("Operation cancelled by user.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TextBlock_ResultCount.Text = LocalizationManager.T("Search canceled");
                }
                else
                {
                    FeatureDiagnostics.Report("Quick Search", "Search failed", ex);
                    LocalizedMessageBox.Show($"Search failed: {ex.Message}", "Quick Search");
                    TextBlock_ResultCount.Text = LocalizationManager.T("Search failed");
                }
            }
            finally
            {
                searchCancellationTokenSource?.Dispose();
                searchCancellationTokenSource = null;
                Button_Search.Content = LocalizationManager.T("Search");
            }
        }

        private string BuildObjectTypeCountsText(QuickSearchExecutionResult result)
        {
            Dictionary<string, int> counts = result.GetCountsByObjectType();
            var parts = new List<string>();

            foreach (string objectType in new[] { "Stored Procedure", "Function", "View", "Table", "SQL Agent Job Step" })
            {
                int count;
                if (!counts.TryGetValue(objectType, out count) || count <= 0) continue;

                parts.Add(LocalizationManager.Format("{0}: {1}", GetObjectTypeLabel(objectType), count));
            }

            return string.Join(" | ", parts);
        }

        private static string GetObjectTypeLabel(string objectType)
        {
            switch (objectType)
            {
                case "Stored Procedure": return LocalizationManager.T("Stored Procedures");
                case "Function": return LocalizationManager.T("Functions");
                case "View": return LocalizationManager.T("Views");
                case "Table": return LocalizationManager.T("Tables");
                case "SQL Agent Job Step": return LocalizationManager.T("SQL Agent Jobs");
                default: return objectType;
            }
        }

        private string GetSearchText()
        {
            return (TextBox_SearchText.Text ?? string.Empty).Trim();
        }

        private void UpdateRecentTerms(string searchText)
        {
            List<string> terms = QuickSearchSettings.AddRecentTerm(searchText);

            // The history list feeds the dropdown; the box itself keeps what the user typed.
            string current = TextBox_SearchText.Text;
            recentTerms.Clear();
            foreach (string term in terms) recentTerms.Add(term);
            BuildSearchHistoryMenu();

            if (!string.Equals(TextBox_SearchText.Text, current, StringComparison.Ordinal))
            {
                TextBox_SearchText.Text = current;
            }
        }

        private void LoadRecentTerms()
        {
            recentTerms.Clear();
            foreach (string term in QuickSearchSettings.LoadRecentTerms()) recentTerms.Add(term);
        }

        private bool AnyTypeSelected()
        {
            return CheckBox_StoredProcedures.IsChecked == true
                || CheckBox_Views.IsChecked == true
                || CheckBox_Functions.IsChecked == true
                || CheckBox_Tables.IsChecked == true
                || CheckBox_AgentJobSteps.IsChecked == true;
        }

        private async void TextBox_SearchText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                runningSearchTask = RunSearchAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Control), true);
                await runningSearchTask;
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (searchCancellationTokenSource != null)
                {
                    searchCancellationTokenSource.Cancel();
                }
                else
                {
                    ClearSearch();
                }

                return;
            }

            if (e.Key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                FocusResults();
                return;
            }

            // Plain Up/Down recall earlier searches, which is what the dropdown used to do.
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                e.Handled = true;
                RecallRecentTerm(e.Key == Key.Up ? -1 : 1);
            }
        }

        /// <summary>Steps through the recent search terms.</summary>
        private void RecallRecentTerm(int offset)
        {
            if (recentTerms.Count == 0) return;

            int index = string.IsNullOrEmpty(TextBox_SearchText.Text)
                ? -1
                : recentTerms.IndexOf(TextBox_SearchText.Text);
            int next = Math.Min(recentTerms.Count - 1, Math.Max(-1, index - offset));
            if (next < 0) next = offset < 0 ? recentTerms.Count - 1 : 0;

            TextBox_SearchText.Text = recentTerms[next];
            TextBox_SearchText.CaretIndex = TextBox_SearchText.Text.Length;
        }

        /// <summary>The little arrow next to the search box lists earlier searches.</summary>
        private void Button_SearchHistory_Click(object sender, RoutedEventArgs e)
        {
            BuildSearchHistoryMenu();
            if (Button_SearchHistory.ContextMenu == null) return;
            Button_SearchHistory.ContextMenu.PlacementTarget = Button_SearchHistory;
            Button_SearchHistory.ContextMenu.IsOpen = true;
        }

        private void BuildSearchHistoryMenu()
        {
            var menu = new ContextMenu();
            foreach (string term in recentTerms)
            {
                var item = new MenuItem { Header = term };
                string captured = term;
                item.Click += (_, __) =>
                {
                    TextBox_SearchText.Text = captured;
                    TextBox_SearchText.CaretIndex = TextBox_SearchText.Text.Length;
                    FocusSearchBox();
                };
                menu.Items.Add(item);
            }

            Button_SearchHistory.ContextMenu = menu.Items.Count > 0 ? menu : null;
        }

        private void ClearSearch()
        {
            TextBox_SearchText.Text = string.Empty;
            DataGrid_SearchResults.ItemsSource = null;
            lastRequest = null;
            lastResultTable = null;
            textMarkerService.RemoveAll();
            SqlEditor.Text = string.Empty;
            TextBlock_ResultCount.Text = string.Empty;
            TextBlock_ObjectTypeCounts.Text = string.Empty;
            TextBlock_Elapsed.Text = string.Empty;
        }

        private void FocusResults()
        {
            if (DataGrid_SearchResults.Items.Count == 0) return;

            DataGrid_SearchResults.Focus();
            if (DataGrid_SearchResults.SelectedIndex < 0) DataGrid_SearchResults.SelectedIndex = 0;
            DataGrid_SearchResults.ScrollIntoView(DataGrid_SearchResults.SelectedItem);
        }

        private void CheckBox_WholeWord_Checked(object sender, RoutedEventArgs e)
        {
            if (!uiReady) return;
            if (CheckBox_WholeWord.IsChecked == true) CheckBox_UseWildcards.IsChecked = false;
        }

        private void CheckBox_UseWildcards_Checked(object sender, RoutedEventArgs e)
        {
            if (!uiReady) return;

            if (CheckBox_UseWildcards.IsChecked == true)
            {
                CheckBox_WholeWord.IsChecked = false;
                CheckBox_Fuzzy.IsChecked = false;
            }
        }

        private void CheckBox_Fuzzy_Checked(object sender, RoutedEventArgs e)
        {
            // Fuzzy and SQL wildcards describe conflicting patterns, so they never combine.
            if (!uiReady) return;
            if (CheckBox_Fuzzy.IsChecked == true) CheckBox_UseWildcards.IsChecked = false;
        }

        #endregion

        #region Result preview, highlight and clipboard

        private void DataGrid_SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(DataGrid_SearchResults.SelectedItem is DataRowView rowView))
            {
                return;
            }

            try
            {
                string sourceText = rowView["SourceText"]?.ToString() ?? string.Empty;
                string location = rowView["MatchLocation"]?.ToString() ?? string.Empty;

                textMarkerService.RemoveAll();
                SqlEditor.Text = sourceText;
                ApplyHighlight(sourceText, location);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Quick Search", "Could not preview the selected search result", ex);
                SqlEditor.Text = string.Empty;
            }
        }

        /// <summary>
        /// Highlights every accepted hit of the executed search (not of whatever is typed right
        /// now) plus the line it lives on, so a long definition stays readable.
        /// </summary>
        private void ApplyHighlight(string text, string location)
        {
            QuickSearchSearchOptions options = lastRequest?.Options;
            if (options == null || !options.HasTerm || string.IsNullOrEmpty(text)) return;

            List<QuickSearchTextMatch> matches = options.Scan(text, MaxHighlightMatches);
            if (matches.Count == 0) return;

            // Only accepted hits are highlighted. When the row matched through its name while the
            // definition only contains the pre-filter anchor, a bounded number of anchors is shown
            // instead so the preview is not left blank.
            var ranges = new List<QuickSearchTextMatch>();
            foreach (QuickSearchTextMatch match in matches)
            {
                if (match.Accepted && match.HasRange) ranges.Add(match);
            }

            if (ranges.Count == 0)
            {
                foreach (QuickSearchTextMatch match in matches)
                {
                    if (!match.HasRange) continue;
                    ranges.Add(match);
                    if (ranges.Count >= MaxAnchorHighlights) break;
                }
            }

            if (ranges.Count == 0) return;

            bool bodyLocation = QuickSearchEngine.IsBodyLocation(location);
            Color matchColor = bodyLocation ? BodyMatchColor : NameMatchColor;
            Color lineColor = GetLineHighlightColor();
            var highlightedLines = new HashSet<int>();

            textMarkerService.BeginUpdate();
            try
            {
                foreach (QuickSearchTextMatch match in ranges)
                {
                    int lineStart;
                    int lineLength;
                    GetLineBounds(text, match.Index, out lineStart, out lineLength);
                    if (lineLength > 0 && highlightedLines.Add(lineStart))
                    {
                        textMarkerService.Create(lineStart, lineLength, lineColor, null);
                    }

                    textMarkerService.Create(match.Index, match.Length, matchColor, Colors.Black);
                }
            }
            finally
            {
                textMarkerService.EndUpdate();
            }
        }

        private static void GetLineBounds(string text, int index, out int lineStart, out int lineLength)
        {
            lineStart = 0;
            lineLength = 0;
            if (string.IsNullOrEmpty(text)) return;

            int position = Math.Max(0, Math.Min(index, text.Length - 1));
            int previousNewLine = text.LastIndexOf('\n', position);
            lineStart = previousNewLine + 1;

            int nextNewLine = text.IndexOf('\n', lineStart);
            int lineEnd = nextNewLine < 0 ? text.Length : nextNewLine;
            lineLength = Math.Max(0, lineEnd - lineStart);
        }

        private Color GetLineHighlightColor()
        {
            var solid = SqlEditor.Background as SolidColorBrush;
            Color background = solid?.Color ?? Colors.White;
            double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luminance > 0.5 ? LightLineHighlightColor : DarkLineHighlightColor;
        }

        private void Button_CopyResults_Click(object sender, RoutedEventArgs e)
        {
            CopyResults(false);
        }

        private void MenuItem_CopySelectedResult_Click(object sender, RoutedEventArgs e)
        {
            CopyResults(true);
        }

        private void MenuItem_CopyAllResults_Click(object sender, RoutedEventArgs e)
        {
            CopyResults(false);
        }

        private void CopyResults(bool selectedOnly)
        {
            if (lastResultTable == null || lastResultTable.Rows.Count == 0)
            {
                TextBlock_ResultCount.Text = LocalizationManager.T("No data to copy");
                return;
            }

            DataRowView selected = selectedOnly ? DataGrid_SearchResults.SelectedItem as DataRowView : null;
            if (selectedOnly && selected == null)
            {
                TextBlock_ResultCount.Text = LocalizationManager.T("Select a result to copy first.");
                return;
            }

            try
            {
                string text = QuickSearchEngine.BuildClipboardText(lastResultTable, selected, true);
                if (!TrySetClipboardText(text))
                {
                    TextBlock_ResultCount.Text = LocalizationManager.Format("Copy failed: {0}", "clipboard unavailable");
                    return;
                }

                int copied = selectedOnly ? 1 : lastResultTable.Rows.Count;
                TextBlock_ResultCount.Text = LocalizationManager.Format("Copied {0} result(s) to the clipboard", copied);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Quick Search", "Copy results failed", ex);
                TextBlock_ResultCount.Text = LocalizationManager.Format("Copy failed: {0}", ex.Message);
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (Exception)
                {
                    // The clipboard is a shared COM resource and can be busy; retry briefly.
                    Thread.Sleep(60);
                }
            }

            return false;
        }

        private void Button_ScriptResult_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(sender is Button button) || !(button.DataContext is DataRowView rowView))
            {
                return;
            }

            try
            { 
                string databaseName = rowView["ScriptDatabaseName"]?.ToString();
                string schemaName = rowView["ScriptSchemaName"]?.ToString();
                string objectName = rowView["ScriptObjectName"]?.ToString();

                string matchLocation = rowView["MatchLocation"]?.ToString();

                if (matchLocation == "JobStep")
                {
                    string jobName = objectName ?? "SQL Agent job";
                    string commandText = rowView["SourceText"]?.ToString() ?? string.Empty;
                    string jobStepScript = "USE [msdb];" + Environment.NewLine + "GO" + Environment.NewLine
                        + "-- SQL Agent job step: " + jobName + Environment.NewLine + commandText;
                    var jobConnection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, jobConnection.ActiveConnectionInfo, null);
                    EnvDTE.TextDocument jobDocument = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                    jobDocument.EndPoint.CreateEditPoint().Insert(jobStepScript);
                    return;
                }

                string selectedObjectName = $"[{databaseName}].[{schemaName}].[{objectName}]";

                string fullScriptResult = ScriptObjectDefinition.GetText(MSSQLToolPackage.PackageInstance, selectedObjectName);

                var connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();

                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, connectionInfo.ActiveConnectionInfo, null);

                EnvDTE.TextDocument doc = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);

                doc.EndPoint.CreateEditPoint().Insert(fullScriptResult);
            }
            catch(Exception ex)
            {
                LocalizedMessageBox.Show($"Scripting failed: {ex.Message}", "Script Object");
            }           

        }

        #endregion

        /// <summary>
        /// One entry of the database picker. Selection changes are pushed to the registry, so the
        /// class raises change notifications for the two way check box binding. Internal because
        /// it wraps the internal <see cref="QuickSearchDatabaseInfo"/>; the XAML template only
        /// uses untyped bindings, so no compiled cross assembly reference is needed.
        /// </summary>
        internal sealed class DatabaseItem : INotifyPropertyChanged
        {
            private bool isSelected;

            public DatabaseItem(QuickSearchDatabaseInfo info, bool isSelected)
            {
                Info = info;
                this.isSelected = isSelected;
            }

            public QuickSearchDatabaseInfo Info { get; }

            public string Name => Info.Name;

            public bool CanBeSearched => Info.CanBeSearched;

            public string DisplayName => Info.Name + Info.Annotation;

            public bool IsSelected
            {
                get => isSelected;
                set
                {
                    if (isSelected == value) return;
                    isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
