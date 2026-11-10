using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>
    /// Bridges the TextManager view the completion pipeline works with to the VS editor's own
    /// snapshot and layout events.  Both are optional: when MEF composition does not provide the
    /// adapter service the callers fall back to the TextManager APIs, so completion keeps working
    /// with slightly less smoothness instead of failing.
    /// </summary>
    [Export(typeof(SqlEditorServices))]
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public sealed class SqlEditorServices : IWpfTextViewCreationListener
    {
        private static SqlEditorServices instance;
        private static readonly Dictionary<ITextBuffer, WeakReference<IWpfTextView>> views =
            new Dictionary<ITextBuffer, WeakReference<IWpfTextView>>();

        [Import]
        public IVsEditorAdaptersFactoryService Adapters { get; set; }

        public SqlEditorServices()
        {
            instance = this;
        }

        // Creating the part when a SQL view appears is what publishes the static instance; a
        // plain export would stay lazy because nothing imports SqlEditorServices directly.
        public void TextViewCreated(IWpfTextView textView)
        {
            instance = this;
            RegisterView(textView);
        }

        /// <summary>
        /// Remembers which view belongs to a buffer so buffer-level parts (the classifier) can read
        /// the caret.  Weak references keep closed views collectable.
        /// </summary>
        internal static void RegisterView(IWpfTextView textView)
        {
            if (textView == null) return;
            try
            {
                ITextBuffer buffer = textView.TextBuffer;
                if (buffer == null) return;
                lock (views)
                {
                    views[buffer] = new WeakReference<IWpfTextView>(textView);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The view currently showing this buffer, when one exists.</summary>
        internal static bool TryGetView(ITextBuffer buffer, out ITextView textView)
        {
            textView = null;
            if (buffer == null) return false;
            try
            {
                WeakReference<IWpfTextView> reference;
                lock (views)
                {
                    if (!views.TryGetValue(buffer, out reference)) return false;
                }

                IWpfTextView found;
                if (reference == null || !reference.TryGetTarget(out found) || found == null) return false;
                if (found.IsClosed) return false;
                textView = found;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Every live SQL editor view.  The editor background re-applies from here when a setting
        /// changes, so it does not depend on its own creation listener having run.
        /// </summary>
        internal static List<IWpfTextView> OpenViews()
        {
            var open = new List<IWpfTextView>();
            lock (views)
            {
                foreach (KeyValuePair<ITextBuffer, WeakReference<IWpfTextView>> pair in views)
                {
                    IWpfTextView view;
                    if (pair.Value == null || !pair.Value.TryGetTarget(out view) || view == null || view.IsClosed) continue;
                    if (!open.Contains(view)) open.Add(view);
                }
            }

            return open;
        }

        /// <summary>The current immutable text snapshot, read without a full COM buffer copy.</summary>
        public static bool TryGetSnapshot(IVsTextView view, out ITextSnapshot snapshot)
        {
            snapshot = null;
            if (view == null) return false;
            try
            {
                IWpfTextView textView = instance?.Adapters?.GetWpfTextView(view);
                if (textView == null) return false;
                snapshot = textView.TextSnapshot;
                return snapshot != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The WPF view, so completion can follow scroll and layout changes by event.</summary>
        public static bool TryGetTextView(IVsTextView view, out IWpfTextView textView)
        {
            textView = null;
            if (view == null) return false;
            try
            {
                textView = instance?.Adapters?.GetWpfTextView(view);
                return textView != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
