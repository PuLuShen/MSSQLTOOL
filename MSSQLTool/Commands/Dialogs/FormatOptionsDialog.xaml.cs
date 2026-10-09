using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MSSQLTool
{
    /// <summary>
    /// The full formatting profile editor shown by Shift+Format and by the Code Format settings page.
    /// Every option of <see cref="FormatterOptions"/> is editable here; the controls carry the
    /// dotted path of the model field they edit in their Tag, which keeps the wiring in one place.
    /// </summary>
    public partial class FormatOptionsDialog : Window
    {
        private const string DefaultPreviewSql =
            "select c.CustomerID, c.Name, CASE WHEN c.Balance > 0 THEN 'Debt' ELSE 'Clear' END AS Status\r\n" +
            "from dbo.Customer c inner join dbo.Orders o on o.CustomerID = c.CustomerID\r\n" +
            "where c.IsActive = 1 and o.TotalAmount > 1000\r\n" +
            "order by c.Name";

        private readonly List<FrameworkElement> optionControls = new List<FrameworkElement>();
        private readonly DispatcherTimer previewTimer;
        private bool suppressEvents;

        /// <summary>The profile being edited. The caller owns the instance it passed in.</summary>
        public FormatterOptions Settings { get; }

        /// <summary>True when the user asked the caller to persist the profile.</summary>
        public bool SaveAsDefault => SaveAsDefaultCheckBox.IsChecked == true;

        public FormatOptionsDialog(FormatterOptions initial = null)
        {
            InitializeComponent();
            LocalizationManager.Apply(this);

            // Work on a clone so cancelling cannot leak UI edits into the caller's instance.
            Settings = initial != null ? initial.Clone() : new FormatterOptions();

            CollectOptionControls(OptionsTabControl, optionControls);
            RefreshUiFromSettings();

            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            previewTimer.Tick += (s, e) =>
            {
                previewTimer.Stop();
                UpdatePreview();
            };

            SourcePreviewBox.Text = DefaultPreviewSql;
            UpdatePreview();
        }

        // ---------------------------------------------------------------------------------
        // Model / UI synchronisation
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Walks the declarative tree and collects every control whose Tag is a dotted path such
        /// as "casing.keywords".  The visual tree cannot be used here because a TabControl only
        /// realises the selected tab.
        /// </summary>
        private static void CollectOptionControls(DependencyObject root, List<FrameworkElement> sink)
        {
            if (root == null)
                return;

            // The tag of a ComboBoxItem is the value to store, not a model path.
            if (root is ComboBoxItem)
                return;

            if (root is FrameworkElement element && element.Tag is string path && path.IndexOf('.') >= 0)
                sink.Add(element);

            if (root is ContentControl contentControl && contentControl.Content is DependencyObject content)
                CollectOptionControls(content, sink);

            if (root is Decorator decorator)
                CollectOptionControls(decorator.Child, sink);

            if (root is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                    CollectOptionControls(child, sink);
                return;
            }

            if (root is ItemsControl itemsControl)
            {
                foreach (object item in itemsControl.Items)
                    CollectOptionControls(item as DependencyObject, sink);
            }
        }

        /// <summary>Resolves "casing.keywords" to the field holding it and the object that owns it.</summary>
        private static FieldInfo ResolveOptionField(object target, string path, out object owner)
        {
            owner = null;
            if (target == null || string.IsNullOrWhiteSpace(path))
                return null;

            string[] parts = path.Split('.');
            object current = target;
            FieldInfo field = null;

            foreach (string part in parts)
            {
                if (current == null)
                    return null;

                field = current.GetType().GetField(part, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                    return null;

                // The owner of the field is the object the field was read from, not the value
                // stored in it; a single-segment path therefore keeps the options instance.
                owner = current;
                current = field.GetValue(current);
            }

            return field;
        }

        private object GetOptionValue(string path)
        {
            FieldInfo field = ResolveOptionField(Settings, path, out object owner);
            return field?.GetValue(owner);
        }

        private static bool TrySetOptionValue(FormatterOptions options, string path, object value)
        {
            FieldInfo field = ResolveOptionField(options, path, out object owner);
            if (field == null)
                return false;

            Type type = field.FieldType;

            try
            {
                if (type == typeof(bool))
                {
                    field.SetValue(owner, value is bool flag ? flag : Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                    return true;
                }

                if (type == typeof(int))
                {
                    if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int number))
                        return false;

                    GetIntRange(path, out int min, out int max);
                    field.SetValue(owner, Math.Min(Math.Max(number, min), max));
                    return true;
                }

                if (type.IsEnum)
                {
                    try
                    {
                        field.SetValue(owner, Enum.Parse(type, Convert.ToString(value, CultureInfo.InvariantCulture), true));
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }
                }

                if (type == typeof(string))
                {
                    field.SetValue(owner, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return true;
                }
            }
            catch (Exception)
            {
                // A malformed value simply leaves the previous value in place.
                return false;
            }

            return false;
        }

        private static void GetIntRange(string path, out int min, out int max)
        {
            switch (path)
            {
                case "indent.indentSize":
                    min = 1;
                    max = 16;
                    return;
                case "compact.singleLineThreshold":
                case "subquery.singleLineThreshold":
                    min = 1;
                    max = 10000;
                    return;
                default:
                    min = 0;
                    max = 100000;
                    return;
            }
        }

        /// <summary>Pushes the whole profile into the controls.</summary>
        private void RefreshUiFromSettings()
        {
            bool previous = suppressEvents;
            suppressEvents = true;
            try
            {
                foreach (FrameworkElement element in optionControls)
                {
                    string path = element.Tag as string;
                    object value = GetOptionValue(path);
                    if (value == null)
                        continue;

                    if (element is CheckBox checkBox)
                    {
                        checkBox.IsChecked = value is bool flag && flag;
                    }
                    else if (element is ComboBox combo)
                    {
                        SelectComboItemByTag(combo, value.ToString());
                    }
                    else if (element is TextBox textBox)
                    {
                        textBox.Text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    }
                }

                SelectComboItemByTag(PresetComboBox, Settings.preset);
            }
            finally
            {
                suppressEvents = previous;
            }
        }

        private static void SelectComboItemByTag(ComboBox combo, string tag)
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

        private static string GetSelectedComboTag(ComboBox combo)
        {
            return (combo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        }

        /// <summary>Editing any individual option turns the profile into a custom one.</summary>
        private void MarkProfileAsCustom()
        {
            Settings.preset = nameof(FormatPreset.Custom);

            bool previous = suppressEvents;
            suppressEvents = true;
            try
            {
                SelectComboItemByTag(PresetComboBox, Settings.preset);
            }
            finally
            {
                suppressEvents = previous;
            }
        }

        // ---------------------------------------------------------------------------------
        // Option handlers
        // ---------------------------------------------------------------------------------

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || Settings == null)
                return;

            string tag = GetSelectedComboTag(PresetComboBox);
            if (!Enum.TryParse(tag, true, out FormatPreset preset))
                return;

            if (preset == FormatPreset.Custom)
                Settings.preset = nameof(FormatPreset.Custom);
            else
                Settings.ApplyPreset(preset);

            RefreshUiFromSettings();
            SchedulePreviewUpdate();
        }

        private void OptionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (suppressEvents || Settings == null)
                return;

            if (!(sender is FrameworkElement element) || !(element.Tag is string path))
                return;

            if (TrySetOptionValue(Settings, path, (element as CheckBox)?.IsChecked == true))
                MarkProfileAsCustom();

            SchedulePreviewUpdate();
        }

        private void OptionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || Settings == null)
                return;

            if (!(sender is FrameworkElement element) || !(element.Tag is string path))
                return;

            string value = GetSelectedComboTag(element as ComboBox);
            if (string.IsNullOrEmpty(value))
                return;

            if (TrySetOptionValue(Settings, path, value))
                MarkProfileAsCustom();

            SchedulePreviewUpdate();
        }

        private void OptionTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressEvents || Settings == null)
                return;

            if (!(sender is FrameworkElement element) || !(element.Tag is string path))
                return;

            if (TrySetOptionValue(Settings, path, ((TextBox)element).Text))
                MarkProfileAsCustom();

            SchedulePreviewUpdate();
        }

        private void SourcePreviewBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SchedulePreviewUpdate();
        }

        private void CheckAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllBooleanOptions(true);
        }

        private void UncheckAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllBooleanOptions(false);
        }

        private void SetAllBooleanOptions(bool value)
        {
            if (Settings == null)
                return;

            Settings.SetAllBooleanSwitches(value);
            MarkProfileAsCustom();
            RefreshUiFromSettings();
            SchedulePreviewUpdate();
        }

        private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (Settings == null)
                return;

            Settings.ApplyPreset(FormatPreset.Standard);
            RefreshUiFromSettings();
            SchedulePreviewUpdate();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (Settings == null)
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

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                Settings.ExportToFile(dialog.FileName);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("The profile could not be exported: " + ex.Message,
                    "Query Format Options", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (Settings == null)
                return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import format profile",
                Filter = "MSSQL Tool format profile (*.mssqltoolformat.json)|*.mssqltoolformat.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = FormatterOptions.ProfileExtension.TrimStart('.'),
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                Settings.CopyFrom(FormatterOptions.ImportFromFile(dialog.FileName));
                RefreshUiFromSettings();
                SchedulePreviewUpdate();
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("The profile could not be imported: " + ex.Message,
                    "Query Format Options", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        // ---------------------------------------------------------------------------------
        // Preview
        // ---------------------------------------------------------------------------------

        private void SchedulePreviewUpdate()
        {
            if (previewTimer == null)
            {
                UpdatePreview();
                return;
            }

            previewTimer.Stop();
            previewTimer.Start();
        }

        private void UpdatePreview()
        {
            if (FormattedPreviewBox == null || Settings == null)
                return;

            string source = SourcePreviewBox?.Text;
            if (string.IsNullOrWhiteSpace(source))
                source = DefaultPreviewSql;

            try
            {
                FormattedPreviewBox.Text = TSqlFormatter.FormatCode(source, Settings);
            }
            catch (TSqlFormatException ex)
            {
                // A syntax error in the sample must never close or break the dialog.
                FormattedPreviewBox.Text = ex.Message;
            }
            catch (Exception ex)
            {
                FormattedPreviewBox.Text = ex.Message;
            }
        }
    }
}
