using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using MSSQLTool.Completion;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;

namespace MSSQLTool
{
    public class KeypressCommandFilter : IOleCommandTarget, IDisposable
    {
        private sealed class NavigationLocation { public IVsTextView View; public int Line; public int Column; }
        private static readonly ConcurrentStack<NavigationLocation> NavigationHistory = new ConcurrentStack<NavigationLocation>();
        private IOleCommandTarget nextCommandTarget;
        private IVsTextView textView;
        private readonly MSSQLToolPackage package;
        private readonly CompletionController completionController;
        private CancellationTokenSource navigationCancellation;
        private bool f12KeyboardHookRegistered;
        public KeypressCommandFilter(MSSQLToolPackage package, IVsTextView textView)
        {
            this.package = package;
            this.textView = textView;
            completionController = new CompletionController(textView);
            completionController.BeforePopupShown += DismissNativeCompletion;
        }

        public void AddToChain()
        {
            // Adds this filter into the command chain
            if (textView != null && textView.AddCommandFilter(this, out nextCommandTarget) != VSConstants.S_OK)
            {
                throw new Exception("Failed to add command filter");
            }

            // SSMS 22 consumes its built-in F12 binding before editor command
            // filters and before its UI-thread keyboard hook. Register this view
            // with one process-wide low-level hook, which runs before accelerator
            // translation and is then restricted back to the focused SQL editor.
            f12KeyboardHookRegistered = F12KeyboardHook.Register(this);
        }

        public int Exec(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // Every keystroke reaches this filter.  An exception escaping it is reported by SSMS as a
            // dialog with a raw .NET message and breaks the key, so failures are logged and the key
            // is passed on to the editor instead.
            try
            {
                return ExecCore(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL editor keys", "A key or command could not be processed by MSSQL Tool", ex);
                try { return nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK; }
                catch { return VSConstants.S_OK; }
            }
        }

        private int ExecCore(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (cmdGroup == VSConstants.GUID_VSStandardCommandSet97
                && (nCmdID == (uint)VSConstants.VSStd97CmdID.Paste
                    || nCmdID == (uint)VSConstants.VSStd97CmdID.Cut
                    || nCmdID == (uint)VSConstants.VSStd97CmdID.Undo
                    || nCmdID == (uint)VSConstants.VSStd97CmdID.Redo))
            {
                int editResult = nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
                completionController.Request(false);
                return editResult;
            }
            if (cmdGroup == VSConstants.GUID_VSStandardCommandSet97 && nCmdID == (uint)VSConstants.VSStd97CmdID.ShellNavBackward && TryNavigateBack())
                return VSConstants.S_OK;
            if (IsGoToDefinitionCommand(cmdGroup, nCmdID))
            {
                if (BeginGoToDefinition()) return VSConstants.S_OK;
            }
            if (cmdGroup == VSConstants.VSStd2K)
            {
                if (ShouldProcessAsteriskExpansionKey(nCmdID) && AsteriskExpansionService.TryExpand(textView))
                {
                    completionController.Dismiss();
                    return VSConstants.S_OK;
                }

                if (completionController.HandleNavigation(nCmdID))
                    return VSConstants.S_OK;

                // Tab is the only completion commit key; Enter must always insert
                // a newline so multi-statement editing stays predictable.
                if (nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                    && completionController.TryCommit())
                    return VSConstants.S_OK;

                if (nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                    && completionController.TryAdvanceSnippet((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                    return VSConstants.S_OK;

                // Ctrl+Space is also a configurable snippet expansion key. Give an
                // exact snippet match first refusal, then fall back to completion.
                var snippetSettings = SettingsManager.GetSnippetSettings();
                bool explicitCompletionCommand = nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                    || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST;
                if (ShouldPreferSnippetOverCompletion(snippetSettings.useSnippets, snippetSettings.replaceKey, explicitCompletionCommand,
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    && TryReplaceSnippet())
                {
                    // Ctrl+Space remains an explicit completion request after the
                    // snippet expands, even when automatic popups are disabled.
                    completionController.Request(true);
                    return VSConstants.S_OK;
                }

                if (nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST)
                {
                    completionController.Request(true);
                    return VSConstants.S_OK;
                }

                if (nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR || nCmdID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE)
                {
                    if (nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR && TryGetTypedCharacter(pvaIn, out char typedCharacter))
                        completionController.TryCommitOnCharacter(typedCharacter);
                    int result = nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
                    completionController.Request(false);
                    return result;
                }

                if (nCmdID == (uint)VSConstants.VSStd2KCmdID.DELETE
                    || nCmdID == (uint)VSConstants.VSStd2KCmdID.LEFT
                    || nCmdID == (uint)VSConstants.VSStd2KCmdID.RIGHT)
                {
                    completionController.Dismiss();
                }
            }

            if (cmdGroup == VSConstants.VSStd2K && IsSupportedKey(nCmdID))
            {
                if (ShouldProcessSnippetKey(nCmdID) && TryReplaceSnippet())
                {
                    // Snippet was replaced — swallow the key so no newline/tab is inserted.
                    completionController.Request(false);
                    return VSConstants.S_OK;
                }

            }

            // Pass along the command so that other command handlers can process it.
            return nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
        }

        private bool IsSupportedKey(uint nCmdID)
        {
            return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST;
        }

        private bool ShouldProcessSnippetKey(uint nCmdID)
        {
            var snippetSettings = SettingsManager.GetSnippetSettings();

            if (!snippetSettings.useSnippets)
            {
                return false;
            }

            return KeyMatches(snippetSettings.replaceKey, nCmdID);
        }

        private bool ShouldProcessAsteriskExpansionKey(uint nCmdID)
        {
            var settings = SettingsManager.GetAsteriskExpansionSettings();
            return settings.useAsteriskExpansion && KeyMatches(settings.triggerKey, nCmdID);
        }

        private bool KeyMatches(SettingsManager.SnippetReplaceKey key, uint nCmdID)
        {
            switch (key)
            {
                case SettingsManager.SnippetReplaceKey.Enter:
                    return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN &&
                           (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift;

                case SettingsManager.SnippetReplaceKey.ShiftEnter:
                    return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN &&
                           (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                case SettingsManager.SnippetReplaceKey.Tab:
                    return nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB;

                case SettingsManager.SnippetReplaceKey.CtrlSpace:
                    return (nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD ||
                            nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST) &&
                           (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                default:
                    return false;
            }
        }

        private bool TryReplaceSnippet()
        {
            if (textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                return false;

            textView.GetCaretPos(out int iLine, out int iColumn);

            textLines.GetLengthOfLine(iLine, out int lineLength);
            textLines.GetLineText(iLine, 0, iLine, lineLength, out string lineText);

            if (string.IsNullOrEmpty(lineText) || iColumn == 0)
                return false;

            int wordStart = iColumn;
            for (int i = iColumn - 1; i >= 0; i--)
            {
                char c = lineText[i];
                if (c == ' ' || c == '\t' || c == '(' || c == ')' || c == ',' || c == ';')
                    break;

                wordStart = i;
            }

            if (wordStart >= iColumn)
                return false;

            string word = lineText.Substring(wordStart, iColumn - wordStart).Trim();
            if (string.IsNullOrEmpty(word))
                return false;

            var dict = SnippetService.SnippetDictionary;
            if (!dict.TryGetValue(word, out SnippetItem snippet))
                return false;

            var settings = SettingsManager.GetSnippetSettings();
            var result = SnippetVariableProcessor.ProcessVariables(snippet.Body, settings.cursorMarker);
            string newText = result.ProcessedText;
            int cursorOffset = result.CursorOffset;

            var indent = wordStart;
            if (indent > 0)
            {
                newText = newText.Replace(Environment.NewLine, Environment.NewLine + new string(' ', indent));
            }

            IntPtr pNewText = Marshal.StringToHGlobalUni(newText);
            try
            {
                using (var edit = EditorEditTransaction.Begin(textLines, "SQL snippet"))
                {
                    TextSpan[] pChangedSpan = new TextSpan[1];
                    textLines.ReplaceLines(iLine, wordStart, iLine, iColumn, pNewText, newText.Length, pChangedSpan);
                    edit.Complete();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pNewText);
            }

            SetCaretPosition(iLine, wordStart, newText, cursorOffset >= 0 ? cursorOffset : newText.Length);
            return true;
        }

        private void SetCaretPosition(int startLine, int startColumn, string text, int offset)
        {
            int targetLine = startLine;
            int targetColumn = startColumn;

            for (int i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    targetLine++;
                    targetColumn = 0;
                    i++;
                }
                else if (text[i] == '\n')
                {
                    targetLine++;
                    targetColumn = 0;
                }
                else
                {
                    targetColumn++;
                }
            }

            textView.SetCaretPos(targetLine, targetColumn);
        }

        public int QueryStatus(ref Guid cmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (cmdGroup == VSConstants.GUID_VSStandardCommandSet97)
            {
                for (int i = 0; i < prgCmds.Length; i++)
                {
                    if (IsGoToDefinitionCommand(cmdGroup, prgCmds[i].cmdID))
                    {
                        // SSMS queries command availability before dispatching F12.
                        // Without advertising support here, Exec is never reached in
                        // SQL editor windows even though it handles GotoDefn below.
                        prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                        return VSConstants.S_OK;
                    }
                }
            }

            if (cmdGroup == VSConstants.VSStd2K)
            {
                for (int i = 0; i < prgCmds.Length; i++)
                {
                    if (prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.RETURN ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.TAB ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST)
                    {
                        prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                        return VSConstants.S_OK;
                    }
                }
            }

            return nextCommandTarget?.QueryStatus(ref cmdGroup, cCmds, prgCmds, pCmdText) ?? VSConstants.S_OK;
        }

        internal static bool IsGoToDefinitionCommand(Guid commandGroup, uint commandId)
            => commandGroup == VSConstants.GUID_VSStandardCommandSet97
                && commandId == (uint)VSConstants.VSStd97CmdID.GotoDefn;

        internal static bool ShouldPreferSnippetOverCompletion(bool snippetsEnabled,
            SettingsManager.SnippetReplaceKey replaceKey, bool explicitCompletionCommand, bool controlPressed)
        {
            return snippetsEnabled && replaceKey == SettingsManager.SnippetReplaceKey.CtrlSpace && controlPressed
                && explicitCompletionCommand;
        }

        private bool BeginGoToDefinition()
        {
            try
            {
                if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
                textView.GetCaretPos(out int line, out int column);
                if (lines.GetLastLineIndex(out int lastLine, out int lastColumn) != VSConstants.S_OK
                    || lines.GetLineText(0, 0, lastLine, lastColumn, out string text) != VSConstants.S_OK) return false;
                int offset = ToOffset(text, line, column);
                ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                SqlMetadataCache.TryGetCached(connection, out MetadataSnapshot metadata);

                // Phase 1 (UI thread): only read the identifier under the caret.
                // Whether F12 is swallowed must be decided synchronously for the
                // low-level keyboard hook, but that only needs the symbol text.
                string symbol = SqlSymbolResolver.ReadMultipartIdentifier(text, offset);
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    MSSQLToolPackage._logger?.Info("F12 reached SQL navigation, but no identifier was resolved at the caret.");
                    return false;
                }

                // Phase 2 (background): semantic resolution may re-parse the
                // document; scripting opens a new editor when it finishes.
                var origin = new NavigationLocation { View = textView, Line = line, Column = column };
                navigationCancellation?.Cancel();
                navigationCancellation?.Dispose();
                navigationCancellation = new CancellationTokenSource();
                CancellationToken navigationToken = navigationCancellation.Token;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        SqlSymbolResolution resolution = await Task.Run(
                            () => SqlSymbolResolver.ResolveFromSymbol(text, offset, symbol, metadata), navigationToken);
                        navigationToken.ThrowIfCancellationRequested();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(navigationToken);
                        if (resolution.LocalDefinitionOffset >= 0)
                        {
                            NavigationHistory.Push(origin);
                            ToLineColumn(text, resolution.LocalDefinitionOffset, out int targetLine, out int targetColumn);
                            textView.SetCaretPos(targetLine, targetColumn);
                            string[] symbolParts = (resolution.Symbol ?? string.Empty).Split('.');
                            string localName = symbolParts[symbolParts.Length - 1].Trim('[', ']');
                            textView.SetSelection(targetLine, targetColumn, targetLine, targetColumn + localName.Length);
                            return;
                        }
                        string objectName = resolution.ObjectName;
                        IDisposable statusFeedback = StatusFeedback.Begin(
                            LocalizationManager.Format("Opening definition script: {0}", objectName));
                        try
                        {
                            string script = await Task.Run(() => ScriptObjectDefinition.GetText(package, objectName, connection), navigationToken);
                            navigationToken.ThrowIfCancellationRequested();
                            if (string.IsNullOrWhiteSpace(script)) return;
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(navigationToken);
                            navigationToken.ThrowIfCancellationRequested();
                            NavigationHistory.Push(origin);
                            Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache.ScriptFactory.CreateNewBlankScript(Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptType.Sql, connection.ActiveConnectionInfo, null);
                            var document = (EnvDTE.TextDocument)Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                            document.EndPoint.CreateEditPoint().Insert(script);
                        }
                        finally { statusFeedback.Dispose(); }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { FeatureDiagnostics.Report("SQL Navigation", "Asynchronous definition navigation failed", ex); }
                });
                return true;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Navigation", "Go to definition could not resolve the identifier", ex);
                return false;
            }
        }

        private static bool TryNavigateBack()
        {
            while (NavigationHistory.TryPop(out NavigationLocation location))
            {
                try
                {
                    if (location.View == null) continue;
                    IntPtr handle = location.View.GetWindowHandle();
                    if (handle != IntPtr.Zero) SetFocus(handle);
                    location.View.SetCaretPos(location.Line, location.Column);
                    location.View.CenterLines(location.Line, 1);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static int ToOffset(string text, int line, int column)
        {
            int offset = 0;
            for (int current = 0; current < line && offset < text.Length; current++)
            {
                int newline = text.IndexOf('\n', offset);
                if (newline < 0) return text.Length;
                offset = newline + 1;
            }
            return Math.Min(text.Length, offset + column);
        }

        private static void ToLineColumn(string text, int offset, out int line, out int column)
        {
            line = 0; column = 0;
            for (int i = 0; i < Math.Min(offset, text.Length); i++)
            {
                if (text[i] == '\n') { line++; column = 0; }
                else if (text[i] != '\r') column++;
            }
        }

        private static bool IsObjectCharacter(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#' || value == '@' || value == '.' || value == '[' || value == ']';

        private void DismissNativeCompletion()
        {
            if (nextCommandTarget == null) return;
            IntPtr handle = textView?.GetWindowHandle() ?? IntPtr.Zero;
            IntPtr focused = GetFocus();
            if (handle == IntPtr.Zero || focused == IntPtr.Zero || (focused != handle && !IsChild(handle, focused))) return;
            Guid group = VSConstants.VSStd2K;
            nextCommandTarget.Exec(ref group, (uint)VSConstants.VSStd2KCmdID.CANCEL, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr window);

        private const int WhKeyboardLowLevel = 13;
        private const int VkF12 = 0x7B;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private delegate IntPtr KeyboardHookProc(int code, IntPtr virtualKey, IntPtr keyData);

        [StructLayout(LayoutKind.Sequential)]
        private struct LowLevelKeyboardInput
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookType, KeyboardHookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr virtualKey, IntPtr keyData);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        private static bool TryGetTypedCharacter(IntPtr variant, out char value)
        {
            value = '\0';
            if (variant == IntPtr.Zero) return false;
            try
            {
                object raw = Marshal.GetObjectForNativeVariant(variant);
                if (raw is char character) { value = character; return true; }
                string text = Convert.ToString(raw);
                if (!string.IsNullOrEmpty(text)) { value = text[0]; return true; }
            }
            catch { }
            return false;
        }

        private bool TryHandlePhysicalF12()
        {
            if (!IsPhysicalF12Target()) return false;
            return BeginGoToDefinition();
        }

        private bool IsPhysicalF12Target()
        {
            if (!ShortcutManager.IsF12Shortcut(SettingsManager.GetScriptObjectShortcut())) return false;
            if (Control.ModifierKeys != Keys.None) return false;

            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
            return foregroundProcessId == GetCurrentProcessId() && textView != null;
        }

        private static class F12KeyboardHook
        {
            private static readonly System.Collections.Generic.List<KeypressCommandFilter> Targets
                = new System.Collections.Generic.List<KeypressCommandFilter>();
            private static KeyboardHookProc callback;
            private static IntPtr hook;
            private static KeypressCommandFilter handledTarget;

            public static bool Register(KeypressCommandFilter target)
            {
                if (!Targets.Contains(target)) Targets.Add(target);
                if (hook != IntPtr.Zero) return true;

                callback = Callback;
                hook = SetWindowsHookEx(WhKeyboardLowLevel, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                {
                    var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    FeatureDiagnostics.Report("SQL Navigation", "Could not install the low-level F12 keyboard hook", error);
                    MSSQLToolPackage._logger?.Error(error, "Could not install the low-level F12 keyboard hook.");
                    Targets.Remove(target);
                    callback = null;
                    return false;
                }

                MSSQLToolPackage._logger?.Info("Low-level F12 keyboard hook installed for SQL editor views.");
                return true;
            }

            public static void Unregister(KeypressCommandFilter target)
            {
                Targets.Remove(target);
                if (ReferenceEquals(handledTarget, target)) handledTarget = null;
                if (Targets.Count != 0 || hook == IntPtr.Zero) return;

                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
                callback = null;
                MSSQLToolPackage._logger?.Info("Low-level F12 keyboard hook removed.");
            }

            private static IntPtr Callback(int code, IntPtr message, IntPtr keyData)
            {
                if (code >= 0 && keyData != IntPtr.Zero)
                {
                    var input = Marshal.PtrToStructure<LowLevelKeyboardInput>(keyData);
                    if (input.VirtualKey == VkF12)
                    {
                        int messageId = message.ToInt32();
                        bool keyDown = messageId == WmKeyDown || messageId == WmSysKeyDown;
                        bool keyUp = messageId == WmKeyUp || messageId == WmSysKeyUp;
                        // Fired for every physical F12 across the process; keep
                        // at Debug so normal Info file logging stays quiet.
                        MSSQLToolPackage._logger?.Debug("Low-level physical F12 event observed: " + messageId);

                        if (keyUp && handledTarget != null)
                        {
                            handledTarget = null;
                            return new IntPtr(1);
                        }

                        if (keyDown && handledTarget != null)
                            return new IntPtr(1);

                        if (keyDown)
                        {
                            KeypressCommandFilter target = FindFocusedTarget();
                            if (target == null)
                            {
                                MSSQLToolPackage._logger?.Debug("F12 was observed, but the focused VS text view is not a registered SQL editor.");
                            }
                            else
                            {
                                try
                                {
                                    if (target.TryHandlePhysicalF12())
                                    {
                                        handledTarget = target;
                                        MSSQLToolPackage._logger?.Info("Physical F12 captured by the active SQL editor.");
                                        return new IntPtr(1);
                                    }

                                    MSSQLToolPackage._logger?.Debug("F12 reached the active SQL editor but was not handled. Configured shortcut: "
                                        + SettingsManager.GetScriptObjectShortcut());
                                }
                                catch (Exception ex)
                                {
                                    FeatureDiagnostics.Report("SQL Navigation", "Low-level F12 dispatch failed", ex);
                                }
                            }
                        }
                    }
                }

                return CallNextHookEx(hook, code, message, keyData);
            }

            private static KeypressCommandFilter FindFocusedTarget()
            {
                try
                {
                    var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
                    if (textManager == null
                        || textManager.GetActiveView(1, null, out IVsTextView focusedView) != VSConstants.S_OK
                        || focusedView == null)
                        return null;

                    for (int index = Targets.Count - 1; index >= 0; index--)
                        if (Targets[index].IsFor(focusedView)) return Targets[index];

                    // ScriptFactory can activate a newly created editor before the
                    // DTE window event has finished exposing its DocData. Register
                    // that focused view synchronously so the next F12 is never lost.
                    KeypressCommandFilter owner = Targets.Count == 0 ? null : Targets[Targets.Count - 1];
                    return owner?.package?.EnsureCommandFilter(focusedView);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not resolve the focused SQL editor for F12.");
                    return null;
                }
            }
        }

        public void Dispose()
        {
            if (f12KeyboardHookRegistered)
            {
                F12KeyboardHook.Unregister(this);
                f12KeyboardHookRegistered = false;
            }
            navigationCancellation?.Cancel();
            navigationCancellation?.Dispose();
            navigationCancellation = null;
            completionController.Dispose();
            try { textView?.RemoveCommandFilter(this); } catch { }
            textView = null;
        }

        public bool IsFor(IVsTextView view)
        {
            if (ReferenceEquals(textView, view)) return true;
            if (textView == null || view == null) return false;
            try
            {
                IntPtr ownHandle = textView.GetWindowHandle();
                return ownHandle != IntPtr.Zero && ownHandle == view.GetWindowHandle();
            }
            catch { return false; }
        }
        public void DismissCompletion() => completionController.Dismiss();
    }
}
