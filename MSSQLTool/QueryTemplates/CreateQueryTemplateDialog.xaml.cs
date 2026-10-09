using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace MSSQLTool
{
    public partial class CreateQueryTemplateDialog : Window
    {
        public CreateQueryTemplateDialog(string initialContent, IEnumerable<string> categories)
        {
            InitializeComponent();
            Title = LocalizationManager.T("Create Query Template");
            ContentBox.Text = initialContent ?? string.Empty;
            CategoryBox.ItemsSource = (categories ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            NameBox.Text = LocalizationManager.T("New query template");
            Loaded += (sender, args) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        public string TemplateName { get; private set; }
        public string Category { get; private set; }
        public string TemplateContent => ContentBox.Text ?? string.Empty;

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string name = (NameBox.Text ?? string.Empty).Trim();
            if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4).TrimEnd();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowValidation("Enter a template name.");
                return;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowValidation("The template name contains characters that cannot be used in a file name.");
                return;
            }

            string category = (CategoryBox.Text ?? string.Empty).Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);
            if (!IsValidCategory(category))
            {
                ShowValidation("Category must be a relative subfolder and cannot contain invalid path characters or '..'.");
                return;
            }

            TemplateName = name;
            Category = category;
            DialogResult = true;
        }

        private static bool IsValidCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return true;
            if (Path.IsPathRooted(category)) return false;
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (string part in category.Split(Path.DirectorySeparatorChar))
            {
                if (string.IsNullOrWhiteSpace(part) || part == "." || part == ".." || part.IndexOfAny(invalid) >= 0)
                    return false;
            }
            return true;
        }

        private void ShowValidation(string message)
        {
            ValidationText.Text = LocalizationManager.T(message);
        }
    }
}
