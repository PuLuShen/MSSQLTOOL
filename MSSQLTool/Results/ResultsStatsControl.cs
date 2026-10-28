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
    /// Content of the floating statistics panel: the statistics of the selection in the results grid,
    /// a column search that moves the caret to a column, and column freezing.
    ///
    /// The floating window is owned by SSMS but is not part of its tool-window layout, so the panel can
    /// stay next to the results without taking over the shell's docking.  Focus behaves differently from
    /// the earlier docked version: the grid keeps the selection colour and the panel never activates
    /// itself, which is why every jump first hands the focus back to the grid and verifies it.
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
        private readonly TextBox freezeCount = new TextBox { Height = 24, Width = 46, Text = "0", VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox columnBox = new ComboBox { Height = 24, Width = 150, IsEditable = true, IsTextSearchEnabled = false, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly List<object[]> matches = new List<object[]>();   // row, column, text of every hit
        private int matchCursor = -1;
        private string lastNeedle = string.Empty;
        private readonly TextBox valueBox = new TextBox { Height = 24, Width = 120, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock foundText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

        private List<string> allColumns = new List<string>();
        private string lastColumnSignature = string.Empty;
        private bool suppressColumnFilter;
        private DataTable table;
        private DataTable cachedTable;
        private object cachedGrid;
        private System.Windows.Threading.DispatcherTimer timer;

        // Material Design icon paths (24 x 24 view box): a pin, crosshairs and a magnifier.  Unfreeze
        // reuses the pin and adds a diagonal slash, which stays readable at 14 px where the stock
        // "pin-off" path turns into a blob.
        private const string PinIcon = "M16,12V4H17V2H7V4H8V12L6,14V16H11.2V22H12.8V16H18V14L16,12Z";
        private const string CrosshairIcon = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4M12,10A2,2 0 0,0 10,12A2,2 0 0,0 12,14A2,2 0 0,0 14,12A2,2 0 0,0 12,10Z";
        private const string SearchIcon = "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";

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

            var freezeRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            freezeRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Freeze first N columns:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            freezeRow.Children.Add(freezeCount);
            Button freeze = CreateIconButton("Freeze", PinIcon);
            freeze.Click += (_, __) => FreezeColumns();
            freezeRow.Children.Add(freeze);
            Button unfreeze = CreateIconButton("Unfreeze", PinIcon, true);
            unfreeze.Click += (_, __) => { freezeCount.Text = "0"; FreezeColumns(); };
            freezeRow.Children.Add(unfreeze);
            root.Children.Add(freezeRow);

            // Both actions use FirstScrollableColumn / FirstScrollableRow: those are the only properties this
            // build actually honours (verified), so the grid always reacts even though its own scroll calls
            // and the key injection are ignored.
            var columnRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            columnRow.Children.Add(new TextBlock { Text = LocalizationManager.T("Locate column:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            columnBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, __) => { if (!suppressColumnFilter) FilterColumns(); }));
            columnRow.Children.Add(columnBox);
            Button locate = CreateIconButton("Locate column:", CrosshairIcon);
            locate.Click += (_, __) => LocateColumn();
            columnRow.Children.Add(locate);
            root.Children.Add(columnRow);

            var valueRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            valueRow.Children.Add(new TextBlock { Text = LocalizationManager.T("Find value:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            valueRow.Children.Add(valueBox);
            Button find = CreateIconButton("Find value:", SearchIcon);
            find.Click += (_, __) => FindValue();
            valueRow.Children.Add(find);
            root.Children.Add(valueRow);
            root.Children.Add(foundText);

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

        /// <summary>
        /// An icon-only button with a localized tooltip.  Text buttons made the panel look cluttered and
        /// pushed the inputs to the right, so the actions are drawn as the familiar pin, crosshair and
        /// magnifier symbols; the tooltip and the automation name keep them accessible.
        /// </summary>
        private static Button CreateIconButton(string tooltipKey, string pathData, bool slashed = false)
        {
            string tooltip = LocalizationManager.T(tooltipKey);
            System.Windows.Shapes.Path icon = CreateIconPath(pathData);
            FrameworkElement visual = icon;

            if (slashed)
            {
                // A diagonal slash over the pin reads as "unpin" far better than the stock pin-off
                // glyph once both are squeezed into 14 pixels.
                var overlay = new Grid { Width = 14, Height = 14 };
                overlay.Children.Add(icon);
                var slash = new System.Windows.Shapes.Line
                {
                    X1 = 1.5,
                    Y1 = 12.5,
                    X2 = 12.5,
                    Y2 = 1.5,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    SnapsToDevicePixels = true
                };
                slash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ToolThemeForegroundBrush");
                if (slash.Stroke == null)
                    slash.Stroke = (Brush)Application.Current?.TryFindResource("ToolThemeForegroundBrush") ?? Brushes.Black;
                overlay.Children.Add(slash);
                visual = overlay;
            }

            var button = new Button
            {
                Content = visual,
                Width = 30,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = tooltip
            };
            System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
            return button;
        }

        private static System.Windows.Shapes.Path CreateIconPath(string pathData)
        {
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Stretch = Stretch.Uniform,
                Width = 14,
                Height = 14,
                SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ToolThemeForegroundBrush");
            if (icon.Fill == null)
                icon.Fill = (Brush)Application.Current?.TryFindResource("ToolThemeForegroundBrush") ?? Brushes.Black;
            return icon;
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

                object grid = cachedGrid;
                if (grid == null) grid = FindGrid(SafeResultsControl());
                if (grid == null) grid = TryGetFocusGrid();
                if (grid == null) grid = cachedGrid;
                if (grid != null)
                {
                    cachedGrid = grid;
                    // The grid is real here, so this is where the context menu can be installed for sure.
                    try { ResultsGridMenu.Attach(grid); } catch (Exception) { }
                }

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
                MSSQLToolPackage._logger?.Debug(ex, "The results statistics window could not read the result set.");
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

        /// <summary>
        /// The dropdown lists the current result columns only.  It used to prepend a saved search history,
        /// so old search terms appeared above the columns; that history is deliberately gone.  When the
        /// result set changes the box is cleared as well, because a term typed for the previous columns
        /// means nothing for the new ones.
        /// </summary>
        private void UpdateColumns(List<string> headers)
        {
            var names = new List<string>();

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
            try
            {
                suppressColumnFilter = true;
                columnBox.IsDropDownOpen = false;
                columnBox.ItemsSource = names;
                columnBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not refresh the column list.");
            }
            finally
            {
                suppressColumnFilter = false;
            }
        }

        /// <summary>Narrows the column dropdown to the names that contain what has been typed.</summary>
        private void FilterColumns()
        {
            try
            {
                if (allColumns.Count == 0) return;
                string text = (columnBox.Text ?? string.Empty).Trim();
                List<string> filtered = text.Length == 0
                    ? allColumns
                    : allColumns.Where(c => c.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (filtered.Count == 0) filtered = allColumns;

                string keep = columnBox.Text;
                columnBox.ItemsSource = filtered;
                columnBox.Text = keep;
                if (!columnBox.IsDropDownOpen) columnBox.IsDropDownOpen = true;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not filter the column list.");
            }
        }

        /// <summary>Clears the locate box after a jump so the next search starts empty.</summary>
        private void ResetColumnSearch()
        {
            try
            {
                suppressColumnFilter = true;
                columnBox.IsDropDownOpen = false;
                columnBox.Text = string.Empty;
                columnBox.ItemsSource = allColumns;
            }
            catch (Exception)
            {
            }
            finally
            {
                suppressColumnFilter = false;
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

        /// <summary>Moves the caret to the chosen column without pinning anything, by walking the cells.</summary>
        private void LocateColumn()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object grid = cachedGrid ?? TryGetFocusGrid();
                if (grid == null || table == null) { foundText.Text = LocalizationManager.T("No result set is available."); return; }

                string needle = (columnBox.Text ?? string.Empty).Trim();
                if (needle.Length == 0) { foundText.Text = LocalizationManager.T("Type part of a column name."); return; }

                List<string> names = ReadColumnNames(grid);
                List<string> source = names.Count > 0 ? names : table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                int target = MatchColumn(source, needle);
                if (target < 0) { foundText.Text = LocalizationManager.Format("No column matches \'{0}\'.", needle); return; }

                if (!TryFocusGrid(grid))
                {
                    foundText.Text = LocalizationManager.T("Click a cell in the results first, then locate again.");
                    return;
                }

                int row, current;
                if (!TryGetCaret(grid, out row, out current))
                {
                    foundText.Text = LocalizationManager.T("The result grid did not report its current cell.");
                    return;
                }

                int delta = target - current;
                MoveCaret(grid, 0, delta);
                int finalRow, finalColumn;
                TryGetCaret(grid, out finalRow, out finalColumn);

                foundText.Text = LocalizationManager.Format("Moved {0} column(s): now at column {1} ({2}), row {3}.",
                    Math.Abs(delta), finalColumn + 1, finalColumn < source.Count ? source[finalColumn] : "?", finalRow + 1);
                // Start the next search from an empty box instead of keeping this term around.
                if (finalColumn == target) ResetColumnSearch();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Info("Locate column failed: " + ex.Message);
            }
        }

        /// <summary>Collects every match and steps through them, so repeated clicks walk all hits.</summary>
        private void FindValue()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object grid = cachedGrid ?? TryGetFocusGrid();
                if (grid == null || table == null) { foundText.Text = LocalizationManager.T("No result set is available."); return; }

                string needle = (valueBox.Text ?? string.Empty).Trim();
                if (needle.Length == 0) { foundText.Text = LocalizationManager.T("Type something to find."); return; }

                // A new needle restarts the walk; the same needle advances to the next hit.
                if (matches.Count == 0 || !string.Equals(lastNeedle, needle, StringComparison.Ordinal))
                {
                    lastNeedle = needle;
                    matches.Clear();
                    matchCursor = -1;
                    for (int row = 0; row < table.Rows.Count; row++)
                        for (int column = 0; column < table.Columns.Count; column++)
                        {
                            string text = Convert.ToString(table.Rows[row][column], CultureInfo.CurrentCulture) ?? string.Empty;
                            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                                matches.Add(new object[] { row, column, text });
                        }
                }

                if (matches.Count == 0)
                {
                    foundText.Text = LocalizationManager.Format("Nothing matches '{0}'.", needle);
                    return;
                }

                matchCursor = (matchCursor + 1) % matches.Count;
                object[] hit = matches[matchCursor];
                int matchRow = (int)hit[0];
                int matchColumn = (int)hit[1];
                string matchText = Convert.ToString(hit[2], CultureInfo.CurrentCulture);

                string where = LocalizationManager.Format("{0}/{1} - row {2}, column {3} ({4}): {5}",
                    matchCursor + 1, matches.Count, matchRow + 1, matchColumn + 1,
                    table.Columns[matchColumn].ColumnName, matchText);

                if (TryFocusGrid(grid))
                {
                    int caretRow, caretColumn;
                    if (TryGetCaret(grid, out caretRow, out caretColumn))
                    {
                        MoveCaret(grid, matchRow - caretRow, matchColumn - caretColumn);
                        int finalRow, finalColumn;
                        TryGetCaret(grid, out finalRow, out finalColumn);
                        where += "  " + LocalizationManager.Format("caret at row {0}, column {1}.", finalRow + 1, finalColumn + 1);
                    }
                }
                else
                {
                    where += "  " + LocalizationManager.T("Click a cell in the results first, then find again.");
                }

                foundText.Text = where;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Info("Find value failed: " + ex.Message);
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>Focuses the results grid and verifies it before any key is sent.</summary>
        private bool TryFocusGrid(object grid)
        {
            var control = grid as WinForms.Control;
            if (control == null) return false;

            try
            {
                WinForms.Form host = control.FindForm();
                if (host != null) SetForegroundWindow(host.Handle);
                control.Focus();
                WinForms.Application.DoEvents();
                System.Threading.Thread.Sleep(60);
                return GetFocus() == control.Handle || control.ContainsFocus;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Info("Focus hand-off failed: " + ex.Message);
                return false;
            }
        }

        private static void Press(WinForms.Keys key)
        {
            const uint KeyUp = 0x0002;
            keybd_event((byte)key, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            keybd_event((byte)key, 0, KeyUp, UIntPtr.Zero);
            WinForms.Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }

        /// <summary>Moves the caret by whole cells, one key at a time, reading the position back.</summary>
        private void MoveCaret(object grid, int deltaRow, int deltaColumn)
        {
            var columnKey = deltaColumn > 0 ? WinForms.Keys.Right : WinForms.Keys.Left;
            for (int i = 0; i < Math.Abs(deltaColumn); i++) Press(columnKey);

            var rowKey = deltaRow > 0 ? WinForms.Keys.Down : WinForms.Keys.Up;
            for (int i = 0; i < Math.Abs(deltaRow); i++) Press(rowKey);
        }

        private static bool TryGetCaret(object grid, out int row, out int column)
        {
            row = column = -1;
            if (grid == null) return false;

            try
            {
                MethodInfo current = grid.GetType().GetMethod("GetCurrentCell");
                if (current != null)
                {
                    object[] args = { 0L, 0 };
                    current.Invoke(grid, args);
                    row = (int)Convert.ToInt64(args[0]);
                    column = Convert.ToInt32(args[1]) - 1;
                    if (column < 0) column = 0;
                    return true;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                var blocks = GridAccess.GetProperty(grid, "SelectedCells") as IEnumerable;
                if (blocks != null)
                    foreach (object block in blocks)
                    {
                        if (block == null) continue;
                        int x = ToInt(GridAccess.GetProperty(block, "X"), -1);
                        long y = ToLong(GridAccess.GetProperty(block, "Y"), -1);
                        if (x < 0 || y < 0) continue;
                        column = Math.Max(0, x - 1);
                        row = (int)y;
                        return true;
                    }
            }
            catch (Exception)
            {
            }

            return false;
        }
        /// <summary>Freezes the first N columns; 0 unfreezes.</summary>
        private void FreezeColumns()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object grid = cachedGrid ?? FindGrid(SafeResultsControl()) ?? TryGetFocusGrid();
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

        /// <summary>
        /// Focused grid, or null.  GridAccess.GetFocusGridControl() throws InvalidCastException whenever the
        /// focused window is not a grid (our own tool window, for instance), and that exception used to abort
        /// the whole refresh.
        /// </summary>
        private static object TryGetFocusGrid()
        {
            try
            {
                object grid = GridAccess.GetFocusGridControl();
                return grid is WinForms.Control ? grid : null;
            }
            catch (Exception)
            {
                return null;
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

                object focused = TryGetFocusGrid();
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
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        public void Dispose() => StopTimer();
    }
}