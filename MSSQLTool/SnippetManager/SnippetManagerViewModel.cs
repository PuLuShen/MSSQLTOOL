using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MSSQLTool
{
    public class SnippetManagerViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<SnippetItem> _snippets;
        private SnippetItem _selectedSnippet;
        private string _editPrefix;
        private string _editDescription;
        private string _editBody;
        private bool _isEditing;
        private bool _isEditMode;

        private bool _useSnippets;
        private SettingsManager.SnippetReplaceKey _replaceKey;
        private string _cursorMarker;
        private string _snippetFolder;
        private bool _useAsteriskExpansion;
        private SettingsManager.SnippetReplaceKey _asteriskTriggerKey;
        private string _statusText;
        private readonly DispatcherTimer _statusResetTimer;

        public SnippetManagerViewModel()
        {
            _snippets = new ObservableCollection<SnippetItem>();

            // This window now hosts the former "Code Snippets" settings page, so it
            // reports its own save state instead of relying on the settings window.
            _statusText = LocalizationManager.T("No unsaved changes");
            _statusResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusResetTimer.Tick += OnStatusResetTimerTick;

            NewCommand = new RelayCommand(OnNew);
            SaveCommand = new RelayCommand(OnSave);
            EditCommand = new RelayCommand(OnEdit, () => SelectedSnippet != null && !IsEditMode);
            DeleteCommand = new RelayCommand(OnDelete);
            DuplicateCommand = new RelayCommand(OnDuplicate);
            ImportLegacyCommand = new RelayCommand(OnImportLegacy);
            CancelCommand = new RelayCommand(OnCancel);
            SaveSettingsCommand = new RelayCommand(OnSaveSettings);

            LoadSettings();
            LoadSnippets();
        }

        public ObservableCollection<SnippetItem> Snippets
        {
            get => _snippets;
            set { _snippets = value; OnPropertyChanged(); }
        }

        public SnippetItem SelectedSnippet
        {
            get => _selectedSnippet;
            set
            {
                _selectedSnippet = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
                if (_selectedSnippet != null && !_isEditMode)
                {
                    EditPrefix = _selectedSnippet.Prefix;
                    EditDescription = _selectedSnippet.Description;
                    EditBody = _selectedSnippet.Body;
                    IsEditing = true;
                    IsEditMode = false;
                }
            }
        }

        public string EditPrefix
        {
            get => _editPrefix;
            set { _editPrefix = value; OnPropertyChanged(); }
        }

        public string EditDescription
        {
            get => _editDescription;
            set { _editDescription = value; OnPropertyChanged(); }
        }

        public string EditBody
        {
            get => _editBody;
            set { _editBody = value; OnPropertyChanged(); }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; OnPropertyChanged(); }
        }

        public bool IsEditMode
        {
            get => _isEditMode;
            set
            {
                _isEditMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEditorReadOnly));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsEditorReadOnly => !IsEditMode;

        public bool UseSnippets
        {
            get => _useSnippets;
            set { _useSnippets = value; OnPropertyChanged(); }
        }

        public SettingsManager.SnippetReplaceKey ReplaceKey
        {
            get => _replaceKey;
            set { _replaceKey = value; OnPropertyChanged(); }
        }

        public string CursorMarker
        {
            get => _cursorMarker;
            set { _cursorMarker = value; OnPropertyChanged(); }
        }

        public string SnippetFolder
        {
            get => _snippetFolder;
            set { _snippetFolder = value; OnPropertyChanged(); }
        }

        public bool UseAsteriskExpansion
        {
            get => _useAsteriskExpansion;
            set { _useAsteriskExpansion = value; OnPropertyChanged(); }
        }

        public SettingsManager.SnippetReplaceKey AsteriskTriggerKey
        {
            get => _asteriskTriggerKey;
            set { _asteriskTriggerKey = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public ICommand NewCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand ImportLegacyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SaveSettingsCommand { get; }

        private void LoadSnippets()
        {
            SnippetService.ReloadSnippets();
            var items = SnippetService.GetAllSnippets();
            _snippets.Clear();
            foreach (var item in items)
            {
                _snippets.Add(item);
            }
        }

        private void LoadSettings()
        {
            var settings = SettingsManager.GetSnippetSettings();
            _useSnippets = settings.useSnippets;
            _replaceKey = settings.replaceKey;
            _cursorMarker = settings.cursorMarker;
            _snippetFolder = settings.snippetFolder;

            var asteriskSettings = SettingsManager.GetAsteriskExpansionSettings();
            _useAsteriskExpansion = asteriskSettings.useAsteriskExpansion;
            _asteriskTriggerKey = asteriskSettings.triggerKey;
        }

        private void OnNew()
        {
            _selectedSnippet = null;
            EditPrefix = string.Empty;
            EditDescription = string.Empty;
            EditBody = string.Empty;
            IsEditing = true;
            IsEditMode = true;
            OnPropertyChanged(nameof(SelectedSnippet));
        }

        private void OnEdit()
        {
            if (_selectedSnippet != null)
                IsEditMode = true;
        }

        private void OnSave()
        {
            if (string.IsNullOrWhiteSpace(EditPrefix))
            {
                LocalizedMessageBox.Show("Prefix is required.", "Snippet Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string savedId;
            if (_selectedSnippet == null)
            {
                var newSnippet = new SnippetItem(EditPrefix.Trim(), EditDescription ?? string.Empty, EditBody ?? string.Empty);
                SnippetService.AddSnippet(newSnippet);
                savedId = newSnippet.Id;
            }
            else
            {
                _selectedSnippet.Prefix = EditPrefix.Trim();
                _selectedSnippet.Description = EditDescription ?? string.Empty;
                _selectedSnippet.Body = EditBody ?? string.Empty;
                SnippetService.UpdateSnippet(_selectedSnippet);
                savedId = _selectedSnippet.Id;
            }

            LoadSnippets();
            SelectedSnippet = _snippets.FirstOrDefault(s => s.Id == savedId);
            IsEditMode = false;
            IsEditing = SelectedSnippet != null;
        }

        private void OnDelete()
        {
            if (_selectedSnippet == null)
                return;

            var result = LocalizedMessageBox.Show(
                $"Delete snippet '{_selectedSnippet.Prefix}'?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                SnippetService.DeleteSnippet(_selectedSnippet.Id);
                LoadSnippets();
                IsEditing = false;
                _selectedSnippet = null;
                IsEditMode = false;
                OnPropertyChanged(nameof(SelectedSnippet));
            }
        }

        private void OnDuplicate()
        {
            if (_selectedSnippet == null)
                return;

            var dup = new SnippetItem(
                _selectedSnippet.Prefix + "_copy",
                _selectedSnippet.Description,
                _selectedSnippet.Body);

            SnippetService.AddSnippet(dup);
            LoadSnippets();
            SelectedSnippet = _snippets.FirstOrDefault(s => s.Id == dup.Id);
        }

        private void OnImportLegacy()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select legacy snippets folder (.sql files)";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    SnippetService.ImportFromLegacyFolder(dialog.SelectedPath);
                    LoadSnippets();
                    LocalizedMessageBox.Show("Import completed.", "Snippet Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void OnCancel()
        {
            IsEditMode = false;

            if (_selectedSnippet != null)
            {
                EditPrefix = _selectedSnippet.Prefix;
                EditDescription = _selectedSnippet.Description;
                EditBody = _selectedSnippet.Body;
            }
            else
            {
                IsEditing = false;
                EditPrefix = string.Empty;
                EditDescription = string.Empty;
                EditBody = string.Empty;
            }
        }

        private void OnSaveSettings()
        {
            // Mirrors the former "Code Snippets" settings page: the snippet options and
            // the asterisk-expansion options are stored as two separate registry values.
            var settings = SettingsManager.GetSnippetSettings();
            settings.useSnippets = UseSnippets;
            settings.replaceKey = ReplaceKey;
            settings.cursorMarker = CursorMarker ?? settings.cursorMarker;
            settings.snippetFolder = SnippetFolder ?? string.Empty;
            bool saved = SettingsManager.SaveSnippetSettings(settings);

            bool asteriskSaved = SettingsManager.SaveAsteriskExpansionSettings(new SettingsManager.AsteriskExpansionSettings
            {
                useAsteriskExpansion = UseAsteriskExpansion,
                triggerKey = AsteriskTriggerKey
            });

            ShowStatus(saved && asteriskSaved, saved && asteriskSaved);
        }

        private void ShowStatus(bool success, bool resetAfterDelay)
        {
            StatusText = LocalizationManager.T(success ? "Saved" : "The settings could not be saved.");
            _statusResetTimer.Stop();
            if (resetAfterDelay)
            {
                _statusResetTimer.Start();
            }
        }

        private void OnStatusResetTimerTick(object sender, EventArgs e)
        {
            _statusResetTimer.Stop();
            StatusText = LocalizationManager.T("No unsaved changes");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
