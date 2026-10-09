using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MSSQLTool
{
    internal static class QueryHistoryPrivacy
    {
        private static readonly Regex AssignmentSecret = new Regex(
            @"(?<key>\b(?:password|pwd|secret|token|access[_-]?key|api[_-]?key)\b\s*(?:=|:)\s*)(?<value>N?'(?:''|[^'])*'|[^\s,;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConnectionPassword = new Regex(
            @"(?<key>\b(?:Password|Pwd)\s*=\s*)(?<value>[^;\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Protect(string sql)
        {
            if (string.IsNullOrEmpty(sql) || !SettingsManager.GetQueryHistoryRedactSensitiveText()) return sql ?? string.Empty;
            string redacted = ConnectionPassword.Replace(sql, "${key}<redacted>");
            return AssignmentSecret.Replace(redacted, "${key}<redacted>");
        }

        public static void CleanupTextFiles()
        {
            int retentionDays = SettingsManager.GetQueryHistoryRetentionDays();
            if (retentionDays <= 0) return;
            string folder = SettingsManager.GetQueryHistoryTextFileFolder();
            if (!Directory.Exists(folder)) return;
            DateTime cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
            foreach (string path in Directory.GetFiles(folder, "query-history-*.jsonl"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch (Exception ex) { FeatureDiagnostics.Report("QueryHistory", "Could not remove expired history file " + path, ex); }
            }
        }
    }
}
