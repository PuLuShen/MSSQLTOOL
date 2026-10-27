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
    /// Floating statistics panel for the results grid.
    ///
    /// It has no switch to remember: clicking in the results brings it up, clicking outside the results
    /// takes it away.  The selection is read from the grid's own API (SelectedCells / GetCurrentCell), so
    /// no clipboard step is involved, and the numbers of the selected rectangle are shown as count, sum,
    /// average, minimum, maximum and distinct.  Column search scrolls the results to the matching column.
    /// </summary>
    internal static class ResultsHoverPopup
    {
        private static HoverWindow window;
        private static DispatcherTimer timer;
        private static bool enabled;
        private static readonly HashSet<WinForms.Control> hooked = new HashSet<WinForms.Control>();
        private static string lastSelectionKey = string.Empty;

        internal static bool IsEnabled => enabled;

        /// <summary>Installs the "click in the results shows the panel" hook; idempotent.</summary>
        internal static void HookGrid(object grid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var control = grid as WinForms.Control;
            if (control == null || hooked.Contains(control)) return;

            try
            {
                control.MouseUp += (sender, e) =>
                {
                    try
                    {
                        if (e.Button == WinForms.MouseButtons.Left || e.Button == WinForms.MouseButtons.Right)
                        {
                            // The panel follows the results: no menu switch needed.
                            Enable();
                        }
                        if (e.Button == WinForms.MouseButtons.Right) AttachContextMenu(GridAccess.GetSQLResultsControl());
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Debug(ex, "Results statistics panel could not follow the click.");
                    }
                };
                control.MouseDown += (sender, e) =>
                {
                    if (e.Button == WinForms.MouseButtons.Left) Enable();
                };
                hooked.Add(control);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not hook the results grid clicks.");
            }
        }

        /// <summary>Adds a toggle to the grid context menu as a convenience; the panel does not need it.</summary>
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

        internal static void Toggle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            enabled = !enabled;
            if (enabled) Show(); else Hide();
        }

        private static void Enable()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            enabled = true;
            Show();
        }

        private static void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (window == null)
            {
                window = new HoverWindow();
                window.Closed += (_, __) => window = null;
            }

            window.RefreshFromSelection();
            window.Show();
            if (!window.IsMouseOver) window.Activate();
            StartTimer();
        }

        private static void Hide()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StopTimer();
            window?.Hide();
        }

        private static void StartTimer()
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, __) =>
            {
                try
                {
                    if (window == null) { StopTimer(); return; }

                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null)
                    {
                        // Clicking anywhere outside the results closes the panel.
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

        /// <summary>The panel itself: a small grid of aligned label/value pairs.</summary>
        private sealed class HoverWindow : Window
        {
            private readonly TextBlock selectionText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            private readonly TextBlock ignoredText = new TextBlock
            {
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            private readonly TextBox columnSearch = new TextBox { Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
            private readonly TextBlock columnStatus = new TextBlock { Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

            private readonly Dictionary<string, TextBlock> values = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
            private DataTable table;

            internal HoverWindow()
            {
                Title = LocalizationManager.T("Result statistics panel");
                Width = 400;
                SizeToContent = SizeToContent.Height;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                ShowInTaskbar = false;
                WindowStyle = WindowStyle.ToolWindow;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.White;

                var root = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
                root.Children.Add(selectionText);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                AddRow(grid, 0, "Count", "数量");
                AddRow(grid, 1, "Sum", "合计");
                AddRow(grid, 2, "Average", "平均");
                AddRow(grid, 3, "Min", "最小");
                AddRow(grid, 4, "Max", "最大");
                AddRow(grid, 5, "Distinct", "去重");
                root.Children.Add(grid);
                root.Children.Add(ignoredText);

                var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                searchRow.Children.Add(new TextBlock
                {
                    Text = LocalizationManager.T("Find column:"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                columnSearch.Width = 180;
                columnSearch.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) JumpToColumn(); };
                searchRow.Children.Add(columnSearch);
                var jump = new Button { Content = LocalizationManager.T("Go"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
                jump.Click += (_, __) => JumpToColumn();
                searchRow.Children.Add(jump);
                var close = new Button { Content = LocalizationManager.T("Close"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
                close.Click += (_, __) => Hide();
                searchRow.Children.Add(close);
                root.Children.Add(searchRow);
                root.Children.Add(columnStatus);

                Content = root;
            }

            private void AddRow(Grid grid, int row, string key, string label)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var caption = new TextBlock
                {
                    Text = LocalizationManager.T(label + ":"),
                    Opacity = 0.75,
                    Margin = new Thickness(0, 1, 8, 3),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(caption, row);
                Grid.SetColumn(caption, 0);
                grid.Children.Add(caption);

                var value = new TextBlock
                {
                    Text = "-",
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 1, 0, 3),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
                values[key] = value;
            }

            /// <summary>Reads the grid selection and fills the rows with the numbers of that rectangle.</summary>
            internal string RefreshFromSelection()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    List<DataTable> tables = GridAccess.GetDataTables();
                    DataTable current = tables?.FirstOrDefault(t => t != null && t.Columns.Count > 0);
                    if (current == null)
                    {
                        selectionText.Text = LocalizationManager.T("No result set is available. Run a query first.");
                        Clear();
                        return string.Empty;
                    }

                    table = current;

                    object grid = GridAccess.GetFocusGridControl();
                    int startRow = 0, startColumn = 0, endRow = current.Rows.Count - 1, endColumn = 0;
                    bool gotSelection = ReadSelection(grid, ref startRow, ref startColumn, ref endRow, ref endColumn);

                    var numbers = new List<decimal>();
                    int textCells = 0;
                    int rowFrom = Math.Max(0, Math.Min(startRow, endRow));
                    int rowTo = Math.Min(current.Rows.Count - 1, Math.Max(startRow, endRow));
                    int columnFrom = Math.Max(0, Math.Min(startColumn, endColumn));
                    int columnTo = Math.Min(current.Columns.Count - 1, Math.Max(startColumn, endColumn));

                    for (int row = rowFrom; row <= rowTo; row++)
                        for (int column = columnFrom; column <= columnTo; column++)
                        {
                            string text = Convert.ToString(current.Rows[row][column], CultureInfo.CurrentCulture) ?? string.Empty;
                            List<decimal> parsed = ResultSetStatistics.ParseNumbers(text);
                            if (parsed.Count > 0) numbers.AddRange(parsed);
                            else if (text.Trim().Length > 0) textCells++;
                        }

                    ResultSetStatistics.NumericSummary summary = ResultSetStatistics.SummarizeNumbers(numbers);
                    int cellCount = (rowTo - rowFrom + 1) * (columnTo - columnFrom + 1);

                    selectionText.Text = gotSelection
                        ? LocalizationManager.Format("Selection: rows {0}-{1}, columns {2}-{3}   ({4} cells)",
                            rowFrom + 1, rowTo + 1, columnFrom + 1, columnTo + 1, cellCount)
                        : LocalizationManager.T("Nothing is selected - select cells in the result grid.");

                    values["Count"].Text = summary.Count.ToString(CultureInfo.CurrentCulture);
                    values["Sum"].Text = Format(summary.Sum, summary.Count);
                    values["Average"].Text = Format(summary.Average, summary.Count);
                    values["Min"].Text = Format(summary.Minimum, summary.Count);
                    values["Max"].Text = Format(summary.Maximum, summary.Count);
                    values["Distinct"].Text = summary.Count == 0 ? "-" : summary.Distinct.ToString(CultureInfo.CurrentCulture);

                    ignoredText.Text = summary.Count == 0
                        ? LocalizationManager.T("No numbers in the selection.")
                        : LocalizationManager.Format("{0} cell(s) with text or empty values were ignored.", textCells);

                    return rowFrom + "|" + rowTo + "|" + columnFrom + "|" + columnTo;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Results statistics panel could not read the result set.");
                    return string.Empty;
                }
            }

            private void Clear()
            {
                foreach (TextBlock value in values.Values) value.Text = "-";
                ignoredText.Text = string.Empty;
            }

            /// <summary>
            /// Column search: typing part of a column name and pressing Go scrolls the results to that
            /// column, so a wide result set can be navigated by name instead of by scrolling.
            /// </summary>
            private void JumpToColumn()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    if (table == null) return;

                    string needle = (columnSearch.Text ?? string.Empty).Trim();
                    if (needle.Length == 0)
                    {
                        columnStatus.Text = LocalizationManager.T("Type part of a column name.");
                        return;
                    }

                    int index = -1;
                    string matched = null;
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        string name = table.Columns[i].ColumnName;
                        if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        index = i;
                        matched = name;
                        break;
                    }

                    object grid = GridAccess.GetFocusGridControl();
                    if (index < 0 || grid == null)
                    {
                        columnStatus.Text = LocalizationManager.Format("No column matches '{0}'.", needle);
                        return;
                    }

                    GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", index);
                    columnStatus.Text = LocalizationManager.Format("Scrolled to column '{0}' (position {1}).", matched, index + 1);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not scroll to the result column.");
                }
            }

            /// <summary>
            /// Reads the selected rectangle through SelectedCells (blocks of cells) and falls back to
            /// GetCurrentCell(out row, out column).
            /// </summary>
            private static bool ReadSelection(object grid, ref int startRow, ref int startColumn, ref int endRow, ref int endColumn)
            {
                if (grid == null) return false;

                try
                {
                    var enumerable = GridAccess.GetProperty(grid, "SelectedCells") as IEnumerable;
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

            private static int ToInt(object value, int fallback)
                => value != null && int.TryParse(value.ToString(), out int number) ? number : fallback;

            private static long ToLong(object value, long fallback)
                => value != null && long.TryParse(value.ToString(), out long number) ? number : fallback;

            private static string Format(decimal value, int count)
                => count == 0 ? "-" : value.ToString("#,0.####", CultureInfo.CurrentCulture);
        }
    }
}
