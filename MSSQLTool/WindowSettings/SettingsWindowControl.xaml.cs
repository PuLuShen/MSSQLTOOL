namespace MSSQLTool
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Navigation;
    using System.Windows.Threading;
    using static MSSQLTool.MSSQLToolPackage;

    /// <summary>
    /// Interaction logic for SettingsWindowControl.
    /// </summary>
    public partial class SettingsWindowControl : UserControl
    {
        private const string FormatStatusSaved = "Saved";
        private const string FormatStatusUnsaved = "Unsaved changes";
        private const string FormatStatusClean = "No unsaved changes";

        private readonly ToolWindowThemeController _themeController;
        private bool updateResultSubscribed;
        private ObservableCollection<SettingsManager.ConnectionColorRule> _connectionColorRules;
        private SettingsManager.ConnectionColorRule _editingConnectionColorRule;
        private readonly Dictionary<TabItem, Visibility> _defaultTabVisibilities = new Dictionary<TabItem, Visibility>();

        /// <summary>The formatting profile currently edited by the Code Format page.</summary>
        private FormatterOptions formatOptions = new FormatterOptions();
        private readonly DispatcherTimer formatPreviewTimer;
        private readonly DispatcherTimer formatSaveStatusTimer;

        /// <summary>Set while pushing model values into the controls, so the change handlers stay quiet.</summary>
        private bool suppressFormatEvents;

        private string tsqlFormatExample = @"while (1=0) 
begin 
select top 10
    c.CustomerID, getDate(),
    CASE WHEN o.TotalAmount > 1000 THEN 'High' ELSE 'Low' END AS OrderSize
FROM Customers c
JOIN Orders o ON c.CustomerID = o.CustomerID CROSS JOIN Regions r
WHERE c.IsActive = 1;

SELECT dbo.func(p.ProductID), p.ProductName FROM Products p; EXEC dbo.test @a = 0, @b = 1;
end
if 1=0 begin select 1; declare @a int, @b varchar(10) = ''
end
go
create procedure dbo.test @a int, @b int = 0
as select 1;
";
        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsWindowControl"/> class.
        /// </summary>
        public SettingsWindowControl()
        {
            this.InitializeComponent();

            _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>();
            ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            UpdateConnectionColorRuleButtons();
            SetConnectionColorRulesDirty(false);

            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);

            this.Loaded += UserControl_Loaded;
            this.Unloaded += UserControl_Unloaded;

            // The preview is refreshed with a short delay so typing in the threshold box does not
            // re-run the formatter on every keystroke.
            formatPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            formatPreviewTimer.Tick += (s, e) =>
            {
                formatPreviewTimer.Stop();
                UpdateFormattedQueryPreview();
            };

            // "Saved" is shown for a few seconds, then the page returns to its idle caption.
            formatSaveStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            formatSaveStatusTimer.Tick += (s, e) =>
            {
                formatSaveStatusTimer.Stop();
                SetFormatSaveStatus(FormatStatusClean);
            };

            // The source query box is part of the page markup, so its change notification is
            // hooked up here instead of in the XAML.
            SourceQueryPreview.TextChanged += (s, e) => ScheduleFormatPreviewUpdate();

            formatOptions = SettingsManager.GetFormatterOptions();
            SourceQueryPreview.Text = tsqlFormatExample;
            RefreshFormatUiFromOptions();
            UpdateFormattedQueryPreview();
        }

        private void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            SubscribeToUpdateResultChanges();
            LoadSavedSettings();
        }

        private void UserControl_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            UnsubscribeFromUpdateResultChanges();
            formatPreviewTimer?.Stop();
            formatSaveStatusTimer?.Stop();
        }

        private void SubscribeToUpdateResultChanges()
        {
            if (updateResultSubscribed)
            {
                return;
            }

            UpdateChecker.LastUpdateResultChanged += UpdateChecker_LastUpdateResultChanged;
            updateResultSubscribed = true;
        }

        private void UnsubscribeFromUpdateResultChanges()
        {
            if (!updateResultSubscribed)
            {
                return;
            }

            UpdateChecker.LastUpdateResultChanged -= UpdateChecker_LastUpdateResultChanged;
            updateResultSubscribed = false;
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void Button_ApplyLanguage_Click(object sender, RoutedEventArgs e)
        {
            string language = UiLanguageSelector.SelectedValue as string;
            LocalizationManager.SetLanguage(language);
            LocalizationManager.Apply(this);
        }

        private void LoadSavedSettings()
        {
            try
            {

                UiLanguageSelector.SelectedValue = SettingsManager.GetUiLanguage();
                ScriptObjectShortcut.Text = SettingsManager.GetScriptObjectShortcut();

                var completionSettings = SettingsManager.GetSqlCompletionSettings();
                UseSqlCompletion.IsChecked = completionSettings.enabled;
                AutomaticSqlCompletion.IsChecked = completionSettings.automaticPopup;
                CompletionTrustServerCertificate.IsChecked = completionSettings.trustServerCertificate;
                CompletionSquareBrackets.IsChecked = completionSettings.useSquareBrackets;
                CompletionUsageLearning.IsChecked = completionSettings.learnFromUsage;
                CompletionAutoRefreshMetadata.IsChecked = completionSettings.autoRefreshMetadata;
                CompletionShowObjectDetails.IsChecked = completionSettings.showObjectDetails;
                CompletionColumnPicker.IsChecked = completionSettings.enableColumnPicker;
                CompletionAutoAliases.IsChecked = completionSettings.autoAddAliases;
                CompletionAliasPrefixes.Text = completionSettings.aliasPrefixToIgnore;
                CompletionCustomAliases.Text = completionSettings.customAliases;
                CompletionJoinRules.Text = completionSettings.joinColumnRules;
                CompletionDelay.Text = completionSettings.delayMilliseconds.ToString();
                CompletionMaximumItems.Text = completionSettings.maximumItems.ToString();

                // Code Format page: the whole profile is loaded from a single FormatterOptions instance.
                formatOptions = SettingsManager.GetFormatterOptions();
                RefreshFormatUiFromOptions();
                SetFormatSaveStatus(FormatStatusClean);
                UpdateFormattedQueryPreview();

                OpenAiApiKey.Password = SettingsManager.GetOpenAiApiKey();

                EnableUpdateChecks.IsChecked = SettingsManager.GetEnableUpdateChecks();
                UpdateUpdateStatus();

                LoadConnectionColorRules();

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred while loading settings");

                string msg = $"Error message: {ex.Message} \nInnerException: {ex.InnerException}";
                LocalizedMessageBox.Show(msg, "Error");
            }

        }

        private void Button_SaveSqlCompletion_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(CompletionDelay.Text, out int delay) || delay < 0 || delay > 1000
                || !int.TryParse(CompletionMaximumItems.Text, out int maximumItems) || maximumItems < 20 || maximumItems > 1000)
            {
                LocalizedMessageBox.Show("Popup delay must be between 0 and 1000 ms, and maximum matches between 20 and 1000.",
                    "SQL Completion", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var completionSettings = SettingsManager.GetSqlCompletionSettings();
            completionSettings.enabled = UseSqlCompletion.IsChecked.GetValueOrDefault();
            completionSettings.automaticPopup = AutomaticSqlCompletion.IsChecked.GetValueOrDefault();
            completionSettings.trustServerCertificate = CompletionTrustServerCertificate.IsChecked.GetValueOrDefault();
            completionSettings.useSquareBrackets = CompletionSquareBrackets.IsChecked.GetValueOrDefault();
            completionSettings.learnFromUsage = CompletionUsageLearning.IsChecked.GetValueOrDefault();
            completionSettings.autoRefreshMetadata = CompletionAutoRefreshMetadata.IsChecked.GetValueOrDefault();
            completionSettings.showObjectDetails = CompletionShowObjectDetails.IsChecked.GetValueOrDefault();
            completionSettings.enableColumnPicker = CompletionColumnPicker.IsChecked.GetValueOrDefault();
            completionSettings.autoAddAliases = CompletionAutoAliases.IsChecked.GetValueOrDefault();
            completionSettings.aliasPrefixToIgnore = CompletionAliasPrefixes.Text ?? string.Empty;
            completionSettings.customAliases = CompletionCustomAliases.Text ?? string.Empty;
            completionSettings.joinColumnRules = CompletionJoinRules.Text ?? string.Empty;
            completionSettings.delayMilliseconds = delay;
            completionSettings.maximumItems = maximumItems;
            if (!SettingsManager.SaveSqlCompletionSettings(completionSettings))
            {
                LocalizedMessageBox.Show("The SQL completion setting could not be saved.",
                    "SQL Completion", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Completion.SqlMetadataCache.InvalidateAll();

            SavedMessage();
        }

        private void SavedMessage()
        {
            LocalizedMessageBox.Show(
                string.Format(System.Globalization.CultureInfo.CurrentUICulture, "The change has been saved", this.ToString()),
                "Setting saved");
        }

        private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterSettingsTabs();
        }

        private void FilterSettingsTabs()
        {
            if (SettingsTabs == null)
            {
                return;
            }

            string filter = (SettingsSearchBox.Text ?? string.Empty).Trim();
            TabItem firstVisible = null;
            TabItem selected = SettingsTabs.SelectedItem as TabItem;

            foreach (object item in SettingsTabs.Items)
            {
                if (!(item is TabItem tab))
                {
                    continue;
                }

                if (!_defaultTabVisibilities.ContainsKey(tab))
                {
                    _defaultTabVisibilities[tab] = tab.Visibility;
                }

                bool matches = filter.Length == 0
                    || (tab.Header as string)?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || (tab.Tag as string)?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                tab.Visibility = matches && _defaultTabVisibilities[tab] == Visibility.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (tab.Visibility == Visibility.Visible && firstVisible == null)
                {
                    firstVisible = tab;
                }
            }

            if (selected != null && selected.Visibility != Visibility.Visible && firstVisible != null)
            {
                SettingsTabs.SelectedItem = firstVisible;
            }
        }

        private void Button_RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (!(SettingsTabs.SelectedItem is TabItem selectedTab))
            {
                return;
            }

            // The tab header is the page identifier; the optional Tag is honoured as well so a
            // page may override the header it is matched by.
            string page = (selectedTab.Tag as string) ?? (selectedTab.Header as string);
            switch (page)
            {
                case "SQL Completion":
                    RestoreSqlCompletionDefaults();
                    break;
                case "Code Format":
                    RestoreCodeFormatDefaults();
                    break;
                default:
                    LocalizedMessageBox.Show("Default values are not available for this page.",
                        "Restore defaults", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
            }

            SavedMessage();
        }

        private void RestoreSqlCompletionDefaults()
        {
            var defaults = new SettingsManager.SqlCompletionSettings();
            UseSqlCompletion.IsChecked = defaults.enabled;
            AutomaticSqlCompletion.IsChecked = defaults.automaticPopup;
            CompletionTrustServerCertificate.IsChecked = defaults.trustServerCertificate;
            CompletionSquareBrackets.IsChecked = defaults.useSquareBrackets;
            CompletionUsageLearning.IsChecked = defaults.learnFromUsage;
            CompletionAutoRefreshMetadata.IsChecked = defaults.autoRefreshMetadata;
            CompletionShowObjectDetails.IsChecked = defaults.showObjectDetails;
            CompletionColumnPicker.IsChecked = defaults.enableColumnPicker;
            CompletionAutoAliases.IsChecked = defaults.autoAddAliases;
            CompletionAliasPrefixes.Text = defaults.aliasPrefixToIgnore;
            CompletionCustomAliases.Text = defaults.customAliases;
            CompletionJoinRules.Text = defaults.joinColumnRules;
            CompletionDelay.Text = defaults.delayMilliseconds.ToString();
            CompletionMaximumItems.Text = defaults.maximumItems.ToString();
            SettingsManager.SaveSqlCompletionSettings(defaults);
            Completion.SqlMetadataCache.InvalidateAll();
        }

        private void RestoreCodeFormatDefaults()
        {
            formatOptions.ApplyPreset(FormatPreset.Standard);
            RefreshFormatUiFromOptions();
            UpdateFormattedQueryPreview();
            SettingsManager.SaveFormatterOptions(formatOptions);
            SetFormatSaveStatus(FormatStatusClean);
        }

        private void buttonWikiPage_Click(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void button_SaveUpdateSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveEnableUpdateChecks(EnableUpdateChecks.IsChecked.GetValueOrDefault(true));
            SavedMessage();
        }

        private void button_CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            UpdateChecker.CheckNow(MSSQLToolPackage.PackageInstance, ignoreSettings: true);
            UpdateUpdateStatus();
        }

        private void UpdateChecker_LastUpdateResultChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(UpdateUpdateStatus));
            }
            catch
            {
            }
        }

        private void UpdateUpdateStatus()
        {
            if (UpdateCheckStatus != null)
            {
                UpdateCheckStatus.Text = UpdateChecker.LastUpdateResult;
            }
        }

        private void Hyperlink_RequestNavigateFormatQueryWiki(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void Button_SaveOpenAi_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveOpenAiApiKey(OpenAiApiKey.Password);

            SavedMessage();
        }

        private async void Button_RefreshCompletionMetadata_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            try
            {
                var connection = ScriptFactoryAccess.GetCurrentOrLastConnectionInfo();
                if (connection == null || string.IsNullOrWhiteSpace(connection.FullConnectionString))
                    throw new InvalidOperationException("Open a connected SQL editor before refreshing metadata.");
                var snapshot = await Completion.SqlMetadataCache.RefreshAsync(connection, System.Threading.CancellationToken.None);
                string detail = snapshot.StatusText + Environment.NewLine
                    + $"Objects: {snapshot.Objects.Count}; procedures: {snapshot.Objects.Count(x => x.Kind == Completion.CompletionItemKind.Procedure)}; tables/views: {snapshot.Objects.Count(x => x.Kind == Completion.CompletionItemKind.Table || x.Kind == Completion.CompletionItemKind.View)}";
                if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage)) detail += Environment.NewLine + snapshot.ErrorMessage;
                CompletionMetadataStatus.Text = detail;
                LocalizedMessageBox.Show(detail, "SQL Completion Metadata");
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Completion Metadata", "Manual refresh failed", ex);
                CompletionMetadataStatus.Text = "Refresh failed: " + UnwrapExceptionMessage(ex);
                LocalizedMessageBox.Show("Refresh failed: " + UnwrapExceptionMessage(ex), "SQL Completion Metadata");
            }
            finally { if (button != null) button.IsEnabled = true; }
        }

        private static string UnwrapExceptionMessage(Exception exception)
        {
            Exception current = exception;
            while (current?.InnerException != null) current = current.InnerException;
            return current?.Message ?? string.Empty;
        }

        private void Button_RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

        private void Button_CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            RefreshDiagnostics();
            if (!string.IsNullOrWhiteSpace(DiagnosticsText.Text)) Clipboard.SetText(DiagnosticsText.Text);
        }

        private void RefreshDiagnostics()
        {
            var report = new StringBuilder();
            report.AppendLine("MSSQL Tool diagnostics");
            report.AppendLine("Assembly: " + typeof(SettingsWindowControl).Assembly.GetName().Version);
            var connection = ScriptFactoryAccess.GetCurrentOrLastConnectionInfo();
            report.AppendLine("Connection: " + (connection?.DisplayName ?? "<none>"));
            foreach (FeatureDiagnostic item in FeatureDiagnostics.Snapshot())
                report.AppendLine($"{item.TimestampUtc:u} [{item.Feature}] {item.Message}{(item.Exception == null ? string.Empty : " | " + item.Exception.Message)}");
            DiagnosticsText.Text = report.ToString();
        }

        private void Button_ApplyScriptObjectShortcut_Click(object sender, RoutedEventArgs e)
        {
            string shortcut = NormalizeShortcut(ScriptObjectShortcut.Text);
            if (shortcut == null)
            {
                LocalizedMessageBox.Show("Enter a shortcut such as F12, Ctrl+F12, or Ctrl+Shift+O. Enter None to remove it.",
                    "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ShortcutManager.ApplyScriptObjectShortcut(shortcut, out string error))
            {
                LocalizedMessageBox.Show("The shortcut could not be applied: " + error,
                    "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!SettingsManager.SaveScriptObjectShortcut(shortcut))
            {
                LocalizedMessageBox.Show("The shortcut was applied, but the setting could not be saved.",
                    "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ScriptObjectShortcut.Text = string.IsNullOrEmpty(shortcut) ? "None" : shortcut;
            SavedMessage();
        }

        private static string NormalizeShortcut(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string[] parts = value.Replace(" ", string.Empty).Split('+');
            if (parts.Length == 0 || parts.Length > 4)
                return null;

            var modifiers = new System.Collections.Generic.List<string>();
            string key = null;
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
                    AddShortcutModifier(modifiers, "Ctrl");
                else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
                    AddShortcutModifier(modifiers, "Shift");
                else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
                    AddShortcutModifier(modifiers, "Alt");
                else if (key == null && IsValidShortcutKey(part))
                    key = part.ToUpperInvariant();
                else
                    return null;
            }

            if (key == null)
                return null;
            modifiers.Add(key);
            return string.Join("+", modifiers);
        }

        private static void AddShortcutModifier(System.Collections.Generic.List<string> modifiers, string modifier)
        {
            if (!modifiers.Contains(modifier))
                modifiers.Add(modifier);
        }

        private static bool IsValidShortcutKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (key.Length == 1 && char.IsLetterOrDigit(key[0])) return true;
            if (key.Length > 1 && char.ToUpperInvariant(key[0]) == 'F' && int.TryParse(key.Substring(1), out int functionKey))
                return functionKey >= 1 && functionKey <= 24;
            return string.Equals(key, "Insert", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Delete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Home", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "End", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------
        // Code Format page
        // ---------------------------------------------------------------------------------

        /// <summary>Pushes <see cref="formatOptions"/> into the Code Format controls.</summary>
        private void RefreshFormatUiFromOptions()
        {
            if (formatOptions == null)
                return;

            bool previous = suppressFormatEvents;
            suppressFormatEvents = true;
            try
            {
                SelectFormatComboByTag(FormatPresetSelector, formatOptions.preset);
                SelectFormatComboByTag(FormatIndentSizeSelector, formatOptions.indent.indentSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SelectFormatComboByTag(FormatKeywordCasingSelector, formatOptions.casing.keywords.ToString());
                SelectFormatComboByTag(FormatFunctionCasingSelector, formatOptions.casing.functions.ToString());
                SelectFormatComboByTag(FormatDataTypeCasingSelector, formatOptions.casing.dataTypes.ToString());
                SelectFormatComboByTag(FormatIdentifierCasingSelector, formatOptions.casing.identifiers.ToString());

                FormatAlignClauseBodies.IsChecked = formatOptions.alignClauseBodies;
                FormatIncludeSemicolons.IsChecked = formatOptions.includeSemicolons;
                FormatKeepShortQuerySingleLine.IsChecked = formatOptions.compact.keepShortQuerySingleLine;
                FormatStackSelectColumns.IsChecked = formatOptions.select.stackSelectColumns;
                FormatNewLineBeforeJoin.IsChecked = formatOptions.lineBreaks.newLineBeforeJoin || formatOptions.select.newLineBeforeJoin;
                FormatNewLinePerCondition.IsChecked = formatOptions.lineBreaks.newLinePerCondition;
                FormatIndentSubquery.IsChecked = formatOptions.indent.indentSubquery || formatOptions.subquery.indentSubquery;
                FormatIndentCase.IsChecked = formatOptions.indent.indentCase;
                FormatStatementPerLine.IsChecked = formatOptions.block.statementPerLine;

                if (FormatSingleLineThreshold != null)
                    FormatSingleLineThreshold.Text = formatOptions.compact.singleLineThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                suppressFormatEvents = previous;
            }
        }

        /// <summary>Reads the casing / indent selectors back into <see cref="formatOptions"/>.</summary>
        private void ApplyFormatSelectorsFromUi()
        {
            if (int.TryParse(GetSelectedComboTag(FormatIndentSizeSelector), out int indentSize))
                formatOptions.indent.indentSize = Math.Min(Math.Max(indentSize, 1), 16);

            if (Enum.TryParse(GetSelectedComboTag(FormatKeywordCasingSelector), true, out TokenCasing keywords))
                formatOptions.casing.keywords = keywords;
            if (Enum.TryParse(GetSelectedComboTag(FormatFunctionCasingSelector), true, out TokenCasing functions))
                formatOptions.casing.functions = functions;
            if (Enum.TryParse(GetSelectedComboTag(FormatDataTypeCasingSelector), true, out TokenCasing dataTypes))
                formatOptions.casing.dataTypes = dataTypes;
            if (Enum.TryParse(GetSelectedComboTag(FormatIdentifierCasingSelector), true, out TokenCasing identifiers))
                formatOptions.casing.identifiers = identifiers;
        }

        /// <summary>Reads the check boxes back into <see cref="formatOptions"/>.</summary>
        private void ApplyFormatCheckBoxesFromUi()
        {
            formatOptions.alignClauseBodies = FormatAlignClauseBodies.IsChecked == true;
            formatOptions.includeSemicolons = FormatIncludeSemicolons.IsChecked == true;
            formatOptions.compact.keepShortQuerySingleLine = FormatKeepShortQuerySingleLine.IsChecked == true;
            formatOptions.select.stackSelectColumns = FormatStackSelectColumns.IsChecked == true;
            formatOptions.lineBreaks.newLinePerSelectColumn = FormatStackSelectColumns.IsChecked == true;
            formatOptions.lineBreaks.newLineBeforeJoin = FormatNewLineBeforeJoin.IsChecked == true;
            formatOptions.select.newLineBeforeJoin = FormatNewLineBeforeJoin.IsChecked == true;
            formatOptions.lineBreaks.newLinePerCondition = FormatNewLinePerCondition.IsChecked == true;
            formatOptions.select.indentOnCondition = FormatNewLinePerCondition.IsChecked == true;
            formatOptions.indent.indentSubquery = FormatIndentSubquery.IsChecked == true;
            formatOptions.subquery.indentSubquery = FormatIndentSubquery.IsChecked == true;
            formatOptions.indent.indentCase = FormatIndentCase.IsChecked == true;
            formatOptions.block.statementPerLine = FormatStatementPerLine.IsChecked == true;
        }

        /// <summary>Any manual change makes the edited profile a custom one.</summary>
        private void MarkFormatOptionsAsCustom()
        {
            if (formatOptions == null)
                return;

            formatOptions.preset = nameof(FormatPreset.Custom);

            bool previous = suppressFormatEvents;
            suppressFormatEvents = true;
            try
            {
                SelectFormatComboByTag(FormatPresetSelector, nameof(FormatPreset.Custom));
            }
            finally
            {
                suppressFormatEvents = previous;
            }
        }

        private void SetFormatSaveStatus(string status)
        {
            if (FormatSaveStatus != null)
                FormatSaveStatus.Text = status;
        }

        private void ScheduleFormatPreviewUpdate()
        {
            if (formatPreviewTimer == null)
            {
                UpdateFormattedQueryPreview();
                return;
            }

            formatPreviewTimer.Stop();
            formatPreviewTimer.Start();
        }

        private void UpdateFormattedQueryPreview()
        {
            if (FormattedQueryPreview == null || formatOptions == null)
                return;

            string source = SourceQueryPreview?.Text;
            if (string.IsNullOrWhiteSpace(source))
                source = tsqlFormatExample;

            try
            {
                FormattedQueryPreview.Text = TSqlFormatter.FormatCode(source, formatOptions);
            }
            catch (TSqlFormatException ex)
            {
                // A syntax error in the sample must never break the settings page.
                FormattedQueryPreview.Text = ex.Message;
            }
            catch (Exception ex)
            {
                FormattedQueryPreview.Text = ex.Message;
            }
        }

        private static string GetSelectedComboTag(ComboBox combo)
        {
            return (combo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        }

        private static void SelectFormatComboByTag(ComboBox combo, string tag)
        {
            if (combo == null)
                return;

            string wanted = tag ?? string.Empty;
            foreach (object entry in combo.Items)
            {
                if (entry is ComboBoxItem item
                    && string.Equals(item.Tag?.ToString() ?? string.Empty, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void FormatPresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressFormatEvents || formatOptions == null)
                return;

            string tag = GetSelectedComboTag(FormatPresetSelector);
            if (!Enum.TryParse(tag, true, out FormatPreset preset))
                return;

            if (preset == FormatPreset.Custom)
                formatOptions.preset = nameof(FormatPreset.Custom);
            else
                formatOptions.ApplyPreset(preset);

            RefreshFormatUiFromOptions();
            ScheduleFormatPreviewUpdate();
            SetFormatSaveStatus(FormatStatusUnsaved);
        }

        private void FormatSetting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (suppressFormatEvents || formatOptions == null)
                return;

            ApplyFormatSelectorsFromUi();
            MarkFormatOptionsAsCustom();
            ScheduleFormatPreviewUpdate();
            SetFormatSaveStatus(FormatStatusUnsaved);
        }

        private void formatSetting_Checked(object sender, RoutedEventArgs e)
        {
            if (suppressFormatEvents || formatOptions == null)
                return;

            ApplyFormatCheckBoxesFromUi();
            MarkFormatOptionsAsCustom();
            ScheduleFormatPreviewUpdate();
            SetFormatSaveStatus(FormatStatusUnsaved);
        }

        private void formatSetting_Unchecked(object sender, RoutedEventArgs e)
        {
            if (suppressFormatEvents || formatOptions == null)
                return;

            ApplyFormatCheckBoxesFromUi();
            MarkFormatOptionsAsCustom();
            ScheduleFormatPreviewUpdate();
            SetFormatSaveStatus(FormatStatusUnsaved);
        }

        private void formatSetting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressFormatEvents || formatOptions == null)
                return;

            if (int.TryParse(FormatSingleLineThreshold.Text, out int threshold))
                formatOptions.compact.singleLineThreshold = Math.Min(Math.Max(threshold, 1), 10000);

            MarkFormatOptionsAsCustom();
            ScheduleFormatPreviewUpdate();
            SetFormatSaveStatus(FormatStatusUnsaved);
        }

        private void Button_SaveApplyAdditionalFormat_Click(object sender, RoutedEventArgs e)
        {
            if (formatOptions == null)
                return;

            if (!SettingsManager.SaveFormatterOptions(formatOptions))
            {
                LocalizedMessageBox.Show("The formatting settings could not be saved. Please try again.",
                    "Code Format", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetFormatSaveStatus(FormatStatusSaved);
            if (formatSaveStatusTimer != null)
            {
                formatSaveStatusTimer.Stop();
                formatSaveStatusTimer.Start();
            }
        }

        private void Button_OpenAdvancedFormatOptions_Click(object sender, RoutedEventArgs e)
        {
            if (formatOptions == null)
                return;

            try
            {
                var dialog = new FormatOptionsDialog(formatOptions.Clone());
                Window owner = Window.GetWindow(this);
                if (owner != null)
                    dialog.Owner = owner;

                if (dialog.ShowDialog() != true)
                    return;

                formatOptions.CopyFrom(dialog.Settings);

                if (dialog.SaveAsDefault && !SettingsManager.SaveFormatterOptions(formatOptions))
                {
                    LocalizedMessageBox.Show("The formatting settings could not be saved. Please try again.",
                        "Code Format", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                RefreshFormatUiFromOptions();
                UpdateFormattedQueryPreview();

                if (dialog.SaveAsDefault)
                {
                    SetFormatSaveStatus(FormatStatusSaved);
                    if (formatSaveStatusTimer != null)
                    {
                        formatSaveStatusTimer.Stop();
                        formatSaveStatusTimer.Start();
                    }
                }
                else
                {
                    SetFormatSaveStatus(FormatStatusUnsaved);
                }
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("The advanced formatting options could not be opened: " + UnwrapExceptionMessage(ex),
                    "Code Format", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_ImportFormatProfile_Click(object sender, RoutedEventArgs e)
        {
            if (formatOptions == null)
                return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import format profile",
                Filter = "MSSQL Tool format profile (*.mssqltoolformat.json)|*.mssqltoolformat.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = FormatterOptions.ProfileExtension.TrimStart('.'),
                CheckFileExists = true
            };

            Window owner = Window.GetWindow(this);
            if (owner != null ? dialog.ShowDialog(owner) != true : dialog.ShowDialog() != true)
                return;

            try
            {
                FormatterOptions imported = FormatterOptions.ImportFromFile(dialog.FileName);
                formatOptions.CopyFrom(imported);
                RefreshFormatUiFromOptions();
                UpdateFormattedQueryPreview();
                SetFormatSaveStatus(FormatStatusUnsaved);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("The profile could not be imported: " + UnwrapExceptionMessage(ex),
                    "Code Format", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_ExportFormatProfile_Click(object sender, RoutedEventArgs e)
        {
            if (formatOptions == null)
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export format profile",
                Filter = "MSSQL Tool format profile (*.mssqltoolformat.json)|*.mssqltoolformat.json|JSON files (*.json)|*.json",
                DefaultExt = "mssqltoolformat.json",
                FileName = "MSSQLToolFormat" + FormatterOptions.ProfileExtension,
                AddExtension = true,
                OverwritePrompt = true
            };

            Window owner = Window.GetWindow(this);
            if (owner != null ? dialog.ShowDialog(owner) != true : dialog.ShowDialog() != true)
                return;

            try
            {
                formatOptions.ExportToFile(dialog.FileName);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("The profile could not be exported: " + UnwrapExceptionMessage(ex),
                    "Code Format", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_RestoreFormatDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (formatOptions == null)
                return;

            formatOptions.ApplyPreset(FormatPreset.Standard);
            RefreshFormatUiFromOptions();
            UpdateFormattedQueryPreview();
            SetFormatSaveStatus(FormatStatusUnsaved);
        }

        // ---------------------------------------------------------------------------------
        // Connection Colors page
        // ---------------------------------------------------------------------------------

        private void LoadConnectionColorRules()
        {
            _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>(SettingsManager.GetConnectionColorRules());
            ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            CancelConnectionColorRuleEdit();
            SetConnectionColorRulesDirty(false);
            UpdateConnectionColorRuleButtons();
        }

        private void EnsureConnectionColorRulesLoaded()
        {
            if (_connectionColorRules == null)
            {
                _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>();
                ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            }
        }

        private string PickColor(string currentHex)
        {
            var dialog = new System.Windows.Forms.ColorDialog();
            try
            {
                dialog.Color = System.Drawing.ColorTranslator.FromHtml(currentHex);
            }
            catch { }
            dialog.FullOpen = true;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return System.Drawing.ColorTranslator.ToHtml(dialog.Color);
            }
            return null;
        }

        private void NewRuleColorPreview_Click(object sender, RoutedEventArgs e)
        {
            var currentBrush = NewRuleColorPreview.Background as SolidColorBrush;
            string currentHex = currentBrush != null
                ? string.Format("#{0:X2}{1:X2}{2:X2}", currentBrush.Color.R, currentBrush.Color.G, currentBrush.Color.B)
                : "#FF4444";

            string picked = PickColor(currentHex);
            if (picked != null)
            {
                try
                {
                    var color = System.Drawing.ColorTranslator.FromHtml(picked);
                    NewRuleColorPreview.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                }
                catch { }
            }
        }

        private void NewRuleColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            NewRuleColorPreview_Click(sender, (RoutedEventArgs)e);
        }

        private void RuleColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SettingsManager.ConnectionColorRule rule)
            {
                string picked = PickColor(rule.StatusBarColor);
                if (picked != null)
                {
                    rule.StatusBarColor = picked;
                    ConnectionColorRulesListView.Items.Refresh();
                    SetConnectionColorRulesDirty(true);
                }
            }
        }

        private void ButtonAddColorRule_Click(object sender, RoutedEventArgs e)
        {
            EnsureConnectionColorRulesLoaded();

            string serverPattern = NewRuleServerPattern.Text?.Trim();
            string databasePattern = NewRuleDatabasePattern.Text?.Trim();

            if (string.IsNullOrEmpty(serverPattern) && string.IsNullOrEmpty(databasePattern))
            {
                LocalizedMessageBox.Show("Fill in at least the server name or the database name.", "Connection Colors");
                return;
            }

            var brush = NewRuleColorPreview.Background as SolidColorBrush;
            string hex = "#FF4444";
            if (brush != null)
            {
                hex = string.Format("#{0:X2}{1:X2}{2:X2}", brush.Color.R, brush.Color.G, brush.Color.B);
            }

            if (_editingConnectionColorRule != null)
            {
                _editingConnectionColorRule.ServerNamePattern = serverPattern ?? string.Empty;
                _editingConnectionColorRule.DatabaseNamePattern = databasePattern ?? string.Empty;
                _editingConnectionColorRule.StatusBarColor = hex;
                ConnectionColorRulesListView.Items.Refresh();
                ConnectionColorRulesListView.SelectedItem = _editingConnectionColorRule;
            }
            else
            {
                var newRule = new SettingsManager.ConnectionColorRule
                {
                    ServerNamePattern = serverPattern ?? string.Empty,
                    DatabaseNamePattern = databasePattern ?? string.Empty,
                    StatusBarColor = hex,
                    IsEnabled = true
                };
                _connectionColorRules.Add(newRule);
                ConnectionColorRulesListView.SelectedItem = newRule;
            }

            SetConnectionColorRulesDirty(true);
            CancelConnectionColorRuleEdit();
            UpdateConnectionColorRuleButtons();
        }

        private void ButtonEditColorRule_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem is SettingsManager.ConnectionColorRule selectedRule)
            {
                NewRuleServerPattern.Text = selectedRule.ServerNamePattern;
                NewRuleDatabasePattern.Text = selectedRule.DatabaseNamePattern;

                try
                {
                    var color = System.Drawing.ColorTranslator.FromHtml(selectedRule.StatusBarColor);
                    NewRuleColorPreview.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                }
                catch { }

                _editingConnectionColorRule = selectedRule;
                ConnectionColorRuleEditor.Header = LocalizationManager.T("Edit rule");
                button_CommitColorRule.Content = LocalizationManager.T("Save changes");
                button_CancelColorRuleEdit.Visibility = Visibility.Visible;
                NewRuleServerPattern.Focus();
            }
        }

        private void ButtonRemoveColorRule_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem is SettingsManager.ConnectionColorRule selectedRule)
            {
                _connectionColorRules.Remove(selectedRule);
                SetConnectionColorRulesDirty(true);
                CancelConnectionColorRuleEdit();
                UpdateConnectionColorRuleButtons();
            }
        }

        private void ButtonCancelColorRuleEdit_Click(object sender, RoutedEventArgs e) => CancelConnectionColorRuleEdit();

        private void CancelConnectionColorRuleEdit()
        {
            _editingConnectionColorRule = null;
            if (ConnectionColorRuleEditor == null)
                return;

            ConnectionColorRuleEditor.Header = LocalizationManager.T("Add new rule");
            button_CommitColorRule.Content = LocalizationManager.T("+ Add");
            button_CancelColorRuleEdit.Visibility = Visibility.Collapsed;
            NewRuleServerPattern.Text = string.Empty;
            NewRuleDatabasePattern.Text = string.Empty;
        }

        private void ButtonMoveColorRuleUp_Click(object sender, RoutedEventArgs e) => MoveSelectedConnectionColorRule(-1);

        private void ButtonMoveColorRuleDown_Click(object sender, RoutedEventArgs e) => MoveSelectedConnectionColorRule(1);

        private void MoveSelectedConnectionColorRule(int offset)
        {
            int oldIndex = ConnectionColorRulesListView.SelectedIndex;
            int newIndex = oldIndex + offset;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= _connectionColorRules.Count)
                return;

            var selectedRule = _connectionColorRules[oldIndex];
            _connectionColorRules.Move(oldIndex, newIndex);
            ConnectionColorRulesListView.SelectedItem = selectedRule;
            SetConnectionColorRulesDirty(true);
            UpdateConnectionColorRuleButtons();
        }

        private void ConnectionColorRulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateConnectionColorRuleButtons();

        private void ConnectionColorRulesListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem != null)
                ButtonEditColorRule_Click(sender, e);
        }

        private void ConnectionColorRuleEnabled_Click(object sender, RoutedEventArgs e)
        {
            SetConnectionColorRulesDirty(true);
        }

        private void UpdateConnectionColorRuleButtons()
        {
            if (button_EditColorRule == null)
                return;

            int index = ConnectionColorRulesListView.SelectedIndex;
            bool hasSelection = index >= 0;
            button_EditColorRule.IsEnabled = hasSelection;
            button_RemoveColorRule.IsEnabled = hasSelection;
            button_MoveColorRuleUp.IsEnabled = hasSelection && index > 0;
            button_MoveColorRuleDown.IsEnabled = hasSelection && index < _connectionColorRules.Count - 1;
        }

        private void SetConnectionColorRulesDirty(bool dirty)
        {
            if (button_SaveConnectionColorRules == null)
                return;

            button_SaveConnectionColorRules.IsEnabled = dirty;
            ConnectionColorSaveStatus.Text = LocalizationManager.T(dirty ? "Unsaved changes" : "No unsaved changes");
        }

        private void Button_SaveConnectionColorRules_Click(object sender, RoutedEventArgs e)
        {
            var rules = new System.Collections.Generic.List<SettingsManager.ConnectionColorRule>(_connectionColorRules);
            if (!SettingsManager.SaveConnectionColorRules(rules))
            {
                LocalizedMessageBox.Show("The connection color rules could not be saved. Please try again.", "Connection Colors",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetConnectionColorRulesDirty(false);
            SavedMessage();
        }

    }
}
