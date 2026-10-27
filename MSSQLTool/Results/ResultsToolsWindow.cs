using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using MSSQLTool.Results;

namespace MSSQLTool.Results
{
    /// <summary>
    /// "Result set tools": search the current result page and summarise it.  Built in code (no XAML) so the
    /// window stays a single small file that does not need designer plumbing.
    /// </summary>
    internal sealed class ResultsToolsWindow : Window
    {
        private readonly TabControl tabs = new TabControl();
        private readonly TextBox searchBox = new TextBox();
        private readonly ListBox matches = new ListBox();
        private readonly TextBlock searchStatus = new TextBlock();
        private readonly DataGrid statisticsGrid = new DataGrid();
        private readonly TextBlock statisticsSummary = new TextBlock();

        private DataTable table;
        private int lastRow = -1;
        private int lastColumn = -1;

        public ResultsToolsWindow()
        {
            Title = LocalizationManager.T("Result set tools");
            Width = 900;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.White;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var refresh = new Button { Content = LocalizationManager.T("Refresh from the results grid"), Padding = new Thickness(10, 3, 10, 3) };
            refresh.Click += (_, __) => LoadData();
            var copy = new Button { Content = LocalizationManager.T("Copy statistics"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            copy.Click += (_, __) => CopyStatistics();
            var close = new Button { Content = LocalizationManager.T("Close"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            close.Click += (_, __) => Close();
            buttons.Children.Add(refresh);
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            tabs.Items.Add(BuildSearchTab());
            tabs.Items.Add(BuildStatisticsTab());
            root.Children.Add(tabs);

            LoadData();
        }

        private TabItem BuildSearchTab()
        {
            var panel = new DockPanel { Margin = new Thickness(8) };
            var input = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            input.Children.Add(new TextBlock { Text = LocalizationManager.T("Find:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            searchBox.Width = 260;
            searchBox.Height = 24;
            searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            searchBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) FindNext(); };
            input.Children.Add(searchBox);
            var next = new Button { Content = LocalizationManager.T("Find next"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            next.Click += (_, __) => FindNext();
            input.Children.Add(next);
            var all = new Button { Content = LocalizationManager.T("Find all"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            all.Click += (_, __) => FindAll();
            input.Children.Add(all);
            DockPanel.SetDock(input, Dock.Top);
            panel.Children.Add(input);

            searchStatus.Opacity = 0.8;
            searchStatus.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(searchStatus, Dock.Bottom);
            panel.Children.Add(searchStatus);

            matches.DisplayMemberPath = "Display";
            matches.MouseDoubleClick += (_, __) => JumpToSelection();
            panel.Children.Add(matches);

            var item = new TabItem { Header = LocalizationManager.T("Search"), Content = panel };
            return item;
        }

        private TabItem BuildStatisticsTab()
        {
            var panel = new DockPanel { Margin = new Thickness(8) };
            statisticsSummary.FontWeight = FontWeights.SemiBold;
            statisticsSummary.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(statisticsSummary, Dock.Top);
            panel.Children.Add(statisticsSummary);

            statisticsGrid.AutoGenerateColumns = true;
            statisticsGrid.IsReadOnly = true;
            statisticsGrid.CanUserAddRows = false;
            statisticsGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
            panel.Children.Add(statisticsGrid);
            return new TabItem { Header = LocalizationManager.T("Statistics"), Content = panel };
        }

        /// <summary>Reads the current result grid.  Called again by the Refresh button.</summary>
        private void LoadData()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                List<DataTable> tables = GridAccess.GetDataTables();
                table = tables?.FirstOrDefault(t => t != null && t.Columns.Count > 0);
                if (table == null)
                {
                    statisticsSummary.Text = LocalizationManager.T("No result set is available. Run a query first.");
                    statisticsGrid.ItemsSource = null;
                    searchStatus.Text = string.Empty;
                    matches.ItemsSource = null;
                    return;
                }

                List<ResultColumnStatistics> stats = ResultSetStatistics.Summarize(table);
                statisticsGrid.ItemsSource = stats.Select(s => new
                {
                    Colonne = s.Name,
                    Type = s.DataType,
                    NonNull = s.NonNullCount,
                    Null = s.NullCount,
                    Distinct = s.DistinctCount,
                    Min = s.Minimum,
                    Max = s.Maximum,
                    Average = s.Average,
                    MaxLength = s.MaximumLength
                }).ToList();
                statisticsSummary.Text = LocalizationManager.Format("Rows: {0}   Columns: {1}   Grids: {2}",
                    table.Rows.Count, table.Columns.Count, tables.Count);
                searchStatus.Text = LocalizationManager.Format("Searching {0} rows x {1} columns.", table.Rows.Count, table.Columns.Count);
                lastRow = -1;
                lastColumn = -1;
            }
            catch (Exception ex)
            {
                statisticsSummary.Text = LocalizationManager.T("The result set could not be read.");
                MSSQLToolPackage._logger?.Error(ex, "Result set tools could not read the grid");
            }
        }

        private void FindNext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (table == null) LoadData();
            if (table == null) return;

            ResultCellMatch match = ResultSetStatistics.Find(table, searchBox.Text, lastRow, lastColumn);
            if (match == null)
            {
                // Wrap around, which is what the user expects from a find box in a grid.
                match = ResultSetStatistics.Find(table, searchBox.Text, -1, -1);
                if (match == null)
                {
                    searchStatus.Text = LocalizationManager.Format("'{0}' was not found.", searchBox.Text);
                    return;
                }
            }

            lastRow = match.Row;
            lastColumn = match.Column;
            searchStatus.Text = LocalizationManager.Format("Found in row {0}, column '{1}'.", match.Row + 1, match.ColumnName);
            matches.ItemsSource = new[] { new { Display = LocalizationManager.Format("Row {0} | {1} | {2}", match.Row + 1, match.ColumnName, Truncate(match.Value)) } };
            matches.SelectedIndex = 0;
            JumpToCell(match.Row, match.Column);
        }

        private void FindAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (table == null) LoadData();
            if (table == null) return;

            string needle = searchBox.Text ?? string.Empty;
            if (needle.Length == 0) return;

            var found = new List<object>();
            int rowCount = table.Rows.Count, columnCount = table.Columns.Count;
            for (int row = 0; row < rowCount && found.Count < 2000; row++)
                for (int column = 0; column < columnCount; column++)
                {
                    string text = Convert.ToString(table.Rows[row][column]) ?? string.Empty;
                    if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found.Add(new
                    {
                        Display = LocalizationManager.Format("Row {0} | {1} | {2}", row + 1, table.Columns[column].ColumnName, Truncate(text)),
                        Row = row,
                        Column = column
                    });
                }

            matches.ItemsSource = found;
            searchStatus.Text = LocalizationManager.Format("{0} match(es). Double-click a row to jump to the cell.", found.Count);
        }

        private void JumpToSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            object selected = matches.SelectedItem;
            if (selected == null) return;

            var property = selected.GetType().GetProperty("Row");
            if (property == null) return;
            int row = Convert.ToInt32(property.GetValue(selected));
            int column = Convert.ToInt32(selected.GetType().GetProperty("Column")?.GetValue(selected) ?? 0);
            JumpToCell(row, column);
        }

        /// <summary>
        /// Best effort: move the results grid to the cell.  SSMS builds differ in what they expose, so a
        /// failure only means the user has to look at the row number we report.
        /// </summary>
        private void JumpToCell(int row, int column)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object grid = GridAccess.GetFocusGridControl();
                if (grid == null) return;

                object storage = GridAccess.GetNonPublicField(grid, "m_gridStorage") ?? GridAccess.GetProperty(grid, "GridStorage");
                object currentCell = GridAccess.GetProperty(grid, "CurrentCell");
                if (currentCell == null) return;

                Type cellType = currentCell.GetType();
                object cell = Activator.CreateInstance(cellType, row, column);
                GridAccess.SetPropertyValue(grid, "CurrentCell", cell);
                foreach (string method in new[] { "SelectCell", "SetCurrentCell", "EnsureVisible" })
                {
                    System.Reflection.MethodInfo info = grid.GetType().GetMethod(method);
                    if (info == null) continue;
                    object[] args = info.GetParameters().Length == 2 ? new object[] { row, column } : new object[] { row };
                    info.Invoke(grid, args);
                    break;
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not move the result grid to the matched cell.");
            }
        }

        private void CopyStatistics()
        {
            try
            {
                if (table == null) return;
                List<ResultColumnStatistics> stats = ResultSetStatistics.Summarize(table);
                Clipboard.SetText(ResultSetStatistics.ToText(table, stats));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not copy the result set statistics");
            }
        }

        private static string Truncate(string text)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 120 ? value : value.Substring(0, 117) + "...";
        }
    }
}
