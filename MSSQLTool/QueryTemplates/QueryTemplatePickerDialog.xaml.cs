using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Xml;

namespace MSSQLTool
{
    /// <summary>
    /// Modeless template picker opened directly from the toolbar button. The
    /// vertical category tree mirrors the templates folder hierarchy and
    /// supports creating, renaming and deleting both categories (folders) and
    /// statements (.sql files), including files that were simply copied into
    /// the folder by hand.
    /// </summary>
    public partial class QueryTemplatePickerDialog : Window
    {
        private static QueryTemplatePickerDialog current;

        private QueryTemplateTreeNode selectedNode;
        private string originalPreviewText = string.Empty;
        private string lastLoadedRelative;
        private bool updatingPreview;
        private bool subscribed;

        public QueryTemplatePickerDialog()
        {
            InitializeComponent();
            TryLoadSqlHighlighting();
            SetActionState();
        }

        /// <summary>Opens the picker (single instance) owned by the SSMS main window.</summary>
        public static void ShowPicker()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (current != null)
            {
                current.Activate();
                return;
            }

            var dialog = new QueryTemplatePickerDialog();
            current = dialog;
            try
            {
                var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                if (shell != null && shell.GetDialogOwnerHwnd(out IntPtr owner) == VSConstants.S_OK && owner != IntPtr.Zero)
                    new WindowInteropHelper(dialog) { Owner = owner };
            }
            catch
            {
                // The owner is cosmetic; the dialog also works unowned.
            }
            dialog.Show();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!subscribed)
            {
                QueryTemplateLibrary.Instance.Changed += Library_Changed;
                subscribed = true;
            }
            RootFolderText.Text = QueryTemplateLibrary.Instance.RootFolder;
            RefreshTree();
            SearchBox.Focus();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (subscribed)
            {
                QueryTemplateLibrary.Instance.Changed -= Library_Changed;
                subscribed = false;
            }
            if (ReferenceEquals(current, this)) current = null;
        }

        private void Library_Changed(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) OnLibraryChanged();
            else Dispatcher.BeginInvoke(new Action(OnLibraryChanged));
        }

        private void OnLibraryChanged()
        {
            // Files dropped/edited directly in the templates folder arrive
            // through the file-system watcher; rebuild while preserving the
            // selection and any unsaved preview edits.
            RefreshTree();
        }

        // ------------------------------------------------------------------
        // Tree building
        // ------------------------------------------------------------------

        private void RefreshTree()
        {
            ObservableCollection<QueryTemplateTreeNode> nodes = BuildTree(SearchBox.Text);

            QueryTemplateTreeNode match = null;
            if (selectedNode != null)
                match = FindByRelative(nodes, selectedNode.RelativePath, selectedNode.IsFolder);
            if (match == null)
                match = FindFirstLeaf(nodes);

            TemplateTree.ItemsSource = nodes;

            if (match != null)
            {
                ExpandAncestors(nodes, match);
                match.IsSelected = true;
            }
            else
            {
                selectedNode = null;
                SetActionState();
                LoadPreview();
            }

            string status = string.Format(LocalizationManager.T("{0} statement(s)"), CountLeaves(nodes));
            if (!string.IsNullOrWhiteSpace(QueryTemplateLibrary.Instance.LastError))
                status += "  |  " + QueryTemplateLibrary.Instance.LastError;
            StatusText.Text = status;
        }

        private ObservableCollection<QueryTemplateTreeNode> BuildTree(string search)
        {
            string upper = (search ?? string.Empty).Trim().ToUpperInvariant();
            var folderNodes = new Dictionary<string, QueryTemplateTreeNode>(StringComparer.OrdinalIgnoreCase);
            var rootNodes = new ObservableCollection<QueryTemplateTreeNode>();

            Action<string> ensureFolder = null;
            ensureFolder = relative =>
            {
                if (string.IsNullOrEmpty(relative) || folderNodes.ContainsKey(relative)) return;
                string[] parts = relative.Split(Path.DirectorySeparatorChar);
                string parentPath = parts.Length <= 1
                    ? string.Empty
                    : string.Join(Path.DirectorySeparatorChar.ToString(), parts, 0, parts.Length - 1);
                ensureFolder(parentPath);

                var node = new QueryTemplateTreeNode { Name = parts[parts.Length - 1], RelativePath = relative, IsFolder = true };
                folderNodes[relative] = node;
                InsertSorted(string.IsNullOrEmpty(parentPath) ? rootNodes : folderNodes[parentPath].Children, node);
            };

            foreach (string folder in QueryTemplateLibrary.Instance.SnapshotFolderPaths())
                ensureFolder(folder);

            foreach (QueryTemplateItem item in QueryTemplateLibrary.Instance.Snapshot())
            {
                ensureFolder(item.Category);
                var node = new QueryTemplateTreeNode
                {
                    Name = item.Name,
                    RelativePath = item.RelativePath,
                    FullPath = item.FullPath,
                    IsFolder = false,
                    IsFavorite = item.IsFavorite
                };
                InsertSorted(string.IsNullOrEmpty(item.Category) ? rootNodes : folderNodes[item.Category].Children, node);
            }

            if (upper.Length > 0) PruneToMatches(rootNodes, upper);
            ApplyExpandPolicy(rootNodes);
            return rootNodes;
        }

        private static void InsertSorted(ObservableCollection<QueryTemplateTreeNode> collection, QueryTemplateTreeNode node)
        {
            int index = 0;
            while (index < collection.Count)
            {
                QueryTemplateTreeNode existing = collection[index];
                if (existing.IsFolder && !node.IsFolder) { index++; continue; }
                if (existing.IsFolder == node.IsFolder
                    && string.Compare(existing.Name, node.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
                {
                    index++;
                    continue;
                }
                break;
            }
            collection.Insert(index, node);
        }

        private static bool PruneToMatches(ObservableCollection<QueryTemplateTreeNode> nodes, string upper)
        {
            bool keptAny = false;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                QueryTemplateTreeNode node = nodes[i];
                if (node.IsFolder)
                {
                    if (PruneToMatches(node.Children, upper)) keptAny = true;
                    else nodes.RemoveAt(i);
                }
                else if (node.SearchText.Contains(upper)) keptAny = true;
                else nodes.RemoveAt(i);
            }
            return keptAny;
        }

        private static void ApplyExpandPolicy(ObservableCollection<QueryTemplateTreeNode> nodes)
        {
            int folderCount = CountFolders(nodes);
            int depthLimit = folderCount <= 40 ? int.MaxValue : 2;
            ApplyExpansion(nodes, 1, depthLimit);
        }

        private static void ApplyExpansion(ObservableCollection<QueryTemplateTreeNode> nodes, int depth, int depthLimit)
        {
            foreach (QueryTemplateTreeNode node in nodes)
            {
                node.IsExpanded = depth < depthLimit;
                if (node.Children.Count > 0) ApplyExpansion(node.Children, depth + 1, depthLimit);
            }
        }

        private static QueryTemplateTreeNode FindByRelative(IEnumerable<QueryTemplateTreeNode> nodes, string relative, bool isFolder)
        {
            if (relative == null) return null;
            foreach (QueryTemplateTreeNode node in nodes)
            {
                if (node.IsFolder == isFolder && string.Equals(node.RelativePath, relative, StringComparison.OrdinalIgnoreCase))
                    return node;
                QueryTemplateTreeNode child = FindByRelative(node.Children, relative, isFolder);
                if (child != null) return child;
            }
            return null;
        }

        private static QueryTemplateTreeNode FindFirstLeaf(IEnumerable<QueryTemplateTreeNode> nodes)
        {
            foreach (QueryTemplateTreeNode node in nodes)
            {
                if (!node.IsFolder) return node;
                QueryTemplateTreeNode child = FindFirstLeaf(node.Children);
                if (child != null) return child;
            }
            return null;
        }

        private static bool ExpandAncestors(IEnumerable<QueryTemplateTreeNode> nodes, QueryTemplateTreeNode target)
        {
            foreach (QueryTemplateTreeNode node in nodes)
            {
                if (ReferenceEquals(node, target)) return true;
                if (node.Children.Count > 0 && ExpandAncestors(node.Children, target))
                {
                    node.IsExpanded = true;
                    return true;
                }
            }
            return false;
        }

        private static int CountLeaves(IEnumerable<QueryTemplateTreeNode> nodes)
        {
            int count = 0;
            foreach (QueryTemplateTreeNode node in nodes)
                count += node.IsFolder ? CountLeaves(node.Children) : 1;
            return count;
        }

        private static int CountFolders(IEnumerable<QueryTemplateTreeNode> nodes)
        {
            int count = 0;
            foreach (QueryTemplateTreeNode node in nodes)
            {
                if (!node.IsFolder) continue;
                count += 1 + CountFolders(node.Children);
            }
            return count;
        }

        private void SelectRelative(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return;
            var nodes = TemplateTree.ItemsSource as ObservableCollection<QueryTemplateTreeNode>;
            QueryTemplateTreeNode node = FindByRelative(nodes, relative, false) ?? FindByRelative(nodes, relative, true);
            if (node == null) return;
            ExpandAncestors(nodes, node);
            node.IsSelected = true;
        }

        // ------------------------------------------------------------------
        // Selection, preview and saving
        // ------------------------------------------------------------------

        private void TemplateTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var node = e.NewValue as QueryTemplateTreeNode;
            var previous = e.OldValue as QueryTemplateTreeNode;
            if (node == null)
            {
                // Transient null during tree rebuilds; keep the current state.
                SetActionState();
                return;
            }
            if (ReferenceEquals(node, selectedNode))
            {
                SetActionState();
                return;
            }

            bool samePath = !node.IsFolder
                && string.Equals(node.RelativePath, lastLoadedRelative, StringComparison.OrdinalIgnoreCase);
            bool hasUnsavedChanges = !samePath && selectedNode != null && !selectedNode.IsFolder
                && !string.Equals(PreviewEditor.Text, originalPreviewText, StringComparison.Ordinal);
            if (hasUnsavedChanges)
            {
                MessageBoxResult discard = LocalizedMessageBox.Show(this,
                    LocalizationManager.T("The statement has unsaved changes. Discard them?"),
                    "Query Templates", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (discard != MessageBoxResult.Yes)
                {
                    e.Handled = true;
                    if (node != null) node.IsSelected = false;
                    if (previous != null) previous.IsSelected = true;
                    return;
                }
            }

            selectedNode = node;
            SetActionState();
            if (samePath && !string.Equals(PreviewEditor.Text, originalPreviewText, StringComparison.Ordinal))
            {
                // Same file re-selected after a tree rebuild: keep unsaved edits.
                return;
            }
            LoadPreview();
        }

        private void LoadPreview()
        {
            updatingPreview = true;
            try
            {
                if (selectedNode == null || selectedNode.IsFolder)
                {
                    lastLoadedRelative = null;
                    originalPreviewText = string.Empty;
                    PreviewEditor.IsReadOnly = true;
                    PreviewEditor.Text = string.Empty;
                    SelectedPathText.Text = selectedNode == null ? string.Empty : selectedNode.RelativePath;
                    return;
                }

                lastLoadedRelative = selectedNode.RelativePath;
                SelectedPathText.Text = selectedNode.RelativePath;
                PreviewEditor.IsReadOnly = false;
                try
                {
                    originalPreviewText = QueryTemplateLibrary.Instance.ReadContent(selectedNode.FullPath);
                    PreviewEditor.Text = originalPreviewText;
                    PreviewEditor.ScrollToHome();
                }
                catch (Exception ex)
                {
                    originalPreviewText = string.Empty;
                    PreviewEditor.IsReadOnly = true;
                    PreviewEditor.Text = "-- " + LocalizationManager.T("Preview unavailable") + Environment.NewLine + "-- " + ex.Message;
                }
            }
            finally
            {
                updatingPreview = false;
                SaveButton.IsEnabled = false;
            }
        }

        private void PreviewEditor_TextChanged(object sender, EventArgs e)
        {
            if (updatingPreview) return;
            SaveButton.IsEnabled = selectedNode != null && !selectedNode.IsFolder
                && !string.Equals(PreviewEditor.Text, originalPreviewText, StringComparison.Ordinal);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveSelected();

        private void SaveSelected()
        {
            if (selectedNode == null || selectedNode.IsFolder) return;
            try
            {
                QueryTemplateLibrary.Instance.WriteContent(selectedNode.FullPath, PreviewEditor.Text);
                originalPreviewText = PreviewEditor.Text;
                SaveButton.IsEnabled = false;
                StatusText.Text = LocalizationManager.T("Saved: ") + selectedNode.RelativePath;
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void SetActionState()
        {
            bool isFile = selectedNode != null && !selectedNode.IsFolder;
            bool hasSelection = selectedNode != null;
            InsertButton.IsEnabled = isFile;
            NewQueryButton.IsEnabled = isFile;
            SaveButton.IsEnabled = isFile
                && !string.Equals(PreviewEditor.Text, originalPreviewText, StringComparison.Ordinal);
            RenameButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
        }

        // ------------------------------------------------------------------
        // CRUD
        // ------------------------------------------------------------------

        private void NewCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            string parent = GetTargetCategoryPath();
            string name = PromptForName(
                LocalizationManager.T("New category"),
                LocalizationManager.Format(parent.Length == 0 ? "Create a category in the templates root:" : "Create a category under '{0}':", parent),
                string.Empty);
            if (name == null) return;

            if (QueryTemplateLibrary.Instance.TryCreateCategory(parent, name, out string created, out string error))
            {
                StatusText.Text = LocalizationManager.T("Category created: ") + created;
                RefreshTree();
                SelectRelative(created);
            }
            else
            {
                ShowError(error);
            }
        }

        private void NewStatementButton_Click(object sender, RoutedEventArgs e)
        {
            string parent = GetTargetCategoryPath();
            string seed = string.Empty;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                seed = QueryTemplateInsertionService.GetCurrentEditorText() ?? string.Empty;
            }
            catch
            {
                // No active editor: create the statement empty.
            }

            string name = PromptForName(
                LocalizationManager.T("New statement"),
                LocalizationManager.Format(parent.Length == 0 ? "Create a statement in the templates root:" : "Create a statement under '{0}':", parent),
                string.Empty);
            if (name == null) return;

            if (QueryTemplateLibrary.Instance.TryCreateTemplate(parent, name, seed, out string fullPath, out string error))
            {
                StatusText.Text = LocalizationManager.T("Statement created: ") + fullPath;
                RefreshTree();
                string cleanName = name.Trim();
                if (cleanName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    cleanName = cleanName.Substring(0, cleanName.Length - 4).TrimEnd();
                SelectRelative(Path.Combine(parent, cleanName + ".sql"));
            }
            else
            {
                ShowError(error);
            }
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedNode == null) return;

            if (selectedNode.IsFolder)
            {
                string newName = PromptForName(LocalizationManager.T("Rename category"),
                    LocalizationManager.T("Category name:"), selectedNode.Name);
                if (newName == null) return;

                string relative = selectedNode.RelativePath;
                int separator = relative.LastIndexOf(Path.DirectorySeparatorChar);
                string target = separator < 0
                    ? newName
                    : relative.Substring(0, separator) + Path.DirectorySeparatorChar + newName;

                if (QueryTemplateLibrary.Instance.TryRenameCategory(relative, newName, out string error))
                {
                    StatusText.Text = LocalizationManager.T("Category renamed: ") + target;
                    RefreshTree();
                    SelectRelative(target);
                }
                else
                {
                    ShowError(error);
                }
                return;
            }

            string statementName = PromptForName(LocalizationManager.T("Rename statement"),
                LocalizationManager.T("Statement name:"), selectedNode.Name);
            if (statementName == null) return;

            QueryTemplateItem item = FindTemplateItem(selectedNode.RelativePath);
            string targetRelative = Path.Combine(Path.GetDirectoryName(selectedNode.RelativePath) ?? string.Empty, statementName + ".sql");
            if (QueryTemplateLibrary.Instance.TryRenameTemplate(item, statementName, out string renameError))
            {
                StatusText.Text = LocalizationManager.T("Statement renamed: ") + targetRelative;
                RefreshTree();
                SelectRelative(targetRelative);
            }
            else
            {
                ShowError(renameError);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedNode == null) return;

            if (selectedNode.IsFolder)
            {
                int statementCount = CountLeaves(selectedNode.Children);
                MessageBoxResult confirmFolder = LocalizedMessageBox.Show(this,
                    string.Format(LocalizationManager.T("Delete category '{0}' and its {1} statement(s)?"),
                        selectedNode.RelativePath, statementCount),
                    "Query Templates", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmFolder != MessageBoxResult.Yes) return;

                if (QueryTemplateLibrary.Instance.TryDeleteCategory(selectedNode.RelativePath, out string error))
                {
                    StatusText.Text = LocalizationManager.T("Category deleted: ") + selectedNode.RelativePath;
                    selectedNode = null;
                    RefreshTree();
                }
                else
                {
                    ShowError(error);
                }
                return;
            }

            MessageBoxResult confirmStatement = LocalizedMessageBox.Show(this,
                string.Format(LocalizationManager.T("Delete statement '{0}'?"), selectedNode.RelativePath),
                "Query Templates", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmStatement != MessageBoxResult.Yes) return;

            QueryTemplateItem statement = FindTemplateItem(selectedNode.RelativePath);
            if (QueryTemplateLibrary.Instance.TryDeleteTemplate(statement, out string deleteError))
            {
                StatusText.Text = LocalizationManager.T("Statement deleted: ") + selectedNode.RelativePath;
                selectedNode = null;
                RefreshTree();
            }
            else
            {
                ShowError(deleteError);
            }
        }

        private QueryTemplateItem FindTemplateItem(string relativePath)
        {
            return QueryTemplateLibrary.Instance.Snapshot()
                .FirstOrDefault(x => string.Equals(x.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Category that receives new items: the selected folder, the selected file's folder, or the root.</summary>
        private string GetTargetCategoryPath()
        {
            if (selectedNode == null) return string.Empty;
            if (selectedNode.IsFolder) return selectedNode.RelativePath;
            return Path.GetDirectoryName(selectedNode.RelativePath) ?? string.Empty;
        }

        private string PromptForName(string title, string label, string initialValue)
        {
            var dialog = new QueryTemplateNameDialog(title, label, initialValue) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Value : null;
        }

        private void ShowError(string message)
        {
            LocalizedMessageBox.Show(message, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // ------------------------------------------------------------------
        // Insert, folder, refresh, search, keyboard
        // ------------------------------------------------------------------

        private void InsertButton_Click(object sender, RoutedEventArgs e) => InsertSelected(false);

        private void NewQueryButton_Click(object sender, RoutedEventArgs e) => InsertSelected(true);

        private void TemplateTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (selectedNode != null && !selectedNode.IsFolder)
                InsertSelected(false);
        }

        private void InsertSelected(bool newQuery)
        {
            if (selectedNode == null || selectedNode.IsFolder) return;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                QueryTemplateInsertionService.InsertFile(selectedNode.FullPath, newQuery);
                StatusText.Text = newQuery
                    ? LocalizationManager.T("Template opened in a new query.")
                    : LocalizationManager.T("Template inserted.");
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(LocalizationManager.T("Could not insert the query template:") + " " + ex.Message,
                    "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = QueryTemplateLibrary.Instance.RootFolder;
                string selectFile = null;
                if (selectedNode != null && !selectedNode.IsFolder)
                {
                    selectFile = selectedNode.FullPath;
                    folder = Path.GetDirectoryName(selectFile);
                }
                else if (selectedNode != null && selectedNode.IsFolder)
                {
                    folder = Path.Combine(QueryTemplateLibrary.Instance.RootFolder, selectedNode.RelativePath);
                }

                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException("Templates folder is currently unavailable: " + folder);

                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.Arguments = string.IsNullOrEmpty(selectFile)
                    ? Quote(folder)
                    : "/select," + Quote(selectFile);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // The Changed event rebuilds the tree.
            QueryTemplateLibrary.Instance.Refresh();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) RefreshTree();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
                {
                    SearchBox.Clear();
                    e.Handled = true;
                    return;
                }
                Close();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                if (SaveButton.IsEnabled) SaveSelected();
                e.Handled = true;
                return;
            }

            if (!TemplateTree.IsKeyboardFocusWithin) return;

            if (e.Key == Key.Enter)
            {
                InsertSelected((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void TryLoadSqlHighlighting()
        {
            try
            {
                using (var stream = typeof(QueryTemplatePickerDialog).Assembly.GetManifestResourceStream("MSSQLTool.QuickSearch.sql.xshd"))
                using (var reader = new XmlTextReader(stream))
                    PreviewEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch
            {
                // Preview remains useful as plain text if the optional highlighter cannot load.
            }
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "") + "\"";
    }

    /// <summary>One node of the picker tree: a category (folder) or a statement file.</summary>
    internal sealed class QueryTemplateTreeNode
    {
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public bool IsFolder { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsExpanded { get; set; } = true;
        public bool IsSelected { get; set; }
        public ObservableCollection<QueryTemplateTreeNode> Children { get; } = new ObservableCollection<QueryTemplateTreeNode>();

        public string Glyph => IsFolder ? "\uD83D\uDCC1" : "\uD83D\uDCC4";
        public string Display => Name + (IsFavorite ? " \u2605" : "");
        public string SearchText => (Name + " " + RelativePath).ToUpperInvariant();
    }

    /// <summary>Small single-value prompt used for category/statement names.</summary>
    internal sealed class QueryTemplateNameDialog : Window
    {
        private readonly TextBox nameBox;
        private readonly TextBlock validationText;

        public QueryTemplateNameDialog(string title, string label, string initialValue)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;

            try
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/MSSQLTool;component/Themes/SharedToolWindowTheme.xaml", UriKind.RelativeOrAbsolute)
                });
                Background = (Brush)Resources["ToolThemeBackgroundBrush"];
                Foreground = (Brush)Resources["ToolThemeForegroundBrush"];
            }
            catch
            {
                // Fall back to system colors when the shared theme is unavailable.
            }

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = label ?? string.Empty, TextWrapping = TextWrapping.Wrap });

            nameBox = new TextBox { Margin = new Thickness(0, 8, 0, 0), Height = 26, Text = initialValue ?? string.Empty };
            panel.Children.Add(nameBox);

            validationText = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.OrangeRed,
                Visibility = Visibility.Collapsed
            };
            panel.Children.Add(validationText);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var okButton = new Button
            {
                Content = LocalizationManager.T("OK"),
                Width = 80,
                Height = 26,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0)
            };
            okButton.Click += (s, e) => Accept();
            var cancelButton = new Button
            {
                Content = LocalizationManager.T("Cancel"),
                Width = 80,
                Height = 26,
                IsCancel = true
            };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += (s, e) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };
        }

        public string Value => nameBox.Text?.Trim();

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                validationText.Text = LocalizationManager.T("Enter a name.");
                validationText.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        }
    }
}
