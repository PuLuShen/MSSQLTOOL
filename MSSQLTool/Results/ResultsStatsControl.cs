using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using WinForms = System.Windows.Forms;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Docked tool window content: the statistics of the selection in the results grid, a column search
    /// that scrolls the results to a column, and column freezing.
    ///
    /// It is a UserControl inside a real SSMS tool window on purpose: the shell then owns the keyboard
    /// focus, so typing in the search box no longer sends Delete to the SQL editor (which happened while
    /// this was a separate floating window).
    /// </summary>
    public sealed class ResultsStatsControl : UserControl, IDisposable
    {
        private readonly TextBlock selectionText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        private readonly TextBlock columnText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        private readonly Dictionary<string, TextBlock> values = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        private readonly ComboBox columnSearch = new ComboBox
        {
            Height = 24,
            Width = 170,
            IsEditable = true,
            IsTextSearchEnabled = false,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly TextBox freezeCount = new TextBox { Height = 24, Width = 46, Text = "0", VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

        private List<string> allColumns = new List<string>();
        private string lastColumnSignature = string.Empty;
        private bool filtering;
        private DataTable table;
        private DataTable cachedTable;
        private object cachedGrid;
        private System.Windows.Threading.DispatcherTimer timer;

        public ResultsStatsControl()
        {
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? Brushes.Transparent;

            var root = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            root.Children.Add(selectionText);
            root.Children.Add(columnText);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddRow(grid, 0, "Count", "数量");
            AddRow(grid, 1, "Sum", "合计");
            AddRow(grid, 2, "Average", "平均");
            AddRow(grid, 3, "Min", "最小");
            AddRow(grid, 4, "Max", "最大");
            AddRow(grid, 5, "Distinct", "去重");
            root.Children.Add(grid);

            root.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 6),
                Background = (Brush)Application.Current?.TryFindResource("ToolThemeSubtleBorderBrush") ?? Brushes.LightGray
            });

            var searchRow = new WrapPanel { Orientation = Orientation.Horizontal };
            searchRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Find column:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            columnSearch.KeyDown += (_, e) => { if (e.Key == Key.Enter) JumpToColumn(); };
            columnSearch.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, __) => ApplyFilter()));
            searchRow.Children.Add(columnSearch);
            var jump = new Button { Content = LocalizationManager.T("Go"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            jump.Click += (_, __) => JumpToColumn();
            searchRow.Children.Add(jump);
            root.Children.Add(searchRow);

            var freezeRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            freezeRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Freeze first N columns:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            freezeRow.Children.Add(freezeCount);
            var freeze = new Button { Content = LocalizationManager.T("Freeze"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            freeze.Click += (_, __) => FreezeColumns();
            freezeRow.Children.Add(freeze);
            var unfreeze = new Button { Content = LocalizationManager.T("Unfreeze"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            unfreeze.Click += (_, __) => { freezeCount.Text = "0"; FreezeColumns(); };
            freezeRow.Children.Add(unfreeze);
            root.Children.Add(freezeRow);

            root.Children.Add(status);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Loaded += (_, __) => StartTimer();
            Unloaded += (_, __) => StopTimer();
            IsVisibleChanged += (_, __) => { if (IsVisible) { StartTimer(); Refresh(); } else StopTimer(); };
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

        /// <summary>The window is open: poll the results grid so the numbers follow the selection.</summary>
        private void StartTimer()
        {
            if (timer != null) return;
            timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, __) => { try { Refresh(); } catch (Exception) { } };
            timer.Start();
        }

        private void StopTimer()
        {
            if (timer == null) return;
            timer.Stop();
            timer = null;
        }

        /// <summary>Reads the current selection and repaints the rows.</summary>
        internal void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                List<DataTable> tables = GridAccess.GetDataTables();
                DataTable current = tables?.FirstOrDefault(t => t != null && t.Columns.Count > 0);
                // Tool windows take the focus away from the results grid, and the grid reader then
                // returns nothing; keep the last good table so the panel keeps working.
                if (current == null) current = cachedTable;
                else cachedTable = current;
                if (current == null)
                {
                    selectionText.Text = LocalizationManager.T("No result set is available. Run a query first.");
                    columnText.Text = string.Empty;
                    Clear();
                    return;
                }

                table = current;

                object grid = GridAccess.GetFocusGridControl();
                if (grid == null) grid = FindGrid(SafeResultsControl());
                if (grid == null) grid = cachedGrid;
                if (grid != null) cachedGrid = grid;

                int startRow = 0, startColumn = 0, endRow = current.Rows.Count - 1, endColumn = 0;
                bool gotSelection = ReadSelection(grid, ref startRow, ref startColumn, ref endRow, ref endColumn);

                int rowFrom = Math.Max(0, Math.Min(startRow, endRow));
                int rowTo = Math.Min(current.Rows.Count - 1, Math.Max(startRow, endRow));
                int columnFrom = Math.Max(0, Math.Min(startColumn, endColumn));
                int columnTo = Math.Min(current.Columns.Count - 1, Math.Max(startColumn, endColumn));

                var numbers = new List<decimal>();
                var dates = new List<DateTime>();
                int textCells = 0;
                for (int row = rowFrom; row <= rowTo; row++)
                    for (int column = columnFrom; column <= columnTo; column++)
                    {
                        object raw = current.Rows[row][column];
                        if (raw == null || raw == DBNull.Value) continue;

                        string text = Convert.ToString(raw, CultureInfo.CurrentCulture) ?? string.Empty;
                        List<decimal> parsed = ResultSetStatistics.ParseNumbers(text);
                        if (parsed.Count > 0) { numbers.AddRange(parsed); continue; }

                        // Dates count too: they get a count, an earliest and a latest.
                        if (raw is DateTime dateValue) { dates.Add(dateValue); continue; }
                        DateTime parsedDate;
                        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out parsedDate))
                        {
                            dates.Add(parsedDate);
                            continue;
                        }

                        if (text.Trim().Length > 0) textCells++;
                    }

                ResultSetStatistics.NumericSummary summary = ResultSetStatistics.SummarizeNumbers(numbers);
                int cellCount = (rowTo - rowFrom + 1) * (columnTo - columnFrom + 1);

                selectionText.Text = gotSelection
                    ? LocalizationManager.Format("Selection: rows {0}-{1}, columns {2}-{3}   ({4} cells)",
                        rowFrom + 1, rowTo + 1, columnFrom + 1, columnTo + 1, cellCount)
                    : LocalizationManager.T("Nothing is selected - select cells in the result grid.");

                List<string> headers = ReadColumnNames(grid);
                UpdateColumns(headers);
                columnText.Text = LocalizationManager.Format("Column(s): {0}", string.Join(", ",
                    Enumerable.Range(columnFrom, columnTo - columnFrom + 1).Select(i =>
                        i < headers.Count && !string.IsNullOrWhiteSpace(headers[i]) ? headers[i] : current.Columns[i].ColumnName)));

                if (summary.Count == 0 && dates.Count > 0)
                {
                    DateTime earliest = dates.Min(), latest = dates.Max();
                    values["Count"].Text = dates.Count.ToString(CultureInfo.CurrentCulture);
                    values["Sum"].Text = "-";
                    values["Average"].Text = "-";
                    values["Min"].Text = earliest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                    values["Max"].Text = latest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                    values["Distinct"].Text = dates.Distinct().Count().ToString(CultureInfo.CurrentCulture);
                    status.Text = LocalizationManager.Format("Date/time selection: {0} value(s), earliest {1}, latest {2}.",
                        dates.Count, earliest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), latest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                    return;
                }

                values["Count"].Text = summary.Count.ToString(CultureInfo.CurrentCulture);
                values["Sum"].Text = Dec(summary.Sum, summary.Count);
                values["Average"].Text = Dec(summary.Average, summary.Count);
                values["Min"].Text = Dec(summary.Minimum, summary.Count);
                values["Max"].Text = Dec(summary.Maximum, summary.Count);
                values["Distinct"].Text = summary.Count == 0 ? "-" : summary.Distinct.ToString(CultureInfo.CurrentCulture);

                status.Text = summary.Count == 0
                    ? LocalizationManager.T("No numbers in the selection.")
                    : LocalizationManager.Format("{0} cell(s) with text or empty values were ignored.", textCells);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "The results statistics window could not read the result set.");
            }
        }

        private void Clear()
        {
            foreach (TextBlock value in values.Values) value.Text = "-";
            status.Text = string.Empty;
        }

        /// <summary>Column names straight from the result set schema of the grid.</summary>
        private static List<string> ReadColumnNames(object grid)
        {
            var names = new List<string>();
            if (grid == null) return names;

            try
            {
                object storage = GridAccess.GetProperty(grid, "GridStorage") ?? GridAccess.GetNonPublicField(grid, "m_gridStorage");
                var schema = storage == null ? null : GridAccess.GetNonPublicField(storage, "m_schemaTable") as DataTable;
                if (schema != null)
                {
                    int index = schema.Columns.Contains("ColumnName") ? schema.Columns["ColumnName"].Ordinal : -1;
                    if (index >= 0)
                        foreach (DataRow row in schema.Rows)
                            names.Add(row[index] == null || row[index] == DBNull.Value ? string.Empty : row[index].ToString());
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not read the result schema.");
            }

            return names;
        }

        /// <summary>Search history first, then the result columns; the list is only rebuilt when it changes.</summary>
        private void UpdateColumns(List<string> headers)
        {
            var names = new List<string>();

            string saved = SettingsManager.GetSqlCompletionSettings()?.resultsPanelColumnSearch;
            if (!string.IsNullOrWhiteSpace(saved))
                names.AddRange(saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => h.Trim()).Where(h => h.Length > 0));

            foreach (string header in headers ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(header) && !names.Contains(header, StringComparer.OrdinalIgnoreCase))
                    names.Add(header);

            if (table != null)
                foreach (DataColumn column in table.Columns)
                    if (!names.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase))
                        names.Add(column.ColumnName);

            allColumns = names;
            string signature = string.Join("|", names);
            if (string.Equals(signature, lastColumnSignature, StringComparison.Ordinal)) return;

            lastColumnSignature = signature;
            ApplyFilter();
        }

        /// <summary>Narrows the dropdown to the columns that contain what has been typed.</summary>
        private void ApplyFilter()
        {
            if (filtering || allColumns.Count == 0) return;
            try
            {
                filtering = true;
                string text = (columnSearch.Text ?? string.Empty).Trim();
                List<string> filtered = text.Length == 0
                    ? allColumns
                    : allColumns.Where(c => c.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (filtered.Count == 0) filtered = allColumns;

                string keep = columnSearch.Text;
                columnSearch.ItemsSource = filtered;
                columnSearch.Text = keep;
                if (!columnSearch.IsDropDownOpen) columnSearch.IsDropDownOpen = true;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not filter the column list.");
            }
            finally
            {
                filtering = false;
            }
        }

        /// <summary>Exact name first, then prefix, then substring.</summary>
        private static int MatchColumn(List<string> names, string needle)
        {
            for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], needle, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < names.Count; i++) if (names[i] != null && names[i].StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < names.Count; i++) if (names[i] != null && names[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return -1;
        }

        /// <summary>Scrolls the results to the column by walking the caret, one key at a time.</summary>
        private void JumpToColumn()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string needle = (columnSearch.Text ?? string.Empty).Trim();
                if (needle.Length == 0)
                {
                    status.Text = LocalizationManager.T("Type part of a column name.");
                    return;
                }

                object grid = GridAccess.GetFocusGridControl() ?? FindGrid(SafeResultsControl()) ?? cachedGrid;
                List<string> names = ReadColumnNames(grid);
                int target = MatchColumn(names.Count > 0 ? names : allColumns, needle);
                if (target < 0 || grid == null)
                {
                    status.Text = LocalizationManager.Format("No column matches '{0}'.", needle);
                    return;
                }

                // The keystrokes must reach the results grid, not this tool window: give the focus
                // back to the grid (and its host window) before sending anything.
                var focus = grid as WinForms.Control;
                if (focus == null)
                {
                    status.Text = LocalizationManager.T("The result grid is not available right now.");
                    return;
                }

                WinForms.Form host = focus.FindForm();
                if (host != null) SetForegroundWindow(host.Handle);
                focus.Focus();
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(60);
                if (focus != null) { focus.Focus(); }

                GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", 0);
                int current = CurrentColumn(grid);
                if (current < 0)
                {
                    SendKey(WinForms.Keys.Home);
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(40);
                    current = CurrentColumn(grid);
                }

                if (current < 0)
                {
                    status.Text = LocalizationManager.T("The result grid did not accept the key presses. Click a cell and try again.");
                    return;
                }

                for (int step = 0; step < 80 && current != target; step++)
                {
                    SendKey(current < target ? WinForms.Keys.Right : WinForms.Keys.Left);
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                    int next = CurrentColumn(grid);
                    if (next < 0) break;
                    current = next;
                }

                for (int settle = 0; settle < 5; settle++)
                {
                    System.Threading.Thread.Sleep(30);
                    System.Windows.Forms.Application.DoEvents();
                    int settled = CurrentColumn(grid);
                    if (settled < 0 || settled == target) break;
                    SendKey(settled < target ? WinForms.Keys.Right : WinForms.Keys.Left);
                    current = settled;
                }

                RememberSearch(needle);
                status.Text = LocalizationManager.Format("Selected column '{0}'.", names.Count > 0 && target < names.Count ? names[target] : needle);
                Refresh();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not scroll to the result column.");
            }
        }

        private void RememberSearch(string needle)
        {
            try
            {
                var settings = SettingsManager.GetSqlCompletionSettings();
                if (settings == null) return;

                var history = (settings.resultsPanelColumnSearch ?? string.Empty)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
                history.RemoveAll(h => string.Equals(h, needle, StringComparison.OrdinalIgnoreCase));
                history.Insert(0, needle);
                if (history.Count > 20) history.RemoveRange(20, history.Count - 20);

                settings.resultsPanelColumnSearch = string.Join(";", history);
                SettingsManager.SaveSqlCompletionSettings(settings);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not store the column search history.");
            }
        }

        /// <summary>Freezes the first N columns; 0 unfreezes.</summary>
        private void FreezeColumns()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object grid = GridAccess.GetFocusGridControl() ?? FindGrid(SafeResultsControl()) ?? cachedGrid;
                if (grid == null) return;

                int count = 0;
                int.TryParse(freezeCount.Text, out count);
                count = Math.Max(0, Math.Min(20, count));
                GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", count);
                status.Text = count == 0
                    ? LocalizationManager.T("Columns unfrozen.")
                    : LocalizationManager.Format("First {0} column(s) frozen.", count);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not freeze the result columns.");
            }
        }

        private static object SafeResultsControl()
        {
            try { return GridAccess.GetSQLResultsControl(); } catch (Exception) { return null; }
        }

        private static object FindGrid(object resultsControl)
        {
            try
            {
                if (resultsControl != null)
                    foreach (string field in new[] { "m_grid", "grid", "m_dataGrid" })
                    {
                        object grid = GridAccess.GetNonPublicField(resultsControl, field);
                        if (grid is WinForms.Control) return grid;
                    }

                object focused = GridAccess.GetFocusGridControl();
                return focused is WinForms.Control ? focused : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads SelectedCells (X is 1-based) and falls back to GetCurrentCell.</summary>
        private static bool ReadSelection(object grid, ref int startRow, ref int startColumn, ref int endRow, ref int endColumn)
        {
            if (grid == null) return false;

            try
            {
                var blocks = GridAccess.GetProperty(grid, "SelectedCells") as IEnumerable;
                if (blocks != null)
                {
                    int minRow = int.MaxValue, minCol = int.MaxValue, maxRow = -1, maxCol = -1;
                    foreach (object block in blocks)
                    {
                        if (block == null) continue;
                        int x = ToInt(GridAccess.GetProperty(block, "X"), -1);
                        long y = ToLong(GridAccess.GetProperty(block, "Y"), -1);
                        int width = ToInt(GridAccess.GetProperty(block, "Width"), 1);
                        long height = ToLong(GridAccess.GetProperty(block, "Height"), 1);
                        if (x < 0 || y < 0) continue;

                        int dataColumn = Math.Max(0, x - 1);
                        minCol = Math.Min(minCol, dataColumn);
                        maxCol = Math.Max(maxCol, dataColumn + Math.Max(1, width) - 1);
                        minRow = Math.Min(minRow, (int)y);
                        maxRow = Math.Max(maxRow, (int)y + (int)Math.Max(1, height) - 1);
                    }

                    if (maxRow >= 0 && maxCol >= 0)
                    {
                        startRow = minRow; endRow = maxRow; startColumn = minCol; endColumn = maxCol;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SelectedCells could not be read.");
            }

            int column = CurrentColumn(grid);
            if (column >= 0)
            {
                startColumn = endColumn = column;
                startRow = endRow = 0;
                return true;
            }

            return false;
        }

        private static int CurrentColumn(object grid)
        {
            if (grid == null) return -1;

            try
            {
                var blocks = GridAccess.GetProperty(grid, "SelectedCells") as IEnumerable;
                if (blocks != null)
                    foreach (object block in blocks)
                    {
                        if (block == null) continue;
                        int x = ToInt(GridAccess.GetProperty(block, "X"), -1);
                        if (x >= 0) return Math.Max(0, x - 1);
                    }
            }
            catch (Exception)
            {
            }

            try
            {
                MethodInfo current = grid.GetType().GetMethod("GetCurrentCell");
                if (current != null)
                {
                    object[] args = { 0L, 0 };
                    current.Invoke(grid, args);
                    return ToInt(args[1], -1);
                }
            }
            catch (Exception)
            {
            }

            return -1;
        }

        private static int ToInt(object value, int fallback)
            => value != null && int.TryParse(value.ToString(), out int number) ? number : fallback;

        private static long ToLong(object value, long fallback)
            => value != null && long.TryParse(value.ToString(), out long number) ? number : fallback;

        private static string Dec(decimal value, int count)
            => count == 0 ? "-" : value.ToString("#,0.####", CultureInfo.CurrentCulture);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        /// <summary>
        /// Injects a real key press.  SendKeys is swallowed by the SSMS host in some builds, while these
        /// OS level events always reach the focused control.
        /// </summary>
        private static void SendKey(WinForms.Keys key)
        {
            const uint KeyUp = 0x0002;
            byte virtualKey = (byte)key;
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(25);
            keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }

        public void Dispose() => StopTimer();
    }
}