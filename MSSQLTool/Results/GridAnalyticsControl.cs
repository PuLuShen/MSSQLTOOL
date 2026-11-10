using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Standalone results panel: shows the real result set, lets the user sort, filter per column,
    /// show/hide and freeze columns, inspect a full cell value, read per-column statistics, switch
    /// between multiple result sets and export as CSV, JSON or INSERT statements.
    /// </summary>
    public sealed class GridAnalyticsControl : UserControl, IDisposable
    {
        private const int ScanCap = 50000;
        private const int DisplayCap = 2000;

        private readonly TextBlock summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            FontWeight = FontWeights.SemiBold
        };
        private readonly TextBlock status = new TextBlock
        {
            Opacity = 0.75,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        private readonly TextBox search = new TextBox
        {
            Width = 180,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = LocalizationManager.T("Filter every column (Ctrl+F)")
        };
        private readonly ComboBox resultSets = new ComboBox
        {
            Width = 190,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = LocalizationManager.T("Result set")
        };
        private readonly ComboBox filterColumns = new ComboBox { Width = 150, Height = 26, Margin = new Thickness(0, 0, 4, 0) };
        private readonly ComboBox filterOperators = new ComboBox { Width = 110, Height = 26, Margin = new Thickness(0, 0, 4, 0) };
        private readonly TextBox filterValue = new TextBox
        {
            Width = 140,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        private readonly DataGrid dataGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            SelectionUnit = DataGridSelectionUnit.Cell,
            SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.All,
            RowHeaderWidth = 52,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Margin = new Thickness(0, 0, 0, 6)
        };
        private readonly DataGrid statsGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            RowHeight = 22,
            ColumnHeaderHeight = 26,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        private readonly TextBox statsFilter = new TextBox
        {
            Width = 170,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = LocalizationManager.T("Filter columns by name or type")
        };
        private readonly TextBlock selectionSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Opacity = 0.85
        };
        private bool refreshing;
        private bool updatingStats;
        private DateTime lastRefreshUtc;
        private readonly DispatcherTimer applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private CancellationTokenSource analysisCts;
        private ICollectionView statsView;
        private string viewNote = string.Empty;
        private string statsNote = string.Empty;
        private readonly Dictionary<DataTable, GridAnalyticsResult> statsCache = new Dictionary<DataTable, GridAnalyticsResult>();
        private readonly List<DataTable> statsCacheOrder = new List<DataTable>();

        private List<DataTable> tables = new List<DataTable>();
        private DataTable source;
        private DataTable displayed;
        private GridAnalyticsResult analytics;
        private readonly List<SortKey> sorts = new List<SortKey>();
        private readonly List<int> hiddenOrdinals = new List<int>();
        private int frozenColumns;
        private bool suppressResultSetChange;

        private static readonly string[] OperatorKeys =
        {
            "Contains", "Equals", "Not equals", "Starts with", "Ends with", "Greater than", "Less than"
        };

        public GridAnalyticsControl()
        {
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.Transparent;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star), MinHeight = 130 });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(summary);

            var filterBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            filterBar.Children.Add(resultSets);
            filterBar.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Filter rows:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            filterBar.Children.Add(search);
            filterBar.Children.Add(filterColumns);
            filterBar.Children.Add(filterOperators);
            filterBar.Children.Add(filterValue);
            filterBar.Children.Add(CreateButton("Apply", ApplyView));
            filterBar.Children.Add(CreateButton("Clear", ClearFilters));
            Grid.SetRow(filterBar, 1);
            root.Children.Add(filterBar);

            var actionBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            actionBar.Children.Add(CreateButton("Refresh", Refresh));
            actionBar.Children.Add(CreateButton("Columns...", ShowColumnMenu));
            actionBar.Children.Add(CreateButton("Export CSV...", ExportCsv));
            actionBar.Children.Add(CreateButton("Export JSON...", ExportJson));
            actionBar.Children.Add(CreateButton("Export INSERT...", ExportInsert));
            actionBar.Children.Add(CreateButton("Copy statistics", CopyStatistics));
            Grid.SetRow(actionBar, 2);
            root.Children.Add(actionBar);

            Grid.SetRow(dataGrid, 3);
            root.Children.Add(dataGrid);

            var statsHost = new Grid();
            statsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            statsHost.Children.Add(selectionSummary);
            var statsHeader = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2) };
            var statsTitle = new TextBlock
            {
                Text = LocalizationManager.T("Column statistics"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var statsHint = new TextBlock
            {
                Text = LocalizationManager.T("Click a row to jump to that column"),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(statsTitle, Dock.Left);
            DockPanel.SetDock(statsHint, Dock.Left);
            DockPanel.SetDock(statsFilter, Dock.Right);
            statsHeader.Children.Add(statsFilter);
            statsHeader.Children.Add(statsTitle);
            statsHeader.Children.Add(statsHint);
            Grid.SetRow(statsHeader, 1);
            statsHost.Children.Add(statsHeader);
            Grid.SetRow(statsGrid, 2);
            statsHost.Children.Add(statsGrid);
            Grid.SetRow(statsHost, 4);
            root.Children.Add(statsHost);

            Grid.SetRow(status, 5);
            root.Children.Add(status);

            Content = root;

            BuildStatsColumns();
            BuildFilterControls();

            search.TextChanged += (_, __) => ScheduleApplyView();
            filterValue.TextChanged += (_, __) => ScheduleApplyView();
            filterColumns.SelectionChanged += (_, __) => ApplyView();
            filterOperators.SelectionChanged += (_, __) => ApplyView();
            applyTimer.Tick += (_, __) => { applyTimer.Stop(); ApplyView(); };
            resultSets.SelectionChanged += (_, __) => OnResultSetChanged();
            dataGrid.Sorting += OnSorting;
            dataGrid.SelectedCellsChanged += (_, __) => UpdateSelectionStatistics();
            dataGrid.MouseDoubleClick += OnCellDoubleClick;
            dataGrid.LoadingRow += (_, e) => { e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture); };
            statsGrid.MouseDoubleClick += OnStatsDoubleClick;
            statsGrid.SelectionChanged += OnStatsSelectionChanged;
            dataGrid.CurrentCellChanged += OnCurrentCellChanged;
            statsFilter.TextChanged += (_, __) => ApplyStatsFilter();
            statsFilter.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    statsFilter.Text = string.Empty;
                    e.Handled = true;
                }
            };
            PreviewKeyDown += OnPreviewKeyDown;

            Loaded += (_, __) => Refresh();
            IsVisibleChanged += (_, __) => { if (IsVisible) Refresh(); };
        }

        private static string T(string text) => LocalizationManager.T(text);

        private Button CreateButton(string text, Action action)
        {
            var button = new Button
            {
                Content = T(text),
                Height = 24,
                FontSize = 12,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 0, 4, 2)
            };
            button.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Results analytics action failed.");
                    status.Text = LocalizationManager.Format("The action failed: {0}", ex.Message);
                }
            };
            return button;
        }

        private void BuildFilterControls()
        {
            filterColumns.Items.Add(T("All columns"));
            filterColumns.SelectedIndex = 0;

            foreach (string key in OperatorKeys) filterOperators.Items.Add(T(key));
            filterOperators.SelectedIndex = 0;

            filterValue.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { ApplyView(); e.Handled = true; }
            };
        }

        /// <summary>
        /// Statistics columns as data, so a regression test can prove every binding path still
        /// resolves to a readable property (the table used to render blank because the values were
        /// fields, which WPF bindings cannot see).
        /// </summary>
        internal static readonly StatsColumnDefinition[] StatsColumns =
        {
            new StatsColumnDefinition("Column", "Name", 170, false),
            new StatsColumnDefinition("Type", "DataType", 72, true),
            new StatsColumnDefinition("Non-null", "NonNull", 62, true),
            new StatsColumnDefinition("Nulls", "NullCount", 56, true),
            new StatsColumnDefinition("Null %", "NullPercentText", 58, true),
            new StatsColumnDefinition("Distinct", "Distinct", 62, true),
            new StatsColumnDefinition("Max length", "MaxLength", 76, true),
            new StatsColumnDefinition("Min", "Minimum", 120, true),
            new StatsColumnDefinition("Max", "Maximum", 120, true),
            new StatsColumnDefinition("Average", "Average", 86, true),
            new StatsColumnDefinition("Sum", "Sum", 96, true),
            new StatsColumnDefinition("Most common", "MostCommonText", 190, false)
        };

        internal sealed class StatsColumnDefinition
        {
            public StatsColumnDefinition(string header, string path, double width, bool rightAlign)
            {
                Header = header;
                Path = path;
                Width = width;
                RightAlign = rightAlign;
            }

            public string Header { get; }
            public string Path { get; }
            public double Width { get; }
            public bool RightAlign { get; }
        }

        private void BuildStatsColumns()
        {
            foreach (StatsColumnDefinition definition in StatsColumns)
                statsGrid.Columns.Add(TextColumn(definition.Header, definition.Path, definition.Width, definition.RightAlign));
        }

        private static DataGridTextColumn TextColumn(string header, string path, double width, bool rightAlign = false)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)));
            if (rightAlign) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));

            return new DataGridTextColumn
            {
                Header = T(header),
                Binding = new Binding(path) { Mode = BindingMode.OneWay },
                ElementStyle = style,
                Width = new DataGridLength(width)
            };
        }

        /// <summary>Reads the current results grids and shows the first (or the previously chosen) one.</summary>
        public void Refresh()
        {
            if (refreshing) return;
            if ((DateTime.UtcNow - lastRefreshUtc).TotalMilliseconds < 400) return;
            lastRefreshUtc = DateTime.UtcNow;
            refreshing = true;

            try
            {
                int previous = Math.Max(0, resultSets.SelectedIndex);
                tables = GridAccess.GetDataTables() ?? new List<DataTable>();
                tables = tables.Where(table => table != null && table.Columns.Count > 0).ToList();

                suppressResultSetChange = true;
                try
                {
                    resultSets.Items.Clear();
                    for (int index = 0; index < tables.Count; index++)
                    {
                        DataTable table = tables[index];
                        resultSets.Items.Add(LocalizationManager.Format(
                            "Result {0}: {1} row(s) x {2} column(s)", index + 1, table.Rows.Count, table.Columns.Count));
                    }
                    resultSets.Visibility = tables.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                    if (tables.Count > 0) resultSets.SelectedIndex = Math.Min(previous, tables.Count - 1);
                }
                finally
                {
                    suppressResultSetChange = false;
                }

                LoadSelectedResult();

                if (source == null)
                {
                    analytics = new GridAnalyticsResult();
                    displayed = null;
                    dataGrid.ItemsSource = null;
                    dataGrid.Columns.Clear();
                    statsGrid.ItemsSource = null;
                    statsView = null;
                    viewNote = string.Empty;
                    statsNote = string.Empty;
                    summary.Text = T("No results grid with data is available.");
                    UpdateStatus();
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not analyze the results grid.");
                status.Text = LocalizationManager.Format("Could not analyze the results grid: {0}", ex.Message);
            }
            finally
            {
                refreshing = false;
            }
        }

        private void OnResultSetChanged()
        {
            if (suppressResultSetChange) return;
            LoadSelectedResult();
        }

        private void LoadSelectedResult()
        {
            int index = resultSets.SelectedIndex;
            if (index < 0 || index >= tables.Count) index = 0;
            source = tables.Count > 0 ? tables[index] : null;
            if (source == null)
            {
                CancelAnalysis();
                analytics = new GridAnalyticsResult();
                displayed = null;
                dataGrid.ItemsSource = null;
                dataGrid.Columns.Clear();
                statsGrid.ItemsSource = null;
                statsView = null;
                selectionSummary.Text = string.Empty;
                viewNote = string.Empty;
                statsNote = string.Empty;
                UpdateStatus();
                return;
            }

            CancelAnalysis();
            hiddenOrdinals.Clear();
            sorts.Clear();
            frozenColumns = 0;
            RebuildColumnFilter();
            selectionSummary.Text = T("Select cells to see their statistics.");

            // Paint the rows first.  The statistics pass used to run before the grid was bound, so a
            // wide result set showed an empty panel until the numbers were done.
            ApplyView();
            AnalyzeAsync(source);
        }

        /// <summary>
        /// The statistics pass is the expensive part on a wide result set, so it runs on a worker
        /// thread while the grid already shows its rows.  A newer request cancels the old scan, and
        /// a repeated look at the same DataTable is served from the small cache instead of rescanning.
        /// </summary>
        private void AnalyzeAsync(DataTable table)
        {
            CancelAnalysis();
            var cts = new CancellationTokenSource();
            analysisCts = cts;

            GridAnalyticsResult cached;
            if (statsCache.TryGetValue(table, out cached) && cached != null &&
                cached.RowCount == table.Rows.Count && cached.ColumnCount == table.Columns.Count)
            {
                ApplyStatistics(cached);
                return;
            }

            statsNote = T("Analyzing the result set...");
            UpdateStatus();
            statsView = null;
            statsGrid.ItemsSource = null;

            Task.Run(() => GridAnalytics.Analyze(table, GridAnalytics.DefaultRowCap, cts.Token), cts.Token)
                .ContinueWith(task =>
                {
                    if (cts.IsCancellationRequested) return;

                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (cts.IsCancellationRequested || !ReferenceEquals(analysisCts, cts)) return;

                            if (task.IsFaulted)
                            {
                                statsNote = LocalizationManager.Format("Could not analyze the results grid: {0}",
                                    task.Exception == null ? string.Empty : task.Exception.GetBaseException().Message);
                                UpdateStatus();
                                return;
                            }

                            if (task.IsCanceled) return;

                            RememberStatistics(table, task.Result);
                            ApplyStatistics(task.Result);
                        }));
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Debug(ex, "Could not dispatch the results analysis.");
                    }
                }, TaskContinuationOptions.None);
        }

        private void CancelAnalysis()
        {
            CancellationTokenSource previous = analysisCts;
            analysisCts = null;
            if (previous == null) return;

            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void RememberStatistics(DataTable table, GridAnalyticsResult result)
        {
            statsCache[table] = result;
            statsCacheOrder.Remove(table);
            statsCacheOrder.Add(table);

            while (statsCacheOrder.Count > 3)
            {
                DataTable oldest = statsCacheOrder[0];
                statsCacheOrder.RemoveAt(0);
                statsCache.Remove(oldest);
            }
        }

        private void ApplyStatistics(GridAnalyticsResult result)
        {
            analytics = result;

            updatingStats = true;
            try
            {
                statsView = new ListCollectionView(result.Columns);
                statsView.Filter = StatsFilterPredicate;
                statsGrid.ItemsSource = statsView;
            }
            finally
            {
                updatingStats = false;
            }

            summary.Text = LocalizationManager.Format(
                "{0} row(s), {1} column(s) | empty cells {2}% | duplicate rows {3}",
                result.RowCount, result.ColumnCount, result.NullCellPercent, result.DuplicateRows);

            statsNote = result.Truncated
                ? LocalizationManager.Format("Statistics cover a sample of the first {0} of {1} row(s).", result.SampledRows, result.RowCount)
                : T("Statistics cover every row in the grid.");
            UpdateStatus();

            ApplyStatsFilter();
        }

        private bool StatsFilterPredicate(object item)
        {
            GridColumnInsight insight = item as GridColumnInsight;
            if (insight == null) return false;

            string needle = (statsFilter.Text ?? string.Empty).Trim();
            if (needle.Length == 0) return true;

            return insight.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   insight.DataType.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   Convert.ToString(insight.Ordinal, CultureInfo.CurrentCulture) == needle;
        }

        private void ApplyStatsFilter()
        {
            if (statsView == null) return;
            try { statsView.Refresh(); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not filter the column statistics."); }
        }

        private void OnStatsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingStats) return;
            FocusGridColumn(statsGrid.SelectedItem as GridColumnInsight);
        }

        /// <summary>Scrolls the data grid so the column behind a statistics row is on screen.</summary>
        private void FocusGridColumn(GridColumnInsight insight)
        {
            if (insight == null || displayed == null) return;

            int ordinal = insight.Ordinal - 1;
            if (ordinal < 0 || ordinal >= displayed.Columns.Count) return;

            string name = displayed.Columns[ordinal].ColumnName;
            DataGridColumn column = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, name, StringComparison.Ordinal));
            if (column == null)
            {
                viewNote = LocalizationManager.Format("Column {0} is hidden; show it from Columns... to see it.", insight.Name);
                UpdateStatus();
                return;
            }

            if (dataGrid.Items.Count == 0) return;
            try { dataGrid.ScrollIntoView(dataGrid.Items[0], column); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not scroll to the selected column."); }
        }

        /// <summary>Selecting a cell in the grid highlights the matching statistics row.</summary>
        private void OnCurrentCellChanged(object sender, EventArgs e)
        {
            if (updatingStats || analytics == null || displayed == null) return;
            if (dataGrid.SelectedCells.Count != 1 || dataGrid.CurrentCell.Column == null) return;

            string name = Convert.ToString(dataGrid.CurrentCell.Column.SortMemberPath);
            if (string.IsNullOrEmpty(name)) return;

            int ordinal = displayed.Columns.Cast<DataColumn>().ToList()
                .FindIndex(column => string.Equals(column.ColumnName, name, StringComparison.Ordinal));
            if (ordinal < 0) return;

            GridColumnInsight insight = analytics.Columns.FirstOrDefault(item => item.Ordinal == ordinal + 1);
            if (insight == null) return;

            updatingStats = true;
            try
            {
                statsGrid.SelectedItem = insight;
                statsGrid.ScrollIntoView(insight);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not select the statistics row.");
            }
            finally
            {
                updatingStats = false;
            }
        }

        private void UpdateStatus()
        {
            if (string.IsNullOrEmpty(viewNote)) status.Text = statsNote;
            else if (string.IsNullOrEmpty(statsNote)) status.Text = viewNote;
            else status.Text = viewNote + "  " + statsNote;
        }

        private void RebuildColumnFilter()
        {
            object previous = filterColumns.SelectedItem;
            filterColumns.Items.Clear();
            filterColumns.Items.Add(T("All columns"));
            if (source != null)
            {
                foreach (DataColumn column in source.Columns)
                    filterColumns.Items.Add(GridAnalytics.DisplayName(column));
            }

            int index = previous == null ? 0 : filterColumns.Items.IndexOf(previous);
            filterColumns.SelectedIndex = index < 0 ? 0 : index;
        }

        private void ApplyView()
        {
            if (source == null) return;

            string needle = (search.Text ?? string.Empty).Trim();
            int filterColumn = filterColumns.SelectedIndex - 1;
            string opKey = filterColumn >= 0 && filterOperators.SelectedIndex >= 0
                ? OperatorKeys[filterOperators.SelectedIndex] : "Contains";
            string operand = (filterValue.Text ?? string.Empty).Trim();

            bool hasColumnFilter = filterColumn >= 0 && filterColumn < source.Columns.Count &&
                                   (operand.Length > 0 || opKey == "Equals" || opKey == "Not equals");

            var rows = new List<DataRow>();
            int scanned = 0;
            try
            {
                foreach (DataRow row in source.Rows)
                {
                    if (scanned >= ScanCap || rows.Count >= DisplayCap) break;
                    scanned++;

                    if (needle.Length > 0 && !RowContains(row, needle)) continue;
                    if (hasColumnFilter && !Matches(row[filterColumn], opKey, operand)) continue;
                    rows.Add(row);
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Filtering the results grid failed.");
                return;
            }

            bool capped = rows.Count >= DisplayCap;
            var filtered = source.Clone();
            foreach (DataRow row in rows) filtered.ImportRow(row);
            displayed = filtered;

            BuildDataColumns();

            var view = new DataView(displayed);
            string sort = BuildSortExpression();
            if (sort.Length > 0) view.Sort = sort;
            dataGrid.ItemsSource = view;

            if (needle.Length > 0 || hasColumnFilter)
            {
                viewNote = capped
                    ? LocalizationManager.Format("{0} matching row(s), showing the first {1}.", rows.Count, DisplayCap)
                    : LocalizationManager.Format("{0} matching row(s).", rows.Count);
            }
            else if (capped)
            {
                viewNote = LocalizationManager.Format("Showing the first {0} rows.", DisplayCap);
            }
            else
            {
                viewNote = string.Empty;
            }

            // The numbers describe the whole result set: say so when the rows on screen are filtered.
            if ((needle.Length > 0 || hasColumnFilter) && analytics != null && analytics.Columns.Count > 0)
            {
                viewNote = string.IsNullOrEmpty(viewNote)
                    ? T("Statistics are for all rows, not only the filtered ones.")
                    : viewNote + "  " + T("Statistics are for all rows, not only the filtered ones.");
            }

            UpdateStatus();
            UpdateSortArrows();
        }

        private string BuildSortExpression()
        {
            var parts = new List<string>();
            foreach (SortKey key in sorts)
            {
                if (key == null || string.IsNullOrEmpty(key.Column)) continue;
                parts.Add("[" + key.Column.Replace("]", "]]") + "] " + (key.Descending ? "DESC" : "ASC"));
            }

            return string.Join(", ", parts);
        }

        private void UpdateSortArrows()
        {
            foreach (DataGridColumn column in dataGrid.Columns)
            {
                SortKey key = sorts.FirstOrDefault(item => item.Column == Convert.ToString(column.SortMemberPath));
                column.SortDirection = key == null ? null : (System.ComponentModel.ListSortDirection?)
                    (key.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending);
            }
        }

        private void BuildDataColumns()
        {
            dataGrid.Columns.Clear();
            if (displayed == null) return;

            for (int index = 0; index < displayed.Columns.Count; index++)
            {
                if (hiddenOrdinals.Contains(index)) continue;

                DataColumn column = displayed.Columns[index];
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                text.SetValue(TextBlock.PaddingProperty, new Thickness(4, 0, 4, 0));
                text.SetBinding(TextBlock.TextProperty, new Binding(column.ColumnName) { Converter = CellTextConverter.Instance });
                text.SetBinding(TextBlock.ToolTipProperty, new Binding(column.ColumnName) { Converter = CellTextConverter.Instance });
                text.SetBinding(TextBlock.FontStyleProperty, new Binding(column.ColumnName) { Converter = CellTextConverter.Instance, ConverterParameter = "style" });
                text.SetBinding(TextBlock.ForegroundProperty, new Binding(column.ColumnName) { Converter = CellTextConverter.Instance, ConverterParameter = "brush" });

                var gridColumn = new DataGridTemplateColumn
                {
                    Header = BuildColumnHeader(column, index),
                    CellTemplate = new DataTemplate { VisualTree = text },
                    SortMemberPath = column.ColumnName,
                    CanUserResize = true,
                    Width = ColumnWidth(column)
                };

                dataGrid.Columns.Add(gridColumn);
            }

            dataGrid.FrozenColumnCount = Math.Min(frozenColumns, dataGrid.Columns.Count);
        }

        private static DataGridLength ColumnWidth(DataColumn column)
        {
            object stored;
            if (column.ExtendedProperties.Contains("columnWidthInPixels") &&
                (stored = column.ExtendedProperties["columnWidthInPixels"]) != null &&
                double.TryParse(Convert.ToString(stored, CultureInfo.InvariantCulture), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double width) && width >= 40 && width <= 900)
            {
                return new DataGridLength(width);
            }

            return new DataGridLength(150);
        }

        private object BuildColumnHeader(DataColumn column, int ordinal)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(new TextBlock
            {
                Text = GridAnalytics.DisplayName(column),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = GridAnalytics.DisplayName(column)
            });
            panel.Children.Add(new TextBlock
            {
                Text = GridAnalytics.FriendlyTypeName(column),
                FontSize = 10,
                Opacity = 0.65
            });

            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Sort ascending", () => SetSort(column.ColumnName, false)));
            menu.Items.Add(MenuItem("Sort descending", () => SetSort(column.ColumnName, true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Hide column", () => { hiddenOrdinals.Add(ordinal); ApplyView(); }));
            menu.Items.Add(MenuItem("Copy column name", () => Clipboard.SetText(GridAnalytics.DisplayName(column))));
            menu.Items.Add(MenuItem("Freeze up to this column", () =>
            {
                int visible = dataGrid.Columns.IndexOf(dataGrid.Columns.FirstOrDefault(item => item.SortMemberPath == column.ColumnName));
                frozenColumns = visible + 1;
                dataGrid.FrozenColumnCount = Math.Min(frozenColumns, dataGrid.Columns.Count);
            }));
            menu.Items.Add(MenuItem("Unfreeze columns", () => { frozenColumns = 0; dataGrid.FrozenColumnCount = 0; }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Top values", () => ShowTopValues(analytics?.Columns.FirstOrDefault(item => item.Name == GridAnalytics.DisplayName(column)))));
            panel.ContextMenu = menu;
            return panel;
        }

        private static MenuItem MenuItem(string text, Action action)
        {
            var item = new MenuItem { Header = T(text) };
            item.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Results panel command failed."); }
            };
            return item;
        }

        private void SetSort(string columnName, bool descending)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            SortKey existing = sorts.FirstOrDefault(item => item.Column == columnName);
            if (existing != null)
            {
                if (shift || sorts.Count == 1)
                {
                    existing.Descending = descending;
                }
                else
                {
                    sorts.Remove(existing);
                }
            }
            else
            {
                if (!shift) sorts.Clear();
                sorts.Add(new SortKey { Column = columnName, Descending = descending });
            }

            ApplyView();
        }

        private void OnSorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            string column = Convert.ToString(e.Column.SortMemberPath);
            if (string.IsNullOrEmpty(column)) return;

            SortKey key = sorts.FirstOrDefault(item => item.Column == column);
            bool descending = key == null ? false : !key.Descending;
            SetSort(column, descending);
        }

        private void ShowColumnMenu()
        {
            if (displayed == null) return;

            var menu = new ContextMenu();
            for (int index = 0; index < displayed.Columns.Count; index++)
            {
                int ordinal = index;
                DataColumn column = displayed.Columns[index];
                var item = new MenuItem
                {
                    Header = GridAnalytics.DisplayName(column),
                    IsCheckable = true,
                    IsChecked = !hiddenOrdinals.Contains(index)
                };
                item.Click += (_, __) =>
                {
                    if (hiddenOrdinals.Contains(ordinal)) hiddenOrdinals.Remove(ordinal);
                    else hiddenOrdinals.Add(ordinal);
                    ApplyView();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Show all columns", () => { hiddenOrdinals.Clear(); ApplyView(); }));
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        private void ClearFilters()
        {
            applyTimer.Stop();
            search.Text = string.Empty;
            filterValue.Text = string.Empty;
            if (filterColumns.Items.Count > 0) filterColumns.SelectedIndex = 0;
            if (filterOperators.Items.Count > 0) filterOperators.SelectedIndex = 0;
            ApplyView();
        }

        /// <summary>
        /// Typing in a filter box rebuilds the visible rows; waiting for a short pause keeps the
        /// keystrokes responsive on a wide result set instead of re-cloning rows for every character.
        /// </summary>
        private void ScheduleApplyView()
        {
            applyTimer.Stop();
            applyTimer.Start();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                search.Focus();
                search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearFilters();
                e.Handled = true;
            }
        }

        private void OnCellDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (dataGrid.SelectedCells.Count == 0) return;
                DataGridCellInfo cell = dataGrid.SelectedCells[0];
                if (cell.Item == null) return;

                DataRowView view = cell.Item as DataRowView;
                if (view == null) return;

                string columnName = Convert.ToString(cell.Column.SortMemberPath);
                DataColumn column = displayed.Columns.Cast<DataColumn>().FirstOrDefault(item => item.ColumnName == columnName);
                if (column == null) return;

                object value = view.Row[column];
                CellValueWindow.Show(this, GridAnalytics.DisplayName(column), GridAnalytics.FriendlyTypeName(column), value);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not show the cell value.");
            }
        }

        private void OnStatsDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ShowTopValues(statsGrid.SelectedItem as GridColumnInsight);
        }

        private void ShowTopValues(GridColumnInsight insight)
        {
            if (insight == null) return;
            if (insight.TopValues.Count == 0)
            {
                status.Text = LocalizationManager.Format("Column {0} has no values to list.", insight.Name);
                return;
            }

            var builder = new StringBuilder();
            int total = 0;
            foreach (KeyValuePair<string, int> pair in insight.TopValues) total += pair.Value;

            builder.AppendLine(LocalizationManager.Format("{0} distinct value(s), {1} non-empty cell(s) sampled",
                insight.Distinct, insight.NonNull));
            builder.AppendLine();
            foreach (KeyValuePair<string, int> pair in insight.TopValues)
            {
                double share = total == 0 ? 0 : pair.Value * 100.0 / total;
                builder.AppendLine(pair.Key + "\t" + pair.Value.ToString(CultureInfo.CurrentCulture) +
                                   "\t" + share.ToString("0.0", CultureInfo.CurrentCulture) + "%");
            }

            TextReportWindow.Show(this, LocalizationManager.Format("Top values: {0}", insight.Name), builder.ToString());
        }

        /// <summary>Summarises the current cell selection: counts, nulls, distinct, length and numeric totals.</summary>
        private void UpdateSelectionStatistics()
        {
            try
            {
                int cells = dataGrid.SelectedCells.Count;
                if (cells == 0)
                {
                    selectionSummary.Text = T("Select cells to see their statistics.");
                    return;
                }

                var rows = new HashSet<DataRowView>();
                var columns = new HashSet<string>(StringComparer.Ordinal);
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                int nulls = 0, nonNull = 0, maxLength = 0, numeric = 0;
                decimal sum = 0;
                decimal? min = null, max = null;

                foreach (DataGridCellInfo cell in dataGrid.SelectedCells)
                {
                    DataRowView view = cell.Item as DataRowView;
                    if (view == null) continue;

                    string name = Convert.ToString(cell.Column.SortMemberPath);
                    DataColumn column = displayed == null ? null : displayed.Columns.Cast<DataColumn>()
                        .FirstOrDefault(item => item.ColumnName == name);
                    if (column == null) continue;

                    rows.Add(view);
                    columns.Add(name);

                    object value = view.Row[column];
                    if (value == null || value == DBNull.Value)
                    {
                        nulls++;
                        continue;
                    }

                    nonNull++;
                    string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                    distinct.Add(text);
                    if (text.Length > maxLength) maxLength = text.Length;

                    if (TryNumber(value, text, out decimal number))
                    {
                        numeric++;
                        sum += number;
                        min = min.HasValue && min.Value <= number ? min : number;
                        max = max.HasValue && max.Value >= number ? max : number;
                    }
                }

                string numbers = numeric > 0
                    ? LocalizationManager.Format(
                        " | numeric {0}: sum {1}, average {2}, min {3}, max {4}", numeric,
                        sum.ToString("0.####", CultureInfo.CurrentCulture),
                        (sum / numeric).ToString("0.####", CultureInfo.CurrentCulture),
                        min.Value.ToString("0.####", CultureInfo.CurrentCulture),
                        max.Value.ToString("0.####", CultureInfo.CurrentCulture))
                    : string.Empty;

                selectionSummary.Text = LocalizationManager.Format(
                    "Selection: {0} cell(s), {1} row(s), {2} column(s), {3} non-null, {4} empty, {5} distinct, longest {6} character(s){7}",
                    cells, rows.Count, columns.Count, nonNull, nulls, distinct.Count, maxLength, numbers);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not summarize the selected cells.");
            }
        }

        private static bool TryNumber(object value, string text, out decimal number)
        {
            number = 0;
            if (value is bool) return false;
            if (IsNumericType(value.GetType()))
            {
                try { number = Convert.ToDecimal(value, CultureInfo.CurrentCulture); return true; }
                catch (Exception) { return false; }
            }

            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
        }

        private void CopyStatistics()
        {
            if (analytics == null) return;
            Clipboard.SetText(GridAnalytics.ToText(analytics));
            status.Text = T("Statistics copied to the clipboard.");
        }

        private void ExportCsv()
        {
            if (displayed == null) return;

            var dialog = new SaveFileDialog
            {
                Title = T("Export results to CSV"),
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            var builder = new StringBuilder();
            builder.AppendLine(string.Join(",", displayed.Columns.Cast<DataColumn>().Select(column => Csv(GridAnalytics.DisplayName(column)))));
            foreach (DataRow row in displayed.Rows)
            {
                builder.AppendLine(string.Join(",", row.ItemArray.Select(value =>
                    value == null || value == DBNull.Value ? string.Empty : Csv(Convert.ToString(value, CultureInfo.CurrentCulture)))));
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            status.Text = LocalizationManager.Format("Exported {0} row(s) to {1}.", displayed.Rows.Count, dialog.FileName);
        }

        private void ExportJson()
        {
            if (displayed == null) return;

            var dialog = new SaveFileDialog
            {
                Title = T("Export results to JSON"),
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = "results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            var names = displayed.Columns.Cast<DataColumn>().Select(GridAnalytics.DisplayName).ToList();
            var builder = new StringBuilder();
            builder.AppendLine("[");
            for (int index = 0; index < displayed.Rows.Count; index++)
            {
                DataRow row = displayed.Rows[index];
                builder.Append("  {");
                for (int columnIndex = 0; columnIndex < names.Count; columnIndex++)
                {
                    if (columnIndex > 0) builder.Append(", ");
                    builder.Append(Json(names[columnIndex])).Append(": ");
                    object value = row[columnIndex];
                    if (value == null || value == DBNull.Value) builder.Append("null");
                    else if (value is bool) builder.Append(((bool)value) ? "true" : "false");
                    else if (IsNumericType(value.GetType())) builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    else builder.Append(Json(Convert.ToString(value, CultureInfo.CurrentCulture)));
                }
                builder.Append("}");
                if (index < displayed.Rows.Count - 1) builder.Append(',');
                builder.AppendLine();
            }
            builder.AppendLine("]");

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            status.Text = LocalizationManager.Format("Exported {0} row(s) to {1}.", displayed.Rows.Count, dialog.FileName);
        }

        private void ExportInsert()
        {
            if (displayed == null) return;

            string table = TextPromptWindow.Ask(this, T("Export INSERT script"), T("Table name"), "dbo.Table");
            if (string.IsNullOrWhiteSpace(table)) return;

            var builder = new StringBuilder();
            var names = displayed.Columns.Cast<DataColumn>()
                .Select(column => "[" + GridAnalytics.DisplayName(column).Replace("]", "]]") + "]")
                .ToList();

            foreach (DataRow row in displayed.Rows)
            {
                var values = new List<string>();
                for (int index = 0; index < names.Count; index++)
                {
                    object value = row[index];
                    values.Add(SqlLiteral(value));
                }

                builder.Append("INSERT INTO ").Append(table.Trim()).Append(" (").Append(string.Join(", ", names))
                    .Append(") VALUES (").Append(string.Join(", ", values)).AppendLine(");");
            }

            var dialog = new SaveFileDialog
            {
                Title = T("Export results to an INSERT script"),
                Filter = "SQL (*.sql)|*.sql|All files (*.*)|*.*",
                FileName = "insert-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".sql",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            status.Text = LocalizationManager.Format("Exported {0} row(s) to {1}.", displayed.Rows.Count, dialog.FileName);
        }

        private static string SqlLiteral(object value)
        {
            if (value == null || value == DBNull.Value) return "NULL";
            if (value is bool) return ((bool)value) ? "1" : "0";
            if (IsNumericType(value.GetType())) return Convert.ToString(value, CultureInfo.InvariantCulture);

            string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            return "N'" + text.Replace("'", "''") + "'";
        }

        private static bool IsNumericType(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        private static string Json(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (char character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        private static string Csv(string value)
        {
            string text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static bool RowContains(DataRow row, string needle)
        {
            // Index the row instead of row.ItemArray: the array copy is one allocation per row and
            // the search runs over the whole result set.
            int count = row.Table == null ? 0 : row.Table.Columns.Count;
            for (int index = 0; index < count; index++)
            {
                object value = row[index];
                if (value == null || value == DBNull.Value) continue;
                string text = Convert.ToString(value, CultureInfo.CurrentCulture);
                if (!string.IsNullOrEmpty(text) && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool Matches(object value, string opKey, string operand)
        {
            string text = value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;

            switch (opKey)
            {
                case "Contains": return text.IndexOf(operand, StringComparison.OrdinalIgnoreCase) >= 0;
                case "Equals": return string.Equals(text, operand, StringComparison.OrdinalIgnoreCase);
                case "Not equals": return !string.Equals(text, operand, StringComparison.OrdinalIgnoreCase);
                case "Starts with": return text.StartsWith(operand, StringComparison.OrdinalIgnoreCase);
                case "Ends with": return text.EndsWith(operand, StringComparison.OrdinalIgnoreCase);
                case "Greater than": return Compare(value, text, operand) > 0;
                case "Less than": return Compare(value, text, operand) < 0;
                default: return text.IndexOf(operand, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static int Compare(object value, string text, string operand)
        {
            if (value is DateTime && DateTime.TryParse(operand, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime moment))
                return ((DateTime)value).CompareTo(moment);

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal left) &&
                decimal.TryParse(operand, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal right))
            {
                return left.CompareTo(right);
            }

            return string.Compare(text, operand, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The host turns Delete/Backspace into editor commands; this edits the focused box.</summary>
        internal bool TryHandleDeleteKey(bool back)
        {
            TextBox box = (search.IsKeyboardFocusWithin ? search : filterValue.IsKeyboardFocusWithin ? filterValue : null);
            if (box == null) return false;

            try
            {
                if (box.SelectionLength > 0)
                {
                    box.SelectedText = string.Empty;
                    return true;
                }

                int caret = box.CaretIndex;
                if (back)
                {
                    if (caret <= 0) return true;
                    box.Select(caret - 1, 1);
                }
                else
                {
                    if (caret >= box.Text.Length) return true;
                    box.Select(caret, 1);
                }

                box.SelectedText = string.Empty;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                CancelAnalysis();
                dataGrid.ItemsSource = null;
                statsGrid.ItemsSource = null;
                statsView = null;
                statsCache.Clear();
                statsCacheOrder.Clear();
            }
            catch (Exception)
            {
            }
        }

        private sealed class SortKey
        {
            public string Column;
            public bool Descending;
        }

        /// <summary>Formats a cell: real text, NULL in italic grey, and the tooltip of the full value.</summary>
        private sealed class CellTextConverter : IValueConverter
        {
            internal static readonly CellTextConverter Instance = new CellTextConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (parameter == null || System.Convert.ToString(parameter) == "text")
                {
                    return value == null || value == DBNull.Value ? "NULL" : System.Convert.ToString(value, culture);
                }

                bool isNull = value == null || value == DBNull.Value;
                string mode = System.Convert.ToString(parameter);
                if (mode == "style") return isNull ? FontStyles.Italic : FontStyles.Normal;
                if (mode == "brush") return isNull ? Brushes.Gray : SystemColors.ControlTextBrush;
                return null;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }

    /// <summary>Read-only viewer for a single cell: full value, type and length.</summary>
    internal sealed class CellValueWindow : Window
    {
        private CellValueWindow(string column, string type, object value)
        {
            Title = LocalizationManager.Format("Cell value: {0}", column);
            Width = 640;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            string text = value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.CurrentCulture);
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock
            {
                Text = LocalizationManager.Format("{0} | {1} | {2} character(s){3}", column, type, (text ?? string.Empty).Length,
                    value == null || value == DBNull.Value ? " | NULL" : string.Empty),
                Margin = new Thickness(0, 0, 0, 6),
                Opacity = 0.8
            });

            var box = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas")
            };
            Grid.SetRow(box, 1);
            root.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var copy = new Button { Content = LocalizationManager.T("Copy"), Width = 90, Height = 26, Margin = new Thickness(0, 0, 6, 0) };
            copy.Click += (_, __) => { try { Clipboard.SetText(box.Text); } catch (Exception) { } };
            var close = new Button { Content = LocalizationManager.T("Close"), Width = 90, Height = 26, IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
        }

        internal static void Show(FrameworkElement owner, string column, string type, object value)
        {
            var window = new CellValueWindow(column, type, value);
            try { window.Owner = Window.GetWindow(owner); } catch (Exception) { }
            window.ShowDialog();
        }
    }

    /// <summary>Plain text report window, used for the top-value list.</summary>
    internal sealed class TextReportWindow : Window
    {
        private TextReportWindow(string title, string text)
        {
            Title = title;
            Width = 460;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var box = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            root.Children.Add(box);

            var close = new Button { Content = LocalizationManager.T("Close"), Width = 90, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), IsCancel = true };
            close.Click += (_, __) => Close();
            Grid.SetRow(close, 1);
            root.Children.Add(close);

            Content = root;
        }

        internal static void Show(FrameworkElement owner, string title, string text)
        {
            var window = new TextReportWindow(title, text);
            try { window.Owner = Window.GetWindow(owner); } catch (Exception) { }
            window.ShowDialog();
        }
    }

    /// <summary>Single-line prompt used for the INSERT target table.</summary>
    internal sealed class TextPromptWindow : Window
    {
        private readonly TextBox box;
        private string result;

        private TextPromptWindow(string title, string label, string initial)
        {
            Title = title;
            Width = 460;
            Height = 170;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            box = new TextBox { Text = initial ?? string.Empty, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetRow(box, 1);
            root.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = LocalizationManager.T("OK"), Width = 90, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (_, __) => { result = box.Text; DialogResult = true; };
            var cancel = new Button { Content = LocalizationManager.T("Cancel"), Width = 90, Height = 26, IsCancel = true };
            cancel.Click += (_, __) => { DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, __) => { box.Focus(); box.SelectAll(); };
        }

        internal static string Ask(FrameworkElement owner, string title, string label, string initial)
        {
            var window = new TextPromptWindow(title, label, initial);
            try { window.Owner = Window.GetWindow(owner); } catch (Exception) { }
            return window.ShowDialog() == true ? window.result : null;
        }
    }
}
