using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Small floating panel that follows the selection in the results grid.  It is toggled from the grid's
    /// own context menu, opens itself when a cell is selected, disappears as soon as the focus leaves the
    /// results, and shows the statistics of the selected column plus a find box and a column jump list.
    /// Independent from <see cref="ResultsToolsWindow"/> on purpose: that one is a full dialog.
    /// </summary>
    internal static class ResultsHoverPopup
    {
        private static HoverWindow window;
        private static DispatcherTimer timer;
        private static bool enabled;
        private static bool attached;
        private static readonly System.Collections.Generic.HashSet<System.Windows.Forms.Control> hooked =
            new System.Collections.Generic.HashSet<System.Windows.Forms.Control>();
        private static string lastCellKey = string.Empty;

        internal static bool IsEnabled => enabled;

        /// <summary>
        /// Adds the toggle to the results grid context menu.  SSMS creates that menu lazily, so this is
        /// called again from every right click; it is idempotent.
        /// </summary>
        internal static void AttachContextMenu(object sqlResultsControl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sqlResultsControl == null) return;

            try
            {
                object grid = FindGrid(sqlResultsControl);
                if (grid == null) return;

                object menu = GridAccess.GetProperty(grid, "ContextMenu") ?? GridAccess.GetNonPublicField(grid, "m_contextMenu");
                var strip = menu as System.Windows.Forms.ContextMenuStrip;
                if (strip == null)
                {
                    // The menu may be created on first right click; retry on the next attach.
                    return;
                }

                if (strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>()
                        .Any(item => string.Equals(item.Name, "MSSQLToolResultsToggle", StringComparison.Ordinal)))
                {
                    attached = true;
                    return;
                }

                var toggle = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.T("Result statistics panel"))
                {
                    Name = "MSSQLToolResultsToggle",
                    CheckOnClick = true,
                    Checked = enabled
                };
                toggle.Click += (_, __) => Toggle();
                strip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                strip.Items.Add(toggle);
                attached = true;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not add the results statistics toggle to the grid menu.");
            }
        }

        /// <summary>
        /// Right clicks are the only moment SSMS has the grid menu at hand, so the toggle is installed
        /// just before the menu opens instead of once after the query.
        /// </summary>
        internal static void HookGrid(object grid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var control = grid as System.Windows.Forms.Control;
            if (control == null || hooked.Contains(control)) return;

            try
            {
                control.MouseUp += (sender, e) =>
                {
                    if (e.Button != System.Windows.Forms.MouseButtons.Right) return;
                    try { AttachContextMenu(GridAccess.GetSQLResultsControl()); } catch (Exception) { }
                };
                hooked.Add(control);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not hook the results grid right click.");
            }
        }

        internal static void Toggle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            enabled = !enabled;
            if (enabled) Show(); else Hide();
        }

        private static void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (window == null)
            {
                window = new HoverWindow();
                window.Closed += (_, __) => { window = null; };
                window.Show();
            }

            window.RefreshFromSelection();
            window.Show();
            window.Activate();
            StartTimer();
        }

        private static void Hide()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StopTimer();
            if (window != null)
            {
                window.Hide();
            }
        }

        /// <summary>
        /// Polls the grid: a new selection updates the panel, and losing the results grid closes it.
        /// SSMS does not raise a managed selection event, so polling is the reliable option here.
        /// </summary>
        private static void StartTimer()
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += (_, __) =>
            {
                try
                {
                    if (!enabled || window == null)
                    {
                        StopTimer();
                        return;
                    }

                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null)
                    {
                        // Focus left the results (clicking anywhere else closes the panel), except inside
                        // the panel itself where the find box lives.  The feature stays enabled: the next
                        // cell selection brings the panel back.
                        if (!window.IsMouseOver) window.Hide();
                        return;
                    }

                    string key = window.RefreshFromSelection();
                    if (!string.IsNullOrEmpty(key) && !string.Equals(key, lastCellKey, StringComparison.Ordinal))
                    {
                        lastCellKey = key;
                        window.Show();
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Results statistics panel poll failed.");
                }
            };
            timer.Start();
        }

        private static void StopTimer()
        {
            if (timer == null) return;
            timer.Stop();
            timer = null;
        }

        /// <summary>Finds the grid control inside the SQL results control.</summary>
        internal static object FindGrid(object sqlResultsControl)
        {
            foreach (string field in new[] { "m_grid", "grid", "m_dataGrid" })
            {
                object grid = GridAccess.GetNonPublicField(sqlResultsControl, field);
                if (grid != null) return grid;
            }

            // Fall back to the focused grid.
            return GridAccess.GetFocusGridControl();
        }

        /// <summary>The small panel itself.</summary>
        private sealed class HoverWindow : Window
        {
            private readonly TextBlock header = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            private readonly TextBlock cellInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
            private readonly DataGrid stats = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false, MaxHeight = 220 };
            private readonly TextBox find = new TextBox { Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
            private readonly ComboBox columns = new ComboBox { Height = 24 };
            private readonly TextBlock findStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

            private DataTable table;
            private int row = -1, column = -1;

            internal HoverWindow()
            {
                Title = LocalizationManager.T("Result statistics panel");
                Width = 520;
                Height = 420;
                Topmost = true;
                ShowInTaskbar = false;
                WindowStyle = WindowStyle.ToolWindow;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.White;

                var panel = new StackPanel { Margin = new Thickness(10) };
                panel.Children.Add(header);
                panel.Children.Add(cellInfo);

                var findRow = new Grid { Margin = new Thickness(0, 8, 0, 6) };
                findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = LocalizationManager.T("Find:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                Grid.SetColumn(label, 0);
                findRow.Children.Add(label);
                find.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) FindNext(); };
                Grid.SetColumn(find, 1);
                findRow.Children.Add(find);
                var next = new Button { Content = LocalizationManager.T("Find next"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
                next.Click += (_, __) => FindNext();
                Grid.SetColumn(next, 2);
                findRow.Children.Add(next);
                panel.Children.Add(findRow);
                panel.Children.Add(findStatus);

                var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
                jumpRow.Children.Add(new TextBlock { Text = LocalizationManager.T("Jump to column:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                columns.Width = 220;
                columns.SelectionChanged += (_, __) =>
                {
                    if (columns.SelectedItem == null || table == null) return;
                    int index = ResultSetStatistics.FindColumn(table, columns.SelectedItem.ToString());
                    if (index >= 0) JumpToCell(0, index);
                };
                jumpRow.Children.Add(columns);
                panel.Children.Add(jumpRow);

                panel.Children.Add(stats);
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }

            /// <summary>Reads the grid, updates the header/statistics for the selected column.</summary>
            internal string RefreshFromSelection()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    List<DataTable> tables = GridAccess.GetDataTables();
                    DataTable current = tables?.FirstOrDefault(t => t != null && t.Columns.Count > 0);
                    if (current == null)
                    {
                        header.Text = LocalizationManager.T("No result set is available. Run a query first.");
                        cellInfo.Text = string.Empty;
                        stats.ItemsSource = null;
                        return string.Empty;
                    }

                    if (!ReferenceEquals(current, table))
                    {
                        table = current;
                        columns.ItemsSource = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                    }

                    object grid = GridAccess.GetFocusGridControl();
                    object cell = grid == null ? null : GridAccess.GetProperty(grid, "CurrentCell");
                    if (cell != null)
                    {
                        object rowValue = GridAccess.GetProperty(cell, "RowIndex") ?? GridAccess.GetProperty(cell, "Row");
                        object columnValue = GridAccess.GetProperty(cell, "ColumnIndex") ?? GridAccess.GetProperty(cell, "Column");
                        if (rowValue != null) int.TryParse(rowValue.ToString(), out row);
                        if (columnValue != null) int.TryParse(columnValue.ToString(), out column);
                    }

                    if (column < 0 || column >= table.Columns.Count) column = 0;
                    if (row < 0 || row >= table.Rows.Count) row = 0;

                    // The panel follows the selected rectangle: SSMS exposes the selection differently
                    // between builds, so every known shape is tried and a single cell is the fallback.
                    int startRow = row, startColumn = column, endRow = row, endColumn = column;
                    ReadSelection(grid, ref startRow, ref startColumn, ref endRow, ref endColumn);
                    if (SelectionCount > 1) { /* keep the range */ }

                    DataTable slice = ResultSetStatistics.Slice(table, startRow, startColumn, endRow, endColumn);
                    if (slice == null) slice = table;

                    string columnName = table.Columns[column].ColumnName;
                    string value = row < table.Rows.Count
                        ? Convert.ToString(table.Rows[row][column] ?? string.Empty) ?? string.Empty
                        : string.Empty;

                    bool single = slice.Rows.Count <= 1 && slice.Columns.Count <= 1;
                    header.Text = single
                        ? LocalizationManager.Format("Column '{0}'   (row {1} of {2})", columnName, row + 1, table.Rows.Count)
                        : LocalizationManager.Format("Selection rows {0}-{1}, columns {2}-{3}   ({4} x {5} cells)",
                            Math.Min(startRow, endRow) + 1, Math.Max(startRow, endRow) + 1,
                            Math.Min(startColumn, endColumn) + 1, Math.Max(startColumn, endColumn) + 1,
                            slice.Rows.Count, slice.Columns.Count);
                    cellInfo.Text = value.Length > 300 ? value.Substring(0, 297) + "..." : value;
                    columns.SelectedItem = columnName;

                    List<ResultColumnStatistics> all = ResultSetStatistics.Summarize(slice);
                    stats.ItemsSource = all.Select(s => new
                    {
                        Column = s.Name,
                        Type = s.DataType,
                        NonNull = s.NonNullCount,
                        Null = s.NullCount,
                        Distinct = s.DistinctCount,
                        Min = s.Minimum,
                        Max = s.Maximum,
                        Average = s.Average,
                        Sum = s.Sum,
                        MaxLength = s.MaximumLength
                    }).ToList();

                    // The key includes the range so a new selection is always picked up.
                    return table.Rows.Count + "|" + startRow + "|" + startColumn + "|" + endRow + "|" + endColumn;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Results statistics panel could not read the result set.");
                    return string.Empty;
                }
            }

            private int selectionCount = 1;
            private int SelectionCount => selectionCount;

            /// <summary>
            /// Reads the selected rectangle.  Known shapes: a selection object with Start/End (row,column),
            /// a Selection with Row/Column/RowCount/ColumnCount, or SelectedCells-style cell objects.
            /// </summary>
            private void ReadSelection(object grid, ref int startRow, ref int startColumn, ref int endRow, ref int endColumn)
            {
                selectionCount = 1;
                if (grid == null) return;

                object selection = GridAccess.GetProperty(grid, "Selection")
                    ?? GridAccess.GetNonPublicField(grid, "m_selection");
                if (selection == null) return;

                int firstRow = Pick(selection, "StartRow", "TopRow", "Row", "RowIndex");
                int firstColumn = Pick(selection, "StartColumn", "LeftColumn", "Column", "ColumnIndex");
                int lastRow = Pick(selection, "EndRow", "BottomRow");
                int lastColumn = Pick(selection, "EndColumn", "RightColumn");
                int rowCount = Pick(selection, "RowCount", "Rows", "Height");
                int columnCount = Pick(selection, "ColumnCount", "Columns", "Width");

                if (firstRow < 0 || firstColumn < 0) return;
                if (lastRow < 0) lastRow = rowCount > 0 ? firstRow + rowCount - 1 : firstRow;
                if (lastColumn < 0) lastColumn = columnCount > 0 ? firstColumn + columnCount - 1 : firstColumn;

                startRow = firstRow;
                startColumn = firstColumn;
                endRow = lastRow;
                endColumn = lastColumn;
                selectionCount = Math.Max(1, (Math.Abs(endRow - startRow) + 1) * (Math.Abs(endColumn - startColumn) + 1));
            }

            private static int Pick(object source, params string[] names)
            {
                foreach (string name in names)
                {
                    object value = GridAccess.GetProperty(source, name) ?? GridAccess.GetNonPublicField(source, name);
                    if (value == null) continue;
                    if (int.TryParse(value.ToString(), out int number)) return number;
                }

                return -1;
            }

            private void FindNext()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (table == null) return;

                ResultCellMatch match = ResultSetStatistics.Find(table, find.Text, row, column)
                    ?? ResultSetStatistics.Find(table, find.Text, -1, -1);
                if (match == null)
                {
                    findStatus.Text = LocalizationManager.Format("'{0}' was not found.", find.Text);
                    return;
                }

                row = match.Row;
                column = match.Column;
                findStatus.Text = LocalizationManager.Format("Found in row {0}, column '{1}'.", match.Row + 1, match.ColumnName);
                JumpToCell(match.Row, match.Column);
                RefreshFromSelection();
            }

            /// <summary>Best effort: move the results grid to a cell (same approach as the dialog window).</summary>
            private void JumpToCell(int targetRow, int targetColumn)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    object grid = GridAccess.GetFocusGridControl();
                    object currentCell = grid == null ? null : GridAccess.GetProperty(grid, "CurrentCell");
                    if (currentCell == null) return;

                    object cell = Activator.CreateInstance(currentCell.GetType(), targetRow, targetColumn);
                    GridAccess.SetPropertyValue(grid, "CurrentCell", cell);
                    foreach (string method in new[] { "SelectCell", "SetCurrentCell", "EnsureVisible" })
                    {
                        System.Reflection.MethodInfo info = grid.GetType().GetMethod(method);
                        if (info == null) continue;
                        object[] args = info.GetParameters().Length == 2 ? new object[] { targetRow, targetColumn } : new object[] { targetRow };
                        info.Invoke(grid, args);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not move the result grid to the matched cell.");
                }
            }
        }
    }
}
