using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool
{
    /// <summary>
    /// Last-resort reader for the Object Explorer tree.  Some SSMS 22 builds do not expose
    /// IObjectExplorerNavigationService, so the native TreeView is read directly to list every
    /// server currently shown in Object Explorer.
    /// </summary>
    internal static class ObjectExplorerTreeReader
    {
        private const uint TvmGetNextItem = 0x110A;
        private const uint TvmSelectItem = 0x110B;
        private const uint TvmGetItemW = 0x113E;
        private const int TvgnRoot = 0;
        private const int TvgnNext = 1;
        private const int TvgnCaret = 9;
        private const uint TvifText = 0x0001;
        private const int TextBufferLength = 1024;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TvItem
        {
            public uint mask;
            public IntPtr hItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public int iSelectedImage;
            public int cChildren;
            public IntPtr lParam;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Selects a server node in Object Explorer.  The selected node's INodeInformation carries
        /// the live connection (including credentials), which is the only reliable way to build a
        /// query connection when FindNode cannot resolve the display name.
        /// </summary>
        /// <summary>
        /// Resolves every Object Explorer root to its live server name by selecting each root in
        /// turn.  Root captions are friendly names, so the caption alone cannot be used as a host.
        /// </summary>
        public static List<string> ResolveServers(Func<string> selectedServerName)
        {
            var result = new List<string>();
            if (selectedServerName == null) return result;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte != null)
                {
                    foreach (Window window in dte.Windows)
                    {
                        if (window == null || window.HWnd == IntPtr.Zero) continue;
                        if (!LooksLikeObjectExplorer(window.Caption ?? string.Empty)) continue;
                        ResolveFromWindow(window.HWnd, selectedServerName, result);
                        if (result.Count > 0) return result;
                    }
                }

                IntPtr mainWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (mainWindow != IntPtr.Zero)
                    ResolveFromWindow(mainWindow, selectedServerName, result);
            }
            catch (Exception)
            {
            }

            return result;
        }

        private static void ResolveFromWindow(IntPtr window, Func<string> selectedServerName, List<string> result)
        {
            foreach (IntPtr tree in FindTreeViews(window))
            {
                foreach (RootItem item in ReadRootItems(tree))
                {
                    SendMessage(tree, TvmSelectItem, (IntPtr)TvgnCaret, item.Handle);
                    try { System.Windows.Forms.Application.DoEvents(); } catch (Exception) { }

                    try
                    {
                        string actual = selectedServerName();
                        if (string.IsNullOrWhiteSpace(actual)) continue;
                        if (!result.Exists(x => string.Equals(x, actual, StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Add(actual);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        public static bool SelectServer(string serverName)
        {
            return SelectServer(serverName, null);
        }

        /// <summary>
        /// Selects a root node by probing the selected node's live server name.  Object Explorer
        /// root captions are friendly display names such as "宝庆 (SQL Server 15.0 - sa)" and do
        /// not contain the host name used by the Quick Search picker.
        /// </summary>
        public static bool SelectServerByConnection(string serverName, Func<string> selectedServerName)
        {
            return SelectServer(serverName, selectedServerName);
        }

        private static bool SelectServer(string serverName, Func<string> selectedServerName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return false;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null) return false;

                foreach (Window window in dte.Windows)
                {
                    if (window == null || window.HWnd == IntPtr.Zero) continue;
                    if (!LooksLikeObjectExplorer(window.Caption ?? string.Empty)) continue;
                    if (TrySelectInWindow(window.HWnd, serverName, selectedServerName)) return true;
                }

                IntPtr mainWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (mainWindow != IntPtr.Zero)
                {
                    if (TrySelectInWindow(mainWindow, serverName, selectedServerName)) return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static bool TrySelectInWindow(IntPtr window, string serverName, Func<string> selectedServerName)
        {
            foreach (IntPtr tree in FindTreeViews(window))
            {
                foreach (RootItem item in ReadRootItems(tree))
                {
                    bool exact = string.Equals(item.Text, serverName, StringComparison.OrdinalIgnoreCase);
                    if (!exact && selectedServerName == null) continue;

                    SendMessage(tree, TvmSelectItem, (IntPtr)TvgnCaret, item.Handle);

                    bool matches = exact;
                    if (!matches && selectedServerName != null)
                    {
                        try
                        {
                            string actual = selectedServerName();
                            matches = string.Equals(actual, serverName, StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    if (matches)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static List<string> ReadServerNames(string preferredServer)
        {
            var names = new List<string>();
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null) return names;

                foreach (Window window in dte.Windows)
                {
                    try
                    {
                        if (window == null || window.HWnd == IntPtr.Zero) continue;
                        string caption = window.Caption ?? string.Empty;
                        if (!LooksLikeObjectExplorer(caption)) continue;

                        names = ReadWindowServers(window.HWnd, preferredServer);
                        if (names.Count > 0) break;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (names.Count == 0)
                {
                    IntPtr mainWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                    if (mainWindow != IntPtr.Zero)
                        names = ReadWindowServers(mainWindow, preferredServer);
                }
            }
            catch (Exception)
            {
            }

            return names;
        }

        private static List<string> ReadWindowServers(IntPtr window, string preferredServer)
        {
            List<string> fallback = null;
            foreach (IntPtr tree in FindTreeViews(window))
            {
                List<RootItem> items = ReadRootItems(tree);
                if (items.Count == 0) continue;

                var treeNames = new List<string>();
                foreach (RootItem item in items) treeNames.Add(item.Text);

                bool hasPreferred = string.IsNullOrWhiteSpace(preferredServer)
                    || treeNames.Exists(n => string.Equals(n, preferredServer, StringComparison.OrdinalIgnoreCase));
                if (hasPreferred) return treeNames;
                if (fallback == null) fallback = treeNames;
            }

            return fallback ?? new List<string>();
        }

        private static List<IntPtr> FindTreeViews(IntPtr window)
        {
            var trees = new List<IntPtr>();
            EnumChildWindows(window, (hWnd, _) =>
            {
                var className = new StringBuilder(256);
                GetClassName(hWnd, className, className.Capacity);
                string value = className.ToString();
                if (value.IndexOf("SysTreeView32", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("TreeView", StringComparison.OrdinalIgnoreCase) >= 0)
                    trees.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return trees;
        }

        private sealed class RootItem
        {
            public IntPtr Handle;
            public string Text;
        }

        private static bool LooksLikeObjectExplorer(string caption)
        {
            if (string.IsNullOrWhiteSpace(caption)) return false;
            string[] markers =
            {
                "Object Explorer", "对象资源管理器", "オブジェクト エクスプローラー",
                "개체 탐색기", "Explorateur d'objets", "Explorador de objetos", "Objekt-Explorer"
            };
            foreach (string marker in markers)
                if (caption.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static List<RootItem> ReadRootItems(IntPtr tree)
        {
            var items = new List<RootItem>();
            IntPtr item = SendMessage(tree, TvmGetNextItem, (IntPtr)TvgnRoot, IntPtr.Zero);
            while (item != IntPtr.Zero)
            {
                string text = ReadItemText(tree, item);
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(new RootItem { Handle = item, Text = text.Trim() });
                item = SendMessage(tree, TvmGetNextItem, (IntPtr)TvgnNext, item);
            }
            return items;
        }

        private static string ReadItemText(IntPtr tree, IntPtr item)
        {
            IntPtr buffer = Marshal.AllocHGlobal(TextBufferLength * 2);
            try
            {
                var tvItem = new TvItem
                {
                    mask = TvifText,
                    hItem = item,
                    pszText = buffer,
                    cchTextMax = TextBufferLength
                };

                IntPtr itemPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TvItem)));
                try
                {
                    Marshal.StructureToPtr(tvItem, itemPtr, false);
                    IntPtr result = SendMessage(tree, TvmGetItemW, IntPtr.Zero, itemPtr);
                    if (result == IntPtr.Zero) return null;
                    tvItem = (TvItem)Marshal.PtrToStructure(itemPtr, typeof(TvItem));
                    return Marshal.PtrToStringUni(tvItem.pszText);
                }
                finally
                {
                    Marshal.FreeHGlobal(itemPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
