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
        private const uint TvmGetItemW = 0x113E;
        private const int TvgnRoot = 0;
        private const int TvgnNext = 1;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

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
            catch (Exception ex)
            {
                QuickSearchWindowControl.Log("Object Explorer TreeView fallback failed: " + ex.Message);
            }

            return names;
        }

        private static List<string> ReadWindowServers(IntPtr window, string preferredServer)
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

            List<string> fallback = null;
            foreach (IntPtr tree in trees)
            {
                List<string> treeNames = ReadRootItems(tree);
                if (treeNames.Count == 0) continue;

                bool hasPreferred = string.IsNullOrWhiteSpace(preferredServer)
                    || treeNames.Exists(n => string.Equals(n, preferredServer, StringComparison.OrdinalIgnoreCase));
                if (hasPreferred) return treeNames;
                if (fallback == null) fallback = treeNames;
            }

            return fallback ?? new List<string>();
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

        private static List<string> ReadRootItems(IntPtr tree)
        {
            var names = new List<string>();
            IntPtr item = SendMessage(tree, TvmGetNextItem, (IntPtr)TvgnRoot, IntPtr.Zero);
            while (item != IntPtr.Zero)
            {
                string text = ReadItemText(tree, item);
                if (!string.IsNullOrWhiteSpace(text)) names.Add(text.Trim());
                item = SendMessage(tree, TvmGetNextItem, (IntPtr)TvgnNext, item);
            }
            return names;
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
