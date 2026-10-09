using EnvDTE;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MSSQLTool
{
    internal static class QueryTemplateInsertionService
    {
        public static void InsertFile(string fullPath, bool forceNewQuery)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string content = QueryTemplateLibrary.Instance.ReadContent(fullPath);
            InsertText(content, forceNewQuery);
            QueryTemplateLibrary.Instance.RecordUsed(fullPath);
        }

        public static void InsertText(string content, bool forceNewQuery)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte == null) throw new InvalidOperationException("SSMS editor automation is unavailable.");

            if (forceNewQuery || !IsSqlDocument(dte.ActiveDocument))
                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);

            IVsTextManager textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager != null
                && textManager.GetActiveView(0, null, out IVsTextView textView) == VSConstants.S_OK
                && textView != null
                && textView.GetBuffer(out IVsTextLines lines) == VSConstants.S_OK
                && lines != null)
            {
                InsertThroughTextView(textView, lines, content ?? string.Empty);
                return;
            }

            InsertThroughAutomation(dte, content ?? string.Empty);
        }

        public static string GetCurrentEditorText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IVsTextManager textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null
                || textManager.GetActiveView(0, null, out IVsTextView textView) != VSConstants.S_OK
                || textView == null)
                return string.Empty;

            if (textView.GetSelectedText(out string selectedText) == VSConstants.S_OK
                && !string.IsNullOrEmpty(selectedText))
                return selectedText;

            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null
                || lines.GetLastLineIndex(out int lastLine, out int lastColumn) != VSConstants.S_OK
                || lines.GetLineText(0, 0, lastLine, lastColumn, out string documentText) != VSConstants.S_OK)
                return string.Empty;

            return documentText ?? string.Empty;
        }

        private static void InsertThroughTextView(IVsTextView view, IVsTextLines lines, string content)
        {
            view.GetSelection(out int startLine, out int startColumn, out int endLine, out int endColumn);
            if (startLine > endLine || startLine == endLine && startColumn > endColumn)
            {
                Swap(ref startLine, ref endLine);
                Swap(ref startColumn, ref endColumn);
            }

            IntPtr text = Marshal.StringToHGlobalUni(content);
            try
            {
                var changed = new Microsoft.VisualStudio.TextManager.Interop.TextSpan[1];
                using (var edit = EditorEditTransaction.Begin(lines, "Insert query template"))
                {
                    int result = lines.ReplaceLines(startLine, startColumn, endLine, endColumn, text, content.Length, changed);
                    ErrorHandler.ThrowOnFailure(result);
                    edit.Complete();
                }

                view.SetCaretPos(changed[0].iEndLine, changed[0].iEndIndex);
                view.SetSelection(changed[0].iEndLine, changed[0].iEndIndex, changed[0].iEndLine, changed[0].iEndIndex);
                view.CenterLines(changed[0].iEndLine, 1);
                view.SendExplicitFocus();
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        private static void InsertThroughAutomation(DTE dte, string content)
        {
            TextSelection selection = dte.ActiveDocument?.Selection as TextSelection;
            if (selection == null) throw new InvalidOperationException("No SQL query editor is available for template insertion.");

            bool openedUndo = false;
            try
            {
                if (!dte.UndoContext.IsOpen)
                {
                    dte.UndoContext.Open("Insert query template", false);
                    openedUndo = true;
                }
                if (!selection.IsEmpty) selection.Delete();
                selection.Insert(content);
                if (openedUndo) dte.UndoContext.Close();
            }
            catch
            {
                if (openedUndo && dte.UndoContext.IsOpen) dte.UndoContext.SetAborted();
                throw;
            }
        }

        private static bool IsSqlDocument(Document document)
        {
            if (document == null) return false;
            string name = document.Name ?? string.Empty;
            string fullName = document.FullName ?? string.Empty;
            return string.Equals(Path.GetExtension(name), ".sql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(fullName), ".sql", StringComparison.OrdinalIgnoreCase);
        }

        private static void Swap(ref int left, ref int right)
        {
            int value = left;
            left = right;
            right = value;
        }
    }
}
