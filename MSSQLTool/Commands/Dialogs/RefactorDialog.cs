using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MSSQLTool.Commands.Dialogs
{
    /// <summary>
    /// Small WPF prompt used by the refactorings.  It is intentionally built in code: the
    /// extension ships a single assembly and the existing dialogs follow the same pattern.
    /// </summary>
    internal static class RefactorDialog
    {
        public static bool PromptRename(string oldName, out string newName, out bool wholeDocument)
        {
            string value = oldName;
            bool scope = false;

            bool confirmed = Show(
                "Rename",
                LocalizationManager.Format("Rename \"{0}\" to:", oldName),
                value,
                "Apply to the whole script instead of the current statement",
                false,
                out value,
                out scope);

            newName = value;
            wholeDocument = scope;
            return confirmed;
        }

        public static bool PromptSchema(out string schema)
        {
            string value = "dbo";
            bool ignored;
            bool confirmed = Show(
                "Qualify object names",
                "Schema to add to unqualified table references:",
                value,
                null,
                false,
                out value,
                out ignored);

            schema = value;
            return confirmed;
        }

        private static bool Show(
            string title,
            string label,
            string initial,
            string checkText,
            bool checkDefault,
            out string value,
            out bool checkState)
        {
            string result = initial ?? string.Empty;
            bool state = checkDefault;
            bool confirmed = false;

            var window = new Window
            {
                Title = LocalizationManager.T(title),
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                Background = SystemColors.WindowBrush
            };

            try
            {
                Window owner = Application.Current != null ? Application.Current.MainWindow : null;
                if (owner != null && !ReferenceEquals(owner, window)) window.Owner = owner;
            }
            catch (Exception)
            {
            }

            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T(label),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var box = new TextBox
            {
                Text = initial ?? string.Empty,
                Height = 26,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(box);

            CheckBox scopeBox = null;
            if (!string.IsNullOrEmpty(checkText))
            {
                scopeBox = new CheckBox
                {
                    Content = LocalizationManager.T(checkText),
                    IsChecked = checkDefault,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                panel.Children.Add(scopeBox);
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            var ok = new Button { Content = LocalizationManager.T("OK"), Width = 84, Height = 26, IsDefault = true };
            var cancel = new Button { Content = LocalizationManager.T("Cancel"), Width = 84, Height = 26, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            ok.Click += (_, __) =>
            {
                result = box.Text;
                state = scopeBox != null && scopeBox.IsChecked == true;
                confirmed = true;
                window.Close();
            };
            cancel.Click += (_, __) => window.Close();

            window.Content = panel;
            window.Loaded += (_, __) =>
            {
                box.Focus();
                box.SelectAll();
            };
            window.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape) window.Close();
            };

            window.ShowDialog();

            value = result;
            checkState = state;
            return confirmed;
        }
    }
}
