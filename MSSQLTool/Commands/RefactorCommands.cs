using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using MSSQLTool.Commands.Dialogs;
using MSSQLTool.Model;
using Task = System.Threading.Tasks.Task;

namespace MSSQLTool.Commands
{
    /// <summary>
    /// Script-scoped rename (table, column, alias or variable).  The heavy lifting lives in
    /// <see cref="SqlRenameService"/>; this class only reads the editor, prompts and applies the
    /// planned spans inside one undo transaction.
    /// </summary>
    internal sealed class RenameSymbolCommand
    {
        public const int CommandId = 4163;
        public const int ContextMenuCommandId = 4166;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private static AsyncPackage package;

        private RenameSymbolCommand() { }

        public static async Task InitializeAsync(AsyncPackage hostPackage)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(hostPackage.DisposalToken);
            package = hostPackage;

            OleMenuCommandService commandService = await hostPackage.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            var menuItem = new MenuCommand(Execute, new CommandID(CommandSet, CommandId));
            commandService.AddCommand(menuItem);
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, ContextMenuCommandId)));
        }

        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null || dte.ActiveDocument == null) return;

                TextDocument textDocument = dte.ActiveDocument.Object("TextDocument") as TextDocument;
                TextSelection selection = dte.ActiveDocument.Selection as TextSelection;
                if (textDocument == null || selection == null) return;

                string fullText = GetFullText(textDocument);
                int caretOffset = GetCaretOffset(selection, fullText);
                SqlToken token = SqlTokenizer.TokenAt(SqlDocumentModel.Get(fullText).Tokens, caretOffset);
                if (token == null || token.Kind != SqlTokenKind.Identifier)
                {
                    RefactorFeedback.Show("Place the caret on a table, column, alias or variable name first.");
                    return;
                }

                string oldName = SqlTokenizer.Unquote(token);
                string newName;
                bool wholeDocument;
                if (!RefactorDialog.PromptRename(oldName, out newName, out wholeDocument)) return;

                SqlRenamePlan plan = SqlRenameService.Build(fullText, caretOffset, newName, wholeDocument);
                if (!plan.Success)
                {
                    RefactorFeedback.Show(LocalizationManager.T(plan.Error));
                    return;
                }

                ApplyChanges(dte, textDocument, fullText, plan.Changes);
                RefactorFeedback.Show(LocalizationManager.Format("Renamed {0} reference(s). Press Ctrl+Z to undo.", plan.Count));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Rename failed.");
                RefactorFeedback.Show(LocalizationManager.Format("Could not rename: {0}", ex.Message));
            }
        }

        internal static string GetFullText(TextDocument textDocument)
        {
            EditPoint start = textDocument.StartPoint.CreateEditPoint();
            return start.GetText(textDocument.EndPoint) ?? string.Empty;
        }

        /// <summary>
        /// Caret index in the buffer.  DTE lines/columns are 1-based while the tokenizer is 0-based,
        /// so the conversion goes through TextPositions instead of relying on AbsoluteCharOffset,
        /// whose origin differs between the DTE automation wrappers.
        /// </summary>
        internal static int GetCaretOffset(TextSelection selection, string fullText)
        {
            TextPoint active = selection.ActivePoint;
            return TextPositions.ToOffset(fullText, active.Line - 1, active.LineCharOffset - 1);
        }

        /// <summary>
        /// Applies spans from the end so earlier offsets stay valid; one undo step.  Positions are
        /// converted back to line/column because DTE's absolute offset origin is not guaranteed to
        /// match the text the plan was built from (CRLF is counted differently by some hosts),
        /// which used to shift every edit and corrupt surrounding text on later lines.
        /// </summary>
        internal static void ApplyChanges(DTE dte, TextDocument textDocument, string fullText, IList<SqlTextChange> changes)
        {
            if (string.IsNullOrEmpty(fullText) || textDocument == null || changes == null) return;

            var ordered = new List<SqlTextChange>(changes);
            ordered.Sort((left, right) => right.Start.CompareTo(left.Start));

            bool undoOpened = false;
            try
            {
                try
                {
                    dte.UndoContext.Open("MSSQL Tool refactoring");
                    undoOpened = true;
                }
                catch (Exception)
                {
                    // Undo grouping is a nicety; the edit itself must still happen.
                }

                foreach (SqlTextChange change in ordered)
                {
                    if (change.Start < 0 || change.Start > fullText.Length) continue;

                    int line, column;
                    TextPositions.ToLineColumn(fullText, change.Start, out line, out column);

                    EditPoint point = textDocument.StartPoint.CreateEditPoint();
                    point.MoveToLineAndOffset(line + 1, column + 1);
                    if (change.Length > 0) point.Delete(change.Length);
                    if (!string.IsNullOrEmpty(change.Text)) point.Insert(change.Text);
                }
            }
            finally
            {
                if (undoOpened)
                {
                    try { dte.UndoContext.Close(); } catch (Exception) { }
                }
            }
        }
    }

    /// <summary>Adds the schema prefix to bare table references (FROM/JOIN/UPDATE/INTO).</summary>
    internal sealed class QualifyObjectNamesCommand
    {
        public const int CommandId = 4164;
        public const int ContextMenuCommandId = 4167;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private QualifyObjectNamesCommand() { }

        public static async Task InitializeAsync(AsyncPackage hostPackage)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(hostPackage.DisposalToken);

            OleMenuCommandService commandService = await hostPackage.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            var menuItem = new MenuCommand(Execute, new CommandID(CommandSet, CommandId));
            commandService.AddCommand(menuItem);
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, ContextMenuCommandId)));
        }

        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null || dte.ActiveDocument == null) return;

                TextDocument textDocument = dte.ActiveDocument.Object("TextDocument") as TextDocument;
                if (textDocument == null) return;

                string schema;
                if (!RefactorDialog.PromptSchema(out schema)) return;

                string fullText = RenameSymbolCommand.GetFullText(textDocument);
                SqlQualifyPlan plan = SqlQualifyService.Build(fullText, schema);
                if (!plan.Success)
                {
                    RefactorFeedback.Show(LocalizationManager.T(plan.Error));
                    return;
                }

                RenameSymbolCommand.ApplyChanges(dte, textDocument, fullText, plan.Changes);
                RefactorFeedback.Show(LocalizationManager.Format("Qualified {0} table reference(s) with the schema \"{1}\".", plan.Count, plan.Schema));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Qualify object names failed.");
                RefactorFeedback.Show(LocalizationManager.Format("Could not qualify object names: {0}", ex.Message));
            }
        }
    }

    /// <summary>Short status-bar feedback that clears itself, shared by the refactorings.</summary>
    internal static class RefactorFeedback
    {
        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                IDisposable feedback = StatusFeedback.Begin(message);
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Task.Delay(5000);
                    feedback.Dispose();
                });
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not show the refactoring status message.");
            }
        }
    }
}
