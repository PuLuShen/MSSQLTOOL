using System;
using System.Reflection;
using Microsoft.VisualStudio.TextManager.Interop;

namespace MSSQLTool.Editor
{
    /// <summary>
    /// Resolves the editor implementation for a view and records what the shell supports.
    /// The probe is reflection-only: no hard dependency on the VS editor assemblies, so a
    /// future SSMS that drops or renames them still loads the package and falls back to
    /// TextManager.
    /// </summary>
    internal static class EditorHostFactory
    {
        private static bool logged;

        public static IEditorHost Create(IVsTextView textView)
        {
            EditorCapabilities capabilities = Probe();
            var host = new TextManager.TextManagerEditorHost(textView, capabilities);
            if (!logged)
            {
                logged = true;
                MSSQLToolPackage._logger?.Info("EDITOR host=" + capabilities.Summary);
            }

            // Second chance for the background picture: a query window restored during startup can
            // exist before the MEF text-view listener is composed, and then no picture is painted at
            // all (the editor just stays white).  Reaching the view here is enough to attach it.
            try
            {
                Microsoft.VisualStudio.Text.Editor.IWpfTextView wpfView;
                if (Vs2022.SqlEditorServices.TryGetTextView(textView, out wpfView))
                    EditorBackground.EnsureAttached(wpfView);
            }
            catch (Exception) { }

            return host;
        }

        internal static EditorCapabilities Probe()
        {
            var capabilities = new EditorCapabilities
            {
                EditorPath = "textmanager",
                ShellVersion = SafeVersion(typeof(Microsoft.VisualStudio.Shell.Package).Assembly)
            };

            try
            {
                Assembly editor = FindLoadedAssembly("Microsoft.VisualStudio.Editor");
                capabilities.EditorAssemblyVersion = SafeVersion(editor);
                capabilities.HasVsEditorAdapter = editor != null
                    && editor.GetType("Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService", false) != null;
            }
            catch (Exception) { }

            try
            {
                Assembly textUi = FindLoadedAssembly("Microsoft.VisualStudio.Text.UI");
                capabilities.HasTextAdornmentLayer = textUi != null
                    && textUi.GetType("Microsoft.VisualStudio.Text.Adornments.ITextAdornmentLayer", false) != null;
                capabilities.HasTrackingSpans = FindLoadedAssembly("Microsoft.VisualStudio.Text.Data") != null;
            }
            catch (Exception) { }

            try
            {
                Assembly wpf = FindLoadedAssembly("Microsoft.VisualStudio.Text.UI.Wpf");
                capabilities.HasWpfTextView = wpf != null
                    && wpf.GetType("Microsoft.VisualStudio.Text.Editor.IWpfTextView", false) != null;
            }
            catch (Exception) { }

            return capabilities;
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return assembly;

            try { return Assembly.Load(new AssemblyName(simpleName)); }
            catch (Exception) { return null; }
        }

        private static string SafeVersion(Assembly assembly)
        {
            try { return assembly == null ? string.Empty : assembly.GetName().Version.ToString(); }
            catch (Exception) { return string.Empty; }
        }
    }
}
