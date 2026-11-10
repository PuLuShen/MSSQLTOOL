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
        // No practical display cap: rows are imported in batches so a large result set stays usable.
        private const int DisplayCap = 1000000;

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
        private readonly ComboBox freezeCount = new ComboBox
        {
            Width = 150,
            Height = 26,
            Margin = new Thickness(0, 0, 14, 0),
            ToolTip = LocalizationManager.T("Pin the first N visible columns so they stay on screen while scrolling right.")
        };
        private readonly TextBox columnSearch = new TextBox
        {
            Width = 170,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = LocalizationManager.T("Find a column by name or type; press Enter to jump to the next matching column.")
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
            RowHeight = 26,
            ColumnHeaderHeight = 32,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
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
            RowHeight = 26,
            ColumnHeaderHeight = 32,
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
        private readonly TextBox fieldFilter = new TextBox
        {
            Height = 26,
            Margin = new Thickness(0, 0, 0, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = LocalizationManager.T("Filter fields by name or type")
        };
        private readonly StackPanel fieldList = new StackPanel();
        private static readonly Brush FieldHoverBrush = new SolidColorBrush(Color.FromRgb(238, 243, 250));
        private static readonly Brush FieldActiveBrush = new SolidColorBrush(Color.FromRgb(214, 231, 250));
        private static readonly Brush ColumnHighlightBrush = new SolidColorBrush(Color.FromRgb(226, 238, 252));
        private Border activeFieldRow;
        private Style columnHighlightStyle;
        private string columnsSignature;
        private List<GridColumnInsight> fieldQueue;
        private int fieldIndex;
        private int fieldGeneration;
        private readonly TextBlock statsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(10, 4, 10, 4),
            Padding = new Thickness(10, 4, 10, 4),
            MaxHeight = 90
        };

        private readonly TextBlock selectionSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(16, 0, 0, 0))
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
        private List<GridColumnInsight> allFields;
        private readonly List<SortKey> sorts = new List<SortKey>();
        private readonly List<int> hiddenOrdinals = new List<int>();
        private int frozenColumns;
        private bool suppressFreezeChange;
        private int lastFoundOrdinal = -1;
        private string lastColumnNeedle = string.Empty;
        private bool suppressResultSetChange;

        private static readonly string[] OperatorKeys =
        {
            "Contains", "Equals", "Not equals", "Starts with", "Ends with", "Greater than", "Less than"
        };

        public GridAnalyticsControl()
        {
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.Transparent;
            ApplyGridLook(dataGrid);
            ApplyGridLook(statsGrid);

            var root = new Grid { Margin = new Thickness(10) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var right = new Grid { Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(right, 1);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 130 });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(summary, 0);
            right.Children.Add(summary);

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
            right.Children.Add(filterBar);

            var actionBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            actionBar.Children.Add(CreateButton("Refresh", Refresh));
            actionBar.Children.Add(CreateButton("Export CSV...", ExportCsv));
            actionBar.Children.Add(CreateButton("Export JSON...", ExportJson));
            actionBar.Children.Add(CreateButton("Export INSERT...", ExportInsert));
            actionBar.Children.Add(CreateButton("Copy statistics", CopyStatistics));
            actionBar.Children.Add(new TextBlock
            {
                Text = T("Freeze columns:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 6, 0)
            });
            actionBar.Children.Add(freezeCount);
            Grid.SetRow(actionBar, 2);
            right.Children.Add(actionBar);

            Grid.SetRow(dataGrid, 4);
            right.Children.Add(dataGrid);

            var statsHost = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            statsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            statsHost.Children.Add(selectionSummary);
            var statsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            statsScroll.Content = statsText;
            Grid.SetRow(statsScroll, 2);
            statsHost.Children.Add(statsScroll);
            Grid.SetRow(statsHost, 5);
            right.Children.Add(statsHost);

            Grid.SetRow(status, 6);
            right.Children.Add(status);

            var fieldPanel = CreateFieldPanel();
            Grid.SetColumn(fieldPanel, 0);
            root.Children.Add(fieldPanel);
            root.Children.Add(right);

            Content = root;

            BuildStatsColumns();
            BuildFilterControls();
            BuildFreezeSelector();

            search.TextChanged += (_, __) => ScheduleApplyView();
            filterValue.TextChanged += (_, __) => ScheduleApplyView();
            columnSearch.KeyDown += OnColumnSearchKeyDown;
            columnSearch.TextChanged += (_, __) => { lastFoundOrdinal = -1; lastColumnNeedle = string.Empty; };
            freezeCount.SelectionChanged += (_, __) => { if (!suppressFreezeChange) SetFrozenColumns(freezeCount.SelectedIndex, true); };
            filterColumns.SelectionChanged += (_, __) => ApplyView();
            filterOperators.SelectionChanged += (_, __) => ApplyView();
            applyTimer.Tick += (_, __) => { applyTimer.Stop(); ApplyView(); };
            resultSets.SelectionChanged += (_, __) => OnResultSetChanged();
            dataGrid.Sorting += OnSorting;
            dataGrid.SelectedCellsChanged += (_, __) => { ClearColumnHighlight(); UpdateSelectionStatistics(); };
            dataGrid.MouseDoubleClick += OnCellDoubleClick;
            dataGrid.LoadingRow += (_, e) => { e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture); };
            // 271 columns: without virtualization every column header and cell factory is built up front.
            dataGrid.EnableColumnVirtualization = true;
            dataGrid.EnableRowVirtualization = true;
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
                Height = 26,
                MinWidth = 64,
                FontSize = 12,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 6, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
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
        /// The freeze selector always offers "none" plus the first ten columns.  The actual number
        /// is clamped to the columns on screen, and the header context menu writes back into it.
        /// </summary>
        private void BuildFreezeSelector()
        {
            suppressFreezeChange = true;
            try
            {
                freezeCount.Items.Clear();
                freezeCount.Items.Add(T("No fixed columns"));
                for (int count = 1; count <= 10; count++)
                    freezeCount.Items.Add(LocalizationManager.Format("First {0} column(s)", count));
                freezeCount.SelectedIndex = 0;
            }
            finally
            {
                suppressFreezeChange = false;
            }
        }

        private void SetFrozenColumns(int count, bool announce)
        {
            int effective = Math.Max(0, Math.Min(count, dataGrid.Columns.Count));
            frozenColumns = effective;
            dataGrid.FrozenColumnCount = effective;
            SyncFreezeSelector();

            if (announce)
            {
                status.Text = effective == 0
                    ? T("Columns unfrozen.")
                    : LocalizationManager.Format("First {0} column(s) frozen.", effective);
            }
        }

        private void SyncFreezeSelector()
        {
            if (freezeCount.Items.Count == 0) return;

            suppressFreezeChange = true;
            try
            {
                freezeCount.SelectedIndex = Math.Min(frozenColumns, freezeCount.Items.Count - 1);
            }
            finally
            {
                suppressFreezeChange = false;
            }
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
                    dataGrid.FrozenColumnCount = 0;
                    dataGrid.Columns.Clear();
                    frozenColumns = 0;
                    SyncFreezeSelector();
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
                dataGrid.FrozenColumnCount = 0;
                dataGrid.Columns.Clear();
                frozenColumns = 0;
                SyncFreezeSelector();
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
            lastFoundOrdinal = -1;
            lastColumnNeedle = string.Empty;
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

            // Opening the window no longer scans every column: the list appears immediately and
            // statistics are produced from the current selection (one cell = that column, several
            // cells = the selected cells together).
            var columns = new List<GridColumnInsight>();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                DataColumn column = table.Columns[i];
                columns.Add(new GridColumnInsight { Ordinal = i, Name = GridAnalytics.DisplayName(column), DataType = column.DataType.Name });
            }

            var placeholder = new GridAnalyticsResult
            {
                RowCount = table.Rows.Count,
                ColumnCount = table.Columns.Count,
                SampledRows = 0,
                Columns = columns
            };
            ApplyStatistics(placeholder);
            allFields = columns;
            RebuildFieldList();
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

            int ordinal = insight.Ordinal;
            if (ordinal < 0 || ordinal >= displayed.Columns.Count) return;

            string name = displayed.Columns[ordinal].ColumnName;
            DataGridColumn column = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, name, StringComparison.Ordinal));
            if (column == null)
            {
                viewNote = LocalizationManager.Format("Column {0} is hidden; tick it in the field list to see it.", insight.Name);
                UpdateStatus();
                return;
            }

            dataGrid.UnselectAll();
            HighlightGridColumn(name);
            if (dataGrid.Items.Count == 0) return;
            try { dataGrid.ScrollIntoView(dataGrid.Items[0], column); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not scroll to the selected column."); }
        }

        /// <summary>Drops the field-driven column tint, e.g. as soon as the user picks a cell.</summary>
        private void ClearColumnHighlight()
        {
            if (activeFieldRow != null)
            {
                activeFieldRow.Background = Brushes.Transparent;
                activeFieldRow = null;
            }

            foreach (DataGridColumn column in dataGrid.Columns)
                column.ClearValue(DataGridColumn.CellStyleProperty);
        }

        /// <summary>Tints every cell of one column so the field the user clicked is obvious in the grid.</summary>
        private void HighlightGridColumn(string name)
        {
            foreach (DataGridColumn column in dataGrid.Columns)
            {
                bool hit = string.Equals(column.SortMemberPath, name, StringComparison.Ordinal);
                if (!hit)
                {
                    column.ClearValue(DataGridColumn.CellStyleProperty);
                    continue;
                }

                if (columnHighlightStyle == null)
                {
                    columnHighlightStyle = new Style(typeof(DataGridCell), dataGrid.CellStyle);
                    columnHighlightStyle.Setters.Add(new Setter(Control.BackgroundProperty, ColumnHighlightBrush));
                }
                column.CellStyle = columnHighlightStyle;
            }
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

            SelectStatisticsRow(ordinal + 1);
        }

        /// <summary>Highlights the statistics row for a column without running the scroll-back handler.</summary>
        private void SelectStatisticsRow(int ordinal)
        {
            if (analytics == null) return;
            GridColumnInsight insight = analytics.Columns.FirstOrDefault(item => item.Ordinal == ordinal);
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
            bool direct = needle.Length == 0 && !hasColumnFilter;
            // Unfiltered view: bind the source table directly. Copying 17k rows cell by cell was a
            // multi-second freeze on wide result sets and bought nothing here.
            var filtered = direct ? source : source.Clone();
            displayed = filtered;

            BuildDataColumns();

            var view = new DataView(displayed);
            string sort = BuildSortExpression();
            dataGrid.ItemsSource = view;

            if (direct)
            {
                if (sort.Length > 0) view.Sort = sort;
            }
            else
            {
                // Import in batches so the window paints immediately and stays responsive while a
                // large result set loads. The sort is applied once every row is in.
                int batchIndex = 0;
                Action loadBatch = null;
                loadBatch = () =>
                {
                    // First batch is small so the grid paints content almost immediately.
                    int size = batchIndex == 0 ? 200 : 2000;
                    int end = Math.Min(rows.Count, batchIndex + size);
                    for (; batchIndex < end; batchIndex++) filtered.ImportRow(rows[batchIndex]);
                    if (batchIndex < rows.Count)
                    {
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, loadBatch);
                        return;
                    }
                    if (sort.Length > 0) view.Sort = sort;
                };
                loadBatch();
            }

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
            if (displayed != null)
            {
                // Filtering and sorting swap the underlying table but keep the same columns. Rebuilding
                // 271 column objects on every view change was the biggest cost.
                string signature = ColumnSignature(displayed);
                if (signature == columnsSignature && dataGrid.Columns.Count > 0)
                {
                    SyncFreezeSelector();
                    return;
                }
                columnsSignature = signature;
            }

            // Clearing the columns while a frozen count is set can throw, so release it first.
            dataGrid.FrozenColumnCount = 0;
            dataGrid.Columns.Clear();
            if (displayed == null)
            {
                frozenColumns = 0;
                SyncFreezeSelector();
                return;
            }

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
            frozenColumns = dataGrid.FrozenColumnCount;
            SyncFreezeSelector();
        }

        private string ColumnSignature(DataTable table)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(table.Columns.Count).Append('|');
            foreach (DataColumn column in table.Columns) builder.Append(column.ColumnName).Append(',');
            builder.Append('|');
            foreach (int ordinal in hiddenOrdinals.OrderBy(item => item)) builder.Append(ordinal).Append(',');
            return builder.ToString();
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

            panel.ContextMenuOpening += (_, __) =>
            {
                // Built on demand: 271 menus with 9 items each was pure startup cost.
                if (panel.ContextMenu == null) panel.ContextMenu = BuildHeaderMenu(column, ordinal);
            };
            return panel;
        }

        private ContextMenu BuildHeaderMenu(DataColumn column, int ordinal)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Sort ascending", () => SetSort(column.ColumnName, false)));
            menu.Items.Add(MenuItem("Sort descending", () => SetSort(column.ColumnName, true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Hide column", () => { hiddenOrdinals.Add(ordinal); ApplyView(); }));
            menu.Items.Add(MenuItem("Copy column name", () => Clipboard.SetText(GridAnalytics.DisplayName(column))));
            menu.Items.Add(MenuItem("Freeze up to this column", () =>
            {
                int visible = dataGrid.Columns.IndexOf(dataGrid.Columns.FirstOrDefault(item => item.SortMemberPath == column.ColumnName));
                SetFrozenColumns(visible + 1, true);
            }));
            menu.Items.Add(MenuItem("Unfreeze columns", () => SetFrozenColumns(0, true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Top values", () => ShowTopValues(analytics?.Columns.FirstOrDefault(item => item.Name == GridAnalytics.DisplayName(column)))));
            return menu;
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

        /// <summary>
        /// Jumps to the next column whose name or type contains the search text, wrapping around at
        /// the end.  A hidden match is revealed first, so searching never hits a column that cannot
        /// be seen.  The first cell of the column is selected and the statistics table follows it.
        /// </summary>
        private void FindNextColumn()
        {
            if (displayed == null)
            {
                status.Text = T("No result set is available.");
                return;
            }

            string needle = (columnSearch.Text ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                status.Text = T("Type part of a column name.");
                columnSearch.Focus();
                return;
            }

            if (!string.Equals(needle, lastColumnNeedle, StringComparison.Ordinal))
            {
                lastColumnNeedle = needle;
                lastFoundOrdinal = -1;
            }

            int start = lastFoundOrdinal < 0 ? 0 : lastFoundOrdinal + 1;
            int found = GridAnalytics.FindColumn(displayed.Columns, needle, start);
            if (found < 0)
            {
                status.Text = LocalizationManager.Format("No column matches '{0}'.", needle);
                lastFoundOrdinal = -1;
                return;
            }

            lastFoundOrdinal = found;
            DataColumn column = displayed.Columns[found];
            string name = GridAnalytics.DisplayName(column);
            string field = column.ColumnName;

            if (hiddenOrdinals.Contains(found))
            {
                hiddenOrdinals.Remove(found);
                ApplyView();
            }

            FocusColumn(field, found + 1);
            status.Text = LocalizationManager.Format("Column '{0}'  ({1} of {2})", name, found + 1, displayed.Columns.Count);
        }

        private void OnColumnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FindNextColumn();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                columnSearch.Text = string.Empty;
                e.Handled = true;
            }
        }

        /// <summary>Scrolls the grid to a column and marks its first cell, without stealing focus.</summary>
        private void FocusColumn(string columnName, int ordinal)
        {
            DataGridColumn gridColumn = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, columnName, StringComparison.Ordinal));
            if (gridColumn == null) return;

            if (dataGrid.Items.Count > 0)
            {
                object item = dataGrid.Items[0];
                var cell = new DataGridCellInfo(item, gridColumn);
                try
                {
                    dataGrid.SelectedCells.Clear();
                    dataGrid.CurrentCell = cell;
                    dataGrid.SelectedCells.Add(cell);
                    dataGrid.ScrollIntoView(item, gridColumn);
                    dataGrid.UpdateLayout();
                    dataGrid.ScrollIntoView(item, gridColumn);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not scroll to the found column.");
                }
            }

            SelectStatisticsRow(ordinal);
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
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                columnSearch.Focus();
                columnSearch.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                search.Focus();
                search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F3)
            {
                FindNextColumn();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (columnSearch.IsKeyboardFocusWithin) columnSearch.Text = string.Empty;
                else ClearFilters();
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
                    columns.Add(GridAnalytics.DisplayName(column));

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

                // Only the scope belongs here: non-null / empty / distinct / longest are repeated by
                // the column line just below, so keeping them in both bars was pure duplication.
                selectionSummary.Text = LocalizationManager.Format(
                    "Selection: {0} cell(s), {1} row(s), {2} column(s)",
                    cells, rows.Count, columns.Count);

                // One cell: describe that column over the whole (displayed) table; several cells:
                // describe the selection as a whole.  A full column scan is capped so a million-row
                // grid can never freeze the window.
                GridColumnInsight insight = cells == 1 ? DescribeSelectedColumn(columns, nonNull, nulls, distinct.Count, maxLength, numeric, sum, min, max)
                                                       : BuildSelectionInsight(columns, nonNull, nulls, distinct.Count, maxLength, numeric, sum, min, max);
                ShowSingleInsight(insight);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not summarize the selected cells.");
            }
        }

        private GridColumnInsight BuildSelectionInsight(ICollection<string> columns, int nonNull, int nulls, int distinct,
            int maxLength, int numeric, decimal sum, decimal? min, decimal? max)
        {
            string name = columns.Count == 1 ? columns.First() : T("Selection");
            return new GridColumnInsight
            {
                Ordinal = 0,
                Name = name,
                DataType = LocalizationManager.Format("{0} column(s)", columns.Count),
                NonNull = nonNull,
                NullCount = nulls,
                Distinct = distinct,
                MaxLength = maxLength,
                IsNumeric = numeric > 0,
                Sum = numeric > 0 ? sum.ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Average = numeric > 0 ? (sum / numeric).ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Minimum = min?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-",
                Maximum = max?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-"
            };
        }

        /// <summary>
        /// A single selected cell describes its whole column.  Only that one column is read, and the
        /// scan stops at a fixed row cap so a million-row grid can never block the window.
        /// </summary>
        private GridColumnInsight DescribeSelectedColumn(ICollection<string> columns, int nonNull, int nulls, int distinct,
            int maxLength, int numeric, decimal sum, decimal? min, decimal? max)
        {
            string name = columns.Count == 1 ? columns.First() : null;
            DataTable source = displayed;
            if (string.IsNullOrEmpty(name) || source == null || !source.Columns.Contains(name))
                return BuildSelectionInsight(columns, nonNull, nulls, distinct, maxLength, numeric, sum, min, max);

            int columnIndex = source.Columns[name].Ordinal;
            const int rowCap = 100000;
            int limit = Math.Min(source.Rows.Count, rowCap);
            var uniques = new HashSet<string>(StringComparer.Ordinal);
            int seenNull = 0, seenNonNull = 0, seenMaxLength = 0, seenNumeric = 0;
            decimal total = 0;
            decimal? low = null, high = null;

            for (int row = 0; row < limit; row++)
            {
                object value = source.Rows[row][columnIndex];
                if (value == null || value == DBNull.Value) { seenNull++; continue; }

                seenNonNull++;
                string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                uniques.Add(text);
                if (text.Length > seenMaxLength) seenMaxLength = text.Length;

                if (TryNumber(value, text, out decimal number))
                {
                    seenNumeric++;
                    total += number;
                    low = low.HasValue && low.Value <= number ? low : number;
                    high = high.HasValue && high.Value >= number ? high : number;
                }
            }

            return new GridColumnInsight
            {
                Ordinal = columnIndex,
                Name = name,
                DataType = source.Columns[columnIndex].DataType.Name,
                NonNull = seenNonNull,
                NullCount = seenNull,
                Distinct = uniques.Count,
                MaxLength = seenMaxLength,
                IsNumeric = seenNumeric > 0,
                Sum = seenNumeric > 0 ? total.ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Average = seenNumeric > 0 ? (total / seenNumeric).ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Minimum = low?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-",
                Maximum = high?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-"
            };
        }

        private void ShowSingleInsight(GridColumnInsight insight)
        {
            if (insight == null) return;

            var result = new GridAnalyticsResult
            {
                RowCount = analytics == null ? 0 : analytics.RowCount,
                ColumnCount = analytics == null ? 0 : analytics.ColumnCount,
                Columns = new List<GridColumnInsight> { insight }
            };
            analytics = result;
            statsText.Text = FormatInsight(insight);
        }

        private UIElement CreateFieldPanel()
        {
            var panel = new DockPanel { LastChildFill = true };

            var title = new TextBlock
            {
                Text = LocalizationManager.T("Fields"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(title, Dock.Top);
            panel.Children.Add(title);

            fieldFilter.TextChanged += (_, __) => RebuildFieldList();
            DockPanel.SetDock(fieldFilter, Dock.Top);
            panel.Children.Add(fieldFilter);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0)
            };
            scroll.Content = fieldList;
            panel.Children.Add(scroll);
            return panel;
        }

        /// <summary>
        /// One row per column: the checkbox drives its visibility in the grid, the name jumps to it.
        /// Rebuilt when the column set or the filter changes, never on every grid keystroke.
        /// </summary>
        private void RebuildFieldList()
        {
            fieldGeneration++;
            fieldQueue = null;
            fieldList.Children.Clear();
            List<GridColumnInsight> fields = allFields;
            if (fields == null) return;

            string needle = (fieldFilter.Text ?? string.Empty).Trim();
            var visible = new List<GridColumnInsight>();
            foreach (GridColumnInsight column in fields)
            {
                if (needle.Length > 0 && !MatchesField(column, needle)) continue;
                visible.Add(column);
            }

            // 271 controls cannot be built in one go without a visible freeze; 40 per dispatcher
            // pass keeps the window responsive.
            fieldQueue = visible;
            fieldIndex = 0;
            LoadFieldBatch(fieldGeneration);
        }

        private void LoadFieldBatch(int generation)
        {
            if (generation != fieldGeneration || fieldQueue == null) return;

            int end = Math.Min(fieldQueue.Count, fieldIndex + 40);
            for (; fieldIndex < end; fieldIndex++)
                fieldList.Children.Add(CreateFieldRow(fieldQueue[fieldIndex]));

            if (fieldIndex < fieldQueue.Count)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => LoadFieldBatch(generation)));
            else
                fieldQueue = null;
        }

        private static bool MatchesField(GridColumnInsight column, string needle)
        {
            if (column == null) return false;
            return (column.Name ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (column.DataType ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private UIElement CreateFieldRow(GridColumnInsight column)
        {
            int ordinal = column.Ordinal;
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };

            var check = new CheckBox
            {
                IsChecked = !hiddenOrdinals.Contains(ordinal),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = LocalizationManager.T("Show or hide this column")
            };
            check.Checked += (_, __) => { hiddenOrdinals.Remove(ordinal); ApplyView(); };
            check.Unchecked += (_, __) =>
            {
                if (!hiddenOrdinals.Contains(ordinal)) hiddenOrdinals.Add(ordinal);
                ApplyView();
            };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);

            var name = new TextBlock
            {
                Text = column.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = LocalizationManager.Format("{0} ({1}) - click to jump to this column", column.Name, column.DataType)
            };
            name.MouseLeftButtonUp += (_, __) => { };
            row.Children.Add(name);

            var host = new Border
            {
                Child = row,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand
            };
            host.MouseEnter += (_, __) =>
            {
                if (!ReferenceEquals(activeFieldRow, host)) host.Background = FieldHoverBrush;
            };
            host.MouseLeave += (_, __) =>
            {
                if (!ReferenceEquals(activeFieldRow, host)) host.Background = Brushes.Transparent;
            };
            host.MouseLeftButtonUp += (_, e) =>
            {
                if (IsInsideCheckBox(e.OriginalSource as DependencyObject)) return;
                // Jump first: it clears the grid selection (and with it the previous tint), then the
                // field row is marked active so the tint survives.
                FocusGridColumn(column);
                SelectFieldRow(host);
            };
            return host;
        }

        /// <summary>Keeps one field row tinted as the active selection.</summary>
        private void SelectFieldRow(Border row)
        {
            if (activeFieldRow != null && !ReferenceEquals(activeFieldRow, row))
                activeFieldRow.Background = Brushes.Transparent;
            activeFieldRow = row;
            row.Background = FieldActiveBrush;
        }

        private static bool IsInsideCheckBox(DependencyObject node)
        {
            for (int depth = 0; node != null && depth < 6; depth++)
            {
                if (node is CheckBox) return true;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        /// <summary>One line per selected scope, shown as text instead of a second grid.</summary>
        private static string FormatInsight(GridColumnInsight insight)
        {
            if (insight == null) return string.Empty;
            return LocalizationManager.Format(
                "{0}  ({1})   non-null {2}   empty {3} ({4})   distinct {5}   longest {6}   sum {7}   average {8}   min {9}   max {10}",
                insight.Name, insight.DataType, insight.NonNull, insight.NullCount, insight.NullPercentText,
                insight.Distinct, insight.MaxLength, insight.Sum, insight.Average, insight.Minimum, insight.Maximum);
        }

        private static void ApplyGridLook(DataGrid grid)
        {
            // Shared look for both grids: roomier rows, a bold header and hairline separators.
            // Kept in code because this window builds its own layout (no XAML).
            grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
            grid.BorderThickness = new Thickness(0, 1, 0, 0);
            grid.BorderBrush = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));

            var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            header.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(40, 0, 0, 0))));
            grid.ColumnHeaderStyle = header;

            var cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            cell.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            // The default system highlight (white on saturated blue) plus a dotted focus rectangle
            // looked heavy on a 271-column grid; use a soft tint and no focus adorners.
            cell.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            var selectedCell = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selectedCell.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(223, 235, 250))));
            selectedCell.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(20, 28, 40))));
            cell.Triggers.Add(selectedCell);
            grid.CellStyle = cell;
        }

        private static bool TryNumber(object value, string text, out decimal number)        {
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
            // Any text box in the panel must keep its own editing keys, not only the two boxes that
            // existed when this guard was written: the field filter was missing, so Backspace there
            // fell through to SSMS and deleted text in the SQL editor instead.
            Window host = Window.GetWindow(this);
            if (host == null || !host.IsActive) return false;

            var box = Keyboard.FocusedElement as TextBox;
            if (box == null || !IsAncestorOf(box)) return true;

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
            Grid.SetRow(box, 2);
            root.Children.Add(box);

            // The raw value is often XML or JSON stored in a text column; let the user pick how to
            // show it instead of forcing them to read one long unbroken line.
            var formatBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            formatBar.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Format:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            var formatBox = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            formatBox.Items.Add(LocalizationManager.T("Original"));
            formatBox.Items.Add("JSON");
            formatBox.Items.Add("XML");
            formatBar.Children.Add(formatBox);
            var hint = new TextBlock
            {
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            formatBar.Children.Add(hint);
            Grid.SetRow(formatBar, 1);
            root.Children.Add(formatBar);

            Action applyFormat = () =>
            {
                string kind = formatBox.SelectedIndex == 1 ? "JSON" : formatBox.SelectedIndex == 2 ? "XML" : string.Empty;
                bool ok = true;
                box.Text = string.IsNullOrEmpty(kind) ? text ?? string.Empty : PrettyPrint(kind, text, out ok);
                hint.Text = ok ? string.Empty : LocalizationManager.Format("Not valid {0}; showing the raw value.", kind);
            };
            formatBox.SelectionChanged += (_, __) => applyFormat();
            formatBox.SelectedIndex = DetectFormat(text);
            applyFormat();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var copy = new Button { Content = LocalizationManager.T("Copy"), Width = 90, Height = 26, Margin = new Thickness(0, 0, 6, 0) };
            copy.Click += (_, __) => { try { Clipboard.SetText(box.Text); } catch (Exception) { } };
            var close = new Button { Content = LocalizationManager.T("Close"), Width = 90, Height = 26, IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>Picks the format that already fits the value, so most cells need no clicks.</summary>
        private static int DetectFormat(string text)
        {
            string trimmed = (text ?? string.Empty).TrimStart();
            if (trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                bool ok;
                PrettyPrint("XML", text, out ok);
                if (ok) return 2;
            }
            else if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                bool ok;
                PrettyPrint("JSON", text, out ok);
                if (ok) return 1;
            }
            return 0;
        }

        /// <summary>Indents XML/JSON; returns the original text and ok=false when it does not parse.</summary>
        private static string PrettyPrint(string kind, string text, out bool ok)
        {
            ok = true;
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            try
            {
                if (kind == "JSON")
                    return Newtonsoft.Json.Linq.JToken.Parse(text).ToString(Newtonsoft.Json.Formatting.Indented);
                if (kind == "XML")
                    return System.Xml.Linq.XDocument.Parse(text).ToString();
            }
            catch (Exception)
            {
                ok = false;
            }
            return text ?? string.Empty;
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
