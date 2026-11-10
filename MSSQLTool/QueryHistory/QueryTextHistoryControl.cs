using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace MSSQLTool
{
    /// <summary>
    /// Query text history (SQL Prompt's "query history"): one row per query, the versions of the
    /// selected query on the right, filters for time, connection and source, and one-click restore
    /// into the current or a new query window.
    /// </summary>
    public sealed class QueryTextHistoryControl : UserControl
    {
        private const int GroupLimit = 20000;
        private const int VersionLimit = 500;

        private static readonly string[] RangeTexts =
        {
            "All time", "Today", "Last 7 days", "Last 30 days", "Last 90 days", "This year"
        };

        private readonly TextBox searchBox = new TextBox
        {
            Height = 26,
            MinWidth = 200,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        private readonly ComboBox rangeBox = CreateFilterBox(120);
        private readonly ComboBox serverBox = CreateFilterBox(170);
        private readonly ComboBox databaseBox = CreateFilterBox(150);
        private readonly ComboBox sourceBox = CreateFilterBox(140);

        private readonly ListView list = new ListView { Margin = new Thickness(0, 8, 0, 8) };
        private readonly ListView versionList = new ListView { Height = 150, Margin = new Thickness(0, 0, 0, 6) };

        private readonly TextBox preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6)
        };

        private readonly TextBlock status = new TextBlock
        {
            Opacity = 0.75,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        private readonly DispatcherTimer debounce;
        private bool rebuildingFilters;
        private List<QueryTextHistoryGroup> groups = new List<QueryTextHistoryGroup>();

        public QueryTextHistoryControl()
        {
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.Transparent;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(Label("Search:"));
            toolbar.Children.Add(searchBox);
            toolbar.Children.Add(Label("Time:"));
            toolbar.Children.Add(rangeBox);
            toolbar.Children.Add(Label("Server:"));
            toolbar.Children.Add(serverBox);
            toolbar.Children.Add(Label("Database:"));
            toolbar.Children.Add(databaseBox);
            toolbar.Children.Add(Label("Source:"));
            toolbar.Children.Add(sourceBox);
            toolbar.Children.Add(CreateButton("Refresh", Refresh));
            toolbar.Children.Add(CreateButton("Import SQL Prompt history...", ImportSqlPrompt));
            root.Children.Add(toolbar);

            BuildGroupColumns();
            BuildVersionColumns();

            var split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 280 });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });

            split.Children.Add(list);
            var splitter = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            Grid.SetColumn(splitter, 1);
            split.Children.Add(splitter);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Versions (select one to view)"),
                FontSize = 11,
                Opacity = 0.75,
                Margin = new Thickness(0, 0, 0, 4)
            });
            Grid.SetRow(versionList, 1);
            right.Children.Add(versionList);
            Grid.SetRow(preview, 2);
            right.Children.Add(preview);

            Grid.SetColumn(right, 2);
            split.Children.Add(right);

            Grid.SetRow(split, 1);
            root.Children.Add(split);

            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(CreateButton("Insert into current query", InsertIntoCurrent));
            buttons.Children.Add(CreateButton("Open in a new query window", OpenInNewQuery));
            buttons.Children.Add(CreateButton("Copy", CopySelected));
            buttons.Children.Add(CreateButton("Delete version", DeleteSelectedVersion));
            buttons.Children.Add(CreateButton("Delete query", DeleteSelectedQuery));

            var bottom = new StackPanel();
            bottom.Children.Add(buttons);
            bottom.Children.Add(status);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = root;

            list.SelectionChanged += (_, __) => LoadVersions();
            list.MouseDoubleClick += (_, __) => InsertIntoCurrent();
            versionList.SelectionChanged += (_, __) => ShowPreview();
            versionList.MouseDoubleClick += (_, __) => InsertIntoCurrent();

            rangeBox.Items.Clear();
            for (int i = 0; i < RangeTexts.Length; i++)
                AddItem(rangeBox, LocalizationManager.T(RangeTexts[i]), i);
            rangeBox.SelectedIndex = 0;

            AddItem(sourceBox, LocalizationManager.T("All sources"), string.Empty);
            AddItem(sourceBox, LocalizationManager.T("Editor snapshots"), "editor");
            AddItem(sourceBox, LocalizationManager.T("SQL Prompt imports"), "SQL Prompt");
            sourceBox.SelectedIndex = 0;

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounce.Tick += (_, __) =>
            {
                debounce.Stop();
                Refresh();
            };

            searchBox.TextChanged += (_, __) => QueueRefresh();
            rangeBox.SelectionChanged += (_, __) => QueueRefresh();
            serverBox.SelectionChanged += (_, __) => QueueRefresh();
            databaseBox.SelectionChanged += (_, __) => QueueRefresh();
            sourceBox.SelectionChanged += (_, __) => QueueRefresh();

            Loaded += (_, __) =>
            {
                QueryTextCaptureService.CaptureActive();
                RefreshFacets();
                Refresh();
            };
        }

        private static ComboBox CreateFilterBox(double width)
        {
            return new ComboBox
            {
                Height = 26,
                Width = width,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = LocalizationManager.T(text),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
        }

        private static void AddItem(ComboBox box, string text, object tag)
        {
            box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
        }

        private static string TagOf(ComboBox box)
        {
            var item = box.SelectedItem as ComboBoxItem;
            return item?.Tag as string ?? string.Empty;
        }

        private Button CreateButton(string text, Action action)
        {
            var button = new Button
            {
                Content = LocalizationManager.T(text),
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 0)
            };
            button.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Query text history action failed.");
                    ShowStatus(LocalizationManager.Format("The action failed: {0}", ex.Message));
                }
            };
            return button;
        }

        private void BuildGroupColumns()
        {
            var view = new GridView();
            view.Columns.Add(Column("Last seen", 132, "LastSeenAt", "yyyy-MM-dd HH:mm"));
            view.Columns.Add(Column("Title", 170, "DisplayTitle", null));
            view.Columns.Add(Column("Type", 90, "KindText", null));
            view.Columns.Add(Column("Connection", 150, "ConnectionText", null));
            view.Columns.Add(Column("Versions", 60, "VersionText", null));
            view.Columns.Add(Column("Preview", 300, "Preview", null));
            list.View = view;
        }

        private void BuildVersionColumns()
        {
            var view = new GridView();
            view.Columns.Add(Column("Last seen", 130, "LastSeenAt", "yyyy-MM-dd HH:mm:ss"));
            view.Columns.Add(Column("Source", 150, "Source", null));
            view.Columns.Add(Column("Preview", 260, "Preview", null));
            versionList.View = view;
        }

        private static GridViewColumn Column(string header, double width, string path, string format)
        {
            var binding = new Binding(path);
            if (!string.IsNullOrEmpty(format)) binding.StringFormat = format;
            return new GridViewColumn
            {
                Header = LocalizationManager.T(header),
                Width = width,
                DisplayMemberBinding = binding
            };
        }

        private void QueueRefresh()
        {
            if (rebuildingFilters) return;
            debounce.Stop();
            debounce.Start();
        }

        private QueryTextHistoryFilter CurrentFilter()
        {
            var filter = new QueryTextHistoryFilter
            {
                Text = searchBox.Text,
                Server = TagOf(serverBox),
                Database = TagOf(databaseBox),
                Source = TagOf(sourceBox)
            };

            DateTime now = DateTime.Now;
            switch (rangeBox.SelectedIndex)
            {
                case 1: filter.From = DateTime.Today; break;
                case 2: filter.From = now.AddDays(-7); break;
                case 3: filter.From = now.AddDays(-30); break;
                case 4: filter.From = now.AddDays(-90); break;
                case 5: filter.From = new DateTime(now.Year, 1, 1); break;
            }

            return filter;
        }

        /// <summary>Rebuilds the server/database pickers without losing the current selection.</summary>
        private void RefreshFacets()
        {
            QueryTextHistoryStore.GetFacets(out List<string> servers, out List<string> databases, out _);
            FillFacetBox(serverBox, "All servers", servers);
            FillFacetBox(databaseBox, "All databases", databases);
        }

        private void FillFacetBox(ComboBox box, string allText, List<string> values)
        {
            string selected = TagOf(box);

            rebuildingFilters = true;
            try
            {
                box.Items.Clear();
                AddItem(box, LocalizationManager.T(allText), string.Empty);
                foreach (string value in values) AddItem(box, value, value);

                box.SelectedIndex = 0;
                if (string.IsNullOrEmpty(selected)) return;

                foreach (ComboBoxItem item in box.Items)
                {
                    if (string.Equals(item.Tag as string, selected, StringComparison.Ordinal))
                    {
                        box.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                rebuildingFilters = false;
            }
        }

        public void Refresh()
        {
            QueryTextHistoryFilter filter = CurrentFilter();
            groups = QueryTextHistoryStore.SearchGroups(filter, GroupLimit, out string error);

            list.ItemsSource = null;
            list.ItemsSource = groups;
            if (groups.Count > 0) list.SelectedIndex = 0;
            else
            {
                versionList.ItemsSource = null;
                preview.Text = string.Empty;
            }

            if (!string.IsNullOrEmpty(error))
            {
                ShowStatus(LocalizationManager.Format("The history could not be read: {0}", error));
                return;
            }

            int matchingQueries = QueryTextHistoryStore.CountGroups(filter, out _);
            int totalVersions = QueryTextHistoryStore.Count(out _);
            int shownVersions = 0;
            foreach (QueryTextHistoryGroup group in groups) shownVersions += group.Versions;

            ShowStatus(filter.IsEmpty
                ? LocalizationManager.Format("{0} queries, {1} versions stored.", matchingQueries, totalVersions)
                : LocalizationManager.Format("Matched {0} queries / {1} versions of {2} stored versions.",
                    matchingQueries, shownVersions, totalVersions));
        }

        private void LoadVersions()
        {
            var group = list.SelectedItem as QueryTextHistoryGroup;
            if (group == null)
            {
                versionList.ItemsSource = null;
                preview.Text = string.Empty;
                return;
            }

            List<QueryTextHistoryEntry> versions = QueryTextHistoryStore.SearchVersions(group.GroupKey, VersionLimit, out string error);
            versionList.ItemsSource = null;
            versionList.ItemsSource = versions;
            versionList.SelectedIndex = versions.Count > 0 ? 0 : -1;
            ShowPreview();

            if (!string.IsNullOrEmpty(error))
                ShowStatus(LocalizationManager.Format("The versions could not be read: {0}", error));
        }

        private void ShowPreview()
        {
            QueryTextHistoryEntry entry = SelectedEntry();
            preview.Text = entry?.QueryText ?? string.Empty;
            preview.CaretIndex = 0;
        }

        private void ShowStatus(string message) => status.Text = message ?? string.Empty;

        /// <summary>The version picked on the right, falling back to the query's newest text.</summary>
        private QueryTextHistoryEntry SelectedEntry()
        {
            if (versionList.SelectedItem is QueryTextHistoryEntry entry) return entry;
            var group = list.SelectedItem as QueryTextHistoryGroup;
            return group?.Latest;
        }

        private void InsertIntoCurrent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            QueryTextHistoryEntry entry = SelectedEntry();
            if (entry == null || string.IsNullOrWhiteSpace(entry.QueryText)) return;

            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ActiveDocument == null)
            {
                ShowStatus(LocalizationManager.T("Open a query window first."));
                return;
            }

            var selection = dte.ActiveDocument.Selection as TextSelection;
            if (selection == null) return;
            if (!selection.IsEmpty) selection.Collapse();
            selection.Insert(entry.QueryText);
            ShowStatus(LocalizationManager.T("Inserted into the current query window."));
        }

        private void OpenInNewQuery()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            QueryTextHistoryEntry entry = SelectedEntry();
            if (entry == null || string.IsNullOrWhiteSpace(entry.QueryText)) return;

            try
            {
                var current = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (current != null && current.ActiveConnectionInfo != null)
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, current.ActiveConnectionInfo, null);
                else
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);

                var document = (TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                document.EndPoint.CreateEditPoint().Insert(entry.QueryText);
                ShowStatus(LocalizationManager.T("Opened in a new query window."));
            }
            catch (Exception ex)
            {
                ShowStatus(LocalizationManager.Format("Could not open a new query window: {0}", ex.Message));
            }
        }

        private void CopySelected()
        {
            QueryTextHistoryEntry entry = SelectedEntry();
            if (entry != null && !string.IsNullOrEmpty(entry.QueryText))
            {
                Clipboard.SetText(entry.QueryText);
                ShowStatus(LocalizationManager.T("Copied."));
            }
        }

        /// <summary>Deletes the version under the cursor; the query itself stays as long as it has others.</summary>
        private void DeleteSelectedVersion()
        {
            QueryTextHistoryEntry entry = versionList.SelectedItem as QueryTextHistoryEntry;
            if (entry == null) return;

            MessageBoxResult answer = LocalizedMessageBox.Show(
                LocalizationManager.T("Delete this version from the history?"),
                "Query History",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            if (!QueryTextHistoryStore.Delete(entry.Id, out string error) && !string.IsNullOrEmpty(error))
            {
                ShowStatus(LocalizationManager.Format("The entry could not be deleted: {0}", error));
                return;
            }

            Refresh();
        }

        private void DeleteSelectedQuery()
        {
            var group = list.SelectedItem as QueryTextHistoryGroup;
            if (group == null) return;

            MessageBoxResult answer = LocalizedMessageBox.Show(
                LocalizationManager.Format("Delete all {0} versions of this query?", group.Versions),
                "Query History",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            if (!QueryTextHistoryStore.DeleteGroup(group.GroupKey, out string error) && !string.IsNullOrEmpty(error))
            {
                ShowStatus(LocalizationManager.Format("The query could not be deleted: {0}", error));
                return;
            }

            Refresh();
        }

        private void ImportSqlPrompt()
        {
            var dialog = new OpenFileDialog
            {
                Title = LocalizationManager.T("Select the SQL Prompt history database"),
                Filter = LocalizationManager.T("SQLite database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*"),
                CheckFileExists = true
            };

            try
            {
                string redGate = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Red Gate");
                if (Directory.Exists(redGate)) dialog.InitialDirectory = redGate;
            }
            catch (Exception) { }

            if (dialog.ShowDialog() != true) return;

            string file = dialog.FileName;
            var window = new ImportProgressWindow
            {
                Owner = System.Windows.Window.GetWindow(this)
            };

            window.Start(
                (progress, token) =>
                {
                    bool success = QueryTextHistoryStore.ImportSqlPrompt(
                        file, progress, token, out int imported, out int skipped, out string importError);

                    return new QueryTextImportOutcome
                    {
                        Success = success,
                        Cancelled = token.IsCancellationRequested,
                        Imported = imported,
                        Skipped = skipped,
                        Error = importError ?? string.Empty
                    };
                },
                outcome =>
                {
                    RefreshFacets();
                    Refresh();
                    if (outcome == null) return;

                    if (outcome.Cancelled)
                    {
                        ShowStatus(LocalizationManager.T("The import was cancelled."));
                        return;
                    }

                    if (!outcome.Success)
                    {
                        ShowStatus(LocalizationManager.Format("The import failed: {0}", outcome.Error));
                        return;
                    }

                    ShowStatus(LocalizationManager.Format("Imported {0} query text(s); {1} already existed.",
                        outcome.Imported, outcome.Skipped));
                });

            window.ShowDialog();
        }
    }

    /// <summary>The dockable tool window that hosts the query text history.</summary>
    [Guid("cd3ffedf-c3ae-4193-939b-aec4d734b756")]
    public sealed class QueryTextHistoryWindow : ToolWindowPane
    {
        public QueryTextHistoryWindow() : base(null)
        {
            Caption = LocalizationManager.T("Query History");
            Content = new QueryTextHistoryControl();
        }
    }
}
