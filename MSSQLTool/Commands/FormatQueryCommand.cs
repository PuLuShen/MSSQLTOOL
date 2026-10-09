using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;
using static MSSQLTool.MSSQLToolPackage;
using System.Windows.Input;
using System.Windows.Interop;

namespace MSSQLTool
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class FormatQueryCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 4131;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private const int IdYes = 6;

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="FormatQueryCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private FormatQueryCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static FormatQueryCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in FormatQueryCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new FormatQueryCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;

            if (dte?.ActiveDocument == null)
            {
                ShowStatusHint(LocalizationManager.T("Open a SQL query window with content first."));
                return;
            }

            FormatterOptions formatSettings = SettingsManager.GetFormatterOptions();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                // Own the dialog by VS main window
                var dlg = new FormatOptionsDialog(formatSettings);
                var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                if (uiShell != null && uiShell.GetDialogOwnerHwnd(out var hwnd) == 0 && hwnd != IntPtr.Zero)
                {
                    new WindowInteropHelper(dlg).Owner = hwnd;
                }

                var ok = dlg.ShowDialog();
                if (ok != true)
                {
                    return; // user canceled
                }

                formatSettings = dlg.Settings;

                if (dlg.SaveAsDefault)
                {
                    SettingsManager.SaveFormatterOptions(formatSettings);
                }
            }

            // Capture everything the formatting needs while we are on the UI thread.
            TextDocument textDoc = dte.ActiveDocument.Object("TextDocument") as TextDocument;
            TextSelection selection = dte.ActiveDocument.Selection as TextSelection;
            if (textDoc == null)
                return;

            string docName = dte.ActiveDocument.FullName;
            int docEndOffset = textDoc.EndPoint.AbsoluteCharOffset;

            string selectedText = selection?.Text;
            bool hasSelection = !string.IsNullOrWhiteSpace(selectedText);

            string sourceText;
            int replaceStart, replaceEnd;
            string leadingWhitespace = string.Empty, trailingWhitespace = string.Empty;

            if (hasSelection)
            {
                sourceText = selectedText;
                replaceStart = selection.TopPoint.AbsoluteCharOffset;
                replaceEnd = selection.BottomPoint.AbsoluteCharOffset;
            }
            else
            {
                sourceText = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);
                if (string.IsNullOrWhiteSpace(sourceText))
                    return;

                replaceStart = 1;
                replaceEnd = docEndOffset;

                // Keep the blank lines at the beginning and the end of the document.
                int leadLength = sourceText.Length - sourceText.TrimStart().Length;
                int trailLength = sourceText.Length - sourceText.TrimEnd().Length;
                leadingWhitespace = sourceText.Substring(0, leadLength);
                trailingWhitespace = sourceText.Substring(sourceText.Length - trailLength);
                sourceText = sourceText.Substring(leadLength, sourceText.Length - leadLength - trailLength);
            }

            SetStatusBar(dte, LocalizationManager.T("Formatting SQL..."));

            // Formatting is CPU bound (two parses + generation); run it off the UI thread
            // so SSMS stays responsive, then apply the result back on the UI thread.
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string formatted = null;
                TSqlFormatException parseError = null;
                Exception failure = null;

                try
                {
                    formatted = await Task.Run(() => TSqlFormatter.FormatCode(sourceText, formatSettings));
                }
                catch (TSqlFormatException ex)
                {
                    parseError = ex;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.package.DisposalToken);

                // The document must not have changed while the code was being formatted,
                // otherwise the captured replace range would no longer match the code.
                TextDocument currentDoc = GetActiveTextDocument(dte, docName, docEndOffset);
                if (currentDoc == null)
                {
                    SetStatusBar(dte, string.Empty);
                    ShowWarning(LocalizationManager.T("The document changed while it was being formatted. Formatting was canceled to avoid losing any code."));
                    return;
                }

                if (parseError != null)
                {
                    if (hasSelection && OfferFormatCurrentStatement(textDoc, formatSettings, replaceStart, replaceEnd, parseError))
                        return;

                    // Make it clear the editor content was not touched.
                    LocalizedMessageBox.Show(
                        LocalizationManager.Format("The SQL could not be parsed, so it was left unchanged: {0}", parseError.Message),
                        LocalizationManager.T("Unable to format T-SQL"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                    return;
                }

                if (failure != null)
                {
                    ShowWarning(failure.Message);
                    return;
                }

                if (string.IsNullOrEmpty(formatted))
                    return; // never wipe the editor content

                try
                {
                    string replacement = hasSelection
                        ? formatted
                        : leadingWhitespace + formatted + trailingWhitespace;

                    EditPoint startPoint = currentDoc.StartPoint.CreateEditPoint();
                    startPoint.MoveToAbsoluteOffset(replaceStart);
                    EditPoint endPoint = currentDoc.StartPoint.CreateEditPoint();
                    endPoint.MoveToAbsoluteOffset(replaceEnd);

                    // A single ReplaceText keeps bookmarks/breakpoints and yields one undo step.
                    startPoint.ReplaceText(endPoint, replacement, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);

                    SetStatusBar(dte, LocalizationManager.T("SQL formatted."));
                }
                catch (Exception ex)
                {
                    ShowWarning(ex.Message);
                }
            });
        }

        /// <summary>
        /// Offered when a selection fails to parse: formatting the whole statement that
        /// contains the selection usually succeeds, because the selection itself may only
        /// be a fragment of it.  Returns true when the situation is fully handled.
        /// </summary>
        private bool OfferFormatCurrentStatement(TextDocument textDoc, FormatterOptions formatSettings, int selectionStart, int selectionEnd, TSqlFormatException parseError)
        {
            int answer = VsShellUtilities.ShowMessageBox(
                this.package,
                parseError.Message + Environment.NewLine + Environment.NewLine
                    + LocalizationManager.T("The selection is not valid T-SQL on its own. Format the current statement instead?"),
                LocalizationManager.T("Unable to format T-SQL"),
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            if (answer != IdYes)
                return false;

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                string fullText = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);

                // DTE offsets are 1-based; TryExtractStatementSpan works with 0-based offsets.
                if (!TSqlFormatter.TryExtractStatementSpan(fullText, selectionStart - 1, selectionEnd - 1, out int spanStart, out int spanEnd))
                {
                    return false;
                }

                string statementText = fullText.Substring(spanStart, spanEnd - spanStart);
                string formattedStatement = TSqlFormatter.FormatCode(statementText, formatSettings);

                EditPoint startPoint = textDoc.StartPoint.CreateEditPoint();
                startPoint.MoveToAbsoluteOffset(spanStart + 1);
                EditPoint endPoint = textDoc.StartPoint.CreateEditPoint();
                endPoint.MoveToAbsoluteOffset(spanEnd);

                startPoint.ReplaceText(endPoint, formattedStatement, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);
                return true;
            }
            catch (TSqlFormatException ex)
            {
                ShowWarning(ex.Message, LocalizationManager.T("Unable to format T-SQL"));
                return true;
            }
            catch (Exception ex)
            {
                ShowWarning(ex.Message);
                return true;
            }
        }

        private static TextDocument GetActiveTextDocument(DTE dte, string docName, int expectedEndOffset)
        {
            try
            {
                if (dte?.ActiveDocument == null || !string.Equals(dte.ActiveDocument.FullName, docName, StringComparison.OrdinalIgnoreCase))
                    return null;

                var textDoc = dte.ActiveDocument.Object("TextDocument") as TextDocument;
                if (textDoc == null || textDoc.EndPoint.AbsoluteCharOffset != expectedEndOffset)
                    return null;

                return textDoc;
            }
            catch
            {
                return null;
            }
        }

        private void ShowWarning(string message, string title = null)
        {
            LocalizedMessageBox.Show(
                message,
                title ?? LocalizationManager.T("Error formatting the code"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        /// <summary>
        /// Shows a short hint in the SSMS status bar that clears itself after a few seconds.
        /// </summary>
        private static void ShowStatusHint(string message)
        {
            IDisposable feedback = StatusFeedback.Begin(message);
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(4000);
                feedback.Dispose();
            });
        }

        private static void SetStatusBar(DTE dte, string text)
        {
            try
            {
                if (dte?.StatusBar != null)
                    dte.StatusBar.Text = text;
            }
            catch
            {
                // The status bar is cosmetic; ignore failures.
            }
        }
    }
}
