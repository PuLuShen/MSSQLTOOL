using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Adds "Jump to column" to the results grid context menu, with every column of the result set as a
    /// sub-item.
    ///
    /// The context menu is the only entry point where the grid holds the keyboard focus, and that focus is
    /// what makes scrolling work: SSMS ignores every programmatic scroll call and swallows key presses sent
    /// from a tool window, but a menu click returns the focus to the grid, so EnsureColumnIsVisible and the
    /// key walk both take effect there.
    /// </summary>
    internal static class ResultsGridMenu
    {
        private static readonly HashSet<object> hooked = new HashSet<object>();

        internal static void AttachTo(object sqlResultsControl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // The grid cannot be found from the results control alone (its field names differ between
                // SSMS builds); the grid containers the rest of the extension already uses are reliable.
                var grids = new List<object>();
                try
                {
                    CollectionBase containers = GridAccess.GetGridContainers();
                    if (containers != null)
                        foreach (object container in containers)
                        {
                            object grid = GridAccess.GetNonPublicField(container, "m_grid");
                            if (grid is Control && !grids.Contains(grid)) grids.Add(grid);
                        }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Info("Results grid menu: could not enumerate the containers: " + ex.Message);
                }

                object fallback = FindGrid(sqlResultsControl);
                if (fallback is Control && !grids.Contains(fallback)) grids.Add(fallback);

                if (grids.Count == 0)
                {
                    MSSQLToolPackage._logger?.Info("Results grid menu: no results grid to attach to yet.");
                    return;
                }

                foreach (object candidate in grids) Attach(candidate);

            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        internal static void Attach(object grid)
        {
            try
            {
                if (grid == null || !hooked.Add(grid)) return;
                var control = grid as Control;
                if (control == null) return;

                // Right click rebuilds the sub-menu from the columns of the result set that is showing.
                control.MouseUp += (_, e) =>
                {
                    if (e.Button != MouseButtons.Right) return;
                    try { InstallMenu(grid); } catch (Exception ex) { Log(ex); }
                };

                InstallMenu(grid);
                MSSQLToolPackage._logger?.Info("Results grid menu: jump-to-column installed on a results grid.");
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        private static void InstallMenu(object grid)
        {
            var control = grid as Control;
            if (control == null) return;

            var menu = control.ContextMenuStrip;
            if (menu == null)
            {
                menu = new ContextMenuStrip();
                control.ContextMenuStrip = menu;
            }

            foreach (ToolStripItem existing in menu.Items)
                if (existing is ToolStripMenuItem item && item.Name == "MSSQLToolJumpToColumn")
                    menu.Items.Remove(existing);

            List<string> columns = ReadColumnNames(grid);
            if (columns.Count == 0) return;

            var root = new ToolStripMenuItem(LocalizationManager.T("Jump to column"))
            {
                Name = "MSSQLToolJumpToColumn"
            };

            for (int i = 0; i < columns.Count; i++)
            {
                string name = columns[i];
                if (string.IsNullOrWhiteSpace(name)) name = "(column " + (i + 1) + ")";
                int index = i;
                var entry = new ToolStripMenuItem(name);
                entry.Click += (_, __) => Jump(grid, index);
                root.DropDownItems.Add(entry);
            }

            menu.Items.Insert(0, root);
            if (menu.Items.Count > 1) menu.Items.Insert(1, new ToolStripSeparator());
        }

        /// <summary>Runs after the menu closes, when the grid has the focus again.</summary>
        private static void Jump(object grid, int columnIndex)
        {
            var control = grid as Control;
            if (control == null) return;

            control.BeginInvoke((Action)(() =>
            {
                try
                {
                    GridAccess.SetPropertyValue(grid, "FirstScrollableColumn", 0);

                    bool scrolled = false;
                    try
                    {
                        object manager = GridAccess.GetNonPublicField(grid, "m_scrollMgr");
                        MethodInfo ensureColumn = manager?.GetType().GetMethod("EnsureColumnIsVisible");
                        if (ensureColumn != null)
                        {
                            foreach (int candidate in new[] { columnIndex + 1, columnIndex })
                            {
                                try
                                {
                                    ensureColumn.Invoke(manager, new object[] { candidate, true, true });
                                    scrolled = true;
                                    break;
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }

                    // Belt and braces: with the grid focused the key walk reaches it, unlike from a tool window.
                    int current = CurrentColumn(grid);
                    if (current < 0) current = 0;
                    for (int step = 0; step < columnIndex && current < columnIndex && step < 80; step++)
                    {
                        PressKey(Keys.Right);
                        int next = CurrentColumn(grid);
                        if (next < 0 || next == current) break;
                        current = next;
                    }

                    for (int step = 0; step < 80 && current > columnIndex; step++)
                    {
                        PressKey(Keys.Left);
                        int next = CurrentColumn(grid);
                        if (next < 0 || next == current) break;
                        current = next;
                    }

                    MSSQLToolPackage._logger?.Info("Results grid menu: jumped to column " + (columnIndex + 1)
                        + (scrolled ? " (scroll manager)" : " (keys)") + ", caret now on " + (current + 1) + ".");
                }
                catch (Exception ex)
                {
                    Log(ex);
                }
            }));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private static void PressKey(Keys key)
        {
            const uint KeyUp = 0x0002;
            keybd_event((byte)key, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(25);
            keybd_event((byte)key, 0, KeyUp, UIntPtr.Zero);
            Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }

        /// <summary>Column names from the grid schema, with a DataTable fallback.</summary>
        private static List<string> ReadColumnNames(object grid)
        {
            var names = new List<string>();

            try
            {
                object storage = GridAccess.GetProperty(grid, "GridStorage") ?? GridAccess.GetNonPublicField(grid, "m_gridStorage");
                var schema = storage == null ? null : GridAccess.GetNonPublicField(storage, "m_schemaTable") as DataTable;
                if (schema != null && schema.Columns.Contains("ColumnName"))
                {
                    int index = schema.Columns["ColumnName"].Ordinal;
                    foreach (DataRow row in schema.Rows)
                        names.Add(row[index] == null || row[index] == DBNull.Value ? string.Empty : row[index].ToString());
                }
            }
            catch (Exception)
            {
            }

            if (names.Count == 0)
                try
                {
                    DataTable table = GridAccess.GetDataTables()?.FirstOrDefault(t => t != null && t.Columns.Count > 0);
                    if (table != null) names.AddRange(table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
                }
                catch (Exception)
                {
                }

            return names;
        }

        private static int CurrentColumn(object grid)
        {
            try
            {
                var blocks = GridAccess.GetProperty(grid, "SelectedCells") as IEnumerable;
                if (blocks != null)
                    foreach (object block in blocks)
                    {
                        if (block == null) continue;
                        object x = GridAccess.GetProperty(block, "X");
                        int column;
                        if (x != null && int.TryParse(x.ToString(), out column)) return Math.Max(0, column - 1);
                    }
            }
            catch (Exception)
            {
            }

            return -1;
        }

        private static object FindGrid(object resultsControl)
        {
            try
            {
                if (resultsControl != null)
                    foreach (string field in new[] { "m_grid", "grid", "m_dataGrid" })
                    {
                        object grid = GridAccess.GetNonPublicField(resultsControl, field);
                        if (grid is Control) return grid;
                    }

                object focused = null;
                try { focused = GridAccess.GetFocusGridControl(); } catch (Exception) { }
                return focused is Control ? focused : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Log(Exception ex)
            => MSSQLToolPackage._logger?.Debug(ex, "Results grid menu failed.");
    }
}
