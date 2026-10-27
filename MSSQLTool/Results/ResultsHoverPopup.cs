using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using WinForms = System.Windows.Forms;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Floating panel that follows the selection in the results grid and reports the numbers in it:
    /// count, sum, average, minimum, maximum and distinct.  Toggled from the grid context menu or from the
    /// Tools menu; the selection is read through the grid's own API (SelectedCells / GetCurrentCell), so no
    /// clipboard round-trip is needed.
    /// </summary>
    internal static class ResultsHoverPopup
    {
        private static HoverWindow window;
        private static DispatcherTimer timer;
        private static bool enabled;
        private static readonly HashSet<WinForms.Control> hooked = new HashSet<WinForms.Control>();
        private static string lastSelectionKey = string.Empty;

        internal static bool IsEnabled => enabled;

        /// <summary>Adds the toggle to the results grid context menu; idempotent, called on every right click.</summary>
        internal static void AttachContextMenu(object sqlResultsControl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sqlResultsControl == null) return;

            try
            {
                object grid = FindGrid(sqlResultsControl);
                if (grid == null) return;

                object menu = GridAccess.GetProperty(grid, "ContextMenu") ?? GridAccess.GetNonPublicField(grid, "m_contextMenu");
                var strip = menu as WinForms.ContextMenuStrip;
                if (strip == null) return;

                if (strip.Items.OfType<WinForms.ToolStripMenuItem>().Any(item => item.Name == "MSSQLToolResultsToggle")) return;

                var toggle = new WinForms.ToolStripMenuItem(LocalizationManager.T("Result statistics panel"))
                {
                    Name = "MSSQLToolResultsToggle",
                    CheckOnClick = true,
                    Checked = enabled
                };
                toggle.Click += (_, __) => Toggle();
                strip.Items.Add(new WinForms.ToolStripSeparator());
                strip.Items.Add(toggle);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not add the results statistics toggle to the grid menu.");
            }
        }

        /// <summary>Installs the right-click hook once per grid so the menu toggle gets a chance to appear.</summary>
        internal static void HookGrid(object grid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var control = grid as WinForms.Control;
            if (control == null || hooked.Contains(control)) return;

            try
            {
                control.MouseUp += (sender, e) =>
                {
                    if (e.Button != WinForms.MouseButtons.Right) return;
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
            window?.Hide();
        }

        /// <summary>
        /// Polls the grid: a new selection updates the panel, leaving the results hides it.  The feature
        /// stays enabled, so the next selection brings the panel back.
        /// </summary>
        private static void StartTimer()
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += (_, __) =>
            {
                try
                {
                    if (!enabled || window == null) { StopTimer(); return; }

                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null)
                    {
                        if (!window.IsMouseOver) window.Hide();
                        return;
                    }

                    string key = window.RefreshFromSelection();
                    if (!string.IsNullOrEmpty(key) && !string.Equals(key, lastSelectionKey, StringComparison.Ordinal))
                    {
                        lastSelectionKey = key;
                        if (!window.IsVisible) window.Show();
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

        internal static object FindGrid(object sqlResultsControl)
        {
            foreach (string field in new[] { "m_grid", "grid", "m_dataGrid" })
            {
                object grid = GridAccess.GetNonPublicField(sqlResultsControl, field);
                if (grid != null) return grid;
            }

            return GridAccess.GetFocusGridControl();
        }

        /// <summary>The panel.</summary>
        private sealed class HoverWindow : Window
        {
            private readonly TextBlock header = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            private readonly TextBlock totals = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
            private readonly TextBlock detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            private readonly ComboBox columns = new ComboBox { Height = 24, Width = 260 };

            private DataTable table;

            internal HoverWindow()
            {
                Title = LocalizationManager.T("Result statistics panel");
                Width = 470;
                Height = 230;
                Topmost = true;
                ShowInTaskbar = false;
                WindowStyle = WindowStyle.ToolWindow;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.White;

                var panel = new StackPanel { Margin = new Thickness(12) };
                panel.Children.Add(header);
                panel.Children.Add(totals);
                panel.Children.Add(detail);

                var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                jumpRow.Children.Add(new TextBlock
                {
                    Text = LocalizationManager.T("Select column:"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                columns.SelectionChanged += (_, __) => SelectColumn();
                jumpRow.Children.Add(columns);
                var close = new Button { Content = LocalizationManager.T("Close"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0) };
                close.Click += (_, __) => Hide();
                jumpRow.Children.Add(close);
                panel.Children.Add(jumpRow);

                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }

            /// <summary>Reads the grid selection and recomputes the numbers of that rectangle.</summary>
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
                        totals.Text = string.Empty;
                        detail.Text = string.Empty;
                        return string.Empty;
                    }

                    if (!ReferenceEquals(current, table))
                    {
                        table = current;
                        columns.ItemsSource = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                    }

                    object grid = GridAccess.GetFocusGridControl();
                    int startRow = 0, startColumn = 0, endRow = table.Rows.Count - 1, endColumn = 0;
                    bool gotSelection = ReadSelection(grid, ref startRow, ref startColumn, ref endRow, ref endColumn);

                    var numbers = new List<decimal>();
                    int textCells = 0;
                    int rowFrom = Math.Max(0, Math.Min(startRow, endRow));
                    int rowTo = Math.Min(table.Rows.Count - 1, Math.Max(startRow, endRow));
                    int columnFrom = Math.Max(0, Math.Min(startColumn, endColumn));
                    int columnTo = Math.Min(table.Columns.Count - 1, Math.Max(startColumn, endColumn));

                    for (int row = rowFrom; row <= rowTo; row++)
                        for (int column = columnFrom; column <= columnTo; column++)
                        {
                            string text = Convert.ToString(table.Rows[row][column], CultureInfo.CurrentCulture) ?? string.Empty;
                            List<decimal> parsed = ResultSetStatistics.ParseNumbers(text);
                            if (parsed.Count > 0) numbers.AddRange(parsed);
                            else if (text.Trim().Length > 0) textCells++;
                        }

                    ResultSetStatistics.NumericSummary summary = ResultSetStatistics.SummarizeNumbers(numbers);
                    int cellCount = (rowTo - rowFrom + 1) * (columnTo - columnFrom + 1);

                    header.Text = gotSelection
                        ? LocalizationManager.Format("Selection: rows {0}-{1}, columns {2}-{3}   ({4} cells)",
                            rowFrom + 1, rowTo + 1, columnFrom + 1, columnTo + 1, cellCount)
                        : LocalizationManager.T("Nothing is selected - select cells in the result grid.");

                    totals.Text = summary.Count == 0
                        ? LocalizationManager.T("No numbers in the selection.")
                        : LocalizationManager.Format("Count {0}   Sum {1}   Average {2}   Min {3}   Max {4}   Distinct {5}",
                            summary.Count, Dec(summary.Sum), Dec(summary.Average), Dec(summary.Minimum), Dec(summary.Maximum), summary.Distinct);

                    detail.Text = summary.Count == 0
                        ? string.Empty
                        : LocalizationManager.Format("{0} cell(s) with text or empty values were ignored.", textCells);

                    if (columns.SelectedItem == null && table.Columns.Count > 0)
                        columns.SelectedItem = table.Columns[columnFrom].ColumnName;

                    return rowFrom + "|" + rowTo + "|" + columnFrom + "|" + columnTo;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Results statistics panel could not read the result set.");
                    return string.Empty;
                }
            }

            /// <summary>
            /// Reads the selected rectangle through SelectedCells (a collection of cell blocks) and falls
            /// back to GetCurrentCell(out row, out column).
            /// </summary>
            private static bool ReadSelection(object grid, ref int startRow, ref int startColumn, ref int endRow, ref int endColumn)
            {
                if (grid == null) return false;

                try
                {
                    object blocks = GridAccess.GetProperty(grid, "SelectedCells");
                    var enumerable = blocks as IEnumerable;
                    if (enumerable != null)
                    {
                        int minRow = int.MaxValue, minColumn = int.MaxValue, maxRow = -1, maxColumn = -1;
                        foreach (object block in enumerable)
                        {
                            if (block == null) continue;
                            int x = ToInt(GridAccess.GetProperty(block, "X"), -1);
                            long y = ToLong(GridAccess.GetProperty(block, "Y"), -1);
                            int width = ToInt(GridAccess.GetProperty(block, "Width"), 1);
                            long height = ToLong(GridAccess.GetProperty(block, "Height"), 1);
                            if (x < 0 || y < 0) continue;

                            minColumn = Math.Min(minColumn, x);
                            maxColumn = Math.Max(maxColumn, x + Math.Max(1, width) - 1);
                            minRow = Math.Min(minRow, (int)y);
                            maxRow = Math.Max(maxRow, (int)y + (int)Math.Max(1, height) - 1);
                        }

                        if (maxRow >= 0 && maxColumn >= 0)
                        {
                            startRow = minRow;
                            endRow = maxRow;
                            startColumn = minColumn;
                            endColumn = maxColumn;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "SelectedCells could not be read.");
                }

                try
                {
                    MethodInfo current = grid.GetType().GetMethod("GetCurrentCell");
                    if (current != null)
                    {
                        object[] args = { 0L, 0 };
                        current.Invoke(grid, args);
                        long row = ToLong(args[0], -1);
                        int column = ToInt(args[1], -1);
                        if (row >= 0 && column >= 0)
                        {
                            startRow = endRow = (int)row;
                            startColumn = endColumn = column;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "GetCurrentCell could not be read.");
                }

                return false;
            }

            /// <summary>Moves the grid to a column, selects the whole column and scrolls it into view.</summary>
            private void SelectColumn()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null || table == null || columns.SelectedItem == null) return;

                    int column = ResultSetStatistics.FindColumn(table, columns.SelectedItem.ToString());
                    if (column < 0) return;

                    long rows = Math.Max(1, table.Rows.Count);
                    object blocks = GridAccess.GetProperty(grid, "SelectedCells");
                    if (blocks != null)
                    {
                        Type collectionType = blocks.GetType();
                        object collection = Activator.CreateInstance(collectionType);
                        Type blockType = collectionType.Assembly.GetType("Microsoft.SqlServer.Management.UI.Grid.BlockOfCells");
                        if (blockType != null)
                        {
                            object block = Activator.CreateInstance(blockType);
                            GridAccess.SetPropertyValue(block, "X", column);
                            GridAccess.SetPropertyValue(block, "Y", 0L);
                            GridAccess.SetPropertyValue(block, "Width", 1);
                            GridAccess.SetPropertyValue(block, "Height", rows);

                            MethodInfo add = collectionType.GetMethod("Add", new[] { blockType });
                            add?.Invoke(collection, new[] { block });
                            GridAccess.SetPropertyValue(grid, "SelectedCells", collection);
                        }
                    }

                    GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", column);
                    GridAccess.SetPropertyValue(grid, "FirstScrollableRow", 0u);
                    (grid as WinForms.Control)?.Focus();
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not select the result column.");
                }
            }

            private static int ToInt(object value, int fallback)
                => value != null && int.TryParse(value.ToString(), out int number) ? number : fallback;

            private static long ToLong(object value, long fallback)
                => value != null && long.TryParse(value.ToString(), out long number) ? number : fallback;

            private static string Dec(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
