using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;

namespace MSSQLTool
{
    /// <summary>
    /// The folder the extension stores its data and configuration in, and why.
    /// </summary>
    public sealed class AppPathResolution
    {
        internal AppPathResolution(string root, string configuredRoot, string source, string unavailableReason)
        {
            Root = root;
            ConfiguredRoot = configuredRoot;
            Source = source;
            UnavailableReason = unavailableReason;
        }

        /// <summary>The folder that is actually used. Never empty.</summary>
        public string Root { get; }

        /// <summary>What the user asked for, before validation. Empty when nothing was configured.</summary>
        public string ConfiguredRoot { get; }

        /// <summary><see cref="AppPaths.SourceEnvironment"/>, <see cref="AppPaths.SourceSettings"/> or <see cref="AppPaths.SourceDefault"/>.</summary>
        public string Source { get; }

        /// <summary>Why a configured folder was rejected and the default is used instead. Null when nothing was rejected.</summary>
        public string UnavailableReason { get; }

        public bool IsCustom => !string.Equals(Source, AppPaths.SourceDefault, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves every file and folder MSSQL Tool keeps, so an installation can store its data and
    /// configuration outside the user profile — on another drive, in a synced folder, or on a
    /// portable device.
    /// <para>
    /// A folder is chosen by <c>MSSQLTOOL_DATA_ROOT</c> (process environment) or by the
    /// <c>DataRootPath</c> setting, in that order; without either, the historical
    /// <c>%LOCALAPPDATA%\MSSQLTool</c> layout is kept exactly as it was. The pointer itself has to
    /// stay in the registry, because it is what tells the extension where the file-stored settings
    /// live.
    /// </para>
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Registry value (and settings key) that holds the configured data folder.</summary>
        public const string DataRootValueName = "DataRootPath";

        /// <summary>Environment variable that overrides the configured folder for this process.</summary>
        public const string DataRootEnvironmentVariable = "MSSQLTOOL_DATA_ROOT";

        public const string FolderName = "MSSQLTool";
        public const string SourceDefault = "default";
        public const string SourceSettings = "settings";
        public const string SourceEnvironment = "environment";

        private static readonly object Gate = new object();
        private static AppPathResolution cached;

        /// <summary>The historical location, used whenever nothing else is configured.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

        /// <summary>Roaming folder the snippet file used before data folders became configurable.</summary>
        public static string LegacySnippetsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName, "snippets.json");

        public static AppPathResolution Resolution
        {
            get
            {
                lock (Gate)
                {
                    return cached ?? (cached = ResolveCore());
                }
            }
        }

        public static string Root => Resolution.Root;
        public static string ConfiguredRoot => Resolution.ConfiguredRoot;
        public static string RootSource => Resolution.Source;
        public static bool IsCustomRoot => Resolution.IsCustom;
        public static string UnavailableReason => Resolution.UnavailableReason;

        public static string LogsFolder => Path.Combine(Root, "MSSQLToolLog");
        public static string QueryHistoryFolder => Path.Combine(Root, "QueryHistory");
        public static string CompletionUsageFile => Path.Combine(Root, "completion-usage.json");
        public static string GitHubProfilesFile => Path.Combine(Root, "github-sync-profiles.json");
        public static string SettingsFile => Path.Combine(Root, "settings.json");

        /// <summary>
        /// Snippets follow a custom data folder; without one they stay in the roaming profile, which
        /// is where every earlier release kept them.
        /// </summary>
        public static string SnippetsFile => IsCustomRoot
            ? Path.Combine(Root, "snippets.json")
            : LegacySnippetsFile;

        /// <summary>Drops the cached resolution so the next read picks up a changed setting.</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                cached = null;
            }
        }

        /// <summary>
        /// Points the extension at <paramref name="path"/>.  A folder that cannot be created or
        /// written to is rejected, so a typo can never leave the extension without a place to work.
        /// </summary>
        public static bool TrySetDataRoot(string path, out string error)
        {
            error = null;
            string normalized = Normalize(path);
            if (normalized == null)
            {
                error = LocalizationManager.T("Enter a valid absolute folder path, for example D:\\MSSQLToolData.");
                return false;
            }

            if (string.Equals(normalized, TrimSeparators(DefaultRoot), StringComparison.OrdinalIgnoreCase))
            {
                // Picking the default location again is the same as clearing the override.
                ClearDataRoot();
                return true;
            }

            string reason;
            if (!TryPrepareFolder(normalized, out reason))
            {
                error = reason;
                return false;
            }

            if (!TryWriteDataRootSetting(normalized))
            {
                error = LocalizationManager.T("The data folder setting could not be saved.");
                return false;
            }

            Invalidate();
            SettingsStore.Invalidate();
            return true;
        }

        /// <summary>Removes the override, returning to <c>%LOCALAPPDATA%\MSSQLTool</c>.</summary>
        public static void ClearDataRoot()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"MSSQLTool\Settings"))
                {
                    key?.DeleteValue(DataRootValueName, false);
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The data folder setting could not be cleared", ex);
            }

            Invalidate();
            SettingsStore.Invalidate();
        }

        /// <summary>
        /// The locations the extension uses, in the order the settings page shows them.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> DescribeLocations()
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Data folder", Root),
                new KeyValuePair<string, string>("Settings", SettingsFile),
                new KeyValuePair<string, string>("Logs", LogsFolder),
                new KeyValuePair<string, string>("Query history", QueryHistoryFolder),
                new KeyValuePair<string, string>("Completion ranking", CompletionUsageFile),
                new KeyValuePair<string, string>("GitHub sync profiles", GitHubProfilesFile),
                new KeyValuePair<string, string>("Snippets", SnippetsFile)
            };
        }

        /// <summary>
        /// Expands environment variables, removes trailing separators and rejects anything that is
        /// not an absolute path.  Returns null when the value cannot be used.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (value.Length == 0) return null;

            try
            {
                if (!Path.IsPathRooted(value)) return null;
                value = Path.GetFullPath(value);
            }
            catch (Exception)
            {
                return null;
            }

            return TrimSeparators(value);
        }

        /// <summary>Creates the folder when needed and proves it is writable.</summary>
        internal static bool TryPrepareFolder(string path, out string reason)
        {
            reason = null;
            try
            {
                Directory.CreateDirectory(path);
                string probe = Path.Combine(path, ".mssqltool-write-test");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                reason = LocalizationManager.Format("The folder cannot be used: {0}", ex.Message);
                return false;
            }
        }

        internal static AppPathResolution ResolveCore()
        {
            string environmentValue = null;
            try
            {
                environmentValue = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            }
            catch (Exception)
            {
                // A hostile environment block must not stop the extension from starting.
            }

            return Resolve(environmentValue, ReadDataRootSetting(), DefaultRoot, path => TryPrepareFolder(path, out string _));
        }

        /// <summary>
        /// Pure resolution used by the tests: environment first, then the stored setting, then the
        /// default.  A candidate the host cannot use falls back to the default and records why.
        /// </summary>
        internal static AppPathResolution Resolve(string environmentValue, string configuredValue,
            string defaultRoot, Func<string, bool> canUse)
        {
            string fallback = TrimSeparators(Normalize(defaultRoot) ?? defaultRoot ?? string.Empty);

            string candidate = null;
            string source = SourceDefault;
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                candidate = environmentValue;
                source = SourceEnvironment;
            }
            else if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                candidate = configuredValue;
                source = SourceSettings;
            }

            if (candidate == null)
                return new AppPathResolution(fallback, null, SourceDefault, null);

            // A rejected folder is reported, but the effective location is the default one: nothing
            // may treat the extension as "configured" while it is really running on the default.
            string normalized = Normalize(candidate);
            if (normalized == null)
            {
                return new AppPathResolution(fallback, candidate.Trim(), SourceDefault,
                    LocalizationManager.Format("The configured data folder is not a valid absolute path: {0}", candidate.Trim()));
            }

            if (string.Equals(normalized, fallback, StringComparison.OrdinalIgnoreCase))
                return new AppPathResolution(fallback, normalized, SourceDefault, null);

            if (canUse == null || canUse(normalized))
                return new AppPathResolution(normalized, normalized, source, null);

            return new AppPathResolution(fallback, normalized, SourceDefault,
                LocalizationManager.Format("The configured data folder is unavailable, so the default folder is used: {0}", normalized));
        }

        private static string ReadDataRootSetting()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"MSSQLTool\Settings", false))
                {
                    return key?.GetValue(DataRootValueName) as string ?? string.Empty;
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool TryWriteDataRootSetting(string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"MSSQLTool\Settings"))
                {
                    if (key == null) return false;
                    key.SetValue(DataRootValueName, value, RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The data folder setting could not be saved", ex);
                return false;
            }
        }

        private static string TrimSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // "D:\" must not become "D:".
            return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
    }
}
