using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MSSQLTool
{
    public partial class SnippetManagerWindowControl : UserControl
    {
        public SnippetManagerWindowControl()
        {
            InitializeComponent();
            var vm = new SnippetManagerViewModel();
            DataContext = vm;

            // The window hosts the former "Code Snippets" settings page, so the saved
            // values have to be displayed as soon as the window is created.
            cmbReplaceKey.SelectedValue = vm.ReplaceKey.ToString();
            cmbAsteriskTriggerKey.SelectedValue = vm.AsteriskTriggerKey.ToString();
        }

        private void cmbReplaceKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is SnippetManagerViewModel vm && cmbReplaceKey.SelectedValue is string selectedValue)
            {
                if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
                {
                    vm.ReplaceKey = key;
                }
            }
        }

        private void cmbAsteriskTriggerKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is SnippetManagerViewModel vm && cmbAsteriskTriggerKey.SelectedValue is string selectedValue)
            {
                if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
                {
                    vm.AsteriskTriggerKey = key;
                }
            }
        }

        private void SelectSnippetFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SnippetManagerViewModel;
            if (vm == null)
            {
                return;
            }

            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = LocalizationManager.T("Select snippets folder");
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(vm.SnippetFolder) && Directory.Exists(vm.SnippetFolder))
                {
                    dialog.SelectedPath = vm.SnippetFolder;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    vm.SnippetFolder = dialog.SelectedPath;
                }
            }
        }
    }
}
