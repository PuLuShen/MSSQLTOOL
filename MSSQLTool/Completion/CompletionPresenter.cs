using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System.Linq;
using System.Drawing.Drawing2D;

namespace MSSQLTool.Completion
{
    internal sealed class CompletionPresenter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll")] private static extern bool GetCaretPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        private readonly Form window;
        private readonly SplitContainer contentSplit;
        private readonly ListBox list;
        private readonly ComboBox category;
        private readonly TextBox details;
        private readonly RichTextBox parameterInfo;
        private readonly Label footer;
        private readonly Font iconFont;
        private IReadOnlyList<CompletionItem> items;
        private List<CompletionItem> allItems = new List<CompletionItem>();
        private IVsTextView lastTextView;
        private MetadataSnapshot lastMetadata;
        private CompletionContext lastContext;
        private bool updatingCategory;
        private bool updatingSplitter;
        private int textLineHeight;

        /// <summary>
        /// The size the user last had, before any placement-driven shrinking.  Kept separately
        /// because the form itself is resized to fit the space beside the caret on every show.
        /// </summary>
        private Size preferredWindowSize;

        /// <summary>Smallest popup the layout may fall back to, matching the form's design minimum.</summary>
        private static readonly Size PreferredMinimumSize = new Size(640, 220);
        private int iconSize;
        private Color selectionBackColor = SystemColors.Highlight;
        private Color selectionForeColor = SystemColors.HighlightText;

        // Theme colors and GDI brushes are resolved once and reused across
        // popup updates; re-resolving WPF theme brushes on every keystroke
        // added measurable overhead to each frame.
        private bool themeResolved;
        private bool themeEventSubscribed;
        private SolidBrush selectionBackBrush;
        private readonly Dictionary<int, SolidBrush> kindBrushCache = new Dictionary<int, SolidBrush>();

        public CompletionPresenter()
        {
            list = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9F),
                DrawMode = DrawMode.OwnerDrawFixed
            };
            category = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Height = 27,
                Font = new Font("Segoe UI", 9F)
            };
            details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F)
            };
            parameterInfo = new RichTextBox
            {
                Dock = DockStyle.Top,
                Height = 58,
                ReadOnly = true,
                DetectUrls = false,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Horizontal,
                Font = new Font("Consolas", 9F),
                Visible = false,
                TabStop = false
            };
            contentSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Size = new Size(780, 500),
                SplitterWidth = 6,
                Panel1MinSize = 260,
                Panel2MinSize = 220
            };
            list.AccessibleName = LocalizationManager.T("SQL completion suggestions");
            iconFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);
            footer = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Padding = new Padding(6, 4, 6, 0),
                Text = LocalizationManager.T("Up/Down select | Tab insert | Esc close"),
                AutoEllipsis = true
            };
            window = new CompletionForm
            {
                Text = LocalizationManager.T("SQL completion"),
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                MinimumSize = PreferredMinimumSize,
                Size = SettingsManager.GetCompletionWindowSize()
            };
            window.AccessibleName = LocalizationManager.T("SQL completion");
            preferredWindowSize = window.Size;
            UpdateScaledMetrics();
            contentSplit.Panel1.Controls.Add(list);
            contentSplit.Panel2.Controls.Add(details);
            window.Controls.Add(contentSplit);
            window.Controls.Add(parameterInfo);
            window.Controls.Add(category);
            window.Controls.Add(footer);
            list.DoubleClick += (_, __) => CommitRequested?.Invoke();
            list.DrawItem += DrawCompletionItem;
            list.SelectedIndexChanged += (_, __) => UpdateDetails();
            category.SelectedIndexChanged += (_, __) =>
            {
                if (!updatingCategory && lastTextView != null)
                    Show(lastTextView, allItems, lastMetadata, lastContext);
            };
            window.ResizeEnd += (_, __) =>
            {
                // Only a user resize counts as the new preference; placement-driven resizing does
                // not raise ResizeEnd.
                preferredWindowSize = window.Size;
                SettingsManager.SaveCompletionWindowSize(window.Size);
            };
            contentSplit.SplitterMoved += (_, __) =>
            {
                if (!updatingSplitter && !contentSplit.Panel2Collapsed)
                    SettingsManager.SaveCompletionDetailsWidth(contentSplit.Panel2.Width);
            };
            window.DpiChanged += (_, __) => UpdateScaledMetrics();
            window.FormClosing += Window_FormClosing;
        }

        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            // Re-resolve colors on the next Show; brushes are rebuilt lazily.
            themeResolved = false;
        }

        public event Action CommitRequested;
        public event Action DismissRequested;
        public bool IsVisible => window.Visible;
        public CompletionItem SelectedItem => list.SelectedIndex >= 0 && items != null && list.SelectedIndex < items.Count ? items[list.SelectedIndex] : null;
        public List<CompletionItem> SelectedItems => list.SelectedItems.Cast<CompletionItem>().ToList();

        public void Show(IVsTextView textView, IReadOnlyList<CompletionItem> newItems, MetadataSnapshot metadata, CompletionContext context)
        {
            // The popup is refreshed from the editor's key handling; a failure here must never take
            // the editor (or the whole SSMS window) down with it.
            try
            {
                ShowCore(textView, newItems, metadata, context);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Completion", "The completion popup could not be refreshed", ex);
                try { window.Hide(); } catch { }
            }
        }

        private void ShowCore(IVsTextView textView, IReadOnlyList<CompletionItem> newItems, MetadataSnapshot metadata, CompletionContext context)
        {
            if (!themeResolved) ApplySsmsTheme();

            // A popup that was hidden starts a new session, and so does a request raised
            // somewhere else while the previous popup is still visible (a mouse click in
            // another part of the document, a new line, a re-triggered Ctrl+Space).  Only a
            // refresh of the very same completion session keeps the user's highlighted row.
            bool continuingSession = IsSameCompletionSession(lastContext, context, window.Visible);

            lastTextView = textView;
            lastMetadata = metadata;
            lastContext = context;
            allItems = newItems?.ToList() ?? new List<CompletionItem>();

            // A new session starts from the top of the list.  The item the user highlighted
            // but never inserted last time is moved to the front, so it is the first thing Tab
            // inserts and the default selection lands on it.
            if (!continuingSession)
                PromoteRememberedItem(allItems, lastUncommittedSelectionKey);

            UpdateCategories();
            newItems = FilterCategory(allItems);
            var completionSettings = SettingsManager.GetSqlCompletionSettings();
            list.SelectionMode = completionSettings.enableColumnPicker && string.Equals(category.SelectedItem as string, "Columns", StringComparison.Ordinal)
                ? SelectionMode.MultiExtended : SelectionMode.One;
            contentSplit.Panel2Collapsed = !completionSettings.showObjectDetails;
            UpdateParameterInfo(metadata, context);
            string selectedKey = continuingSession ? ItemKey(SelectedItem) : null;
            list.BeginUpdate();
            try
            {
                // Update the existing native list in place. Clearing it first
                // briefly paints an empty popup and looks like a close/reopen.
                int sharedCount = Math.Min(list.Items.Count, newItems.Count);
                for (int i = 0; i < sharedCount; i++)
                {
                    if (!string.Equals(ItemKey(list.Items[i] as CompletionItem), ItemKey(newItems[i]), StringComparison.Ordinal)
                        || !string.Equals((list.Items[i] as CompletionItem)?.Description, newItems[i].Description, StringComparison.Ordinal))
                        list.Items[i] = newItems[i];
                }
                while (list.Items.Count > newItems.Count)
                    list.Items.RemoveAt(list.Items.Count - 1);
                for (int i = list.Items.Count; i < newItems.Count; i++)
                    list.Items.Add(newItems[i]);
                items = newItems;
            }
            finally { list.EndUpdate(); }
            if (list.Items.Count == 0 && !parameterInfo.Visible) { Hide(); return; }
            int restoredIndex = -1;
            if (!string.IsNullOrEmpty(selectedKey))
            {
                for (int i = 0; i < newItems.Count; i++)
                {
                    if (string.Equals(ItemKey(newItems[i]), selectedKey, StringComparison.Ordinal))
                    {
                        restoredIndex = i;
                        break;
                    }
                }
            }
            list.SelectedIndex = ResolveSelectedIndex(list.Items.Count, restoredIndex);
            UpdateDetails();
            int maximumItems = SettingsManager.GetSqlCompletionSettings().maximumItems;
            footer.Text = newItems.Count >= maximumItems
                ? LocalizationManager.Format("Showing first {0} matches - keep typing to narrow results", maximumItems)
                : LocalizationManager.Format("{0} matches | Up/Down select | Tab insert | Esc close", newItems.Count);
            if (newItems.Count > 0 && !string.IsNullOrWhiteSpace(newItems[0].ScopeKey))
                footer.Text += " | " + newItems[0].ScopeKey;
            if (metadata != null && metadata != MetadataSnapshot.Empty)
                footer.Text += " | " + metadata.StatusText;
            SqlEditorDiagnostic diagnostic = context?.Diagnostics?.Find(d => d.Code != "SQL_PARSE");
            if (diagnostic != null) footer.Text += " | Warning: " + diagnostic.Message;

            PositionAndShow(textView);
        }

        /// <summary>
        /// Shows the popup with no suggestions while the metadata cache is
        /// loading, so a member-list request (e.g. "dbo.") during the first
        /// seconds on a connection is visibly pending instead of silent.
        /// </summary>
        public void ShowLoading(IVsTextView textView)
        {
            if (!themeResolved) ApplySsmsTheme();
            lastTextView = textView;
            lastMetadata = MetadataSnapshot.Empty;
            lastContext = null;
            allItems = new List<CompletionItem>();
            items = new List<CompletionItem>();
            list.BeginUpdate();
            try { list.Items.Clear(); }
            finally { list.EndUpdate(); }
            list.SelectedIndex = -1;
            parameterInfo.Visible = false;
            UpdateDetails();
            footer.Text = LocalizationManager.T("Metadata loading — suggestions will appear when ready.");
            PositionAndShow(textView);
        }

        private void PositionAndShow(IVsTextView textView)
        {
            IntPtr handle = textView.GetWindowHandle();

            // The popup must sit on the line *below* the caret, so the caret stays visible while
            // typing. The editor's own APIs are asked first: they report the true caret line, the
            // real line height (font/DPI aware) and screen coordinates. The Win32 caret is only a
            // fallback, because the SSMS 22 SQL editor is a WPF surface whose Win32 caret position
            // can be stale and would place the popup right on top of the caret.
            if (!TryGetCaretPlacement(textView, out Point caretTop, out int lineHeight))
            {
                NativePoint point;
                IntPtr coordinateWindow = GetFocus();
                if (coordinateWindow == IntPtr.Zero) coordinateWindow = handle;
                if (!GetCaretPos(out point))
                {
                    // No caret information at all: fall back to the top of the editor.
                    point = new NativePoint { X = 0, Y = 0 };
                }

                ClientToScreen(coordinateWindow, ref point);
                caretTop = new Point(point.X, point.Y);
                lineHeight = textLineHeight;
            }

            // A host that reports the caret without a line height would otherwise place the popup
            // one pixel under the caret and clip the line, so an unusable height is replaced by the
            // presenter's own metrics.
            if (lineHeight < 8)
                lineHeight = Math.Max(textLineHeight, 16);

            Screen screen = Screen.FromPoint(caretTop);
            Rectangle area = screen.WorkingArea;

            // The popup is sized to the room on the side of the caret it is placed on, so it can
            // never end up covering the line being edited.
            CompletionPopupLayout layout = CompletionPopupPlacement.Resolve(
                area,
                preferredWindowSize,
                PreferredMinimumSize,
                caretTop,
                lineHeight);

            window.MinimumSize = layout.MinimumSize;
            if (window.Width != layout.Size.Width) window.Width = layout.Size.Width;
            if (window.Height != layout.Size.Height) window.Height = layout.Size.Height;
            ApplyDetailsWidth();

            window.Location = layout.Location;
            if (!window.Visible)
                window.Show(new NativeWindowOwner(handle));

            // WinForms DPI auto-scaling can adjust a form's bounds while its handle is created, so
            // the exact position is re-asserted on the handle itself. This also refreshes the
            // Z-order: a non-activating owned form can stay Visible yet fall behind SSMS after an
            // application switch.
            SetWindowPos(window.Handle, IntPtr.Zero, layout.Location.X, layout.Location.Y, 0, 0,
                SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        /// <summary>
        /// Resolves the top-left corner of the caret's line in screen coordinates together with
        /// the editor's line height.  Returns false when the editor cannot report the caret, so
        /// the caller can fall back to the Win32 caret.
        /// </summary>
        private static bool TryGetCaretPlacement(IVsTextView textView, out Point caretTop, out int lineHeight)
        {
            caretTop = Point.Empty;
            lineHeight = 0;
            if (textView == null) return false;

            try
            {
                if (textView.GetLineHeight(out int editorLineHeight) == VSConstants.S_OK && editorLineHeight > 0)
                    lineHeight = editorLineHeight;

                if (textView.GetCaretPos(out int line, out int column) != VSConstants.S_OK)
                    return false;

                // Column 0 anchors the popup to the start of the caret line, which keeps it from
                // jumping horizontally while the caret moves inside that line.
                Microsoft.VisualStudio.OLE.Interop.POINT[] points = new Microsoft.VisualStudio.OLE.Interop.POINT[1];
                if (textView.GetPointOfLineColumn(line, 0, points) != VSConstants.S_OK)
                    return false;

                int x = points[0].x;
                int y = points[0].y;

                // GetPointOfLineColumn is documented as screen coordinates, but hosts differ.
                // Detect the convention by testing the point against the editor's window rect.
                if (GetWindowRect(textView.GetWindowHandle(), out NativeRect editorRect))
                {
                    bool insideWindow = x >= editorRect.Left - 8 && x <= editorRect.Right + 8
                        && y >= editorRect.Top - 8 && y <= editorRect.Bottom + 8;
                    if (!insideWindow)
                    {
                        NativePoint clientPoint = new NativePoint { X = x, Y = y };
                        IntPtr window = textView.GetWindowHandle();
                        if (ClientToScreen(window, ref clientPoint))
                        {
                            x = clientPoint.X;
                            y = clientPoint.Y;
                        }
                    }
                }

                caretTop = new Point(x, y);
                return true;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "The editor could not report the caret position; falling back to the Win32 caret.");
                return false;
            }
        }

        public bool HandleNavigation(uint commandId)
        {
            if (!IsVisible) return false;
            bool control = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            if (control && (commandId == (uint)VSConstants.VSStd2KCmdID.LEFT || commandId == (uint)VSConstants.VSStd2KCmdID.RIGHT))
            {
                CycleCategory(commandId == (uint)VSConstants.VSStd2KCmdID.RIGHT ? 1 : -1);
                return true;
            }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.UP) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.DOWN) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.PAGEUP) { MoveSelection(-VisibleItemCount); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.PAGEDN) { MoveSelection(VisibleItemCount); return true; }
            // HOME/END and BOL/EOL stay with the editor: hijacking them for list
            // navigation breaks line-navigation muscle memory while the popup is
            // open. The caret-moved watcher closes the popup after the jump.
            if (commandId == (uint)VSConstants.VSStd2KCmdID.CANCEL) { Hide(); return true; }
            return false;
        }

        private int VisibleItemCount => Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));

        private void MoveSelection(int offset)
        {
            if (list.Items.Count == 0) return;
            list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, list.SelectedIndex + offset));
        }

        private static string ItemKey(CompletionItem item) => item == null ? null : item.Kind + "|" + item.DisplayText;

        /// <summary>
        /// True when <paramref name="current"/> refreshes the popup that is already showing
        /// <paramref name="previous"/>.  A closed popup, a request from another line, or a
        /// request whose word starts somewhere else all start a new completion session, which
        /// resets the selection to the first row instead of restoring the previous highlight.
        /// </summary>
        internal static bool IsSameCompletionSession(CompletionContext previous, CompletionContext current, bool popupVisible)
        {
            if (!popupVisible || previous == null || current == null) return false;
            // Frames of one request (metadata placeholder, then the real list) share the context.
            if (ReferenceEquals(previous, current)) return true;

            // Another request at the same caret is an explicit re-trigger: it starts a new session.
            if (previous.CaretLine != current.CaretLine) return false;
            if (previous.CaretColumn == current.CaretColumn) return false;

            // The caret must still be inside the word the previous frame was completing.
            return previous.ReplacementStartColumn == current.ReplacementStartColumn
                && current.CaretColumn >= current.ReplacementStartColumn;
        }

        /// <summary>
        /// Moves the item that matches <paramref name="rememberedKey"/> to the front of the list.
        /// Returns the index it ended up at, or -1 when the list does not contain it any more.
        /// </summary>
        internal static int PromoteRememberedItem(IList<CompletionItem> items, string rememberedKey)
        {
            if (items == null || items.Count == 0 || string.IsNullOrEmpty(rememberedKey)) return -1;

            for (int i = 0; i < items.Count; i++)
            {
                if (!string.Equals(ItemKey(items[i]), rememberedKey, StringComparison.Ordinal)) continue;
                if (i == 0) return 0;

                CompletionItem remembered = items[i];
                items.RemoveAt(i);
                items.Insert(0, remembered);
                return 0;
            }

            return -1;
        }

        /// <summary>
        /// The row a refreshed list selects: the restore match while the same session keeps
        /// typing, otherwise the first row.
        /// </summary>
        internal static int ResolveSelectedIndex(int itemCount, int restoredIndex)
            => itemCount <= 0 ? -1 : restoredIndex >= 0 && restoredIndex < itemCount ? restoredIndex : 0;

        private void UpdateCategories()
        {
            string selected = category.SelectedItem as string ?? "All";
            var available = new List<string> { "All" };
            AddCategory(available, "Columns", CompletionItemKind.Column);
            if (allItems.Any(i => i.Kind == CompletionItemKind.Table || i.Kind == CompletionItemKind.View || i.Kind == CompletionItemKind.Synonym)) available.Add("Objects");
            if (allItems.Any(i => i.Kind == CompletionItemKind.Procedure || i.Kind == CompletionItemKind.Function)) available.Add("Routines");
            if (allItems.Any(i => i.Kind == CompletionItemKind.Server || i.Kind == CompletionItemKind.Database || i.Kind == CompletionItemKind.Schema)) available.Add("Containers");
            AddCategory(available, "Joins", CompletionItemKind.Join);
            AddCategory(available, "Snippets", CompletionItemKind.Snippet);
            AddCategory(available, "Keywords", CompletionItemKind.Keyword);
            // Rebuilding the combo box on every frame disturbs keyboard focus and
            // costs layout; only touch it when the category set really changes.
            bool unchanged = category.Items.Count == available.Count;
            if (unchanged)
                for (int i = 0; i < available.Count; i++)
                    if (!string.Equals(category.Items[i] as string, available[i], StringComparison.Ordinal)) { unchanged = false; break; }
            if (unchanged) return;
            updatingCategory = true;
            try
            {
                category.Items.Clear();
                category.Items.AddRange(available.Cast<object>().ToArray());
                category.SelectedItem = available.Contains(selected) ? selected : "All";
            }
            finally { updatingCategory = false; }
        }

        private void AddCategory(List<string> available, string name, CompletionItemKind kind)
        {
            if (allItems.Any(i => i.Kind == kind)) available.Add(name);
        }

        private IReadOnlyList<CompletionItem> FilterCategory(IReadOnlyList<CompletionItem> source)
        {
            string selected = category.SelectedItem as string ?? "All";
            if (selected == "All") return source;
            return source.Where(i => selected == "Columns" ? i.Kind == CompletionItemKind.Column
                : selected == "Objects" ? i.Kind == CompletionItemKind.Table || i.Kind == CompletionItemKind.View || i.Kind == CompletionItemKind.Synonym
                : selected == "Routines" ? i.Kind == CompletionItemKind.Procedure || i.Kind == CompletionItemKind.Function
                : selected == "Containers" ? i.Kind == CompletionItemKind.Server || i.Kind == CompletionItemKind.Database || i.Kind == CompletionItemKind.Schema
                : selected == "Joins" ? i.Kind == CompletionItemKind.Join
                : selected == "Snippets" ? i.Kind == CompletionItemKind.Snippet
                : selected == "Keywords" && i.Kind == CompletionItemKind.Keyword).ToList();
        }

        private void CycleCategory(int direction)
        {
            if (category.Items.Count <= 1) return;
            int index = category.SelectedIndex < 0 ? 0 : category.SelectedIndex;
            category.SelectedIndex = (index + direction + category.Items.Count) % category.Items.Count;
        }

        private void UpdateDetails()
        {
            CompletionItem item = SelectedItem;
            details.Text = item == null ? string.Empty : (!string.IsNullOrWhiteSpace(item.DetailText) ? item.DetailText : item.Description ?? string.Empty);
        }

        private void ApplyDetailsWidth()
        {
            if (contentSplit.Panel2Collapsed || contentSplit.ClientSize.Width <= 0) return;
            updatingSplitter = true;
            try
            {
                contentSplit.SplitterDistance = CalculateSplitterDistance(contentSplit.ClientSize.Width,
                    SettingsManager.GetCompletionDetailsWidth(), contentSplit.Panel1MinSize,
                    contentSplit.Panel2MinSize, contentSplit.SplitterWidth);
            }
            finally { updatingSplitter = false; }
        }

        internal static int CalculateSplitterDistance(int totalWidth, int detailsWidth, int minimumLeft, int minimumRight, int splitterWidth)
        {
            int available = Math.Max(0, totalWidth - splitterWidth);
            int right = Math.Max(minimumRight, Math.Min(detailsWidth, Math.Max(minimumRight, available - minimumLeft)));
            return Math.Max(minimumLeft, Math.Min(available - minimumRight, available - right));
        }

        private void Window_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing)
                return;

            e.Cancel = true;
            window.Hide();
            DismissRequested?.Invoke();
        }

        private void UpdateScaledMetrics()
        {
            textLineHeight = Math.Max(list.Font.Height + 2, TextRenderer.MeasureText("Ag", list.Font).Height);
            iconSize = Math.Max(16, textLineHeight - 1);
            list.ItemHeight = textLineHeight + 8;
            footer.Height = textLineHeight + 10;
            parameterInfo.Height = (textLineHeight * 2) + 14;
            footer.Padding = new Padding(6, Math.Max(2, (footer.Height - list.Font.Height) / 2), 6, 0);
            list.Invalidate();
        }

        private void DrawCompletionItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || items == null || e.Index >= items.Count) return;
            CompletionItem item = items[e.Index];
            e.DrawBackground();

            bool selected = (e.State & DrawItemState.Selected) != 0;
            if (selected)
            {
                e.Graphics.FillRectangle(GetSelectionBackBrush(), e.Bounds);
            }
            Color primary = selected ? selectionForeColor : list.ForeColor;
            Rectangle bounds = e.Bounds;
            int iconLeft = bounds.Left + 7;
            int iconTop = bounds.Top + Math.Max(2, (bounds.Height - iconSize) / 2);
            DrawKindIcon(e.Graphics, new Rectangle(iconLeft, iconTop, iconSize, iconSize), item.Kind);
            int textLeft = iconLeft + iconSize + 6;
            TextRenderer.DrawText(e.Graphics, item.DisplayText ?? string.Empty, list.Font,
                new Rectangle(textLeft, bounds.Top, Math.Max(0, bounds.Right - textLeft - 7), bounds.Height), primary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        private void DrawKindIcon(Graphics graphics, Rectangle bounds, CompletionItemKind kind)
        {
            Color background = KindColor(kind);
            SmoothingMode oldMode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                graphics.FillEllipse(GetKindBrush(background), bounds);
                TextRenderer.DrawText(graphics, KindGlyph(kind), iconFont, bounds, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            finally { graphics.SmoothingMode = oldMode; }
        }

        private static Color KindColor(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return Color.FromArgb(40, 126, 166);
                case CompletionItemKind.View: return Color.FromArgb(37, 145, 115);
                case CompletionItemKind.Procedure: return Color.FromArgb(112, 85, 170);
                case CompletionItemKind.Function: return Color.FromArgb(190, 108, 35);
                case CompletionItemKind.Column: return Color.FromArgb(60, 130, 65);
                case CompletionItemKind.Database: return Color.FromArgb(38, 110, 190);
                case CompletionItemKind.Server: return Color.FromArgb(76, 99, 120);
                case CompletionItemKind.Schema: return Color.FromArgb(75, 125, 145);
                case CompletionItemKind.Parameter: return Color.FromArgb(165, 82, 118);
                case CompletionItemKind.Snippet: return Color.FromArgb(180, 137, 20);
                case CompletionItemKind.Keyword: return Color.FromArgb(85, 85, 85);
                case CompletionItemKind.Join: return Color.FromArgb(0, 130, 145);
                default: return Color.FromArgb(105, 105, 105);
            }
        }

        private static string KindGlyph(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return "T";
                case CompletionItemKind.View: return "V";
                case CompletionItemKind.Procedure: return "P";
                case CompletionItemKind.Function: return "ƒ";
                case CompletionItemKind.Column: return "C";
                case CompletionItemKind.Database: return "D";
                case CompletionItemKind.Server: return "S";
                case CompletionItemKind.Schema: return "□";
                case CompletionItemKind.Parameter: return "@";
                case CompletionItemKind.Synonym: return "↗";
                case CompletionItemKind.Sequence: return "#";
                case CompletionItemKind.Type: return "{}";
                case CompletionItemKind.Snippet: return "‹›";
                case CompletionItemKind.Join: return "⋈";
                default: return "K";
            }
        }

        private void ApplySsmsTheme()
        {
            if (!themeEventSubscribed)
            {
                // Subscribing requires the VS shell, which is absent when the
                // presenter is constructed outside SSMS (regression tests).
                try
                {
                    VSColorTheme.ThemeChanged += OnVsThemeChanged;
                    themeEventSubscribed = true;
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("SQL Completion", "Theme change notifications unavailable", ex);
                    themeEventSubscribed = true;
                }
            }
            System.Windows.Media.Brush background = VsThemeBrushResolver.ResolveBrush(null, EnvironmentColors.ToolWindowBackgroundBrushKey);
            System.Windows.Media.Brush foreground = VsThemeBrushResolver.ResolveBrush(null, EnvironmentColors.ToolWindowTextBrushKey);
            System.Windows.Media.Brush selected = VsThemeBrushResolver.ResolveEnvironmentBrushByName(null, "SystemHighlightBrushKey");
            System.Windows.Media.Brush selectedText = VsThemeBrushResolver.ResolveEnvironmentBrushByName(null, "SystemHighlightTextBrushKey");
            Color back = ToDrawingColor(background, SystemColors.Window);
            Color fore = ToDrawingColor(foreground, SystemColors.WindowText);
            selectionBackColor = ToDrawingColor(selected, SystemColors.Highlight);
            selectionForeColor = ToDrawingColor(selectedText, SystemColors.HighlightText);
            window.BackColor = back;
            window.ForeColor = fore;
            list.BackColor = back;
            list.ForeColor = fore;
            category.BackColor = back;
            category.ForeColor = fore;
            details.BackColor = back;
            details.ForeColor = fore;
            parameterInfo.BackColor = back;
            parameterInfo.ForeColor = fore;
            footer.BackColor = back;
            footer.ForeColor = fore;
            themeResolved = true;
        }

        private SolidBrush GetSelectionBackBrush()
        {
            if (selectionBackBrush == null || selectionBackBrush.Color != selectionBackColor)
            {
                selectionBackBrush?.Dispose();
                selectionBackBrush = new SolidBrush(selectionBackColor);
            }
            return selectionBackBrush;
        }

        private SolidBrush GetKindBrush(Color color)
        {
            int key = color.ToArgb();
            if (!kindBrushCache.TryGetValue(key, out SolidBrush brush)) kindBrushCache[key] = brush = new SolidBrush(color);
            return brush;
        }

        private static Color ToDrawingColor(System.Windows.Media.Brush brush, Color fallback)
        {
            var solid = brush as System.Windows.Media.SolidColorBrush;
            return solid == null ? fallback : Color.FromArgb(solid.Color.A, solid.Color.R, solid.Color.G, solid.Color.B);
        }

        internal static string BuildParameterInfo(MetadataSnapshot metadata, CompletionContext context)
        {
            if (metadata == null || context == null || string.IsNullOrWhiteSpace(context.TargetObject)) return null;
            string target = DatabaseIdentifier.NormalizeSqlServer(context.TargetObject);
            var allParameters = metadata.Parameters
                .Where(p => NameMatches(p.ObjectName, target))
                .OrderBy(p => p.Ordinal)
                .ToList();
            var parameters = allParameters.Select((p, index) =>
                (IsActiveParameter(p, index, context) ? "> " : "  ") + p.Name + " " + p.TypeDisplay
                + (p.HasDefaultValue ? " = default" : string.Empty) + (p.IsOutput ? " OUTPUT" : string.Empty)).ToList();
            if (parameters.Count == 0) return null;
            return target + Environment.NewLine + string.Join("    ", parameters);
        }

        internal static bool HasParameterInfo(MetadataSnapshot metadata, CompletionContext context)
            => !string.IsNullOrWhiteSpace(BuildParameterInfo(metadata, context));

        private void UpdateParameterInfo(MetadataSnapshot metadata, CompletionContext context)
        {
            string value = BuildParameterInfo(metadata, context);
            parameterInfo.Visible = !string.IsNullOrWhiteSpace(value);
            parameterInfo.Text = value ?? string.Empty;
            if (!parameterInfo.Visible) return;
            int marker = parameterInfo.Text.IndexOf("> ", StringComparison.Ordinal);
            if (marker < 0) return;
            int end = parameterInfo.Text.IndexOf("    ", marker, StringComparison.Ordinal);
            if (end < 0) end = parameterInfo.Text.Length;
            parameterInfo.Select(marker, end - marker);
            parameterInfo.SelectionBackColor = selectionBackColor;
            parameterInfo.SelectionColor = selectionForeColor;
            parameterInfo.Select(0, 0);
        }

        private static bool IsActiveParameter(RoutineParameterMetadata parameter, int index, CompletionContext context)
            => !string.IsNullOrWhiteSpace(context.ActiveParameter)
                ? string.Equals(parameter.Name, context.ActiveParameter, StringComparison.OrdinalIgnoreCase)
                : index == context.ArgumentIndex;

        private static bool NameMatches(string left, string right)
        {
            left = DatabaseIdentifier.NormalizeSqlServer(left);
            right = DatabaseIdentifier.NormalizeSqlServer(right);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || left.EndsWith("." + right, StringComparison.OrdinalIgnoreCase)
                || right.EndsWith("." + left, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The item highlighted when the previous popup session ended without an insert.  It is
        /// promoted to the top of the next list so the default selection lands on it.
        /// </summary>
        private string lastUncommittedSelectionKey;

        /// <summary>Set while a commit is closing the popup, so the inserted item is not remembered.</summary>
        private bool selectionWasCommitted;

        public void Hide()
        {
            if (!window.Visible)
            {
                selectionWasCommitted = false;
                return;
            }

            // A committed value is ranked by the usage store; remembering it as an
            // uncommitted highlight as well would promote it twice.
            if (!selectionWasCommitted)
            {
                CompletionItem selected = SelectedItem;
                if (selected != null) lastUncommittedSelectionKey = ItemKey(selected);
            }

            selectionWasCommitted = false;
            window.Hide();
        }

        /// <summary>
        /// Clears the remembered highlight.  Called when an item is actually inserted: a committed
        /// value is already fed into the usage-based ranking and must not be promoted as well.
        /// </summary>
        public void NotifyCommitted()
        {
            lastUncommittedSelectionKey = null;
            selectionWasCommitted = true;
        }
        public void Dispose()
        {
            if (themeEventSubscribed)
            {
                try { VSColorTheme.ThemeChanged -= OnVsThemeChanged; } catch { }
                themeEventSubscribed = false;
            }
            iconFont.Dispose();
            selectionBackBrush?.Dispose();
            foreach (SolidBrush brush in kindBrushCache.Values) brush.Dispose();
            kindBrushCache.Clear();
            window.Dispose();
        }

        private sealed class NativeWindowOwner : IWin32Window
        {
            public NativeWindowOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        private sealed class CompletionForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            // WS_EX_NOACTIVATE: clicking the popup (list, details pane, resize
            // border) must not move keyboard focus away from the SQL editor.
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return cp;
                }
            }
        }
    }
}
