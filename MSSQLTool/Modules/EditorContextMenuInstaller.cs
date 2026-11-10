using System;
using System.Collections.Generic;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell.Interop;

namespace MSSQLTool
{
    /// <summary>
    /// Puts the refactoring commands into the SQL editor's right-click menu.
    ///
    /// The command table already places them in the editor context menu, but SSMS builds that menu
    /// itself and ignores placements coming from another package, so nothing appeared.  This walks
    /// the shell command bars at startup, finds the "Code Window" popup (the one the SQL editor
    /// shows) and adds the commands through the proffer service.
    /// Everything is guarded: a failure only costs the context entries, never the editor.
    /// </summary>
    internal static class EditorContextMenuInstaller
    {
        private static readonly string[] CommandNames =
        {
            "MSSQLTool.FixAtCaretContextMenu",
            "MSSQLTool.RenameSymbolContextMenu",
            "MSSQLTool.QualifyObjectNamesContextMenu"
        };

        private static readonly string[] Captions =
        {
            "修复光标处的问题",
            "重命名",
            "为对象名添加架构"
        };

        private static bool installed;
        private static int attempts;
        private static int reinforceTicks;
        private static bool loggedComposition;
        private static readonly HashSet<string> seenBars = new HashSet<string>(StringComparer.Ordinal);

        public static void Install(DTE2 dte, IVsProfferCommands3 proffer)
        {
            if (installed || dte == null || proffer == null) return;
            attempts++;

            bool matchedAny = false;
            try
            {
                CommandBars bars = dte.CommandBars as CommandBars;
                if (bars == null) return;

                int popups = 0;
                int matched = 0;
                var names = new List<string>();

                foreach (CommandBar bar in bars)
                {
                    try
                    {
                        bool isPopup = bar.Type == MsoBarType.msoBarTypePopup;
                        if (isPopup)
                        {
                            popups++;
                            if (names.Count < 20) names.Add(bar.Name + " / " + bar.NameLocal);
                        }

                        if (!LooksLikeSqlEditorMenu(bar)) continue;
                        matched++;
                        matchedAny = true;
                        int before = SafeControlCount(bar);
                        for (int i = 0; i < CommandNames.Length; i++)
                            Add(proffer, bar, CommandNames[i], Captions[i]);
                        MSSQLToolPackage._logger?.Info(
                            "CTX-MENU bar='" + bar.NameLocal + "' before=" + before
                            + " after=" + SafeControlCount(bar));
                        if (names.Count < 25)
                            names.Add("menu controls: " + SafeCaptions(bar));
                    }
                    catch (Exception)
                    {
                    }
                }

                MSSQLToolPackage._logger?.Info(
                    "CTX-MENU attempt=" + attempts + " popups=" + popups + " matched=" + matched);
                if (matched == 0 && names.Count > 0)
                    MSSQLToolPackage._logger?.Info("CTX-MENU popup names: " + string.Join(" | ", names));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not add the editor context menu items.");
            }

            if (matchedAny)
            {
                installed = true;
                ScheduleReinforce(dte, proffer);
            }
            else if (attempts < 20) ScheduleRetry(dte, proffer);
        }

        /// <summary>
        /// SSMS may rebuild the popup when the menu is shown again.  Re-adding is idempotent
        /// (an existing caption is skipped), so keep a short reinforcement window.
        /// </summary>
        private static void ScheduleReinforce(DTE2 dte, IVsProfferCommands3 proffer)
        {
            if (reinforceTicks > 0) return;
            try
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(10)
                };
                timer.Tick += (_, __) =>
                {
                    if (++reinforceTicks > 30)
                    {
                        timer.Stop();
                        return;
                    }

                    Reinforce(dte, proffer);
                };
                timer.Start();
            }
            catch (Exception)
            {
            }
        }

        private static void Reinforce(DTE2 dte, IVsProfferCommands3 proffer)
        {
            try
            {
                CommandBars bars = dte.CommandBars as CommandBars;
                if (bars == null) return;
                foreach (CommandBar bar in bars)
                {
                    try
                    {
                        if (!LooksLikeSqlEditorMenu(bar)) continue;
                        if (seenBars.Add(bar.Name + "|" + (int)bar.Type))
                        {
                            MSSQLToolPackage._logger?.Info(
                                "CTX-MENU hit bar='" + bar.NameLocal + "' name='" + bar.Name
                                + "' type=" + bar.Type + " count=" + SafeControlCount(bar));
                        }

                        if (!loggedComposition && SafeControlCount(bar) > 0)
                        {
                            loggedComposition = true;
                            MSSQLToolPackage._logger?.Info(
                                "CTX-MENU composition bar='" + bar.NameLocal + "' count="
                                + SafeControlCount(bar) + " captions: " + SafeCaptions(bar));
                        }

                        for (int i = 0; i < CommandNames.Length; i++)
                            Add(proffer, bar, CommandNames[i], Captions[i]);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Context menus can be created lazily by the shell, so a startup pass may see none.  Retry
        /// a few times; after that the log shows popups=0 and the caller can switch strategy.
        /// </summary>
        private static void ScheduleRetry(DTE2 dte, IVsProfferCommands3 proffer)
        {
            try
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (_, __) =>
                {
                    timer.Stop();
                    Install(dte, proffer);
                };
                timer.Start();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The T-SQL editor's right-click menu is the "SQL Files Editor Context" popup.  The generic
        /// "Code Window" popup is a different menu (Cut/Copy/Git/Quick Actions) and the
        /// "SQLEditor" bar is a toolbar, so both are excluded: injecting into them only produced
        /// entries nobody could see, plus a stray toolbar.
        /// </summary>
        private static bool LooksLikeSqlEditorMenu(CommandBar bar)
        {
            try
            {
                if (bar.Type != MsoBarType.msoBarTypePopup) return false;

                string name = bar.Name ?? string.Empty;
                string local = bar.NameLocal ?? string.Empty;
                if (string.Equals(name, "SQL Files Editor Context", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(local, "SQL 文件编辑器上下文", StringComparison.Ordinal))
                    return true;

                // Fallback for a renamed menu: a popup whose name mentions SQL and that carries
                // T-SQL-only items.
                if (name.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) < 0
                    && local.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                foreach (CommandBarControl control in bar.Controls)
                {
                    string caption = control == null ? null : control.Caption;
                    if (string.IsNullOrEmpty(caption)) continue;
                    caption = caption.Replace("&", string.Empty).Trim();
                    if (caption.IndexOf("查询选项", StringComparison.Ordinal) >= 0
                        || caption.IndexOf("显示估计的执行计划", StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static void Add(IVsProfferCommands3 proffer, CommandBar bar, string canonicalName, string caption)
        {
            try
            {
                if (ContainsCaption(bar, caption)) return;

                object created;
                proffer.AddCommandBarControl(canonicalName, bar, (uint)bar.Controls.Count,
                    (uint)vsCommandBarType.vsCommandBarTypeMenu, out created);

                var control = created as CommandBarControl;
                if (control != null && !string.IsNullOrEmpty(caption))
                {
                    try { control.Caption = caption; }
                    catch (Exception) { }
                }

                MSSQLToolPackage._logger?.Info(
                    "CTX-MENU added='" + caption + "' bar='" + bar.NameLocal + "' ok=" + (created != null));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not add context menu command " + canonicalName);
            }
        }

        private static int SafeControlCount(CommandBar bar)
        {
            try { return bar.Controls == null ? -1 : bar.Controls.Count; }
            catch (Exception) { return -1; }
        }

        private static string SafeCaptions(CommandBar bar)
        {
            var parts = new List<string>();
            try
            {
                foreach (CommandBarControl control in bar.Controls)
                {
                    if (control == null) continue;
                    string caption = control.Caption;
                    if (string.IsNullOrEmpty(caption)) continue;
                    parts.Add(caption.Replace("&", string.Empty).Trim());
                    if (parts.Count >= 30) break;
                }
            }
            catch (Exception)
            {
            }

            return parts.Count == 0 ? "(empty)" : string.Join(" | ", parts);
        }

        private static bool ContainsCaption(CommandBar bar, string caption)
        {
            try
            {
                foreach (CommandBarControl control in bar.Controls)
                {
                    string value = control == null ? null : control.Caption;
                    if (string.IsNullOrEmpty(value)) continue;
                    if (string.Equals(value.Replace("&", string.Empty).Trim(), caption, StringComparison.Ordinal))
                        return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }
    }
}
