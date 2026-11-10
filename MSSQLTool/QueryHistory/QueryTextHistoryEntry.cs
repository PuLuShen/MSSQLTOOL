using System;
using System.Globalization;

namespace MSSQLTool
{
    /// <summary>
    /// One stored query text.  Unlike <see cref="QueryHistoryEntry"/> (executed batches with
    /// timings), this is the text of a query tab as the user left it, executed or not.
    /// </summary>
    public sealed class QueryTextHistoryEntry
    {
        public long Id { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public DateTime CapturedAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public string Title { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public int CaptureCount { get; set; } = 1;

        /// <summary>
        /// Identity of the query this text belongs to: the file when the tab was saved, otherwise the
        /// tab title plus its connection.  SQL Prompt shows one row per query and lists the saved
        /// texts as versions; this key is what makes that grouping possible.
        /// </summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>First non-empty line, shortened for the list.</summary>
        public string Preview
        {
            get
            {
                string text = QueryText ?? string.Empty;
                foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    return trimmed.Length <= 110 ? trimmed : trimmed.Substring(0, 107) + "...";
                }

                return string.Empty;
            }
        }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(FilePath))
                {
                    try { return System.IO.Path.GetFileName(FilePath); } catch (Exception) { }
                }

                return "SQL";
            }
        }

        public string ConnectionText
        {
            get
            {
                if (string.IsNullOrEmpty(ServerName) && string.IsNullOrEmpty(DatabaseName)) return string.Empty;
                if (string.IsNullOrEmpty(DatabaseName)) return ServerName;
                if (string.IsNullOrEmpty(ServerName)) return DatabaseName;
                return ServerName + " / " + DatabaseName;
            }
        }
    }

    /// <summary>
    /// One query in the history list: the most recently seen text of a query plus how many versions
    /// it has.  Selecting it shows the versions on the right.
    /// </summary>
    public sealed class QueryTextHistoryGroup
    {
        public string GroupKey { get; set; } = string.Empty;
        public QueryTextHistoryEntry Latest { get; set; } = new QueryTextHistoryEntry();
        public int Versions { get; set; } = 1;
        public DateTime FirstSeenAt { get; set; }

        public DateTime LastSeenAt => Latest?.LastSeenAt ?? default(DateTime);
        public string DisplayTitle => Latest?.DisplayTitle ?? "SQL";
        public string ConnectionText => Latest?.ConnectionText ?? string.Empty;
        public string Preview => Latest?.Preview ?? string.Empty;
        public string VersionText => Versions <= 1 ? "1" : Versions.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// True for a query that only ever lived in a query tab: SSMS keeps those in %TEMP% and gives
        /// every new one the next SQLQueryN name, so the same title repeats across unrelated tabs.
        /// </summary>
        public bool IsUnsaved
        {
            get
            {
                string path = Latest?.FilePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path)) return true;
                return QueryTextHistoryStore.IsTemporaryQueryPath(path);
            }
        }

        /// <summary>File or unsaved tab, shown next to the title so the repeated SQLQueryN names read clearly.</summary>
        public string KindText => LocalizationManager.T(IsUnsaved ? "Unsaved tab" : "File");
    }

    /// <summary>What the history list is filtered by; every field is optional.</summary>
    public sealed class QueryTextHistoryFilter
    {
        public string Text { get; set; } = string.Empty;
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string Server { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;

        /// <summary>Empty for everything, "editor" for captured tabs, "SQL Prompt" for imports.</summary>
        public string Source { get; set; } = string.Empty;

        public bool IsEmpty => string.IsNullOrWhiteSpace(Text)
            && !From.HasValue && !To.HasValue
            && string.IsNullOrWhiteSpace(Server)
            && string.IsNullOrWhiteSpace(Database)
            && string.IsNullOrWhiteSpace(Source);
    }
}
