using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MSSQLTool.Analysis;

namespace MSSQLTool.Rendering
{
    /// <summary>
    /// The editable rule grid.  It is hosted both by the "Rules…" dialog and by the
    /// Tools &gt; Options page, so the two can never disagree about how a rule is edited.
    /// The control never writes settings itself: callers persist <see cref="Collect"/>.
    /// </summary>
    internal sealed class AnalysisRulesControl : UserControl
    {
        private static readonly string[] SeverityChoices =
        {
            LocalizationManager.T("Default"),
            LocalizationManager.T("Error"),
            LocalizationManager.T("Warning"),
            LocalizationManager.T("Info"),
            LocalizationManager.T("Off")
        };

        private readonly CheckBox enabled;
        private readonly CheckBox squiggles;
        private readonly CheckBox infoSquiggles;
        private readonly CheckBox hover;
        private readonly DataGridView grid;
        private AnalysisSettings working = new AnalysisSettings();

        public AnalysisRulesControl()
        {
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                WrapContents = false,
                AutoSize = false
            };
            enabled = new CheckBox
            {
                Text = LocalizationManager.T("Enable code analysis"),
                AutoSize = true,
                Checked = working.enabled,
                Margin = new Padding(0, 4, 20, 0)
            };
            squiggles = new CheckBox
            {
                Text = LocalizationManager.T("Show squiggles in the editor"),
                AutoSize = true,
                Checked = working.showSquiggles,
                Margin = new Padding(0, 4, 20, 0)
            };
            infoSquiggles = new CheckBox
            {
                Text = LocalizationManager.T("Show squiggles for Info findings"),
                AutoSize = true,
                Checked = working.showInfoSquiggles,
                Margin = new Padding(0, 4, 20, 0)
            };
            hover = new CheckBox
            {
                Text = LocalizationManager.T("Show hover explanations"),
                AutoSize = true,
                Checked = working.showHover,
                Margin = new Padding(0, 4, 0, 0)
            };
            top.Controls.Add(enabled);
            top.Controls.Add(squiggles);
            top.Controls.Add(infoSquiggles);
            top.Controls.Add(hover);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Enabled",
                HeaderText = LocalizationManager.T("Enabled"),
                FillWeight = 12,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Rule",
                HeaderText = LocalizationManager.T("Rule"),
                FillWeight = 56,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Category",
                HeaderText = LocalizationManager.T("Category"),
                FillWeight = 14,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "Severity",
                HeaderText = LocalizationManager.T("Severity"),
                FillWeight = 18,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DataSource = SeverityChoices
            });

            Controls.Add(grid);
            Controls.Add(top);

            grid.CurrentCellDirtyStateChanged += (_, __) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.DataError += (_, e) => { e.ThrowException = false; };

            LoadFrom(SettingsManager.GetAnalysisSettings());
        }

        /// <summary>Replaces the grid contents with the supplied settings.</summary>
        public void LoadFrom(AnalysisSettings settings)
        {
            working = settings ?? new AnalysisSettings();
            enabled.Checked = working.enabled;
            squiggles.Checked = working.showSquiggles;
            infoSquiggles.Checked = working.showInfoSquiggles;
            hover.Checked = working.showHover;

            grid.Rows.Clear();
            foreach (RuleDefinition rule in RuleCatalog.All)
            {
                bool on = !working.IsRuleDisabled(rule.Id)
                    && (rule.EnabledByDefault || working.SeverityOverride(rule.Id) != null);
                string text = (LocalizationManager.IsChinese ? rule.TitleZh : rule.TitleEn) + "  (" + rule.Id + ")";
                int index = grid.Rows.Add(on, text, rule.Category, SeverityChoices[IndexOfSeverity(rule, working)]);
                grid.Rows[index].Tag = rule;
            }
        }

        /// <summary>Restores the shipped defaults in the grid (still unsaved).</summary>
        public void RestoreDefaults() => LoadFrom(new AnalysisSettings());

        /// <summary>Builds the settings the grid describes, without touching the registry.</summary>
        public AnalysisSettings Collect()
        {
            var result = new AnalysisSettings
            {
                enabled = enabled.Checked,
                showSquiggles = squiggles.Checked,
                showInfoSquiggles = infoSquiggles.Checked,
                showHover = hover.Checked,
                ruleSeverity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (DataGridViewRow row in grid.Rows)
            {
                var rule = row.Tag as RuleDefinition;
                if (rule == null) continue;
                bool on = row.Cells["Enabled"].Value is bool value && value;
                int severity = Array.IndexOf(SeverityChoices, Convert.ToString(row.Cells["Severity"].Value));
                if (severity < 0) severity = 0;

                if (!on || severity == 4)
                {
                    result.ruleSeverity[rule.Id] = "off";
                }
                else if (severity == 1) result.ruleSeverity[rule.Id] = "error";
                else if (severity == 2) result.ruleSeverity[rule.Id] = "warning";
                else if (severity == 3) result.ruleSeverity[rule.Id] = "info";
                // severity 0 keeps the catalog default, so no override is stored.
            }

            return result;
        }

        private static int IndexOfSeverity(RuleDefinition rule, AnalysisSettings settings)
        {
            if (settings.IsRuleDisabled(rule.Id)) return 4;
            AnalysisSeverity? overridden = settings.SeverityOverride(rule.Id);
            if (overridden == null) return 0;
            switch (overridden.Value)
            {
                case AnalysisSeverity.Error: return 1;
                case AnalysisSeverity.Warning: return 2;
                default: return 3;
            }
        }
    }
}
