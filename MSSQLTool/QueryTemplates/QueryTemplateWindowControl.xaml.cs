using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Xml;

namespace MSSQLTool
{
    public partial class QueryTemplateWindowControl : UserControl
    {
        private const string GitHubQueryLibraryZipUrl = "https://github.com/PuLuShen/MSSQLTOOL/archive/main.zip";
        private const string GitHubQueryLibraryFolderInZip = "MSSQLTOOL-main/query-library";

        private readonly ToolWindowThemeController themeController;
        private readonly DispatcherTimer settingsStatusTimer;
        private bool subscribed;

        public QueryTemplateWindowControl()
        {
            InitializeComponent();
            themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            TryLoadSqlHighlighting();
            SetActionState(null);

            // This window now hosts the former "Query Templates" settings page, so it
            // reports its own save state.
            settingsStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            settingsStatusTimer.Tick += SettingsStatusTimer_Tick;
            SettingsStatusText.Text = LocalizationManager.T("No unsaved changes");
            try
            {
                TemplatesFolderBox.Text = QueryTemplateLibrary.Instance.RootFolder;
            }
            catch
            {
                // The templates folder is shown again as soon as the window is loaded.
            }
        }

        public void ActivateSearch()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }));
        }

        private void Control_Loaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            QueryTemplateLibrary.Instance.Refresh();
            RefreshView();
            ActivateSearch();
        }

        private void Control_Unloaded(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (subscribed) return;
            QueryTemplateLibrary.Instance.Changed += Library_Changed;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            QueryTemplateLibrary.Instance.Changed -= Library_Changed;
            subscribed = false;
        }

        private void Library_Changed(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) RefreshView();
            else Dispatcher.BeginInvoke(new Action(RefreshView));
        }

        private void RefreshView()
        {
            string selectedPath = SelectedTemplate?.FullPath;
            IEnumerable<QueryTemplateItem> query = QueryTemplateLibrary.Instance.Snapshot();
            string search = (SearchBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.SearchText.Contains(search));

            string scope = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
            if (scope == "Favorites") query = query.Where(x => x.IsFavorite);
            if (scope == "Recent") query = query.Where(x => x.LastUsedUtc.HasValue).OrderByDescending(x => x.LastUsedUtc.Value);

            List<QueryTemplateItem> filtered = query.ToList();
            TemplateGrid.ItemsSource = filtered;
            RootFolderText.Text = QueryTemplateLibrary.Instance.RootFolder;

            QueryTemplateItem selection = filtered.FirstOrDefault(x => string.Equals(x.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selection == null) selection = filtered.FirstOrDefault();
            TemplateGrid.SelectedItem = selection;
            if (selection != null) TemplateGrid.ScrollIntoView(selection);

            string status = string.Format(LocalizationManager.T("{0} template(s)"), filtered.Count);
            if (!string.IsNullOrWhiteSpace(QueryTemplateLibrary.Instance.LastError))
                status += "  |  " + QueryTemplateLibrary.Instance.LastError;
            StatusText.Text = status;
        }

        private QueryTemplateItem SelectedTemplate => TemplateGrid.SelectedItem as QueryTemplateItem;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) RefreshView();
        }

        private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) RefreshView();
        }

        private void TemplateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QueryTemplateItem item = SelectedTemplate;
            SetActionState(item);
            if (item == null)
            {
                PreviewEditor.Text = string.Empty;
                SelectedPathText.Text = string.Empty;
                return;
            }

            SelectedPathText.Text = item.RelativePath;
            try
            {
                PreviewEditor.Text = QueryTemplateLibrary.Instance.ReadContent(item);
                PreviewEditor.ScrollToHome();
            }
            catch (Exception ex)
            {
                PreviewEditor.Text = "-- " + LocalizationManager.T("Preview unavailable") + Environment.NewLine + "-- " + ex.Message;
            }
        }

        private void TemplateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedTemplate != null && FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) != null)
                InsertSelected(false);
        }

        private void InsertButton_Click(object sender, RoutedEventArgs e) => InsertSelected(false);
        private void NewQueryButton_Click(object sender, RoutedEventArgs e) => InsertSelected(true);

        private void InsertSelected(bool newQuery)
        {
            QueryTemplateItem item = SelectedTemplate;
            if (item == null) return;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                QueryTemplateInsertionService.InsertFile(item.FullPath, newQuery);
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

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateItem item = SelectedTemplate;
            if (item == null) return;
            QueryTemplateLibrary.Instance.ToggleFavorite(item);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateLibrary.Instance.Refresh();
        }

        private void RefreshTemplatesSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateLibrary.Instance.Refresh();
            SetSettingsStatus(LocalizationManager.T("The list of templates has been updated."));
        }

        private void SelectTemplatesFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = LocalizationManager.T("Select templates folder");
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Directory.Exists(TemplatesFolderBox.Text)
                    ? TemplatesFolderBox.Text
                    : (Directory.Exists(QueryTemplateLibrary.Instance.RootFolder)
                        ? QueryTemplateLibrary.Instance.RootFolder
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                // The folder is only applied when Save is pressed, matching the folder
                // text box behaviour of the former settings page.
                TemplatesFolderBox.Text = dialog.SelectedPath;
                SetSettingsStatus(LocalizationManager.T("No unsaved changes"));
            }
        }

        private void SaveTemplatesFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (!QueryTemplateLibrary.Instance.TrySetRoot(TemplatesFolderBox.Text, true, out string error))
            {
                SetSettingsStatus(error, true);
                LocalizedMessageBox.Show(error, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            TemplatesFolderBox.Text = QueryTemplateLibrary.Instance.RootFolder;
            SetSettingsStatus(LocalizationManager.T("Saved"));
        }

        private async void DownloadScriptsButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string targetPath = SettingsManager.GetTemplatesFolder();
            string tempZipPath = null;

            try
            {
                if (button != null) button.IsEnabled = false;
                SetSettingsStatus(LocalizationManager.T("Downloading..."));

                if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);

                DownloadResult result = await Task.Run(() =>
                {
                    tempZipPath = DownloadGitHubRepoZip(GitHubQueryLibraryZipUrl);
                    ExtractSpecificFolderFromZip(tempZipPath, GitHubQueryLibraryFolderInZip, targetPath,
                        out int addedFiles, out int skippedFiles);
                    return new DownloadResult { Added = addedFiles, Skipped = skippedFiles };
                });

                if (result.Added + result.Skipped == 0)
                    throw new InvalidDataException(LocalizationManager.T("The downloaded archive does not contain the query-library folder."));

                QueryTemplateLibrary.Instance.Refresh();
                string message = LocalizationManager.Format(
                    "MSSQL Tool Query Library has been downloaded. Added: {0}; existing files kept: {1}.",
                    result.Added, result.Skipped);
                SetSettingsStatus(message);
                LocalizedMessageBox.Show(message, "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                string message = LocalizationManager.Format("An error occurred: {0}", ex.Message);
                SetSettingsStatus(message, true);
                LocalizedMessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempZipPath) && File.Exists(tempZipPath)) File.Delete(tempZipPath);
                }
                catch
                {
                    // A leftover temporary archive is harmless.
                }
            }
        }

        private void GitHubRepositoryLink_Click(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(ex.Message, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            e.Handled = true;
        }

        private void SetSettingsStatus(string message, bool keepUntilChanged = false)
        {
            SettingsStatusText.Text = message;
            settingsStatusTimer.Stop();
            if (!keepUntilChanged && IsLoaded)
            {
                settingsStatusTimer.Start();
            }
        }

        private void SettingsStatusTimer_Tick(object sender, EventArgs e)
        {
            settingsStatusTimer.Stop();
            SettingsStatusText.Text = LocalizationManager.T("No unsaved changes");
        }

        private sealed class DownloadResult
        {
            public int Added;
            public int Skipped;
        }

        private static string DownloadGitHubRepoZip(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                // Mimic a browser's User-Agent string.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");

                string tempPath = Path.Combine(Path.GetTempPath(), "MSSQLTool-query-library-" + Guid.NewGuid().ToString("N") + ".zip");
                byte[] data = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                File.WriteAllBytes(tempPath, data);
                return tempPath;
            }
        }

        private static void ExtractSpecificFolderFromZip(string zipPath, string folderPath, string destinationPath, out int added, out int skipped)
        {
            added = 0;
            skipped = 0;
            string destinationRoot = Path.GetFullPath(destinationPath);
            if (!destinationRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                destinationRoot += Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string prefix = folderPath.TrimEnd('/') + "/";
                    if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    string relativePath = entry.FullName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    string path = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                    if (!path.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The downloaded archive contains an invalid path.");

                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(path);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        if (File.Exists(path))
                        {
                            skipped++;
                            continue;
                        }

                        entry.ExtractToFile(path, false);
                        added++;
                    }
                }
            }
        }

        private void CreateTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                string initialContent = QueryTemplateInsertionService.GetCurrentEditorText();
                List<string> categories = QueryTemplateLibrary.Instance.Snapshot()
                    .Select(x => x.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var dialog = new CreateQueryTemplateDialog(initialContent, categories);
                Window owner = Window.GetWindow(this);
                if (owner != null) dialog.Owner = owner;
                if (dialog.ShowDialog() != true) return;

                string root = Path.GetFullPath(QueryTemplateLibrary.Instance.RootFolder);
                string folder = string.IsNullOrEmpty(dialog.Category) ? root : Path.Combine(root, dialog.Category);
                string target = Path.GetFullPath(Path.Combine(folder, dialog.TemplateName + ".sql"));
                string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The template path is outside the configured templates folder.");

                if (File.Exists(target))
                {
                    MessageBoxResult overwrite = LocalizedMessageBox.Show(
                        "A template with this name already exists. Replace it?", "Query Templates",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (overwrite != MessageBoxResult.Yes) return;
                }

                Directory.CreateDirectory(folder);
                File.WriteAllText(target, dialog.TemplateContent, new UTF8Encoding(false));
                ScopeBox.SelectedIndex = 0;
                SearchBox.Text = dialog.TemplateName;
                QueryTemplateLibrary.Instance.Refresh();
                StatusText.Text = LocalizationManager.T("Template created: ") + target;
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(LocalizationManager.T("Could not create the query template:") + " " + ex.Message,
                    "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MigrateFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = LocalizationManager.T("Select the new templates folder");
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                if (!QueryTemplateLibrary.Instance.TryMigrateTo(dialog.SelectedPath, out QueryTemplateMigrationResult result, out string error))
                {
                    LocalizedMessageBox.Show(error, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                RootFolderText.Text = result.DestinationFolder;
                LocalizedMessageBox.Show(
                    string.Format(LocalizationManager.T("Migrated {0} template(s) to the new folder. The original folder was kept at:\n{1}"),
                        result.CopiedCount, result.SourceFolder),
                    "Query Templates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = LocalizationManager.T("Select templates folder");
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Directory.Exists(QueryTemplateLibrary.Instance.RootFolder)
                    ? QueryTemplateLibrary.Instance.RootFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                if (!QueryTemplateLibrary.Instance.TrySetRoot(dialog.SelectedPath, true, out string error))
                    LocalizedMessageBox.Show(error, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(QueryTemplateLibrary.Instance.RootFolder, null);
        }

        private void ShowFileButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateItem item = SelectedTemplate;
            if (item != null) OpenFolder(Path.GetDirectoryName(item.FullPath), item.FullPath);
        }

        private static void OpenFolder(string folder, string selectFile)
        {
            try
            {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Templates folder is currently unavailable: " + folder);
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.Arguments = string.IsNullOrEmpty(selectFile) ? Quote(folder) : "/select," + Quote(selectFile);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(ex.Message, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || SelectedTemplate == null) return;
            if (!SearchBox.IsKeyboardFocusWithin && !TemplateGrid.IsKeyboardFocusWithin) return;
            e.Handled = true;
            InsertSelected((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
        }

        private void SetActionState(QueryTemplateItem item)
        {
            bool enabled = item != null;
            InsertButton.IsEnabled = enabled;
            NewQueryButton.IsEnabled = enabled;
            FavoriteButton.IsEnabled = enabled;
            ShowFileButton.IsEnabled = enabled;
            FavoriteButton.Content = item?.IsFavorite == true
                ? LocalizationManager.T("Remove favorite")
                : LocalizationManager.T("Add favorite");
        }

        private void TryLoadSqlHighlighting()
        {
            try
            {
                using (var stream = typeof(QueryTemplateWindowControl).Assembly.GetManifestResourceStream("MSSQLTool.QuickSearch.sql.xshd"))
                using (var reader = new XmlTextReader(stream))
                    PreviewEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch
            {
                // Preview remains useful as plain text if the optional highlighter cannot load.
            }
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "") + "\"";

        private static T FindVisualParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }
    }
}
