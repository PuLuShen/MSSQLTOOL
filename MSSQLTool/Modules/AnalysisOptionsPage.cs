using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using MSSQLTool.Analysis;
using MSSQLTool.Rendering;

namespace MSSQLTool.Modules
{
    /// <summary>
    /// Tools &gt; Options &gt; MSSQL Tool &gt; Code Analysis.  The page hosts the same grid as the
    /// "Rules…" dialog and persists through <see cref="SettingsManager"/>, so both entry points
    /// read and write the identical registry JSON.
    /// </summary>
    public sealed class AnalysisOptionsPage : DialogPage
    {
        private AnalysisRulesControl control;

        protected override IWin32Window Window
        {
            get { return control ?? (control = new AnalysisRulesControl()); }
        }

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            control?.LoadFrom(SettingsManager.GetAnalysisSettings());
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            if (e.ApplyBehavior == ApplyKind.Apply) Save();
            base.OnApply(e);
        }

        public override void SaveSettingsToStorage()
        {
            base.SaveSettingsToStorage();
            Save();
        }

        private void Save()
        {
            if (control == null) return;
            try
            {
                AnalysisSettings settings = control.Collect();
                if (!SettingsManager.SaveAnalysisSettings(settings))
                    FeatureDiagnostics.Report("Code Analysis", "Saving analysis settings from Tools > Options failed", null);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Code Analysis", "Saving analysis settings from Tools > Options failed", ex);
            }
        }
    }
}
