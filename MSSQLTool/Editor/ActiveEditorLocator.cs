using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace MSSQLTool.Editor
{
    /// <summary>
    /// The single place that asks the shell for the active SQL editor.  Everything above this
    /// file talks to <see cref="IEditorHost"/>, so a future SSMS that changes how the active
    /// view is obtained only requires a change here.
    /// </summary>
    internal static class ActiveEditorLocator
    {
        /// <summary>Creates a host for the active editor, or null when no SQL editor is open.</summary>
        public static IEditorHost CreateHost()
        {
            IVsTextView view = GetActiveView();
            if (view == null) return null;
            try { return EditorHostFactory.Create(view); }
            catch (Exception) { return null; }
        }

        /// <summary>Runs the metadata-aware SELECT * expansion at the active caret.</summary>
        public static bool ExpandAsterisk()
        {
            IVsTextView view = GetActiveView();
            if (view == null) return false;
            return AsteriskExpansionService.TryExpand(view);
        }

        private static IVsTextView GetActiveView()
        {
            try
            {
                var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
                if (textManager != null && textManager.GetActiveView(1, null, out IVsTextView view) == VSConstants.S_OK)
                    return view;
            }
            catch (Exception) { }
            return null;
        }
    }
}
