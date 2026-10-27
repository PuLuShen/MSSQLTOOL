using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;

namespace MSSQLTool.Results
{
    /// <summary>Opens the result set tools window (search + statistics) for the current grid.</summary>
    internal sealed class ResultsToolsCommand
    {
        public const int CommandId = 4158;
        /// <summary>Tools menu switch for the floating statistics panel.</summary>
        public const int TogglePanelCommandId = 4159;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly AsyncPackage package;

        private ResultsToolsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
            commandService.AddCommand(new MenuCommand(TogglePanel, new CommandID(CommandSet, TogglePanelCommandId)));
        }

        public static ResultsToolsCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ResultsToolsCommand(package, commandService);
        }

        private void TogglePanel(object sender, EventArgs e)
        {
            package.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                // Toggle the docked statistics window: show it without stealing the focus when it is
                // closed, bring it forward (and refresh) when it is already open.
                var window = package.FindToolWindow(typeof(ResultsStatsWindow), 0, true) as ResultsStatsWindow;
                if (window == null || window.Frame == null) return;

                var frame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)window.Frame;
                window.RefreshContent();
                frame.ShowNoActivate();
            });
        }

        private void Execute(object sender, EventArgs e)
        {
            package.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                var window = new ResultsToolsWindow();
                var owner = System.Windows.Application.Current?.MainWindow;
                if (owner != null && !ReferenceEquals(owner, window)) window.Owner = owner;
                window.Show();
                window.Activate();
            });
        }
    }
}