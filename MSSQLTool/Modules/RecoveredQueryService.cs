using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool
{
    /// <summary>
    /// SSMS keeps AutoRecover snapshots under %LOCALAPPDATA%\Microsoft\SSMS\BackupFiles.  When a
    /// session dies before SSMS writes AutoRecoverDat, those files are still on disk but the shell
    /// has nothing to reopen.  This helper offers them once per abnormal exit, and can also be
    /// called manually to open the newest snapshots.
    /// </summary>
    internal static class RecoveredQueryService
    {
        private const int MaxFiles = 10;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        public static string MarkerPath => Path.Combine(AppPaths.Root, "session-open.marker");

        public static bool PreviousSessionWasAbnormal()
        {
            try { return File.Exists(MarkerPath); }
            catch (Exception) { return false; }
        }

        public static void MarkSessionStarted()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not write the session marker.");
            }
        }

        public static void MarkSessionEnded()
        {
            try
            {
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not delete the session marker.");
            }
        }

        /// <summary>Asks the user once after an abnormal exit, when SSMS has no native recovery data.</summary>
        public static void OfferRecoveryIfNeeded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!PreviousSessionWasAbnormal()) return;
                if (HasNativeAutoRecoverData()) return;   // SSMS will restore its own tabs

                List<string> files = FindRecentRecoveredFiles();
                if (files.Count == 0) return;

                var result = System.Windows.MessageBox.Show(
                    LocalizationManager.Format("MSSQL Tool found {0} auto-recovered query file(s) from the previous session. Open them for editing?", files.Count),
                    LocalizationManager.T("Recovered queries"),
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (result != System.Windows.MessageBoxResult.Yes) return;

                int opened = OpenFiles(files);
                if (opened == 0)
                {
                    System.Windows.MessageBox.Show(
                        LocalizationManager.T("Could not open the auto-recovered queries. See the log for details."),
                        LocalizationManager.T("Recovered queries"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not offer the auto-recovered queries.");
            }
        }

        /// <summary>Opens the newest recovered snapshots as new query windows.</summary>
        public static int OpenFiles(IEnumerable<string> files)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int opened = 0;
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            foreach (string file in files ?? Enumerable.Empty<string>())
            {
                try
                {
                    string text = File.ReadAllText(file);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (connection != null && connection.ActiveConnectionInfo != null)
                        ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, connection.ActiveConnectionInfo, null);
                    else
                        ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);

                    var document = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                    document.EndPoint.CreateEditPoint().Insert(
                        "-- Recovered from: " + file + Environment.NewLine + text);
                    opened++;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not open the recovered query: " + file);
                }
            }

            return opened;
        }

        public static List<string> FindRecentRecoveredFiles()
        {
            var result = new List<string>();
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "SSMS", "BackupFiles");
                if (!Directory.Exists(root)) return result;

                DateTime cutoff = DateTime.UtcNow - MaxAge;
                var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(IsRecoveredSnapshot)
                    .Select(path => new { Path = path, Time = SafeLastWriteTime(path) })
                    .Where(x => x.Time >= cutoff)
                    .ToList();
                if (candidates.Count == 0) return result;

                // SSMS writes one snapshot per open statement on every auto-recover tick.  Opening
                // every snapshot would open the same query many times, so take only the newest
                // recovery batch, and inside it only the newest snapshot of each document.
                string latestBatch = candidates
                    .GroupBy(x => SnapshotBatchKey(x.Path))
                    .OrderByDescending(group => group.Max(x => x.Time))
                    .First().Key;

                result = candidates
                    .Where(x => string.Equals(SnapshotBatchKey(x.Path), latestBatch, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => DocumentKey(x.Path))
                    .Select(group => group.OrderByDescending(x => x.Time).First())
                    .OrderByDescending(x => x.Time)
                    .Take(MaxFiles)
                    .Select(x => x.Path)
                    .ToList();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not enumerate the auto-recovered query files.");
            }

            return result;
        }

        private static bool IsRecoveredSnapshot(string path)
        {
            string name = Path.GetFileName(path) ?? string.Empty;
            if (name.IndexOf(".sql", StringComparison.OrdinalIgnoreCase) < 0) return false;

            return name.StartsWith("Recovered-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("~AutoRecover", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime SafeLastWriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return DateTime.MinValue; }
        }

        /// <summary>"Recovered-10月-28-2026-0925下午" groups the snapshots of one recovery tick.</summary>
        private static string SnapshotBatchKey(string path)
        {
            string name = Path.GetFileName(path) ?? string.Empty;
            if (!name.StartsWith("Recovered-", StringComparison.OrdinalIgnoreCase)) return "~AutoRecover";

            int dot = name.IndexOf('.', "Recovered-".Length);
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>"Recovered-...SQLQuery1.sql0" and "~AutoRecover.SQLQuery1.sql" are the same tab.</summary>
        private static string DocumentKey(string path)
        {
            string name = Path.GetFileName(path) ?? string.Empty;
            int dot = name.IndexOf('.');
            string document = dot >= 0 && dot + 1 < name.Length ? name.Substring(dot + 1) : name;
            return Regex.Replace(document, @"\.sql\d*$", ".sql", RegexOptions.IgnoreCase).ToLowerInvariant();
        }

        private static bool HasNativeAutoRecoverData()
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "SSMS");
                if (!Directory.Exists(root)) return false;

                foreach (string folder in Directory.EnumerateDirectories(root, "AutoRecoverDat", SearchOption.AllDirectories))
                {
                    if (Directory.EnumerateFiles(folder, "*.dat").Any()) return true;
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not inspect the SSMS AutoRecover data.");
            }

            return false;
        }
    }
}
