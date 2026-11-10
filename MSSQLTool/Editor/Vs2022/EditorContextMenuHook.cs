using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace MSSQLTool
{
    /// <summary>
    /// Adds the refactoring commands to the SQL editor's context menu at the WPF layer.
    ///
    /// Two other routes were tried and both are dead ends in SSMS 22: command-table placements on
    /// the shell's Code Window menu never appear in the SQL editor, and the DTE command bars only
    /// expose a "代码窗口" popup whose contents are the generic Visual Studio editor items, not the
    /// SQL menu the user actually sees.  This hook watches the editor's WPF element instead: when a
    /// context menu opens, it checks whether the menu looks like the T-SQL one and appends the
    /// three commands if it does.  Everything is guarded so a failure only costs the entries.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class EditorContextMenuHook : IWpfTextViewCreationListener
    {
        private static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private static readonly string[] Captions =
        {
            "修复光标处的问题",
            "重命名",
            "为对象名添加架构"
        };

        private static readonly int[] CommandIds = { 4162, 4166, 4167 };

        /// <summary>Captions that only the T-SQL editor menu carries; used to avoid touching other menus.</summary>
        private static readonly string[] SqlMarkers = { "查询选项", "显示估计的执行计划", "IntelliSense", "包括实际执行计划" };

        private static bool loggedOnce;

        public void TextViewCreated(IWpfTextView textView)
        {
            try
            {
                if (textView == null) return;
                textView.VisualElement.AddHandler(
                    FrameworkElement.ContextMenuOpeningEvent,
                    new ContextMenuEventHandler(OnContextMenuOpening),
                    true);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "CTX-WPF could not attach the context menu hook.");
            }
        }

        private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                var element = e.OriginalSource as FrameworkElement ?? e.Source as FrameworkElement;
                if (element == null) return;

                ContextMenu menu = element.ContextMenu ?? ContextMenuService.GetContextMenu(element);
                if (menu == null)
                {
                    if (!loggedOnce)
                    {
                        loggedOnce = true;
                        MSSQLToolPackage._logger?.Info(
                            "CTX-WPF menu opening on " + element.GetType().Name + " without a WPF ContextMenu.");
                    }

                    return;
                }

                if (!LooksLikeSqlMenu(menu))
                {
                    if (!loggedOnce)
                    {
                        loggedOnce = true;
                        MSSQLToolPackage._logger?.Info(
                            "CTX-WPF saw a menu with " + menu.Items.Count + " items that is not the SQL menu.");
                    }

                    return;
                }

                Append(menu);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "CTX-WPF failed while handling ContextMenuOpening.");
            }
        }

        private static bool LooksLikeSqlMenu(ContextMenu menu)
        {
            int hits = 0;
            foreach (object item in menu.Items)
            {
                var header = HeaderText(item);
                if (string.IsNullOrEmpty(header)) continue;
                foreach (string marker in SqlMarkers)
                {
                    if (header.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hits++;
                        break;
                    }
                }

                if (hits >= 2) return true;
            }

            return false;
        }

        private static string HeaderText(object item)
        {
            var menuItem = item as MenuItem;
            return menuItem == null ? null : menuItem.Header as string;
        }

        private static void Append(ContextMenu menu)
        {
            if (ContainsOurItems(menu)) return;

            menu.Items.Add(new Separator());
            for (int i = 0; i < CommandIds.Length; i++)
            {
                int id = CommandIds[i];
                var item = new MenuItem { Header = Captions[i] };
                item.Click += (_, __) => Invoke(id);
                menu.Items.Add(item);
            }

            MSSQLToolPackage._logger?.Info(
                "CTX-WPF added " + CommandIds.Length + " items to the SQL editor context menu.");
        }

        private static bool ContainsOurItems(ContextMenu menu)
        {
            foreach (object item in menu.Items)
            {
                var header = HeaderText(item);
                if (string.IsNullOrEmpty(header)) continue;
                if (string.Equals(header, Captions[1], StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static void Invoke(int commandId)
        {
            try
            {
                var service = Package.GetGlobalService(typeof(System.ComponentModel.Design.IMenuCommandService))
                    as System.ComponentModel.Design.IMenuCommandService;
                if (service == null) return;
                service.GlobalInvoke(new System.ComponentModel.Design.CommandID(CommandSet, commandId));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "CTX-WPF could not invoke command " + commandId + ".");
            }
        }
    }
}
