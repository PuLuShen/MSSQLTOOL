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
        private static DateTime shownUtc = DateTime.MinValue;
        private static int missedResultsTicks;

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
            if (window != null && window.IsVisible) return;
            Show();
        }

        private static void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Already on screen: only refresh.  Calling Show() again repaints the window, which is what made
            // the panel flicker several times a second (the log showed "shown" repeated in bursts).
            if (window != null && window.IsVisible)
            {
                window.RefreshFromSelection();
                return;
            }

            if (window == null)
            {
                window = new HoverWindow();
                window.Closed += (_, __) => { window.RememberPosition(); window = null; };
            }

            window.RefreshFromSelection();
            window.PlaceNextToResults();
            window.Show();
            shownUtc = DateTime.UtcNow;
            missedResultsTicks = 0;
            MSSQLToolPackage._logger?.Info("Results statistics panel shown (position " + Math.Round(window.Left) + "," + Math.Round(window.Top) + ").");
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

                    object results = null;
                    try { results = GridAccess.GetSQLResultsControl(); } catch (Exception) { results = null; }

                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null && results != null) grid = FindGrid(results);

                    if (results == null && grid == null)
                    {
                        // A results tab is not the active document any more: the user clicked outside the
                        // results.  Three consecutive ticks are required because the shell reports no
                        // results control for a moment while a tab is switching, which used to hide the
                        // panel right after showing it.
                        bool graceOver = (DateTime.UtcNow - shownUtc) > TimeSpan.FromMilliseconds(1200);
                        missedResultsTicks++;
                        if (graceOver && missedResultsTicks >= 3 && !window.IsMouseOver && !window.IsKeyboardFocusWithin)
                        {
                            MSSQLToolPackage._logger?.Info("Results statistics panel hidden: no active results tab for 3 ticks, pointer outside the panel.");
                            window.Hide();
                        }
                        return;
                    }

                    if (missedResultsTicks > 0)
                    {
                        MSSQLToolPackage._logger?.Info("Results statistics panel kept: a results tab is active again after " + missedResultsTicks + " tick(s).");
                        missedResultsTicks = 0;
                    }

                    string key = window.RefreshFromSelection();
                    if (!string.IsNullOrEmpty(key) && !string.Equals(key, lastSelectionKey, StringComparison.Ordinal))
                    {
                        lastSelectionKey = key;
                        if (!window.IsVisible)
                        {
                            window.Show();
                            shownUtc = DateTime.UtcNow;
                            missedResultsTicks = 0;
                            MSSQLToolPackage._logger?.Info("Results statistics panel re-shown after a selection change.");
                        }
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
            private DataTable cachedTable;
            private DateTime cachedTableUtc = DateTime.MinValue;

            internal HoverWindow()
            {
                Title = LocalizationManager.T("Result statistics panel");
                Width = 400;
                SizeToContent = SizeToContent.Height;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                ShowInTaskbar = false;
                // Showing the panel must not take the focus: SSMS paints the grid selection grey while the
                // grid is inactive, and the panel would then hide itself because the grid lost the focus.
                ShowActivated = false;
                Focusable = false;
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
                    // Reading every result set as a DataTable costs milliseconds and used to run four times a
                    // second; a short cache keeps the panel responsive without freezing the grid.
                    if (cachedTable == null || (DateTime.UtcNow - cachedTableUtc) > TimeSpan.FromSeconds(1.5))
                    {
                        List<DataTable> tables = GridAccess.GetDataTables();
                        cachedTable = tables?.FirstOrDefault(t => t != null && t.Columns.Count > 0);
                        cachedTableUtc = DateTime.UtcNow;
                    }

                    DataTable current = cachedTable;
                    if (current == null)
                    {
                        selectionText.Text = LocalizationManager.T("No result set is available. Run a query first.");
                        Clear();
                        return string.Empty;
                    }

                    table = current;

                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null)
                    {
                        try { grid = FindGrid(GridAccess.GetSQLResultsControl()); } catch (Exception) { grid = null; }
                    }

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

                    object grid = GridAccess.GetFocusGridControl();
                    int storedIndex = -1;
                    string matched = null;

                    // The grid knows its columns even when the DataTable we fetched belongs to another
                    // result set, so the authoritative list comes from GridColumnsInfo.
                    List<string> names = ReadGridColumnNames(grid);
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (names[i] == null || names[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        storedIndex = i;
                        matched = names[i];
                        break;
                    }

                    if (storedIndex < 0 && table != null)
                    {
                        for (int i = 0; i < table.Columns.Count; i++)
                        {
                            if (table.Columns[i].ColumnName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            storedIndex = i;
                            matched = table.Columns[i].ColumnName;
                            break;
                        }
                    }

                    if (storedIndex < 0 || grid == null)
                    {
                        columnStatus.Text = LocalizationManager.Format("No column matches '{0}'.", needle);
                        return;
                    }

                    // FirstScrollableColumn is a UI index; convert from the storage index when possible.
                    int uiIndex = storedIndex;
                    try
                    {
                        MethodInfo map = grid.GetType().GetMethod("GetUIColumnIndexByStorageIndex");
                        object mapped = map?.Invoke(grid, new object[] { storedIndex });
                        if (mapped != null && int.TryParse(mapped.ToString(), out int converted) && converted >= 0) uiIndex = converted;
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Debug(ex, "Could not map the column index.");
                    }

                    GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", uiIndex);
                    columnStatus.Text = LocalizationManager.Format("Scrolled to column '{0}'.", matched);
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

            /// <summary>Column headers straight from the grid (authoritative, whatever the DataTable says).</summary>
            private static List<string> ReadGridColumnNames(object grid)
            {
                var names = new List<string>();
                if (grid == null) return names;

                try
                {
                    object infos = GridAccess.GetProperty(grid, "GridColumnsInfo");
                    var enumerable = infos as IEnumerable;
                    if (enumerable == null) return names;

                    foreach (object info in enumerable)
                    {
                        if (info == null) continue;
                        object header = GridAccess.GetProperty(info, "HeaderText") ?? GridAccess.GetProperty(info, "Text")
                            ?? GridAccess.GetProperty(info, "ColumnName");
                        names.Add(header == null ? string.Empty : header.ToString());
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not read the grid column headers.");
                }

                return names;
            }

            /// <summary>
            /// Puts the panel to the right of the results grid (or restores the last position), so it never
            /// covers the rows the user is reading.
            /// </summary>
            internal void PlaceNextToResults()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    string saved = SettingsManager.GetSqlCompletionSettings()?.resultsPanelPosition;
                    double left, top;
                    if (TryParse(saved, out left, out top) && IsOnScreen(left, top))
                    {
                        Left = left;
                        Top = top;
                        return;
                    }

                    var control = GridAccess.GetFocusGridControl() as WinForms.Control;
                    if (control == null) return;

                    var screen = WinForms.Screen.FromControl(control);
                    System.Drawing.Point origin = control.PointToScreen(System.Drawing.Point.Empty);
                    double candidateLeft = origin.X + control.Width + 12;
                    double candidateTop = origin.Y + 60;
                    if (candidateLeft + ActualWidth > screen.WorkingArea.Right)
                        candidateLeft = Math.Max(screen.WorkingArea.Left, origin.X - Width - 12);

                    Left = candidateLeft;
                    Top = Math.Min(candidateTop, screen.WorkingArea.Bottom - Height - 12);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not place the results statistics panel.");
                }
            }

            /// <summary>Remembers the position when the user moves or closes the panel.</summary>
            internal void RememberPosition()
            {
                try
                {
                    var settings = SettingsManager.GetSqlCompletionSettings();
                    if (settings == null) return;
                    settings.resultsPanelPosition = Math.Round(Left) + "," + Math.Round(Top);
                    SettingsManager.SaveSqlCompletionSettings(settings);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not remember the results statistics panel position.");
                }
            }

            private static bool TryParse(string text, out double left, out double top)
            {
                left = top = 0;
                if (string.IsNullOrWhiteSpace(text)) return false;
                string[] parts = text.Split(',');
                return parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out left)
                    && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out top);
            }

            private static bool IsOnScreen(double left, double top)
            {
                foreach (WinForms.Screen screen in WinForms.Screen.AllScreens)
                {
                    if (left >= screen.WorkingArea.Left - 50 && left <= screen.WorkingArea.Right - 50
                        && top >= screen.WorkingArea.Top - 50 && top <= screen.WorkingArea.Bottom - 50) return true;
                }

                return false;
            }

            private static string Format(decimal value, int count)
                => count == 0 ? "-" : value.ToString("#,0.####", CultureInfo.CurrentCulture);
        }
    }
}
