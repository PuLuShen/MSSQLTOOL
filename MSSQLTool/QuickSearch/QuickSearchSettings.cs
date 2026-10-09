using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace MSSQLTool
{
    /// <summary>
    /// Persistence for the Quick Search tool window. The values live next to the other
    /// MSSQL Tool settings in HKCU\MSSQLTool\Settings, but this class owns its own
    /// keys ("QuickSearchDatabaseSelection" and "QuickSearchRecentTerms") and never touches
    /// SettingsManager, so the two can evolve independently.
    ///
    /// Every registry call is wrapped in try/catch: a read-only or unavailable registry must
    /// never break the tool window.
    /// </summary>
    internal static class QuickSearchSettings
    {
        private const string RegistryPath = @"MSSQLTool\Settings";
        private const string DatabaseSelectionValueName = "QuickSearchDatabaseSelection";
        private const string RecentTermsValueName = "QuickSearchRecentTerms";
        private const string NetworkDatabaseSelectionValueName = "QuickSearchDatabaseSelectionByServer";

        private const int MaxRecentTerms = 20;
        private const string AllDatabasesToken = "All";
        private const string SpecificDatabasesToken = "Specific";

        // Unit separator: cannot be typed into the search box or a database name.
        private const string Separator = "\u001F";

        internal sealed class DatabaseSelection
        {
            public bool AllDatabases { get; set; } = true;
            public string ServerName { get; set; } = string.Empty;
            public List<string> Databases { get; set; } = new List<string>();

            /// <summary>
            /// True when the stored selection belongs to a different server; the caller should
            /// then fall back to "everything selected" instead of applying stale database names.
            /// </summary>
            public bool MatchesServer(string serverName)
            {
                if (string.IsNullOrWhiteSpace(ServerName)) return true;
                return string.Equals(ServerName, serverName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static DatabaseSelection LoadDatabaseSelection()
        {
            var selection = new DatabaseSelection();
            string raw = ReadString(DatabaseSelectionValueName);

            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    string[] parts = raw.Split(new[] { Separator }, StringSplitOptions.None);
                    selection.AllDatabases = !string.Equals(parts[0], SpecificDatabasesToken, StringComparison.Ordinal);
                    if (parts.Length > 1) selection.ServerName = parts[1];
                    for (int i = 2; i < parts.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(parts[i])) selection.Databases.Add(parts[i]);
                    }
                }
                catch (Exception)
                {
                    selection = new DatabaseSelection();
                }
            }

            if (selection.AllDatabases)
            {
                // The friendly "All user databases on the server" mode is the default. A
                // per-server list is still remembered so switching back is not destructive.
                DatabaseSelection perServer = LoadPerServerSelection(selection.ServerName);
                if (perServer != null && perServer.Databases.Count > 0 && selection.Databases.Count == 0)
                {
                    selection.Databases = perServer.Databases;
                }
            }

            return selection;
        }

        public static void SaveDatabaseSelection(bool allDatabases, string serverName, IEnumerable<string> databases)
        {
            var builder = new StringBuilder();
            builder.Append(allDatabases ? AllDatabasesToken : SpecificDatabasesToken);
            builder.Append(Separator);
            builder.Append(Clean(serverName));

            var names = new List<string>();
            if (databases != null)
            {
                foreach (string name in databases) names.Add(Clean(name));
            }

            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                builder.Append(Separator);
                builder.Append(name);
            }

            WriteString(DatabaseSelectionValueName, builder.ToString());
            WriteString(NetworkDatabaseSelectionValueName, string.Join(Separator, names));
        }

        private static DatabaseSelection LoadPerServerSelection(string serverName)
        {
            string raw = ReadString(NetworkDatabaseSelectionValueName);
            if (string.IsNullOrEmpty(raw)) return null;

            try
            {
                var selection = new DatabaseSelection { AllDatabases = false, ServerName = serverName ?? string.Empty };
                foreach (string name in raw.Split(new[] { Separator }, StringSplitOptions.None))
                {
                    if (!string.IsNullOrWhiteSpace(name)) selection.Databases.Add(name);
                }

                return selection;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static List<string> LoadRecentTerms()
        {
            var terms = new List<string>();
            string raw = ReadString(RecentTermsValueName);
            if (string.IsNullOrEmpty(raw)) return terms;

            try
            {
                foreach (string term in raw.Split(new[] { Separator }, StringSplitOptions.None))
                {
                    if (string.IsNullOrWhiteSpace(term)) continue;
                    if (!terms.Contains(term)) terms.Add(term);
                    if (terms.Count >= MaxRecentTerms) break;
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }

            return terms;
        }

        /// <summary>
        /// Adds a term to the front of the recent list, removes duplicates and returns the
        /// updated list so the caller can rebind its drop-down.
        /// </summary>
        public static List<string> AddRecentTerm(string term)
        {
            var terms = LoadRecentTerms();
            string cleaned = Clean(term);

            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                terms.RemoveAll(existing => string.Equals(existing, cleaned, StringComparison.OrdinalIgnoreCase));
                terms.Insert(0, cleaned);
            }

            while (terms.Count > MaxRecentTerms) terms.RemoveAt(terms.Count - 1);

            WriteString(RecentTermsValueName, string.Join(Separator, terms));
            return terms;
        }

        public static void ClearRecentTerms()
        {
            WriteString(RecentTermsValueName, string.Empty);
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value.Replace(Separator, string.Empty).Trim();
        }

        private static string ReadString(string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                {
                    return key?.GetValue(valueName) as string ?? string.Empty;
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void WriteString(string valueName, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    if (key == null) return;

                    if (string.IsNullOrEmpty(value))
                    {
                        key.DeleteValue(valueName, false);
                    }
                    else
                    {
                        key.SetValue(valueName, value, RegistryValueKind.String);
                    }
                }
            }
            catch (Exception)
            {
                // Persistence is best effort only.
            }
        }
    }
}
