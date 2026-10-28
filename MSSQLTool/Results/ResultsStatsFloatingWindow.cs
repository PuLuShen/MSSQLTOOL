using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Floating statistics window. It hosts the same control as the docked version did, but stands on its
    /// own: it is placed next to the results grid, remembers where it was and how big it was, and never
    /// takes the focus when it appears so the grid selection keeps its colour.
    /// </summary>
    public sealed class ResultsStatsFloatingWindow : Window
    {
        private readonly ResultsStatsControl content;

        private const int WhKeyboardLowLevel = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int VkBack = 0x08;
        private const int VkDelete = 0x2E;

        private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);
        private static IntPtr deleteHook;
        private static KeyboardHookProc deleteHookCallback;
        private static ResultsStatsFloatingWindow deleteHookOwner;

        /// <summary>The window that is currently open, if any.</summary>
        internal static ResultsStatsFloatingWindow Current { get; set; }

        public ResultsStatsFloatingWindow()
        {
            Title = LocalizationManager.T("Result statistics panel");
            Width = 380;
            Height = 460;
            MinWidth = 260;
            MinHeight = 240;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = false;
            // Appearing must not steal the focus from the results grid.
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? SystemColors.WindowBrush;

            content = new ResultsStatsControl();
            Content = content;

            // ShowActivated=false keeps the grid selection alive, but it also means the first click
            // in the panel does not move the keyboard focus here: Delete and the arrow keys then
            // still reach the SQL editor.  Activate explicitly when the user clicks the panel.
            PreviewMouseDown += (_, __) =>
            {
                try { if (!IsActive) Activate(); }
                catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not activate the statistics window."); }
            };

            // SSMS translates Delete into an editor command before WPF sees it, even when the
            // floating window's text box owns the keyboard.  A low-level hook is the only place
            // early enough to keep Delete inside the panel; it edits the focused box itself.
            Loaded += (_, __) => InstallDeleteGuard();
            Closed += (_, __) => UninstallDeleteGuard();

            RestoreGeometry();

            // Give the window the SSMS main window as its owner: without it the panel disappears behind
            // the shell as soon as the results grid is clicked.
            try
            {
                var uiShell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
                IntPtr owner;
                if (uiShell != null && uiShell.GetDialogOwnerHwnd(out owner) == 0 && owner != IntPtr.Zero)
                    new System.Windows.Interop.WindowInteropHelper(this) { Owner = owner };
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not give the statistics window an owner.");
            }
            Closed += (_, __) => { RememberGeometry(); if (ReferenceEquals(Current, this)) Current = null; };
            Closed += (_, __) => content.Dispose();
        }

        private void InstallDeleteGuard()
        {
            if (deleteHook != IntPtr.Zero) return;

            try
            {
                deleteHookCallback = DeleteGuardCallback;
                deleteHook = SetWindowsHookEx(WhKeyboardLowLevel, deleteHookCallback, GetModuleHandle(null), 0);
                if (deleteHook == IntPtr.Zero)
                {
                    MSSQLToolPackage._logger?.Info("Could not install the panel Delete guard (Win32 error "
                        + Marshal.GetLastWin32Error() + ").");
                    deleteHookCallback = null;
                    return;
                }

                deleteHookOwner = this;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not install the panel Delete guard.");
                deleteHook = IntPtr.Zero;
                deleteHookCallback = null;
            }
        }

        private static void UninstallDeleteGuard()
        {
            if (deleteHook == IntPtr.Zero) return;

            try
            {
                UnhookWindowsHookEx(deleteHook);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not remove the panel Delete guard.");
            }
            finally
            {
                deleteHook = IntPtr.Zero;
                deleteHookCallback = null;
                deleteHookOwner = null;
            }
        }

        private static IntPtr DeleteGuardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && deleteHook != IntPtr.Zero)
                {
                    long message = wParam.ToInt64();
                    if (message == WmKeyDown || message == WmSysKeyDown)
                    {
                        int virtualKey = Marshal.ReadInt32(lParam);
                        if (virtualKey == VkDelete || virtualKey == VkBack)
                        {
                            ResultsStatsFloatingWindow owner = Current ?? deleteHookOwner;
                            if (owner != null && owner.TryHandleDeleteKey(virtualKey == VkBack))
                                return new IntPtr(1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "The panel Delete guard failed.");
            }

            return CallNextHookEx(deleteHook, code, wParam, lParam);
        }

        private bool TryHandleDeleteKey(bool back)
        {
            if (!IsVisible || content == null) return false;
            return content.TryHandleDeleteKey(back);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        /// <summary>Shows the panel (creating it when needed) without stealing the focus.</summary>
        internal static void ShowOrFocus()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Current != null)
                {
                    if (!Current.IsVisible) Current.Show();
                    Current.RefreshContent();
                    return;
                }

                var window = new ResultsStatsFloatingWindow();
                Current = window;
                window.ShowActivated = false;
                window.Show();
                window.RefreshContent();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Info("Could not show the statistics panel: " + ex.Message);
            }
        }

        /// <summary>Repaints the numbers, used when the window is shown from the toolbar.</summary>
        public void RefreshContent() => content.Refresh();

        private void RestoreGeometry()
        {
            try
            {
                var settings = SettingsManager.GetSqlCompletionSettings();

                double width, height;
                if (TryParsePair(settings?.resultsPanelSize, out width, out height))
                {
                    if (width >= 240 && height >= 200 && width <= 2000 && height <= 1600)
                    {
                        Width = width;
                        Height = height;
                    }
                }

                double left, top;
                if (TryParsePair(settings?.resultsPanelPosition, out left, out top))
                {
                    // Only restore a position that is still on a screen that exists.
                    foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
                    {
                        var bounds = screen.WorkingArea;
                        if (left >= bounds.Left - 50 && left <= bounds.Right - 100 &&
                            top >= bounds.Top - 20 && top <= bounds.Bottom - 100)
                        {
                            Left = left;
                            Top = top;
                            return;
                        }
                    }
                }

                PlaceNextToResults();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not restore the statistics window geometry.");
            }
        }

        private void RememberGeometry()
        {
            try
            {
                var settings = SettingsManager.GetSqlCompletionSettings();
                if (settings == null) return;

                settings.resultsPanelPosition = Math.Round(Left) + "," + Math.Round(Top);
                settings.resultsPanelSize = Math.Round(Width) + "," + Math.Round(Height);
                SettingsManager.SaveSqlCompletionSettings(settings);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not store the statistics window geometry.");
            }
        }

        private static bool TryParsePair(string value, out double first, out double second)
        {
            first = second = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string[] parts = value.Split(',');
            if (parts.Length != 2) return false;

            return double.TryParse(parts[0].Trim(), out first) && double.TryParse(parts[1].Trim(), out second);
        }

        /// <summary>Right of the results grid when a position cannot be restored.</summary>
        private void PlaceNextToResults()
        {
            try
            {
                object resultsControl = null;
                try { resultsControl = GridAccess.GetSQLResultsControl(); } catch (Exception) { }

                var control = resultsControl as System.Windows.Forms.Control;
                if (control != null && control.IsHandleCreated && !control.IsDisposed)
                {
                    System.Drawing.Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                    Left = bounds.Right - Width - 20;
                    Top = bounds.Top + 40;
                    return;
                }

                Left = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Right - Width - 40;
                Top = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Top + 80;
            }
            catch (Exception)
            {
                Left = 200;
                Top = 150;
            }
        }
    }
}
