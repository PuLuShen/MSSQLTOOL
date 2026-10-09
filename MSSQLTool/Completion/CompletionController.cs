using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Linq;

namespace MSSQLTool.Completion
{
    internal sealed class CompletionController : IDisposable
    {
        private readonly IVsTextView textView;
        private readonly CompletionPresenter presenter = new CompletionPresenter();
        private readonly SynchronizationContext uiContext;
        private readonly System.Windows.Forms.Timer sessionMonitor;
        private CancellationTokenSource requestCancellation;
        private CompletionContext currentContext;
        private int currentLine;
        private int currentColumn;
        private int currentDocLastLine = -1;
        private int currentDocLastColumn = -1;
        private string currentDocumentText;
        private readonly List<TextSpan> snippetStops = new List<TextSpan>();
        private int snippetStopIndex = -1;

        public CompletionController(IVsTextView textView)
        {
            this.textView = textView;
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            presenter.CommitRequested += () => CommitCurrent();
            presenter.DismissRequested += Dismiss;
            sessionMonitor = new System.Windows.Forms.Timer { Interval = 200 };
            sessionMonitor.Tick += (_, __) => { if (presenter.IsVisible && !IsCaretCurrent()) Dismiss(); };
            sessionMonitor.Start();
        }

        public event Action BeforePopupShown;

        public bool IsVisible => presenter.IsVisible;

        public void Request(bool explicitRequest)
        {
            var settings = SettingsManager.GetSqlCompletionSettings();
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            if (!settings.enabled || (!explicitRequest && !settings.automaticPopup))
            {
                requestCancellation = null;
                currentContext = null;
                presenter.Hide();
                return;
            }
            requestCancellation = new CancellationTokenSource();
            CancellationToken token = requestCancellation.Token;

            // One full-document read per keystroke; the text before and after the
            // caret are slices of it. The previous flow issued a second COM
            // GetLineText call just for the before-caret slice.
            string text;
            int line;
            int column;
            if (!TryReadDocument(out text, out line, out column, out int docLastLine, out int docLastColumn)) return;
            currentDocLastLine = docLastLine;
            currentDocLastColumn = docLastColumn;
            int caretOffset = GetOffset(text, line, column);
            string beforeCaret = text.Substring(0, Math.Min(caretOffset, text.Length));
            string afterCaret = text.Substring(beforeCaret.Length);
            currentLine = line;
            currentColumn = column;
            currentDocumentText = text;
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            string metadataScope = connection == null ? string.Empty : (connection.ServerName + "|" + connection.Database);
            var requestMarker = new CompletionContext { IsExplicitRequest = explicitRequest, MetadataScope = metadataScope };
            currentContext = requestMarker;
            _ = LoadAndShowAsync(requestMarker, beforeCaret, line, column, afterCaret, connection, token,
                explicitRequest, settings.delayMilliseconds);
        }

        private async Task LoadAndShowAsync(CompletionContext requestMarker, string beforeCaret, int caretLine, int caretColumn,
            string afterCaret, ScriptFactoryAccess.ConnectionInfo connection, CancellationToken token,
            bool explicitRequest, int delayMilliseconds)
        {
            CompletionContext context = requestMarker;
            try
            {
                if (!explicitRequest)
                    await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);

                context = await Task.Run(() => SqlCompletionAnalyzer.Analyze(beforeCaret, caretColumn, afterCaret), token).ConfigureAwait(false);
                context.IsExplicitRequest = explicitRequest;
                context.MetadataScope = requestMarker.MetadataScope;
                context.CaretLine = caretLine;
                context.CaretColumn = caretColumn;
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Interlocked.CompareExchange(ref currentContext, context, requestMarker), requestMarker)) return;

                if (context.Suppress || (!explicitRequest && context.Kind == CompletionContextKind.General && string.IsNullOrEmpty(context.Prefix)))
                {
                    PostDismiss(context, token);
                    return;
                }

                // Once metadata is cached, avoid showing a metadata-free list first.
                // That preliminary frame was immediately replaced by the full list
                // and made the popup visibly disappear/repaint on every keystroke.
                bool hasCachedMetadata = connection != null
                    && SqlMetadataCache.TryGetCached(connection, out MetadataSnapshot _);
                if (context.Kind != CompletionContextKind.Member && !hasCachedMetadata)
                    PostItems(context, SqlCompletionAnalyzer.BuildItems(context, MetadataSnapshot.Empty), MetadataSnapshot.Empty, token);
                else if (context.Kind == CompletionContextKind.Member && !hasCachedMetadata)
                    _ = ShowLoadingFrameAsync(context, connection, token);

                MetadataSnapshot metadata = await SqlMetadataCache.GetAsync(connection, token).ConfigureAwait(false);
                metadata = await LoadReferencedDatabasesAsync(context, connection, metadata, token).ConfigureAwait(false);
                await SqlMetadataCache.ResolveSynonymColumnsAsync(connection, metadata, context, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                PostItems(context, SqlCompletionAnalyzer.BuildItems(context, metadata), metadata, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Completion", "Completion request failed", ex);
                var failed = new MetadataSnapshot { ErrorMessage = ex.Message };
                PostItems(context, SqlCompletionAnalyzer.BuildItems(context, MetadataSnapshot.Empty), failed, token);
            }
        }

        private void PostDismiss(CompletionContext context, CancellationToken token)
        {
            uiContext.Post(_ =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(currentContext, context) && IsCurrentSnapshot())
                    presenter.Hide();
            }, null);
        }

        // A member-list request (e.g. "dbo.") before the metadata cache is warm
        // used to show nothing at all. After a short grace period — so fast
        // loads never flash an empty popup — park a loading frame in the popup.
        private async Task ShowLoadingFrameAsync(CompletionContext context, ScriptFactoryAccess.ConnectionInfo connection, CancellationToken token)
        {
            try
            {
                await Task.Delay(400, token).ConfigureAwait(false);
                if (connection != null && SqlMetadataCache.TryGetCached(connection, out MetadataSnapshot _)) return;
                uiContext.Post(_ =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(currentContext, context) || !IsCurrentSnapshot()) return;
                    if (!presenter.IsVisible)
                    {
                        BeforePopupShown?.Invoke();
                        presenter.ShowLoading(textView);
                    }
                }, null);
            }
            catch (OperationCanceledException) { }
        }

        // Merged cross-database snapshots, keyed by connection + referenced
        // databases/linked servers. Deep-cloning a whole database snapshot per
        // keystroke dominated CPU time in multi-database scripts; the merge
        // only changes when the referenced set changes. Capped in size and
        // cleared whenever the metadata cache is invalidated.
        private static readonly ConcurrentDictionary<string, MetadataSnapshot> MergedSnapshotCache =
            new ConcurrentDictionary<string, MetadataSnapshot>(StringComparer.Ordinal);

        static CompletionController()
        {
            SqlMetadataCache.Invalidated += MergedSnapshotCache.Clear;
        }

        private static async Task<MetadataSnapshot> LoadReferencedDatabasesAsync(CompletionContext context,
            ScriptFactoryAccess.ConnectionInfo connection, MetadataSnapshot current, CancellationToken token)
        {
            if (connection == null || current == null) return current ?? MetadataSnapshot.Empty;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string> inspect = value =>
            {
                if (!DatabaseIdentifier.TrySplitSqlServer(value, out List<string> parts)) return;
                string first = parts.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first) && current.LinkedServers.Any(s => string.Equals(s, first, StringComparison.OrdinalIgnoreCase)))
                {
                    linked[first] = parts.Count > 1 ? parts[1] : string.Empty;
                    return;
                }
                if (!string.IsNullOrWhiteSpace(first) && current.Databases.Any(d => string.Equals(d, first, StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(first, connection.Database, StringComparison.OrdinalIgnoreCase)) names.Add(first);
            };
            inspect(context.Qualifier);
            inspect(context.TargetObject);
            foreach (string source in context.Aliases.Values) inspect(source);
            if (names.Count == 0 && linked.Count == 0) return current;

            string cacheKey = connection.FullConnectionString
                + "#" + string.Join("|", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                + "#" + string.Join("|", linked.Select(p => p.Key + ">" + p.Value).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            if (MergedSnapshotCache.TryGetValue(cacheKey, out MetadataSnapshot cachedMerged)) return cachedMerged;

            MetadataSnapshot merged = CloneSnapshot(current);
            foreach (string name in names)
            {
                MetadataSnapshot additional = await SqlMetadataCache.GetDatabaseAsync(connection, name, token).ConfigureAwait(false);
                foreach (DatabaseObjectMetadata item in additional.Objects) merged.Objects.Add(CloneObject(item, true));
                foreach (RoutineParameterMetadata item in additional.Parameters) merged.Parameters.Add(item);
                foreach (ForeignKeyMetadata item in additional.ForeignKeys) merged.ForeignKeys.Add(item);
                if (!string.IsNullOrWhiteSpace(additional.ErrorMessage)) merged.ErrorMessage = additional.ErrorMessage;
            }
            foreach (var pair in linked)
            {
                MetadataSnapshot additional = await SqlMetadataCache.GetLinkedServerAsync(connection, pair.Key, pair.Value, token).ConfigureAwait(false);
                foreach (DatabaseObjectMetadata item in additional.Objects) merged.Objects.Add(CloneObject(item, true));
                foreach (RoutineParameterMetadata item in additional.Parameters) merged.Parameters.Add(item);
                foreach (var databases in additional.LinkedServerDatabases) merged.LinkedServerDatabases[databases.Key] = databases.Value;
                if (!string.IsNullOrWhiteSpace(additional.ErrorMessage)) merged.ErrorMessage = additional.ErrorMessage;
            }
            if (MergedSnapshotCache.Count >= 6) MergedSnapshotCache.Clear();
            MergedSnapshotCache[cacheKey] = merged;
            return merged;
        }

        internal static DatabaseObjectMetadata CloneObject(DatabaseObjectMetadata source, bool external)
        {
            var result = new DatabaseObjectMetadata
            {
                Server = source.Server, Database = source.Database, Schema = source.Schema, Name = source.Name,
                Kind = source.Kind, IsSystem = source.IsSystem, IsTableValuedFunction = source.IsTableValuedFunction,
                IsExternal = external || source.IsExternal, SynonymBaseObjectName = source.SynonymBaseObjectName,
                Definition = source.Definition, Description = source.Description, EstimatedRowCount = source.EstimatedRowCount
            };
            foreach (ColumnMetadata column in source.Columns)
                result.Columns.Add(new ColumnMetadata
                {
                    Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                    IsIdentity = column.IsIdentity, IsComputed = column.IsComputed, IsPrimaryKey = column.IsPrimaryKey,
                    IsForeignKey = column.IsForeignKey, IsUnique = column.IsUnique, Ordinal = column.Ordinal,
                    MaxLength = column.MaxLength, Precision = column.Precision, Scale = column.Scale,
                    DefaultDefinition = column.DefaultDefinition, Description = column.Description
                });
            foreach (IndexMetadata index in source.Indexes)
            {
                var copy = new IndexMetadata { Name = index.Name, TypeDescription = index.TypeDescription, IsUnique = index.IsUnique, IsPrimaryKey = index.IsPrimaryKey };
                copy.KeyColumns.AddRange(index.KeyColumns);
                copy.IncludedColumns.AddRange(index.IncludedColumns);
                result.Indexes.Add(copy);
            }
            foreach (CheckConstraintMetadata constraint in source.CheckConstraints)
                result.CheckConstraints.Add(new CheckConstraintMetadata { Name = constraint.Name, Definition = constraint.Definition });
            result.ProjectionSources.AddRange(source.ProjectionSources);
            return result;
        }

        private static MetadataSnapshot CloneSnapshot(MetadataSnapshot source)
        {
            var result = new MetadataSnapshot
            {
                LoadedUtc = source.LoadedUtc, Scope = source.Scope, ErrorMessage = source.ErrorMessage,
                IsPartial = source.IsPartial, CompatibilityLevel = source.CompatibilityLevel
            };
            result.Objects.AddRange(source.Objects);
            result.Parameters.AddRange(source.Parameters);
            result.ForeignKeys.AddRange(source.ForeignKeys);
            result.Schemas.AddRange(source.Schemas);
            result.Databases.AddRange(source.Databases);
            result.LinkedServers.AddRange(source.LinkedServers);
            foreach (var pair in source.LinkedServerDatabases) result.LinkedServerDatabases[pair.Key] = new List<string>(pair.Value);
            return result;
        }

        private void PostItems(CompletionContext context, List<CompletionItem> items, MetadataSnapshot metadata, CancellationToken token)
        {
            uiContext.Post(_ =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(currentContext, context) || !IsCurrentSnapshot()) return;
                if (items.Count == 0 && !CompletionPresenter.HasParameterInfo(metadata, context))
                    presenter.Hide();
                else
                {
                    // Dismiss SSMS's native completion only when opening our popup.
                    // Sending CANCEL for every in-place refresh can disturb the
                    // editor and causes a visible close/reopen cycle.
                    if (!presenter.IsVisible)
                        BeforePopupShown?.Invoke();
                    presenter.Show(textView, items, metadata, context);
                }
            }, null);
        }

        public void RefreshMetadata()
        {
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            SqlMetadataCache.Invalidate(connection);
            Request(true);
        }

        public bool HandleNavigation(uint commandId) => presenter.HandleNavigation(commandId);

        public bool TryCommit()
        {
            if (!presenter.IsVisible || presenter.SelectedItem == null) return false;
            return CommitCurrent();
        }

        public bool TryCommitOnCharacter(char value)
        {
            CompletionItem item = presenter.SelectedItem;
            if (!presenter.IsVisible || item == null) return false;
            bool shouldCommit = ShouldCommitOnCharacter(item, value);
            if (!shouldCommit) return false;
            return CommitCurrent();
        }

        internal static bool ShouldCommitOnCharacter(CompletionItem item, char value)
        {
            if (item == null) return false;
            return value == '.' && (item.Kind == CompletionItemKind.Server || item.Kind == CompletionItemKind.Database || item.Kind == CompletionItemKind.Schema)
                || value == '(' && item.Kind == CompletionItemKind.Function
                || value == ' ' && item.Kind == CompletionItemKind.Procedure
                || value == ',' && item.Kind == CompletionItemKind.Column;
        }

        public bool TryAdvanceSnippet(bool backwards)
        {
            if (snippetStops.Count == 0) return false;
            snippetStopIndex += backwards ? -1 : 1;
            if (snippetStopIndex < 0) snippetStopIndex = 0;
            if (snippetStopIndex >= snippetStops.Count) { snippetStops.Clear(); snippetStopIndex = -1; return false; }
            TextSpan span = snippetStops[snippetStopIndex];
            textView.SetSelection(span.iStartLine, span.iStartIndex, span.iEndLine, span.iEndIndex);
            return true;
        }

        public void Dismiss() { requestCancellation?.Cancel(); presenter.Hide(); }

        private bool CommitCurrent()
        {
            CompletionItem item = presenter.SelectedItem;
            if (item == null || currentContext == null || !IsCurrentSnapshot()) { Dismiss(); return false; }
            List<CompletionItem> selectedItems = presenter.SelectedItems;
            bool insertMultipleColumns = selectedItems.Count > 1 && selectedItems.All(i => i.Kind == CompletionItemKind.Column);
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
            textView.GetCaretPos(out int line, out int column);
            int start = Math.Min(column, currentContext.ReplacementStartColumn);
            string insertText = GetCommitInsertText(item, selectedItems);
            SnippetVariableResult snippetResult = null;
            if (item.Kind == CompletionItemKind.Snippet)
            {
                snippetResult = SnippetVariableProcessor.ProcessVariables(insertText, SettingsManager.GetSnippetSettings().cursorMarker);
                insertText = snippetResult.ProcessedText;
            }
            IntPtr value = Marshal.StringToHGlobalUni(insertText);
            try
            {
                using (var edit = EditorEditTransaction.Begin(lines, "SQL completion"))
                {
                    lines.ReplaceLines(line, start, line, column, value, insertText.Length, new TextSpan[1]);
                    edit.Complete();
                }
                snippetStops.Clear();
                snippetStopIndex = -1;
                if (snippetResult != null && snippetResult.TabStops.Count > 0)
                {
                    foreach (SnippetTabStop stop in snippetResult.TabStops)
                        snippetStops.Add(ToTextSpan(line, start, insertText, stop.Offset, stop.Length));
                    TryAdvanceSnippet(false);
                }
                else
                {
                    int caretOffset = snippetResult != null && snippetResult.CursorOffset >= 0 ? snippetResult.CursorOffset : insertText.Length;
                    TextSpan caret = ToTextSpan(line, start, insertText, caretOffset, 0);
                    textView.SetCaretPos(caret.iStartLine, caret.iStartIndex);
                }
            }
            finally { Marshal.FreeHGlobal(value); }
            foreach (CompletionItem selected in insertMultipleColumns ? selectedItems : new List<CompletionItem> { item })
                CompletionUsageStore.Record(selected);
            // An inserted value is ranked by the usage store; it must not also be promoted as a
            // "remembered highlight" when the popup hides right after.
            presenter.NotifyCommitted();
            bool requestAfterSnippet = ShouldRequestCompletionAfterCommit(item);
            bool explicitSnippetContinuation = currentContext.IsExplicitRequest;
            Dismiss();
            if (requestAfterSnippet) Request(explicitSnippetContinuation);
            return true;
        }

        internal static string GetCommitInsertText(CompletionItem current, IReadOnlyCollection<CompletionItem> selected)
        {
            if (current == null) return string.Empty;
            if (selected != null && selected.Count > 1 && selected.All(i => i != null && i.Kind == CompletionItemKind.Column))
                return string.Join(", ", selected.Select(i => i.InsertText));
            return current.InsertText ?? string.Empty;
        }

        internal static bool ShouldRequestCompletionAfterCommit(CompletionItem item)
            => item != null && item.Kind == CompletionItemKind.Snippet;

        private bool IsCurrentSnapshot()
        {
            if (currentContext == null || textView == null) return false;
            // Cheap staleness checks first: posted frames usually arrive while
            // the user has already kept typing, and a caret or document-shape
            // mismatch is detected without reading the full document text.
            if (textView.GetCaretPos(out int line, out int column) != VSConstants.S_OK) return false;
            if (line != currentLine || column != currentColumn) return false;
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
            if (lines.GetLastLineIndex(out int lastLine, out int lastColumn) != VSConstants.S_OK) return false;
            if (lastLine != currentDocLastLine || lastColumn != currentDocLastColumn) return false;
            if (lines.GetLineText(0, 0, lastLine, lastColumn, out string text) != VSConstants.S_OK) return false;
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            string scope = connection == null ? string.Empty : connection.ServerName + "|" + connection.Database;
            return currentContext != null && IsCompletionSnapshotCurrent(currentDocumentText, text, currentLine, currentColumn,
                currentContext.MetadataScope, line, column, scope);
        }

        internal static bool IsCompletionSnapshotCurrent(string originalText, string currentText, int originalLine,
            int originalColumn, string originalScope, int currentLine, int currentColumn, string currentScope)
            => originalLine == currentLine && originalColumn == currentColumn
                && string.Equals(originalText, currentText, StringComparison.Ordinal)
                && string.Equals(originalScope ?? string.Empty, currentScope ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        private bool IsCaretCurrent()
        {
            if (currentContext == null || textView == null) return false;
            return textView.GetCaretPos(out int line, out int column) == VSConstants.S_OK && line == currentLine && column == currentColumn;
        }

        private static TextSpan ToTextSpan(int startLine, int startColumn, string text, int offset, int length)
        {
            int line = startLine;
            int column = startColumn;
            int endOffset = Math.Min(text.Length, offset + length);
            int endLine = line;
            int endColumn = column;
            for (int i = 0; i < endOffset; i++)
            {
                bool newline = text[i] == '\n';
                if (newline) { if (i < offset) { line++; column = 0; } endLine++; endColumn = 0; }
                else { if (i < offset) column++; endColumn++; }
            }
            return new TextSpan { iStartLine = line, iStartIndex = column, iEndLine = endLine, iEndIndex = endColumn };
        }

        private bool TryReadDocument(out string text, out int line, out int column, out int docLastLine, out int docLastColumn)
        {
            text = null; line = column = 0; docLastLine = docLastColumn = 0;
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
            textView.GetCaretPos(out line, out column);
            if (lines.GetLastLineIndex(out docLastLine, out docLastColumn) != VSConstants.S_OK) return false;
            return lines.GetLineText(0, 0, docLastLine, docLastColumn, out text) == VSConstants.S_OK;
        }

        private static int GetOffset(string text, int line, int column)
        {
            int offset = 0;
            for (int i = 0; i < line && offset < text.Length; i++)
            {
                int next = text.IndexOf('\n', offset);
                if (next < 0) return text.Length;
                offset = next + 1;
            }
            return Math.Min(text.Length, offset + column);
        }

        public void Dispose()
        {
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            sessionMonitor.Stop();
            sessionMonitor.Dispose();
            presenter.Dispose();
        }
    }
}
