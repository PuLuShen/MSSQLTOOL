using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MSSQLTool
{
    /// <summary>
    /// Copies the extension's data into a newly configured folder.
    /// <para>
    /// It only ever copies: the source stays exactly as it is, so pointing the extension somewhere
    /// else and pointing it back is always safe.  Settings are not copied here — the settings file
    /// seeds itself from the registry the first time it is used.
    /// </para>
    /// </summary>
    internal static class DataFolderMigration
    {
        /// <summary>Files kept directly in the data folder.</summary>
        private static readonly string[] DataFiles =
        {
            "github-sync-profiles.json",
            "settings.json",
            "snippets.json"
        };

        /// <summary>
        /// Runs the migration and returns a report for the user, or throws with a reason.
        /// </summary>
        public static string CopyData(string sourceRoot, string targetRoot)
            => CopyData(sourceRoot, targetRoot, !SettingsManager.HasExplicitTemplatesFolder);

        /// <summary>
        /// Runs the migration and returns a report for the user, or throws with a reason.
        /// <paramref name="includeTemplates"/> is false when the user picked a template folder
        /// himself, in which case it must be left alone.
        /// </summary>
        public static string CopyData(string sourceRoot, string targetRoot, bool includeTemplates)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(targetRoot))
                return string.Empty;

            if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var report = new StringBuilder();
            int files = 0;

            foreach (string name in DataFiles)
            {
                string source = Path.Combine(sourceRoot, name);
                if (!File.Exists(source)) continue;

                string destination = Path.Combine(targetRoot, name);
                if (File.Exists(destination)) continue;

                Directory.CreateDirectory(targetRoot);
                File.Copy(source, destination, false);
                files++;
                report.AppendLine("  " + name);
            }

            // Snippets historically lived in the roaming profile, so they are copied from there as
            // well when the data folder itself has none.
            string legacySnippets = AppPaths.LegacySnippetsFile;
            string targetSnippets = Path.Combine(targetRoot, "snippets.json");
            if (!File.Exists(targetSnippets) && File.Exists(legacySnippets))
            {
                Directory.CreateDirectory(targetRoot);
                File.Copy(legacySnippets, targetSnippets, false);
                files++;
                report.AppendLine("  snippets.json (from the roaming profile)");
            }

            files += CopyFolder(Path.Combine(sourceRoot, "QueryHistory"), Path.Combine(targetRoot, "QueryHistory"), report);

            // Query templates live in their own folder. They are only migrated while the user has not
            // picked a folder himself, because an explicit choice must be left alone.
            if (includeTemplates)
            {
                string templatesSource = Directory.Exists(Path.Combine(sourceRoot, "QueryTemplates"))
                    ? Path.Combine(sourceRoot, "QueryTemplates")
                    : AppPaths.LegacyTemplatesFolder;
                files += CopyFolder(templatesSource, Path.Combine(targetRoot, "QueryTemplates"), report);
            }

            if (files == 0)
                return string.Empty;

            return LocalizationManager.Format("Moved {0} file(s) to the new data folder:", files) + Environment.NewLine + report.ToString().TrimEnd();
        }

        private static int CopyFolder(string source, string target, StringBuilder report)
        {
            if (!Directory.Exists(source)) return 0;

            int copied = 0;
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destination = Path.Combine(target, relative);
                if (File.Exists(destination)) continue;

                string folder = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.Copy(file, destination, false);
                copied++;
            }

            if (copied > 0)
                report.AppendLine("  " + Path.GetFileName(source) + Path.DirectorySeparatorChar + " (" + copied + " " + LocalizationManager.T("file(s)") + ")");

            return copied;
        }
    }
}
