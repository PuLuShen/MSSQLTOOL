using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ICSharpCode.AvalonEdit;
using System.Windows.Input;

namespace MSSQLTool
{
    /// <summary>
    /// Keeps editing keys inside the template preview.  SSMS turns Delete/Backspace/Return into
    /// editor commands before the WPF AvalonEdit control receives them, so the preview otherwise
    /// loses those keys to the SQL editor behind it.  The hook is installed only while a template
    /// editor is open and only acts on the editor that actually has the keyboard focus.
    /// </summary>
    internal static class TemplateEditorKeyGuard
    {
        private const int WhKeyboardLowLevel = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int VkBack = 0x08;
        private const int VkReturn = 0x0D;
        private const int VkDelete = 0x2E;

        private static readonly List<TextEditor> Editors = new List<TextEditor>();
        private static IntPtr hook;
        private static KeyboardHookProc callback;

        private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);

        public static void Attach(TextEditor editor)
        {
            if (editor == null) return;
            if (!Editors.Contains(editor)) Editors.Add(editor);
            EnsureHook();
        }

        public static void Detach(TextEditor editor)
        {
            if (editor != null) Editors.Remove(editor);
            if (Editors.Count == 0) RemoveHook();
        }

        private static void EnsureHook()
        {
            if (hook != IntPtr.Zero) return;

            try
            {
                callback = HookCallback;
                hook = SetWindowsHookEx(WhKeyboardLowLevel, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                {
                    MSSQLToolPackage._logger?.Info("Could not install the template editor key guard (Win32 error "
                        + Marshal.GetLastWin32Error() + ").");
                    callback = null;
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not install the template editor key guard.");
                hook = IntPtr.Zero;
                callback = null;
            }
        }

        private static void RemoveHook()
        {
            if (hook == IntPtr.Zero) return;

            try { UnhookWindowsHookEx(hook); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not remove the template editor key guard."); }
            finally
            {
                hook = IntPtr.Zero;
                callback = null;
            }
        }

        private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && hook != IntPtr.Zero)
                {
                    long message = wParam.ToInt64();
                    if (message == WmKeyDown || message == WmSysKeyDown)
                    {
                        int virtualKey = Marshal.ReadInt32(lParam);
                        if (virtualKey == VkDelete || virtualKey == VkBack || virtualKey == VkReturn)
                        {
                            TextEditor editor = FindFocusedEditor();
                            if (editor != null && TryEdit(editor, virtualKey))
                                return new IntPtr(1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "The template editor key guard failed.");
            }

            return CallNextHookEx(hook, code, wParam, lParam);
        }

        private static TextEditor FindFocusedEditor()
        {
            for (int index = Editors.Count - 1; index >= 0; index--)
            {
                TextEditor editor = Editors[index];
                try
                {
                    if (editor != null && editor.IsVisible && editor.IsKeyboardFocusWithin) return editor;
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private static bool TryEdit(TextEditor editor, int virtualKey)
        {
            try
            {
                var document = editor.Document;
                if (document == null) return false;

                int offset = editor.CaretOffset;
                int selectionStart = editor.SelectionStart;
                int selection = editor.SelectionLength;
                bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                if (virtualKey == VkReturn)
                {
                    // Ctrl+Enter is a shortcut elsewhere; let it through unchanged.
                    if (control) return false;

                    if (selection > 0)
                    {
                        offset = selectionStart;
                        document.Remove(offset, selection);
                    }
                    string newline = Environment.NewLine + CurrentIndent(document, offset);
                    document.Insert(offset, newline);
                    editor.CaretOffset = offset + newline.Length;
                    return true;
                }

                if (selection > 0)
                {
                    document.Remove(selectionStart, selection);
                    editor.CaretOffset = selectionStart;
                    return true;
                }

                if (virtualKey == VkBack)
                {
                    if (offset <= 0) return true;
                    int count = control ? PreviousWordLength(document.Text, offset) : 1;
                    document.Remove(offset - count, count);
                    editor.CaretOffset = offset - count;
                    return true;
                }

                if (virtualKey == VkDelete)
                {
                    if (offset >= document.TextLength) return true;
                    int count = control ? NextWordLength(document.Text, offset) : 1;
                    document.Remove(offset, Math.Min(count, document.TextLength - offset));
                    editor.CaretOffset = offset;
                    return true;
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not edit the template preview.");
            }

            return false;
        }

        private static string CurrentIndent(ICSharpCode.AvalonEdit.Document.TextDocument document, int offset)
        {
            try
            {
                var line = document.GetLineByOffset(Math.Min(offset, document.TextLength));
                int length = Math.Max(0, Math.Min(offset - line.Offset, line.Length));
                string beforeCaret = document.GetText(line.Offset, length);
                int indent = 0;
                while (indent < beforeCaret.Length && (beforeCaret[indent] == ' ' || beforeCaret[indent] == '\t')) indent++;
                return beforeCaret.Substring(0, indent);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static int PreviousWordLength(string text, int offset)
        {
            int index = offset;
            while (index > 0 && char.IsWhiteSpace(text[index - 1])) index--;
            while (index > 0 && !char.IsWhiteSpace(text[index - 1])) index--;
            return Math.Max(1, offset - index);
        }

        private static int NextWordLength(string text, int offset)
        {
            int index = offset;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            return Math.Max(1, index - offset);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
