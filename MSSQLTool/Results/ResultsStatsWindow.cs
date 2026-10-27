using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Docked window that shows the statistics of the current result set selection.  Being a real SSMS tool
    /// window means the shell owns the keyboard focus, the panel can be docked or auto-hidden, and its
    /// position is saved with the layout.
    /// </summary>
    [Guid("0f6b7d51-3a4e-4c2b-9d38-2a6f5c7e9b13")]
    public sealed class ResultsStatsWindow : ToolWindowPane
    {
        public ResultsStatsWindow() : base(null)
        {
            Caption = LocalizationManager.T("Result statistics panel");
            Content = new ResultsStatsControl();
        }

        /// <summary>Refreshes the numbers when the window is shown from the toolbar.</summary>
        public void RefreshContent()
        {
            (Content as ResultsStatsControl)?.Refresh();
        }
    }
}