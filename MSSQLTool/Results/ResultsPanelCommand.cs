using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace MSSQLTool.Results
{
    /// <summary>Toolbar entry that shows the floating statistics panel.</summary>
    internal static class ResultsPanelCommand
    {
        public const int CommandId = 4159;

        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            var id = new CommandID(CommandSet, CommandId);
            var item = new OleMenuCommand((_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { ResultsStatsFloatingWindow.ShowOrFocus(); }
                catch (Exception ex) { MSSQLToolPackage._logger?.Info("Could not show the statistics panel: " + ex.Message); }
            }, id);

            commandService.AddCommand(item);
        }
    }
}