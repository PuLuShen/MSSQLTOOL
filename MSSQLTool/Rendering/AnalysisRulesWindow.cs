using System;
using System.Drawing;
using System.Windows.Forms;
using MSSQLTool.Analysis;

namespace MSSQLTool.Rendering
{
    /// <summary>
    /// Rule settings dialog opened from the Code Analysis tool window.  It is thin on purpose:
    /// the grid lives in <see cref="AnalysisRulesControl"/>, which the Tools &gt; Options page
    /// reuses, and the registry remains the only persisted form of these values.
    /// </summary>
    internal sealed class AnalysisRulesWindow : Form
    {
        private readonly AnalysisRulesControl control;

        public AnalysisRulesWindow()
        {
            Text = LocalizationManager.T("Code analysis rules");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(780, 540);
            MinimumSize = new Size(580, 380);
            Font = SystemFonts.MessageBoxFont;

            control = new AnalysisRulesControl();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 88, Dock = DockStyle.Right };
            var cancel = new Button { Text = LocalizationManager.T("Cancel"), DialogResult = DialogResult.Cancel, Width = 88, Dock = DockStyle.Right, Margin = new Padding(8, 0, 0, 0) };
            var reset = new Button { Text = LocalizationManager.T("Restore defaults"), Width = 120, Dock = DockStyle.Left };
            reset.Click += (_, __) => control.RestoreDefaults();
            var hint = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                Text = LocalizationManager.T("Changes apply to the next analysis.")
            };

            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(reset);
            bottom.Controls.Add(hint);   // Fill last so the docked buttons keep their space

            Controls.Add(control);
            Controls.Add(bottom);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (DialogResult != DialogResult.OK) return;

            try
            {
                if (SettingsManager.SaveAnalysisSettings(control.Collect())) return;
                e.Cancel = true;
                MessageBox.Show(this,
                    LocalizationManager.T("The settings could not be saved."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                FeatureDiagnostics.Report("Code Analysis", "Saving analysis settings failed", ex);
            }
        }
    }
}
