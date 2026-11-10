using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace MSSQLTool
{
    /// <summary>Opens the query text history tool window.</summary>
    internal sealed class QueryTextHistoryWindowCommand
    {
        public const int CommandId = 4165;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly AsyncPackage package;

        private QueryTextHistoryWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package;
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        public static QueryTextHistoryWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;
            Instance = new QueryTextHistoryWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            package.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    ToolWindowPane window = await package.ShowToolWindowAsync(
                        typeof(QueryTextHistoryWindow), 0, true, package.DisposalToken);
                    if (window?.Frame == null) throw new NotSupportedException("Cannot create the query text history window.");

                    var frame = (IVsWindowFrame)window.Frame;
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
                }
                catch (Exception ex)
                {
                    VsShellUtilities.ShowMessageBox(package,
                        LocalizationManager.Format("Failed to open the query text history: {0}", ex.Message),
                        "MSSQL Tool", OLEMSGICON.OLEMSGICON_CRITICAL,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            });
        }
    }
}
