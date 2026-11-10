using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace MSSQLTool
{
    /// <summary>
    /// Captures the text of query tabs so the text history is useful even when a query was never
    /// executed.  Snapshots are taken when a document is saved, when a tab is closed and every
    /// couple of minutes for the active tab; the store de-duplicates identical text per query.
    /// <para>
    /// SSMS backs every unsaved tab with a throw-away file in %TEMP% (SQLQuery1.sql, then ~vsXXXX.sql
    /// after an auto-recovery) and hands the same names to later tabs.  The path therefore cannot be
    /// the identity of a tab: each open temporary document gets a token of its own here, and the
    /// history uses that token while the tab exists.  A saved file is identified by its path.
    /// </para>
    /// </summary>
    internal static class QueryTextCaptureService
    {
        private static DTE dte;
        private static DispatcherTimer timer;

        /// <summary>Per-document capture state, keyed by temporary path or by saved file path.</summary>
        private static readonly Dictionary<string, CaptureState> captureStates =
            new Dictionary<string, CaptureState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last temporary tab that was captured while it was the active document.</summary>
        private static CaptureState lastActiveTemporaryTab;

        private sealed class CaptureState
        {
            private string groupKey = string.Empty;

            public string TabToken = string.Empty;
            public string LastHash = string.Empty;
            public DateTime LastSeenUtc = DateTime.UtcNow;
            public string LastPath = string.Empty;
            public string LastTitle = string.Empty;

            /// <summary>Creation stamp of the temporary file, used to notice a recycled name.</summary>
            public DateTime TemporaryFileCreatedUtc;

            public string GroupKey
            {
                get { return groupKey; }
                set { groupKey = value ?? string.Empty; }
            }
        }

        public static void Initialize(AsyncPackage package, DTE dteInstance)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dteInstance == null) return;

            dte = dteInstance;
            try
            {
                dte.Events.DocumentEvents.DocumentSaved += OnDocumentSaved;
                dte.Events.WindowEvents.WindowClosing += OnWindowClosing;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("QueryTextHistory", "Document events could not be subscribed", ex);
            }

            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            timer.Tick += (_, __) => Capture(dte.ActiveDocument, "idle");
            timer.Start();

            // Capture what is already open, so the first session after installing is not empty.
            try
            {
                foreach (Document document in dte.Documents)
                {
                    if (document != null && document.Saved) Capture(document, "startup");
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not capture the documents that were already open.");
            }
        }

        public static void Shutdown()
        {
            try
            {
                if (timer != null)
                {
                    timer.Stop();
                    timer = null;
                }

                if (dte != null)
                {
                    dte.Events.DocumentEvents.DocumentSaved -= OnDocumentSaved;
                    dte.Events.WindowEvents.WindowClosing -= OnWindowClosing;
                    dte = null;
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not detach the query text capture events.");
            }

            captureStates.Clear();
            lastActiveTemporaryTab = null;
        }

        private static void OnDocumentSaved(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // Save As turns a temporary tab into a real file.  When the saved text is exactly what
                // the tab held at its last capture, the versions belong to that tab and are moved to
                // the file; checking the text keeps "Save All" from moving another tab's history.
                MoveSavedTabToFile(document);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not move the saved tab history to its file.");
            }

            Capture(document, "save");
        }

        private static void OnWindowClosing(Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Document document = window?.Document;
            try
            {
                Capture(document, "close");
            }
            finally
            {
                ForgetDocument(document);
            }
        }

        /// <summary>Saves one snapshot.  Never throws: capture must not disturb the editor.</summary>
        internal static void Capture(Document document, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (document == null) return;

            try
            {
                if (!IsSqlDocument(document)) return;

                string text = ReadText(document);
                if (text == null || text.Trim().Length < 8) return;

                string path = string.Empty;
                try { path = document.FullName ?? string.Empty; } catch (Exception) { }

                string title = string.Empty;
                try { title = document.Name ?? string.Empty; } catch (Exception) { }

                bool temporary = IsTemporaryDocument(path);
                CaptureState state = StateFor(path, title, temporary);

                // The connection is what separates two unsaved tabs that share a title, and it is what
                // the history filters offer; only the active tab can report it reliably.
                string server = string.Empty;
                string database = string.Empty;
                bool isActive = false;
                try
                {
                    if (dte != null && dte.ActiveDocument == document)
                    {
                        isActive = true;
                        var connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                        if (connection != null)
                        {
                            server = connection.ServerName ?? string.Empty;
                            database = connection.Database ?? string.Empty;
                        }
                    }
                }
                catch (Exception)
                {
                }

                string hash = QueryTextHistoryStore.ComputeHash(text);
                string groupKey = QueryTextHistoryStore.BuildGroupKey(
                    title, path, server, database, temporary ? state.TabToken : null);

                var entry = new QueryTextHistoryEntry
                {
                    QueryText = text,
                    CapturedAt = DateTime.Now,
                    LastSeenAt = DateTime.Now,
                    Title = title,
                    FilePath = path,
                    ServerName = server,
                    DatabaseName = database,
                    GroupKey = groupKey,
                    Source = "editor:" + (reason ?? "snapshot")
                };

                // The store de-duplicates per (query, text); touching a known text only refreshes its
                // last-seen stamp, so an unchanged tab still shows up as used.
                if (!QueryTextHistoryStore.TryInsert(entry, out bool inserted, out string error))
                {
                    MSSQLToolPackage._logger?.Info("Query text snapshot failed: " + error);
                }
                else if (inserted)
                {
                    MSSQLToolPackage._logger?.Debug("Query text snapshot stored (" + reason + ", " + text.Length + " chars).");
                }

                state.LastHash = hash;
                state.LastSeenUtc = DateTime.UtcNow;
                state.LastPath = path;
                state.LastTitle = title;
                state.GroupKey = groupKey;

                if (isActive && temporary) lastActiveTemporaryTab = state;
                else if (isActive) lastActiveTemporaryTab = null;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Query text capture failed.");
            }
        }

        /// <summary>Captures the active tab on demand (used by the history window refresh).</summary>
        public static void CaptureActive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { Capture(dte?.ActiveDocument, "manual"); } catch (Exception) { }
        }

        /// <summary>
        /// Moves the versions of the active temporary tab onto the file it was just saved as.  Only
        /// called when the saved text matches that tab's last captured text.
        /// </summary>
        private static void MoveSavedTabToFile(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string path;
            try { path = document?.FullName ?? string.Empty; } catch (Exception) { return; }
            if (string.IsNullOrWhiteSpace(path) || IsTemporaryDocument(path)) return;

            CaptureState tab = lastActiveTemporaryTab;
            if (tab == null || string.IsNullOrEmpty(tab.TabToken)) return;

            string text = ReadText(document);
            if (text == null) return;
            if (!string.Equals(QueryTextHistoryStore.ComputeHash(text), tab.LastHash, StringComparison.Ordinal))
                return;

            string title = string.Empty;
            try { title = document.Name ?? string.Empty; } catch (Exception) { }

            string target = QueryTextHistoryStore.BuildGroupKey(title, path, string.Empty, string.Empty);
            if (string.Equals(target, tab.GroupKey, StringComparison.Ordinal)) return;

            if (QueryTextHistoryStore.MoveGroup(tab.GroupKey, target, path, out string error))
            {
                tab.GroupKey = target;
                tab.LastPath = path;
                tab.LastTitle = title;
            }
            else if (!string.IsNullOrEmpty(error))
            {
                MSSQLToolPackage._logger?.Info("Could not move the tab history to its file: " + error);
            }

            lastActiveTemporaryTab = null;
        }

        private static CaptureState StateFor(string path, string title, bool temporary)
        {
            string key = temporary
                ? "temp:" + (string.IsNullOrWhiteSpace(path) ? "name:" + (title ?? string.Empty).ToLowerInvariant() : path.ToLowerInvariant())
                : "file:" + (path ?? string.Empty).ToLowerInvariant();

            if (captureStates.TryGetValue(key, out CaptureState state))
            {
                // The temporary file of a closed tab is reused for a new tab.  A newer creation stamp
                // means a different document, so it gets a different identity.
                if (temporary && IsRecycledTemporaryName(state, path))
                    state = null;
            }

            if (state == null)
            {
                state = new CaptureState();
                if (temporary)
                {
                    state.TabToken = Guid.NewGuid().ToString("n");
                    state.TemporaryFileCreatedUtc = CreatedUtc(path);
                }

                captureStates[key] = state;
                PruneStates();
            }
            else if (temporary)
            {
                DateTime created = CreatedUtc(path);
                if (created != default(DateTime)) state.TemporaryFileCreatedUtc = created;
            }

            return state;
        }

        private static bool IsRecycledTemporaryName(CaptureState state, string path)
        {
            if (state.TemporaryFileCreatedUtc == default(DateTime)) return false;

            DateTime created = CreatedUtc(path);
            // A second of slack: the stamp is read while SSMS may still be flushing the file.
            return created != default(DateTime) && created > state.TemporaryFileCreatedUtc.AddSeconds(1);
        }

        private static DateTime CreatedUtc(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return default(DateTime);

            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.CreationTimeUtc : default(DateTime);
            }
            catch (Exception)
            {
                return default(DateTime);
            }
        }

        private static void ForgetDocument(Document document)
        {
            if (document == null) return;

            try
            {
                string path = string.Empty;
                try { path = document.FullName ?? string.Empty; } catch (Exception) { }
                string name = string.Empty;
                try { name = document.Name ?? string.Empty; } catch (Exception) { }

                captureStates.Remove("temp:" + path.ToLowerInvariant());
                captureStates.Remove("temp:name:" + name.ToLowerInvariant());
                captureStates.Remove("file:" + path.ToLowerInvariant());

                if (lastActiveTemporaryTab != null &&
                    string.Equals(lastActiveTemporaryTab.LastPath, path, StringComparison.OrdinalIgnoreCase))
                    lastActiveTemporaryTab = null;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Keeps the per-document map small; closed tabs are normally removed explicitly.</summary>
        private static void PruneStates()
        {
            if (captureStates.Count <= 200) return;

            var stale = new List<string>();
            DateTime cutoff = DateTime.UtcNow.AddHours(-12);
            foreach (KeyValuePair<string, CaptureState> pair in captureStates)
            {
                if (pair.Value.LastSeenUtc < cutoff) stale.Add(pair.Key);
            }

            foreach (string key in stale) captureStates.Remove(key);
        }

        private static string ReadText(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                TextDocument textDocument = document.Object("TextDocument") as TextDocument;
                if (textDocument == null) return null;

                var start = textDocument.StartPoint.CreateEditPoint();
                return start.GetText(textDocument.EndPoint) ?? string.Empty;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsSqlDocument(Document document)
        {
            string path;
            try { path = document.FullName; } catch (Exception) { return false; }
            if (string.IsNullOrEmpty(path)) return true;   // unsaved SSMS query tab
            if (QueryTextHistoryStore.IsTemporaryQueryPath(path)) return true;

            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".sql", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for an unsaved tab, including the temporary documents SSMS creates for one.</summary>
        private static bool IsTemporaryDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            return QueryTextHistoryStore.IsTemporaryQueryPath(path);
        }
    }
}
