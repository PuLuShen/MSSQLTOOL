using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace MSSQLTool
{
    /// <summary>
    /// Drops the SQL completion metadata cache for the active connection and
    /// reloads it eagerly, so schema changes made outside of this plugin
    /// (other developers, other tools) are picked up without waiting for the
    /// next completion request.
    /// </summary>
    internal sealed class RefreshCompletionMetadataCommand
    {
        public const int CommandId = 4146;

        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly MSSQLToolPackage package;

        private RefreshCompletionMetadataCommand(MSSQLToolPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static RefreshCompletionMetadataCommand Instance
        {
            get;
            private set;
        }

        public static async Task InitializeAsync(MSSQLToolPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new RefreshCompletionMetadataCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (connection == null || string.IsNullOrWhiteSpace(connection.FullConnectionString))
                {
                    // Without a connection nothing can be invalidated; reporting
                    // success here would leave the user with stale metadata.
                    LocalizedMessageBox.Show(
                        LocalizationManager.T("Connect to a server first — open a connected query window, then run refresh again."),
                        "Refresh Completion Metadata", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Completion.SqlMetadataCache.Invalidate(connection);

                // Reload eagerly: GetAsync shows the "Loading SQL completion
                // metadata..." status animation itself and completes only when
                // the new snapshot is ready, so the follow-up status text is a
                // real result, not a promise.
                Task reload = Task.Run(async () =>
                {
                    try
                    {
                        Completion.MetadataSnapshot snapshot =
                            await Completion.SqlMetadataCache.GetAsync(connection, CancellationToken.None).ConfigureAwait(false);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                        ReportResult(snapshot);
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Error(ex, "Manual SQL completion metadata refresh failed");
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        ShowStatus(LocalizationManager.T("SQL completion metadata refresh failed.") + " " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Manual SQL completion metadata refresh failed");
                LocalizedMessageBox.Show(
                    LocalizationManager.T("Could not refresh SQL completion metadata:") + " " + ex.Message,
                    "Refresh Completion Metadata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReportResult(Completion.MetadataSnapshot snapshot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (snapshot == null)
            {
                ShowStatus(LocalizationManager.T("SQL completion metadata refreshed."));
                return;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
            {
                ShowStatus(LocalizationManager.Format("SQL completion metadata refresh failed: {0}", snapshot.ErrorMessage));
                return;
            }

            ShowStatus(LocalizationManager.Format("SQL completion metadata refreshed ({0} objects, {1} columns).",
                snapshot.Objects.Count, snapshot.Objects.Sum(o => o.Columns.Count)));
        }

        private static void ShowStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText(text);
        }
    }
}
