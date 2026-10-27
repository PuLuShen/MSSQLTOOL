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
        private static DateTime hiddenUtc = DateTime.MinValue;

        internal static bool IsEnabled => enabled;

        /// <summary>
        /// Installs the context menu toggle and the poll for a results control.  Everything is guarded:
        /// this runs from window activation and query execution, where an exception would disturb SSMS
        /// itself (the log showed InvalidCastException escaping into the activation path).
        /// </summary>
        internal static void AttachTo(object sqlResultsControl)
        {
            try { HookGrid(FindGrid(sqlResultsControl)); } catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Hook grid failed."); }
            try { AttachContextMenu(sqlResultsControl); } catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Context menu install failed."); }
            try { EnsurePolling(); } catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Starting the panel poll failed."); }
        }

        /// <summary>
        /// Makes sure the poll runs.  Called after every execution, because that is when the results grid
        /// exists: the poll is what notices a selection change and brings the panel up, so it must not
        /// depend on a click handler being installed on a particular control instance.
        /// </summary>
        internal static void EnsurePolling()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            enabled = true;
            StartTimer();
        }

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
                enabled = true;
                StartTimer();
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

        /// <summary>Toolbar entry point: always shows the panel rather than toggling it away.</summary>
        internal static void ShowFromCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            enabled = true;
            Show();
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
                window.RestoreSize();
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
            // The poll keeps running: it is what notices the next click in the results and shows the panel
            // again, so closing it once must not mean "open it from the menu from now on".
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

                    // A results tab is active: bring the panel back even when the selection did not change
                    // (for example clicking the same cell again), unless the user just closed it.
                    bool recentlyClosed = (DateTime.UtcNow - hiddenUtc) < TimeSpan.FromSeconds(2);
                    if (!window.IsVisible && !recentlyClosed)
                    {
                        window.PlaceNextToResults();
                        window.Show();
                        shownUtc = DateTime.UtcNow;
                        missedResultsTicks = 0;
                        MSSQLToolPackage._logger?.Info("Results statistics panel shown because a results tab is active.");
                    }

                    string key = window.RefreshFromSelection();
                    if (!string.IsNullOrEmpty(key) && !string.Equals(key, lastSelectionKey, StringComparison.Ordinal))
                    {
                        lastSelectionKey = key;
                        if (!window.IsVisible)
                        {
                            // A click in the results changed the selection: bring the panel back without
                            // asking the user to find the menu entry again.
                            window.PlaceNextToResults();
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
            try
            {
                if (sqlResultsControl != null)
                {
                    foreach (string field in new[] { "m_grid", "grid", "m_dataGrid" })
                    {
                        object grid = GridAccess.GetNonPublicField(sqlResultsControl, field);
                        if (grid is WinForms.Control) return grid;
                    }
                }

                object focused = GridAccess.GetFocusGridControl();
                return focused is WinForms.Control ? focused : null;
            }
            catch (Exception ex)
            {
                // A code window control reaches this path while windows activate; that must stay silent.
                MSSQLToolPackage._logger?.Debug(ex, "Could not locate the results grid.");
                return null;
            }
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
            // Editable combo box: typing still works and previous searches stay one click away.
            private readonly ComboBox columnSearch = new ComboBox { Height = 24, Width = 170, IsEditable = true, IsTextSearchEnabled = false, VerticalContentAlignment = VerticalAlignment.Center };
            private readonly TextBox freezeCount = new TextBox { Height = 24, Width = 46, VerticalContentAlignment = VerticalAlignment.Center, Text = "0" };
            private readonly TextBlock columnStatus = new TextBlock { Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

            private readonly Dictionary<string, TextBlock> values = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
            private DataTable table;
            private DataTable cachedTable;
            private List<string> cachedNames;
            private int lastColumnNameCount = -1;
            private string lastColumnSearchSignature = string.Empty;
            private DateTime cachedTableUtc = DateTime.MinValue;

            internal HoverWindow()
            {
                Title = LocalizationManager.T("Result statistics panel");
                Width = 400;
                Height = 430;
                MinWidth = 260;
                MinHeight = 240;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                Topmost = true;
                ShowInTaskbar = false;
                // grid is inactive, and the panel would then hide itself because the grid lost the focus.
                ShowActivated = false;
                WindowStyle = WindowStyle.ToolWindow;
                // Manual: CenterScreen would ignore the position computed from the results grid.
                WindowStartupLocation = WindowStartupLocation.Manual;
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

                var searchRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Orientation = Orientation.Horizontal };
                searchRow.Children.Add(new TextBlock
                {
                    Text = LocalizationManager.T("Find column:"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                columnSearch.Width = 130;
                columnSearch.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) JumpToColumn(); };
                                columnSearch.Loaded += (_, __) => LoadColumnSearchHistory();
                // Editable combo boxes do not open their list while typing unless asked to.
                columnSearch.PreviewTextInput += (_, __) => { if (!columnSearch.IsDropDownOpen) columnSearch.IsDropDownOpen = true; };
                // A window shown without activation does not take the keyboard focus on click; ask for it
                // explicitly, otherwise Delete and the arrow keys go to the SQL editor behind the panel.
                PreviewMouseDown += (_, __) => { try { Activate(); } catch (Exception) { } };
                columnSearch.PreviewMouseDown += (_, __) =>
                {
                    try { Activate(); columnSearch.Focus(); } catch (Exception) { }
                };
                searchRow.Children.Add(columnSearch);
                var jump = new Button { Content = LocalizationManager.T("Go"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
                jump.Click += (_, __) => JumpToColumn();
                searchRow.Children.Add(jump);
                root.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 10, 0, 2),
                    Background = (Brush)Application.Current?.TryFindResource("ToolThemeSubtleBorderBrush") ?? Brushes.LightGray
                });

                root.Children.Add(searchRow);

                var freezeRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Orientation = Orientation.Horizontal };
                freezeRow.Children.Add(new TextBlock
                {
                    Text = LocalizationManager.T("Freeze first N columns:"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                freezeRow.Children.Add(freezeCount);
                var freezeButton = new Button { Content = LocalizationManager.T("Freeze"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
                freezeButton.Click += (_, __) => FreezeColumns();
                freezeRow.Children.Add(freezeButton);
                var unfreezeButton = new Button { Content = LocalizationManager.T("Unfreeze"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
                unfreezeButton.Click += (_, __) => { freezeCount.Text = "0"; FreezeColumns(); };
                freezeRow.Children.Add(unfreezeButton);
                root.Children.Add(freezeRow);
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

                    int uiStartRow = 0, uiStartColumn = 0, uiEndRow = current.Rows.Count - 1, uiEndColumn = 0;
                    bool gotSelection = ReadSelection(grid, ref uiStartRow, ref uiStartColumn, ref uiEndRow, ref uiEndColumn);

                    // SelectedCells reports UI column indexes; the DataTable uses storage order.  Without this
                    // mapping the panel summarised the neighbouring column and showed Column_N names.
                    int startRow = uiStartRow, endRow = uiEndRow;
                    int startColumn = ToStorageColumn(grid, uiStartColumn);
                    int endColumn = ToStorageColumn(grid, uiEndColumn);
                    if (startColumn < 0) startColumn = uiStartColumn;
                    if (endColumn < 0) endColumn = uiEndColumn;

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

                    List<string> gridHeaders = ReadGridColumnNames(grid);

                    // Fill the search list here: this is the one place where the header names are known.
                    UpdateColumnSearchItems(gridHeaders);
                    string columnNames = string.Join(", ", Enumerable.Range(columnFrom, columnTo - columnFrom + 1)
                        .Select(i =>
                        {
                            int ui = ToUiColumn(grid, i);
                            return (ui >= 0 && ui < gridHeaders.Count && !string.IsNullOrWhiteSpace(gridHeaders[ui]))
                                ? gridHeaders[ui]
                                : current.Columns[i].ColumnName;
                        }));
                    selectionText.Text = gotSelection
                        ? LocalizationManager.Format("Selection: rows {0}-{1}, columns {2}-{3}   ({4} cells)",
                            rowFrom + 1, rowTo + 1, columnFrom + 1, columnTo + 1, cellCount)
                          + Environment.NewLine + LocalizationManager.Format("Column(s): {0}", columnNames)
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
            /// <summary>
            /// Fills the column box: the names searched before first, then every column of the current
            /// result set, so the list is never empty and typing narrows it down.
            /// </summary>
            /// <summary>Column names of the result set, with the search history first; only rebuilds on change.</summary>
            private void UpdateColumnSearchItems(List<string> gridHeaders)
            {
                try
                {
                    var names = new List<string>();
                    string saved = SettingsManager.GetSqlCompletionSettings()?.resultsPanelColumnSearch;
                    if (!string.IsNullOrWhiteSpace(saved))
                        names.AddRange(saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(h => h.Trim()).Where(h => h.Length > 0));

                    if (gridHeaders != null)
                        foreach (string header in gridHeaders)
                            if (!string.IsNullOrWhiteSpace(header) && !names.Contains(header, StringComparer.OrdinalIgnoreCase))
                                names.Add(header);

                    if (names.Count == 0) return;
                    string signature = string.Join("|", names);
                    if (string.Equals(signature, lastColumnSearchSignature, StringComparison.Ordinal)) return;

                    lastColumnSearchSignature = signature;
                    System.Windows.Controls.ComboBox box = columnSearch;
                    string current = box.Text;
                    box.ItemsSource = names;
                    box.Text = current;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not fill the column search list.");
                }
            }

            private void LoadColumnSearchHistory()
            {
                try
                {
                    var names = new List<string>();

                    string saved = SettingsManager.GetSqlCompletionSettings()?.resultsPanelColumnSearch;
                    if (!string.IsNullOrWhiteSpace(saved))
                        names.AddRange(saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(h => h.Trim()).Where(h => h.Length > 0));

                    object grid = GridAccess.GetFocusGridControl() ?? FindGrid(GridAccess.GetSQLResultsControl());
                    foreach (string column in ReadGridColumnNames(grid))
                        if (!string.IsNullOrWhiteSpace(column) && !names.Contains(column, StringComparer.OrdinalIgnoreCase))
                            names.Add(column);

                    if (table != null)
                        foreach (DataColumn column in table.Columns)
                            if (!names.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase))
                                names.Add(column.ColumnName);

                    if (names.Count > 0) columnSearch.ItemsSource = names;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not fill the column search list.");
                }
            }
            /// <summary>Remembers a successful search at the top of the list.</summary>
            private void RememberColumnSearch(string needle)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(needle)) return;
                    var settings = SettingsManager.GetSqlCompletionSettings();
                    if (settings == null) return;

                    var history = (settings.resultsPanelColumnSearch ?? string.Empty)
                        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
                    history.RemoveAll(h => string.Equals(h, needle, StringComparison.OrdinalIgnoreCase));
                    history.Insert(0, needle);
                    if (history.Count > 20) history.RemoveRange(20, history.Count - 20);

                    settings.resultsPanelColumnSearch = string.Join(";", history);
                    SettingsManager.SaveSqlCompletionSettings(settings);
                    columnSearch.ItemsSource = history;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not store the column search history.");
                }
            }

            /// <summary>
            /// Column search: picks the column, selects it as one block and lets the grid scroll to the
            /// selection.  FirstScrollableColumn is the frozen-column boundary, not a scroll position, so it
            /// is used by the separate "freeze" action instead.
            /// </summary>
            private void JumpToColumn()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    string needle = ((columnSearch.Text ?? string.Empty)).Trim();
                    if (needle.Length == 0)
                    {
                        columnStatus.Text = LocalizationManager.T("Type part of a column name.");
                        return;
                    }

                    object grid = GridAccess.GetFocusGridControl();
                    List<string> names = ReadGridColumnNames(grid);
                    int index = MatchColumn(names, needle);
                    if (index < 0)
                    {
                        columnStatus.Text = LocalizationManager.Format("No column matches '{0}'.", needle);
                        return;
                    }

                    SelectColumnBlock(grid, index);
                    RememberColumnSearch(needle);
                    columnStatus.Text = LocalizationManager.Format("Selected column '{0}'.", names[index]);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not jump to the result column.");
                }
            }

            /// <summary>Exact name first, then prefix, then substring - "DDZ" must not resolve to "DDZT".</summary>
            private static int MatchColumn(List<string> names, string needle)
            {
                for (int i = 0; i < names.Count; i++)
                    if (string.Equals(names[i], needle, StringComparison.OrdinalIgnoreCase)) return i;

                for (int i = 0; i < names.Count; i++)
                    if (names[i] != null && names[i].StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return i;

                for (int i = 0; i < names.Count; i++)
                    if (names[i] != null && names[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return i;

                return -1;
            }

            /// <summary>
            /// Scrolls the results to a column the way a user would: focus the grid, go to the first column
            /// and step right N times.  The grid ignores every programmatic scroll call (EnsureCellIsVisible,
            /// CurrentCell, SelectedCells), so this is the only reliable way to move it, and it never pins
            /// any column.
            /// </summary>
            private void SelectColumnBlock(object grid, int columnIndex)
            {
                if (grid == null) return;
                try
                {
                    // Undo any freeze so the horizontal scrolling is not restricted.
                    GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", 0);

                    var focus = grid as WinForms.Control;
                    if (focus == null) return;
                    focus.Focus();
                    try { grid.GetType().GetMethod("Select")?.Invoke(grid, null); } catch (Exception) { }

                    // Walk from wherever the user is, so only the column changes and never the row.
                    int current = CurrentColumn(grid);
                    if (current < 0)
                    {
                        // No caret could be read: fall back to the top-left cell once, then re-read.
                        WinForms.SendKeys.SendWait("^{HOME}");
                        WinForms.Application.DoEvents();
                        System.Threading.Thread.Sleep(40);
                        current = CurrentColumn(grid);
                    }

                    if (current < 0)
                    {
                        columnStatus.Text = LocalizationManager.T("The result grid did not accept the key presses. Click a cell and try again.");
                        return;
                    }

                    // One key at a time, reading the caret back each time: SendKeys both drops and doubles
                    // presses, so a fixed count cannot be trusted.
                    for (int step = 0; step < 80 && current != columnIndex; step++)
                    {
                        WinForms.SendKeys.SendWait(current < columnIndex ? "{RIGHT}" : "{LEFT}");
                        WinForms.Application.DoEvents();
                        System.Threading.Thread.Sleep(20);
                        int next = CurrentColumn(grid);
                        if (next < 0) break;
                        if (next == current)
                        {
                            // The grid swallowed this key; nudge again rather than spinning forever.
                            WinForms.SendKeys.SendWait(current < columnIndex ? "{RIGHT}" : "{LEFT}");
                            WinForms.Application.DoEvents();
                            System.Threading.Thread.Sleep(20);
                            next = CurrentColumn(grid);
                            if (next == current) break;
                        }

                        current = next;
                    }

                    if (current != columnIndex)
                    // A key can arrive after the last read; settle and converge once more.
                    for (int settle = 0; settle < 5; settle++)
                    {
                        System.Threading.Thread.Sleep(30);
                        WinForms.Application.DoEvents();
                        int settled = CurrentColumn(grid);
                        if (settled < 0 || settled == columnIndex) { current = settled < 0 ? current : settled; break; }
                        WinForms.SendKeys.SendWait(settled < columnIndex ? "{RIGHT}" : "{LEFT}");
                        current = settled;
                    }

                        MSSQLToolPackage._logger?.Info("Column jump ended on " + current + " instead of " + columnIndex + ".");
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not scroll to the result column.");
                }
            }


            /// <summary>Column the caret is on, or -1 when it cannot be read.</summary>
            private static int CurrentColumn(object grid)
            {
                try
                {
                    var blocks = GridAccess.GetProperty(grid, "SelectedCells") as IEnumerable;
                    if (blocks != null)
                    {
                        foreach (object block in blocks)
                        {
                            if (block == null) continue;
                            int x = ToInt(GridAccess.GetProperty(block, "X"), -1);
                            if (x >= 0) return Math.Max(0, x - 1);
                        }
                    }
                }
                catch (Exception) { }

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
                catch (Exception) { }

                return -1;
            }

            /// <summary>Freezes the first N columns (0 unfreezes), which is what FirstScrollableColumn means.</summary>
            private void FreezeColumns()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    object grid = GridAccess.GetFocusGridControl();
                    if (grid == null) return;

                    int count = 0;
                    int.TryParse(freezeCount.Text, out count);
                    count = Math.Max(0, Math.Min(20, count));
                    GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", count);
                    columnStatus.Text = count == 0
                        ? LocalizationManager.T("Columns unfrozen.")
                        : LocalizationManager.Format("First {0} column(s) frozen.", count);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not freeze the result columns.");
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

                            // X is 1-based: the grid puts a row-header column at 0, so the result columns start at 1.
                            int dataColumn = Math.Max(0, x - 1);
                            minColumn = Math.Min(minColumn, dataColumn);
                            maxColumn = Math.Max(maxColumn, dataColumn + Math.Max(1, width) - 1);
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

            /// <summary>UI column index (what SelectedCells reports) to storage index (what the DataTable uses).</summary>
            private static int ToStorageColumn(object grid, int uiIndex)
            {
                if (grid == null || uiIndex < 0) return -1;
                try
                {
                    MethodInfo map = grid.GetType().GetMethod("GetStorageColumnIndexByUIIndex");
                    object mapped = map?.Invoke(grid, new object[] { uiIndex });
                    if (mapped != null && int.TryParse(mapped.ToString(), out int storage) && storage >= 0) return storage;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not map a UI column to a storage column.");
                }

                return -1;
            }

            private static int ToUiColumn(object grid, int storageIndex)
            {
                if (grid == null || storageIndex < 0) return -1;
                try
                {
                    MethodInfo map = grid.GetType().GetMethod("GetUIColumnIndexByStorageIndex");
                    object mapped = map?.Invoke(grid, new object[] { storageIndex });
                    if (mapped != null && int.TryParse(mapped.ToString(), out int ui) && ui >= 0) return ui;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not map a storage column to a UI column.");
                }

                return -1;
            }

            private static int ToInt(object value, int fallback)
                => value != null && int.TryParse(value.ToString(), out int number) ? number : fallback;

            private static long ToLong(object value, long fallback)
                => value != null && long.TryParse(value.ToString(), out long number) ? number : fallback;
            /// <summary>
            /// Column names of the result set.  The grid's rendered header strings are not always available,
            /// but its GridStorage keeps the schema table of the result set - the same table the scripting
            /// code already reads - and that one carries the real column names.
            /// </summary>
            private List<string> ReadGridColumnNames(object grid)
            {
                if (cachedNames != null) return cachedNames;
                var names = new List<string>();
                cachedNames = names;
                if (grid == null) return names;

                try
                {
                    object storage = GridAccess.GetProperty(grid, "GridStorage")
                        ?? GridAccess.GetNonPublicField(grid, "m_gridStorage");
                    var schema = storage == null ? null : GridAccess.GetNonPublicField(storage, "m_schemaTable") as DataTable;
                    if (schema != null)
                    {
                        int nameIndex = schema.Columns.Contains("ColumnName") ? schema.Columns["ColumnName"].Ordinal
                            : schema.Columns.Contains("Column") ? schema.Columns["Column"].Ordinal : -1;
                        if (nameIndex >= 0)
                        {
                            foreach (DataRow row in schema.Rows)
                            {
                                object value = row[nameIndex];
                                names.Add(value == null || value == DBNull.Value ? string.Empty : value.ToString());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not read the result set schema table.");
                }

                if (names.Count != lastColumnNameCount)
                {
                    lastColumnNameCount = names.Count;
                    MSSQLToolPackage._logger?.Info("Results statistics panel: " + names.Count + " column name(s) from the result schema.");
                }
                return names;
            }
            /// <summary>
            /// Puts the panel to the right of the results grid (or restores the last position), so it never
            /// covers the rows the user is reading.
            /// </summary>
            /// <summary>Restores the size the user left the panel at, when it is still usable.</summary>
            internal void RestoreSize()
            {
                try
                {
                    string saved = SettingsManager.GetSqlCompletionSettings()?.resultsPanelSize;
                    double width, height;
                    if (!TryParse(saved, out width, out height)) return;
                    if (width < 240 || height < 200 || width > 2000 || height > 1600) return;
                    Width = width;
                    Height = height;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not restore the results statistics panel size.");
                }
            }

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
                    if (control == null)
                    {
                        // The focus grid is often unavailable; the results control covers the whole results
                        // area and is a good enough anchor for "to the right of the results".
                        try { control = GridAccess.GetSQLResultsControl() as WinForms.Control; } catch (Exception) { control = null; }
                    }

                    if (control == null || control.Width == 0)
                    {
                        WinForms.Screen active = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
                        Left = active.WorkingArea.Right - Width - 24;
                        Top = active.WorkingArea.Top + 80;
                        return;
                    }

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
                    settings.resultsPanelSize = Math.Round(Width) + "," + Math.Round(Height);
                    settings.resultsPanelSize = Math.Round(Width) + "," + Math.Round(Height);
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