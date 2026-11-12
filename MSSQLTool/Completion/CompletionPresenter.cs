using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Diagnostics;
using System.Linq;
using System.Drawing.Drawing2D;

namespace MSSQLTool.Completion
{
    internal sealed class CompletionPresenter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCaretPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZorder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint GaRoot = 2;

        private readonly Form window;
        private IntPtr ownerHandle;
        private int anchorLine = -1;
        private int anchorColumn = 0;
        private int anchorFirstVisible = 0;
        private readonly SplitContainer contentSplit;
        private readonly ListBox list;
        private readonly ComboBox category;
        private readonly TextBox details;
        private readonly RichTextBox parameterInfo;
        private readonly Label footer;
        private readonly Font iconFont;
        private Font matchFont;
        private string lastFilterPrefix = string.Empty;
        private const TextFormatFlags ItemTextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        private IReadOnlyList<CompletionItem> items;
        private List<CompletionItem> allItems = new List<CompletionItem>();
        private IVsTextView lastTextView;
        private MetadataSnapshot lastMetadata;
        private CompletionContext lastContext;
        private bool updatingCategory;
        private bool updatingSplitter;
        // True only after the user moved the highlight with the arrow keys or the mouse.  A
        // refresh triggered by typing must not keep an older row selected: the first row is the
        // best match and has to stay the default until the user chooses something else.
        private bool userMovedSelection;
        private string lastParameterText = string.Empty;
        private float lastParameterWidth = -1;

        /// <summary>True once the user picked a row with the keyboard or mouse in this session.</summary>
        public bool HasUserNavigated => userMovedSelection;
        private int textLineHeight;
        private int parameterMinHeight;
        private Font parameterBoldFont;

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
                // Parameters wrap instead of scrolling sideways: a procedure with a dozen arguments
                // cannot be read on a single line, and the horizontal scrollbar hid most of it.
                WordWrap = true,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical,
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
            list.MouseDown += (_, __) => userMovedSelection = true;
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
                // A wider or narrower popup rewraps the parameters.
                UpdateParameterInfoHeight();
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
        public List<CompletionItem> SelectedItems
        {
            get
            {
                var selected = new List<CompletionItem>();
                if (items == null) return selected;
                foreach (int index in list.SelectedIndices)
                    if (index >= 0 && index < items.Count) selected.Add(items[index]);
                return selected;
            }
        }

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
            finally
            {
            }
        }

        private void ShowCore(IVsTextView textView, IReadOnlyList<CompletionItem> newItems, MetadataSnapshot metadata, CompletionContext context)
        {
            if (!themeResolved) ApplySsmsTheme();

            // The list is refreshed on every keystroke.  Keeping the row the user moved to is part
            // of the editing feel: only a brand-new popup starts at the first row.
            CompletionItem previousItem = SelectedItem;
            string previousKey = previousItem != null ? ItemKey(previousItem) : null;
            int previousIndex = list.SelectedIndex;
            bool sessionContinues = window.Visible && lastContext != null && context != null
                && lastContext.Kind == context.Kind
                && string.Equals(lastContext.CurrentStatement ?? string.Empty, context.CurrentStatement ?? string.Empty, StringComparison.Ordinal);
            // Typing replaces the manual choice.  The flag used to survive for the whole session, so
            // one earlier arrow press made every later preview get force-cleared as "navigated",
            // and the highlight stayed on a row the new prefix no longer matched.
            bool prefixChanged = !string.Equals(lastFilterPrefix, context?.Prefix ?? string.Empty, StringComparison.Ordinal);
            lastFilterPrefix = context?.Prefix ?? string.Empty;

            lastTextView = textView;
            lastMetadata = metadata;
            lastContext = context;
            allItems = newItems?.ToList() ?? new List<CompletionItem>();

            UpdateCategories();
            newItems = FilterCategory(allItems);
            var completionSettings = SettingsManager.GetSqlCompletionSettings();
            list.SelectionMode = completionSettings.enableColumnPicker && string.Equals(category.SelectedItem as string, "Columns", StringComparison.Ordinal)
                ? SelectionMode.MultiExtended : SelectionMode.One;
            bool showDetails = completionSettings.showObjectDetails;
            if (contentSplit.Panel2Collapsed == showDetails)
                contentSplit.Panel2Collapsed = !showDetails;
            UpdateParameterInfo(metadata, context);
            list.BeginUpdate();
            try
            {
                // The control only has to know *how many* rows exist: DrawCompletionItem paints
                // from items[e.Index], so assigning items[i] one by one (a change notification
                // each, ~130 per keystroke) was pure overhead.  Sync the count and repaint once.
                items = newItems;
                // Shrinking removes only the tail; growing adds in one batch notification
                // instead of one Add per row.
                while (list.Items.Count > newItems.Count)
                    list.Items.RemoveAt(list.Items.Count - 1);
                int missing = newItems.Count - list.Items.Count;
                if (missing > 0)
                {
                    var placeholders = new object[missing];
                    object placeholder = 0;
                    for (int i = 0; i < missing; i++) placeholders[i] = placeholder;
                    list.Items.AddRange(placeholders);
                }
                list.Invalidate();
            }
            finally { list.EndUpdate(); }
            if (items.Count == 0 && !parameterInfo.Visible) { Hide(); return; }

            // A popup that was already open keeps the selected row when it survives the filter;
            // a new popup starts at the first row.  Without an explicit arrow/mouse move the
            // refresh always returns to the first row, which is the best match for the new input.
            if (!KeepsManualSelection(sessionContinues, prefixChanged)) userMovedSelection = false;
            list.SelectedIndex = ChooseSelectionIndex(sessionContinues, userMovedSelection, items, previousKey, previousIndex);
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
            // The strip wraps against the popup width, so its height is only final once the
            // window has the size it is shown with.  Re-measuring every keystroke forces a full
            // layout pass, so only the visible text or the width triggers it.
            string parameterText = parameterInfo.Text ?? string.Empty;
            if (!string.Equals(parameterText, lastParameterText, StringComparison.Ordinal)
                || window.Width != lastParameterWidth)
            {
                lastParameterText = parameterText;
                lastParameterWidth = window.Width;
                UpdateParameterInfoHeight();
            }
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
            string progress = SqlMetadataCache.LoadProgress.Current;
            footer.Text = string.IsNullOrWhiteSpace(progress)
                ? LocalizationManager.T("Metadata loading — suggestions will appear when ready.")
                : progress;
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

            bool sizeChanged = window.Width != layout.Size.Width || window.Height != layout.Size.Height;
            if (sizeChanged)
            {
                // MinimumSize must be applied before the new size or WinForms clamps it.
                // All four assignments force a layout pass, so they only run when the size
                // actually changes; a keystroke inside the same popup block skips them.
                window.MinimumSize = layout.MinimumSize;
                window.Width = layout.Size.Width;
                window.Height = layout.Size.Height;
            }
            // Kept outside the guard: the details pane also changes width when the
            // category or the selected row changes, not only with the window size.
            ApplyDetailsWidth();

            bool locationChanged = window.Location != layout.Location;
            if (locationChanged) window.Location = layout.Location;

            IntPtr owner = ResolveOwnerWindow(handle);
            bool ownerChanged = owner != ownerHandle;
            bool wasVisible = window.Visible;
            if (ownerChanged && wasVisible)
            {
                // A WinForms form cannot change its native owner while it is visible.  Recreate
                // the ownership so the popup is tied to the visible SSMS frame, not the hidden
                // HwndlessEditorFakeHwndWrapper that reports itself as the editor window.
                window.Hide();
                wasVisible = false;
            }
            if (!wasVisible)
                window.Show(new NativeWindowOwner(owner));
            ownerHandle = owner;

            // The editor HWND is a hidden HwndlessEditorFakeHwndWrapper.  A popup owned by that
            // window is still below the visible SSMS frame, so SWP_NOZORDER alone leaves it
            // invisible.  Raise it only while the real owner is foreground; when the user switches
            // to another application, the session timer dismisses it instead of leaving it on top.
            bool ownerForeground = IsOwnerForeground(owner);
            if (!wasVisible || ownerChanged || locationChanged || sizeChanged || ownerForeground)
            {
                uint flags = SwpNoSize | SwpNoActivate | SwpShowWindow;
                if (!ownerForeground) flags |= SwpNoZorder;
                SetWindowPos(window.Handle, IntPtr.Zero, layout.Location.X, layout.Location.Y, 0, 0, flags);
            }
            CaptureAnchor(textView);
        }

        /// <summary>
        /// The visible SSMS frame, not the editor's hidden fake HWND, owns the popup.  Owning the
        /// popup to the real frame keeps it above SSMS without forcing it above other applications.
        /// </summary>
        private static IntPtr ResolveOwnerWindow(IntPtr editorHandle)
        {
            try
            {
                var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                if (uiShell != null
                    && uiShell.GetDialogOwnerHwnd(out IntPtr owner) == VSConstants.S_OK
                    && owner != IntPtr.Zero)
                    return owner;
            }
            catch (Exception) { }

            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground != IntPtr.Zero && BelongsToCurrentProcess(foreground))
                {
                    IntPtr root = GetAncestor(foreground, GaRoot);
                    if (root != IntPtr.Zero) return root;
                }
            }
            catch (Exception) { }

            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
                }
            }
            catch (Exception) { }

            return editorHandle;
        }

        private static bool BelongsToCurrentProcess(IntPtr window)
        {
            GetWindowThreadProcessId(window, out uint processId);
            return processId == GetCurrentProcessId();
        }

        private static bool IsOwnerForeground(IntPtr owner)
        {
            if (owner == IntPtr.Zero) return false;
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            if (foreground == owner) return true;
            // A child of the SSMS frame (the editor, a toolbar) is still owned by that frame.
            // A modal dialog is deliberately not: the timer then dismisses the popup instead of
            // raising it above the dialog.
            return GetAncestor(foreground, GaRoot) == owner;
        }

        /// <summary>True while the popup's real owner is the foreground window.</summary>
        public bool OwnerIsForeground => IsOwnerForeground(ownerHandle);

        /// <summary>
        /// Resolves the visual top-left corner of the caret in screen coordinates together with
        /// the editor's line height.  Asking for the caret column (rather than column 0) is what
        /// makes wrapped lines work: the returned point is the caret's actual wrapped row, not the
        /// first screen row of the logical line.  Returns false when the editor cannot report the
        /// caret, so the caller can fall back to the Win32 caret.
        /// </summary>
        private static bool TryGetCaretPlacement(IVsTextView textView, out Point caretTop, out int lineHeight)
        {
            caretTop = Point.Empty;
            lineHeight = 0;
            if (textView == null) return false;

            try
            {
                IntPtr editorHandle = textView.GetWindowHandle();

                if (textView.GetLineHeight(out int editorLineHeight) == VSConstants.S_OK && editorLineHeight > 0)
                    lineHeight = editorLineHeight;

                if (textView.GetCaretPos(out int line, out int column) != VSConstants.S_OK)
                    return false;

                // Use the caret's visual column.  With word wrap enabled SSMS can put the caret on
                // a continuation row; column 0 would report the first row and the popup would then
                // be placed over the caret.
                Microsoft.VisualStudio.OLE.Interop.POINT[] points = new Microsoft.VisualStudio.OLE.Interop.POINT[1];
                if (textView.GetPointOfLineColumn(line, column, points) != VSConstants.S_OK)
                    return false;

                int x = points[0].x;
                int y = points[0].y;

                // SSMS 22's editor returns this point in client coordinates (the diagnostic log
                // showed raw (798,685) with the editor at screen (507,202), i.e. the real caret was
                // (1305,887)).  Converting unconditionally is required: the old "is the raw point
                // inside the editor rect?" heuristic was ambiguous once Object Explorer closed and
                // the editor moved left, which put the popup over the caret.
                NativePoint screenPoint = new NativePoint { X = x, Y = y };
                if (ClientToScreen(editorHandle, ref screenPoint))
                {
                    x = screenPoint.X;
                    y = screenPoint.Y;
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
            if (commandId == (uint)VSConstants.VSStd2KCmdID.UP) { userMovedSelection = true; list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.DOWN) { userMovedSelection = true; list.SelectedIndex = Math.Min(items.Count - 1, list.SelectedIndex + 1); return true; }
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
            if (items.Count == 0) return;
            userMovedSelection = true;
            list.SelectedIndex = Math.Max(0, Math.Min(items.Count - 1, list.SelectedIndex + offset));
        }

        private static string ItemKey(CompletionItem item) => item?.Key;

        /// <summary>
        /// The row a refreshed list selects.  Always the first one: the popup never restores a row
        /// the user moved to earlier, so the highlight cannot end up parked further down the list.
        /// </summary>
        internal static int InitialSelectionIndex(int itemCount) => itemCount <= 0 ? -1 : 0;

        /// <summary>
        /// True only while a manual arrow/mouse choice still describes the current input.  A new
        /// prefix makes the filter's own best match authoritative again; keeping the old row there
        /// left the list highlighting a word the caret no longer typed, and the inline preview was
        /// force-cleared as "navigated" on every keystroke that followed.
        /// </summary>
        internal static bool KeepsManualSelection(bool sessionContinues, bool prefixChanged)
            => sessionContinues && !prefixChanged;

        /// <summary>
        /// The row a refreshed list selects: the same item when it survived the new filter, the
        /// closest index otherwise.  Never row 0 when the user had moved elsewhere.
        /// </summary>
        internal static int ResolveSelectionIndex(IReadOnlyList<CompletionItem> current, string previousKey, int previousIndex)
        {
            if (current == null || current.Count == 0) return -1;
            if (!string.IsNullOrEmpty(previousKey))
            {
                for (int i = 0; i < current.Count; i++)
                    if (string.Equals(ItemKey(current[i]), previousKey, StringComparison.Ordinal)) return i;
            }

            return Math.Max(0, Math.Min(current.Count - 1, previousIndex));
        }

        /// <summary>
        /// The highlight after a refresh: a row the user picked with the keyboard or the mouse is
        /// preserved when it survives the filter, otherwise the first row (the best match) wins.
        /// Typing therefore never leaves the highlight parked on an earlier, now less relevant row.
        /// </summary>
        internal static int ChooseSelectionIndex(bool sessionContinues, bool userMovedSelection,
            IReadOnlyList<CompletionItem> current, string previousKey, int previousIndex)
        {
            if (!sessionContinues || !userMovedSelection) return InitialSelectionIndex(current?.Count ?? 0);
            if (current == null || current.Count == 0) return -1;

            // Keep the row the user moved to only while it is still part of the list; when it was
            // filtered out, the best match takes over so the highlight is never arbitrary.
            if (!string.IsNullOrEmpty(previousKey))
            {
                for (int i = 0; i < current.Count; i++)
                    if (string.Equals(ItemKey(current[i]), previousKey, StringComparison.Ordinal)) return i;
            }

            return InitialSelectionIndex(current.Count);
        }

        private void UpdateCategories()
        {
            string selected = category.SelectedItem as string ?? "All";
            var available = new List<string> { "All" };
            AddCategory(available, "Columns", CompletionItemKind.Column);
            // One pass over the candidates instead of three full scans: this runs on every keystroke.
            bool hasObjects = false, hasRoutines = false, hasContainers = false;
            for (int i = 0; i < allItems.Count; i++)
            {
                switch (allItems[i].Kind)
                {
                    case CompletionItemKind.Table:
                    case CompletionItemKind.View:
                    case CompletionItemKind.Synonym: hasObjects = true; break;
                    case CompletionItemKind.Procedure:
                    case CompletionItemKind.Function: hasRoutines = true; break;
                    case CompletionItemKind.Server:
                    case CompletionItemKind.Database:
                    case CompletionItemKind.Schema: hasContainers = true; break;
                }
            }
            if (hasObjects) available.Add("Objects");
            if (hasRoutines) available.Add("Routines");
            if (hasContainers) available.Add("Containers");
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
            parameterMinHeight = (textLineHeight * 2) + 14;
            parameterInfo.Height = parameterMinHeight;
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
            var textBounds = new Rectangle(textLeft, bounds.Top, Math.Max(0, bounds.Right - textLeft - 7), bounds.Height);
            DrawItemText(e.Graphics, item.DisplayText ?? string.Empty, textBounds, primary, selected);
            e.DrawFocusRectangle();
        }

        private void DrawItemText(Graphics graphics, string text, Rectangle bounds, Color color, bool selected)
        {
            int hit = FindHighlight(text, lastFilterPrefix, out int hitLength);
            if (hit < 0)
            {
                TextRenderer.DrawText(graphics, text, list.Font, bounds, color, ItemTextFlags);
                return;
            }

            if (matchFont == null) matchFont = new Font(list.Font, FontStyle.Bold);
            int x = bounds.Left;
            if (hit > 0)
            {
                string before = text.Substring(0, hit);
                DrawSegment(graphics, before, ref x, bounds, color, list.Font);
            }

            string match = text.Substring(hit, hitLength);
            DrawSegment(graphics, match, ref x, bounds, GetMatchColor(selected, color), matchFont);

            string after = text.Substring(hit + hitLength);
            if (after.Length > 0)
                DrawSegment(graphics, after, ref x, bounds, color, list.Font);
        }

        private static void DrawSegment(Graphics graphics, string text, ref int x, Rectangle bounds, Color color, Font font)
        {
            Size measured = TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, bounds.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var segment = new Rectangle(x, bounds.Top, measured.Width, bounds.Height);
            TextRenderer.DrawText(graphics, text, font, segment, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            x += measured.Width;
        }

        private static int FindHighlight(string text, string prefix, out int length)
        {
            length = 0;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(prefix)) return -1;
            string needle = prefix.TrimStart('@');
            if (needle.Length == 0) return -1;
            int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            length = needle.Length;
            return index;
        }

        private Color GetMatchColor(bool selected, Color primary)
        {
            // Selected rows already use a light foreground; bolding alone marks the match without
            // hurting contrast.  Unselected rows get an accent on the matched run.
            return selected ? primary : Color.FromArgb(0x0A, 0x5C, 0xB0);
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
            var allParameters = metadata.ParametersFor(target)
                .Where(p => NameMatches(p.ObjectName, target))
                .OrderBy(p => p.Ordinal)
                .ToList();
            var parameters = allParameters.Select((p, index) =>
                (IsActiveParameter(p, index, context) ? "> " : "  ") + p.Name + " " + p.TypeDisplay
                + (p.HasDefaultValue ? " = default" : string.Empty) + (p.IsOutput ? " OUTPUT" : string.Empty)).ToList();
            if (parameters.Count == 0) return null;
            // The strip used to be an unlabelled object name over one long parameter line; a
            // "Parameters" header says what the area is, and the arguments wrap on their own.
            return LocalizationManager.T("Parameters") + "  " + target
                + Environment.NewLine + string.Join("    ", parameters);
        }

        /// <summary>Length of the header line ([Parameters]  object), which is drawn in bold.</summary>
        internal static int ParameterHeaderLength(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int breakIndex = text.IndexOf('\r');
            if (breakIndex < 0) breakIndex = text.IndexOf('\n');
            return breakIndex < 0 ? text.Length : breakIndex;
        }

        internal static bool HasParameterInfo(MetadataSnapshot metadata, CompletionContext context)
            => !string.IsNullOrWhiteSpace(BuildParameterInfo(metadata, context));

        private void UpdateParameterInfo(MetadataSnapshot metadata, CompletionContext context)
        {
            string value = BuildParameterInfo(metadata, context);
            parameterInfo.Visible = !string.IsNullOrWhiteSpace(value);
            parameterInfo.Text = value ?? string.Empty;
            if (!parameterInfo.Visible) return;

            // Header: "Parameters  dbo.Proc" in bold, so the strip explains itself.
            int header = ParameterHeaderLength(parameterInfo.Text);
            if (header > 0)
            {
                parameterInfo.Select(0, header);
                parameterInfo.SelectionFont = GetParameterBoldFont();
            }

            int marker = parameterInfo.Text.IndexOf("> ", StringComparison.Ordinal);
            if (marker >= 0)
            {
                int end = parameterInfo.Text.IndexOf("    ", marker, StringComparison.Ordinal);
                if (end < 0) end = parameterInfo.Text.Length;
                parameterInfo.Select(marker, end - marker);
                parameterInfo.SelectionBackColor = selectionBackColor;
                parameterInfo.SelectionColor = selectionForeColor;
                parameterInfo.SelectionFont = GetParameterBoldFont();
            }

            parameterInfo.Select(0, 0);
            UpdateParameterInfoHeight();
        }

        private Font GetParameterBoldFont()
            => parameterBoldFont ?? (parameterBoldFont = new Font(parameterInfo.Font, FontStyle.Bold));

        /// <summary>
        /// Fits the strip to the wrapped arguments, up to about 40% of the popup so the list keeps
        /// the rest; anything longer scrolls vertically instead of disappearing to the right.
        /// </summary>
        private void UpdateParameterInfoHeight()
        {
            if (!parameterInfo.Visible) return;
            int height = parameterMinHeight;
            try
            {
                // Where the last character ended up: with word wrap on, that is the number of
                // lines the arguments really need at the current width.
                Point last = parameterInfo.GetPositionFromCharIndex(parameterInfo.TextLength);
                height = last.Y + textLineHeight + 12;
            }
            catch
            {
            }

            int maximum = Math.Max(parameterMinHeight, (int)(window.ClientSize.Height * 0.4));
            parameterInfo.Height = Math.Max(parameterMinHeight, Math.Min(maximum, height));
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

        public void Hide()
        {
            userMovedSelection = false;
            if (!window.Visible) return;
            window.Hide();
        }

        /// <summary>
        /// Re-anchors a visible popup after the editor scrolled, was resized or switched to a
        /// split view.  The caret has not moved, but the screen position it maps to has.
        /// </summary>
        public void Reposition(IVsTextView textView)
        {
            try
            {
                if (window == null || !window.Visible) return;
                IVsTextView target = textView ?? lastTextView;
                if (target == null) return;
                if (IsAnchorCurrent(target)) return;
                PositionAndShow(target);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Completion", "The completion popup could not be repositioned", ex);
                try { window.Hide(); } catch { }
            }
        }

        private bool IsAnchorCurrent(IVsTextView textView)
        {
            int line = -1, column = 0, firstVisible = 0;
            try
            {
                if (textView.GetCaretPos(out line, out column) != VSConstants.S_OK) return false;
                if (textView.GetScrollInfo(1, out _, out _, out _, out firstVisible) != VSConstants.S_OK) firstVisible = 0;
            }
            catch (Exception) { return false; }
            return line == anchorLine && column == anchorColumn && firstVisible == anchorFirstVisible && anchorLine >= 0;
        }

        private void CaptureAnchor(IVsTextView textView)
        {
            try
            {
                if (textView.GetCaretPos(out int line, out int column) == VSConstants.S_OK)
                {
                    anchorLine = line;
                    anchorColumn = column;
                    anchorFirstVisible = textView.GetScrollInfo(1, out _, out _, out _, out int firstVisible) == VSConstants.S_OK ? firstVisible : 0;
                }
            }
            catch (Exception) { }
        }
        public void Dispose()
        {
            if (themeEventSubscribed)
            {
                try { VSColorTheme.ThemeChanged -= OnVsThemeChanged; } catch { }
                themeEventSubscribed = false;
            }
            iconFont.Dispose();
            parameterBoldFont?.Dispose();
            parameterBoldFont = null;
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
