using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using MSSQLTool.Analysis;
using MSSQLTool.Editor;
using MSSQLTool.Model;

namespace MSSQLTool.Rendering
{
    /// <summary>
    /// The "Code Analysis" tool window: the document-wide list of problems.  Squiggles answer
    /// "what is wrong here"; this list answers "what is wrong in this query", which is also the
    /// only place Info-level findings are visible by default.
    /// </summary>
    internal sealed class AnalysisResultsControl : UserControl
    {
        private readonly ListView list;
        private readonly Label summary;
        private readonly Button fixButton;
        private readonly Button refreshButton;
        private readonly Button rulesButton;
        private List<AnalysisDiagnostic> current = new List<AnalysisDiagnostic>();
        private string currentText = string.Empty;

        public AnalysisResultsControl()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;

            var header = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 4, 6, 0) };
            refreshButton = new Button { Text = LocalizationManager.T("Re-analyze"), AutoSize = true, Location = new Point(6, 4) };
            fixButton = new Button { Text = LocalizationManager.T("Apply fix"), AutoSize = true, Location = new Point(110, 4) };
            rulesButton = new Button { Text = LocalizationManager.T("Rules"), AutoSize = true, Location = new Point(210, 4) };
            refreshButton.Click += (_, __) => RefreshAnalysis();
            fixButton.Click += (_, __) => ApplySelectedFix();
            rulesButton.Click += (_, __) => EditRules();
            header.Controls.Add(refreshButton);
            header.Controls.Add(fixButton);
            header.Controls.Add(rulesButton);

            summary = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 8, 0), Text = string.Empty };
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            list.Columns.Add(LocalizationManager.T("Severity"), 70);
            list.Columns.Add(LocalizationManager.T("Rule"), 180);
            list.Columns.Add(LocalizationManager.T("Message"), 420);
            list.Columns.Add(LocalizationManager.T("Location"), 90);
            list.DoubleClick += (_, __) => NavigateToSelected();
            list.SelectedIndexChanged += (_, __) => UpdateFixButton();

            Controls.Add(list);
            Controls.Add(summary);
            Controls.Add(header);

            AnalysisResultsBroker.Changed += OnBrokerChanged;
            Load += (_, __) => Render();
            Disposed += (_, __) => AnalysisResultsBroker.Changed -= OnBrokerChanged;
        }

        private void OnBrokerChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(new Action(Render)); } catch (Exception) { }
        }

        private void Render()
        {
            current = AnalysisResultsBroker.Results ?? new List<AnalysisDiagnostic>();
            currentText = AnalysisResultsBroker.DocumentText ?? string.Empty;
            AnalysisSettings settings = AnalysisResultsBroker.LastSettings ?? new AnalysisSettings();

            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (AnalysisDiagnostic diagnostic in current.OrderBy(d => d.Start))
                {
                    TextPositions.ToLineColumn(currentText, diagnostic.Start, out int line, out _);
                    var item = new ListViewItem(SeverityText(diagnostic.Severity)) { Tag = diagnostic };
                    item.SubItems.Add(AnalysisResultsBroker.RuleTitle(diagnostic.RuleId));
                    item.SubItems.Add(diagnostic.Message);
                    item.SubItems.Add(LocalizationManager.Format("Line {0}", line + 1));
                    item.ForeColor = diagnostic.Severity == AnalysisSeverity.Error ? Color.DarkRed
                        : diagnostic.Severity == AnalysisSeverity.Warning ? Color.FromArgb(0x9C, 0x65, 0x00)
                        : SystemColors.ControlText;
                    list.Items.Add(item);
                }
            }
            finally { list.EndUpdate(); }

            int errors = current.Count(d => d.Severity == AnalysisSeverity.Error);
            int warnings = current.Count(d => d.Severity == AnalysisSeverity.Warning);
            summary.Text = !settings.enabled
                ? LocalizationManager.T("Code analysis is disabled. Enable it in Settings > Code Analysis.")
                : current.Count == 0
                    ? LocalizationManager.T("No problems found.")
                    : string.Format("{0}: {1}   {2}: {3}   {4}: {5}",
                        LocalizationManager.T("Error"), errors,
                        LocalizationManager.T("Warning"), warnings,
                        LocalizationManager.T("Info"), current.Count - errors - warnings);
            UpdateFixButton();
        }

        private void EditRules()
        {
            try
            {
                using (var window = new AnalysisRulesWindow())
                {
                    if (window.ShowDialog(this) != DialogResult.OK) return;
                }
                // The window already persisted the new settings; re-run now so the list and the
                // squiggles reflect them without waiting for the next idle debounce.
                RefreshAnalysis();
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Code Analysis", "Editing analysis rules failed", ex);
            }
        }

        private void UpdateFixButton()
        {
            var item = list.SelectedItems.Count > 0 ? list.SelectedItems[0] : null;
            var diagnostic = item?.Tag as AnalysisDiagnostic;
            fixButton.Enabled = QuickFixService.CanFix(diagnostic);
        }

        private void NavigateToSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var diagnostic = list.SelectedItems[0].Tag as AnalysisDiagnostic;
            if (diagnostic == null) return;
            using (IEditorHost host = ActiveEditorLocator.CreateHost())
            {
                if (host == null) return;
                TextPositions.ToLineColumn(currentText, diagnostic.Start, out int line, out int column);
                host.TryNavigateTo(line, column);
            }
        }

        private void ApplySelectedFix()
        {
            if (list.SelectedItems.Count == 0) return;
            var diagnostic = list.SelectedItems[0].Tag as AnalysisDiagnostic;
            if (!QuickFixService.CanFix(diagnostic)) return;

            using (IEditorHost host = ActiveEditorLocator.CreateHost())
            {
                if (host == null) return;
                bool applied = QuickFixActions.TryApply(host, currentText, diagnostic);
                summary.Text = applied
                    ? LocalizationManager.T("Fix applied.")
                    : LocalizationManager.T("No automatic fix is available for this rule.");
                if (!applied) return;
            }

            // The editor changed under the analysis: re-run against the new text immediately so
            // the list and the squiggles do not show a stale result for a few hundred milliseconds.
            try
            {
                string text = currentText;
                using (IEditorHost host = ActiveEditorLocator.CreateHost())
                {
                    if (host != null && host.TryReadAllText(out string fresh)) text = fresh;
                }
                AnalysisSettings settings = SettingsManager.GetAnalysisSettings();
                var results = new AnalysisEngine().Analyze(text, settings);
                AnalysisResultsBroker.Publish("active", text, results, settings);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Code Analysis", "Re-analysis after a fix failed", ex);
            }
        }

        private void RefreshAnalysis()
        {
            try
            {
                string text;
                using (IEditorHost host = ActiveEditorLocator.CreateHost())
                {
                    if (host == null || !host.TryReadAllText(out text)) return;
                }
                var settings = SettingsManager.GetAnalysisSettings();
                var results = new AnalysisEngine().Analyze(text, settings);
                AnalysisResultsBroker.Publish("active", text, results, settings);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Code Analysis", "Manual analysis failed", ex);
            }
        }

        private static string SeverityText(AnalysisSeverity severity)
        {
            switch (severity)
            {
                case AnalysisSeverity.Error: return LocalizationManager.T("Error");
                case AnalysisSeverity.Warning: return LocalizationManager.T("Warning");
                default: return LocalizationManager.T("Info");
            }
        }
    }

    [Guid("3f8f0e51-6b7a-4c2a-9a3e-6d1f4b8c2e77")]
    public sealed class AnalysisResultsWindow : ToolWindowPane
    {
        public AnalysisResultsWindow() : base(null)
        {
            Caption = LocalizationManager.T("Code Analysis");
            Content = new AnalysisResultsControl();
        }
    }

    internal sealed class AnalysisResultsCommand
    {
        public const int CommandId = 4160;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly AsyncPackage package;

        private AnalysisResultsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            var id = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, id));
        }

        public static AnalysisResultsCommand Instance { get; private set; }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new AnalysisResultsCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            package.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ToolWindowPane window = await package.ShowToolWindowAsync(typeof(AnalysisResultsWindow), 0, true, package.DisposalToken);
                if (window?.Frame == null) throw new NotSupportedException("Cannot create tool window");
                var frame = (IVsWindowFrame)window.Frame;
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
            });
        }
    }
}
