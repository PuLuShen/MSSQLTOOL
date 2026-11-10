using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using MSSQLTool.Analysis;
using MSSQLTool.Editor;
using MSSQLTool.Model;

namespace MSSQLTool.Rendering
{
    /// <summary>
    /// "Fix problem at caret": runs the analysis for the caret statement and applies the first
    /// available quick fix without opening the Code Analysis tool window.  The editor's own
    /// light-bulb API is deliberately avoided; this command is the discoverable substitute and
    /// shares its edit path with the tool window.
    /// </summary>
    internal static class FixAtCaretCommand
    {
        public const int CommandId = 4161;
        public const int ContextMenuCommandId = 4162;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            if (package == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
            if (commandService == null) return;
            var commandId = new CommandID(CommandSet, CommandId);
            var command = new MenuCommand((_, __) => Apply(), commandId);
            commandService.AddCommand(command);
            var contextCommandId = new CommandID(CommandSet, ContextMenuCommandId);
            commandService.AddCommand(new MenuCommand((_, __) => Apply(), contextCommandId));
        }

        private static void Apply()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                using (IEditorHost host = ActiveEditorLocator.CreateHost())
                {
                    if (host == null) return;
                    if (!host.TryReadAllText(out string text)) return;

                    AnalysisSettings settings = SettingsManager.GetAnalysisSettings();
                    if (!settings.enabled) return;

                    int caret = 0;
                    if (host.TryGetCaret(out int line, out int column))
                        caret = TextPositions.ToOffset(text, line, column);

                    AnalysisDiagnostic diagnostic = QuickFixService.FindFixableAtCaret(text, caret, settings);
                    if (diagnostic == null)
                    {
                        ShowStatus(LocalizationManager.T("Nothing to fix at the caret."));
                        return;
                    }

                    bool applied = QuickFixActions.TryApply(host, text, diagnostic);
                    ShowStatus(applied
                        ? LocalizationManager.T("Fix applied.")
                        : LocalizationManager.T("No automatic fix is available for this rule."));
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Code Analysis", "Fix at caret failed", ex);
            }
        }

        private static void ShowStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var provider = ServiceProvider.GlobalProvider;
                var statusBar = provider?.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsStatusbar))
                    as Microsoft.VisualStudio.Shell.Interop.IVsStatusbar;
                statusBar?.SetText(message ?? string.Empty);
            }
            catch (Exception) { }
        }
    }
}
