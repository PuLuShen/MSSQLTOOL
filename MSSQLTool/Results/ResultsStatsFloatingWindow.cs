using System;
using System.Windows;
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
