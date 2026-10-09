using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MSSQLTool
{
    public class QueryHistoryViewModel : INotifyPropertyChanged
    {
        private const int PageSize = 100;
        private sealed class FileEntry { public DateTime StartTime { get; set; } public DateTime FinishTime { get; set; } public string ElapsedTime { get; set; } public long TotalRowsReturned { get; set; } public string ExecResult { get; set; } public string QueryText { get; set; } public string DataSource { get; set; } public string DatabaseName { get; set; } public string LoginName { get; set; } public string WorkstationId { get; set; } }
        private sealed class LoadResult { public List<QueryHistoryRecord> Records = new List<QueryHistoryRecord>(); public int Total; public string Storage; }

        private QueryHistoryRecord _selectedRecord;
        private DateTime? _filterFromDate, _filterToDate;
        private string _filterServer, _filterDatabase, _filterQueryText, _filterLogin, _filterResult = "All";
        private bool _isLoading;
        private string _statusMessage = "Preparing query history...", _statusKind = "Info", _resultSummary = "0 results", _lastRefreshed = "";
        private int _pageNumber = 1, _totalCount;
        private int _refreshVersion;
        private CancellationTokenSource _cancellation;

        public ObservableCollection<QueryHistoryRecord> QueryHistoryRecords { get; } = new ObservableCollection<QueryHistoryRecord>();
        public ObservableCollection<string> ResultOptions { get; } = new ObservableCollection<string> { "All", "Succeeded", "Failed", "Cancelled" };
        public QueryHistoryRecord SelectedRecord { get => _selectedRecord; set => Set(ref _selectedRecord, value, nameof(SelectedRecord)); }
        public DateTime? FilterFromDate { get => _filterFromDate; set => Set(ref _filterFromDate, value, nameof(FilterFromDate)); }
        public DateTime? FilterToDate { get => _filterToDate; set => Set(ref _filterToDate, value, nameof(FilterToDate)); }
        public string FilterServer { get => _filterServer; set => Set(ref _filterServer, value, nameof(FilterServer)); }
        public string FilterDatabase { get => _filterDatabase; set => Set(ref _filterDatabase, value, nameof(FilterDatabase)); }
        public string FilterQueryText { get => _filterQueryText; set => Set(ref _filterQueryText, value, nameof(FilterQueryText)); }
        public string FilterLogin { get => _filterLogin; set => Set(ref _filterLogin, value, nameof(FilterLogin)); }
        public string FilterResult { get => _filterResult; set => Set(ref _filterResult, value, nameof(FilterResult)); }
        public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value, nameof(IsLoading)); }
        public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value, nameof(StatusMessage)); }
        public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value, nameof(StatusKind)); }
        public string ResultSummary { get => _resultSummary; private set => Set(ref _resultSummary, value, nameof(ResultSummary)); }
        public string LastRefreshed { get => _lastRefreshed; private set => Set(ref _lastRefreshed, value, nameof(LastRefreshed)); }
        public int PageNumber { get => _pageNumber; private set => Set(ref _pageNumber, value, nameof(PageNumber)); }
        public string PageSummary => _totalCount == 0
            ? LocalizationManager.T("Page 0 / 0")
            : LocalizationManager.Format("Page {0} / {1}", PageNumber, Math.Max(1, (int)Math.Ceiling(_totalCount / (double)PageSize)));
        public bool CanGoPrevious => PageNumber > 1 && !IsLoading;
        public bool CanGoNext => PageNumber * PageSize < _totalCount && !IsLoading;
        public ICommand RefreshCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand NextPageCommand { get; }

        public QueryHistoryViewModel()
        {
            RefreshCommand = new RelayCommand(() => StartRefresh(true), () => !IsLoading);
            ClearFilterCommand = new RelayCommand(ClearFilters, () => !IsLoading);
            PreviousPageCommand = new RelayCommand(() => { PageNumber--; StartRefresh(false); }, () => CanGoPrevious);
            NextPageCommand = new RelayCommand(() => { PageNumber++; StartRefresh(false); }, () => CanGoNext);
            StartRefresh(true);
        }

        public void ApplyDatePreset(int days) { FilterFromDate = days <= 1 ? DateTime.Today : DateTime.Today.AddDays(-(days - 1)); FilterToDate = DateTime.Today; StartRefresh(true); }
        public void ApplyConnectionFilter(string server, string database) { FilterServer = server ?? ""; FilterDatabase = database ?? ""; StartRefresh(true); }
        private void ClearFilters() { FilterFromDate = FilterToDate = null; FilterServer = FilterDatabase = FilterQueryText = FilterLogin = ""; FilterResult = "All"; StartRefresh(true); }
        private async void StartRefresh(bool resetPage)
        {
            if (resetPage) PageNumber = 1;
            CancellationTokenSource previous = _cancellation;
            previous?.Cancel();
            previous?.Dispose();
            _cancellation = new CancellationTokenSource();
            int version = Interlocked.Increment(ref _refreshVersion);
            await RefreshAsync(_cancellation.Token, version);
        }

        private async Task RefreshAsync(CancellationToken token, int version)
        {
            if (FilterFromDate.HasValue && FilterToDate.HasValue && FilterFromDate.Value.Date > FilterToDate.Value.Date)
            {
                IsLoading = false;
                SetStatus(LocalizationManager.T("The From date cannot be later than the To date."), "Error");
                CommandManager.InvalidateRequerySuggested();
                return;
            }
            IsLoading = true; SetStatus(LocalizationManager.T("Loading query history..."), "Loading"); CommandManager.InvalidateRequerySuggested();
            try
            {
                LoadResult data = await Task.Run(() => Load(token), token); token.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref _refreshVersion)) return;
                QueryHistoryRecords.Clear(); foreach (var record in data.Records) QueryHistoryRecords.Add(record);
                _totalCount = data.Total; SelectedRecord = QueryHistoryRecords.FirstOrDefault();
                ResultSummary = _totalCount == 0
                    ? LocalizationManager.T("No matching queries")
                    : LocalizationManager.Format("Showing {0}-{1} of {2:N0}",
                        (PageNumber - 1) * PageSize + 1, (PageNumber - 1) * PageSize + data.Records.Count, _totalCount);
                LastRefreshed = LocalizationManager.Format("Last refreshed {0}", DateTime.Now.ToString("HH:mm:ss")); OnPropertyChanged(nameof(PageSummary));
                string saveError = MSSQLToolPackage.QueryHistoryLastPersistenceError;
                if (!string.IsNullOrWhiteSpace(saveError)) SetStatus(LocalizationManager.T("History loaded, but the latest background save failed: ") + saveError, "Warning");
                else if (_totalCount == 0) SetStatus(LocalizationManager.T("Recording is active, but no records match the current filters."), "Empty");
                else SetStatus(LocalizationManager.Format("Recording is active · {0}", data.Storage), "Success");
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (version == Volatile.Read(ref _refreshVersion))
                {
                    QueryHistoryRecords.Clear(); _totalCount = 0; ResultSummary = LocalizationManager.T("Unable to load history");
                    SetStatus(LocalizationManager.T("Query history could not be loaded: ") + ex.Message, "Error");
                }
            }
            finally
            {
                if (version == Volatile.Read(ref _refreshVersion))
                {
                    IsLoading = false;
                    OnPropertyChanged(nameof(CanGoPrevious));
                    OnPropertyChanged(nameof(CanGoNext));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private LoadResult Load(CancellationToken token)
        {
            string mode = SettingsManager.GetQueryHistoryStorageMode();
            if (string.Equals(mode, SettingsManager.QueryHistoryStorageModeDisabled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("recording is disabled. Turn it on in the Query History window options.");
            if (string.Equals(mode, SettingsManager.QueryHistoryStorageModeTextFiles, StringComparison.OrdinalIgnoreCase))
                return LoadFiles(token);

            return LoadSqlite(token);
        }

        /// <summary>
        /// Reads a page from the local SQLite history store.  Filtering and paging happen in the
        /// database so a long history stays responsive.
        /// </summary>
        private LoadResult LoadSqlite(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var filter = new QueryHistoryStoreFilter
            {
                FromDate = FilterFromDate,
                ToDate = FilterToDate,
                Server = FilterServer,
                Database = FilterDatabase,
                Login = FilterLogin,
                QueryTerms = Terms(FilterQueryText),
                ResultKind = FilterResult,
                PageNumber = PageNumber,
                PageSize = PageSize
            };

            List<QueryHistoryStoreEntry> rows = QueryHistorySqliteStore.Query(filter, out int total, out string error);
            token.ThrowIfCancellationRequested();
            if (error != null)
                throw new InvalidOperationException(error);

            var result = new LoadResult { Total = total, Storage = "local SQLite store" };
            foreach (QueryHistoryStoreEntry row in rows)
            {
                QueryHistoryRecord record = Build(
                    (int)Math.Min(int.MaxValue, row.QueryId), row.StartTime, row.FinishTime, row.ElapsedTime,
                    row.TotalRowsReturned, row.ExecResult, row.QueryText, row.DataSource, row.DatabaseName,
                    row.LoginName, row.WorkstationId);
                result.Records.Add(record);
            }

            return result;
        }

        private LoadResult LoadFiles(CancellationToken token)
        {
            string folder = SettingsManager.GetQueryHistoryTextFileFolder();
            var records = new List<QueryHistoryRecord>();
            int total = 0;
            int keep = Math.Max(PageSize, PageNumber * PageSize);
            if (Directory.Exists(folder)) foreach (string file in Directory.GetFiles(folder, "*.jsonl").OrderByDescending(x => x)) foreach (string line in File.ReadLines(file))
            {
                token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var x = JsonConvert.DeserializeObject<FileEntry>(line);
                    if (x == null) continue;
                    QueryHistoryRecord record = Build(0, x.StartTime, x.FinishTime, x.ElapsedTime, x.TotalRowsReturned, x.ExecResult, x.QueryText, x.DataSource, x.DatabaseName, x.LoginName, x.WorkstationId);
                    if (!FilterMemory(new[] { record }).Any()) continue;
                    total++;
                    records.Add(record);
                    if (records.Count > keep * 2)
                        records = records.OrderByDescending(item => item.Date).Take(keep).ToList();
                }
                catch (JsonException) { }
            }
            var page = records.OrderByDescending(x => x.Date).Take(keep).Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
            for (int i = 0; i < page.Count; i++) page[i].Id = (PageNumber - 1) * PageSize + i + 1;
            return new LoadResult { Records = page, Total = total, Storage = "local text files" };
        }

        private IEnumerable<QueryHistoryRecord> FilterMemory(IEnumerable<QueryHistoryRecord> source)
        {
            if (FilterFromDate.HasValue) source = source.Where(x => x.Date >= FilterFromDate.Value.Date); if (FilterToDate.HasValue) source = source.Where(x => x.Date < FilterToDate.Value.Date.AddDays(1));
            source = Contains(source, x => x.DataSource, FilterServer); source = Contains(source, x => x.DatabaseName, FilterDatabase); source = Contains(source, x => x.LoginName, FilterLogin); foreach (string term in Terms(FilterQueryText)) source = Contains(source, x => x.QueryText, term);
            return string.IsNullOrWhiteSpace(FilterResult) || FilterResult == "All" ? source : source.Where(x => ResultMatches(x.ExecResult, FilterResult));
        }
        private static IEnumerable<QueryHistoryRecord> Contains(IEnumerable<QueryHistoryRecord> source, Func<QueryHistoryRecord, string> pick, string value) => string.IsNullOrWhiteSpace(value) ? source : source.Where(x => (pick(x) ?? "").IndexOf(value.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        private static string[] Terms(string text) => string.IsNullOrWhiteSpace(text) ? new string[0] : text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        private static bool ResultMatches(string value, string result) { value = value ?? ""; return result == "Succeeded" ? Has(value, "success") || Has(value, "succeed") : result == "Cancelled" ? Has(value, "cancel") : Has(value, "fail") || Has(value, "error"); }
        private static bool Has(string value, string term) => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        private static QueryHistoryRecord Build(int id, DateTime start, DateTime finish, string elapsed, long rows, string result, string query, string server, string database, string login, string workstation)
        {
            query = query ?? ""; string shortText = string.Join(" ", query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); if (shortText.Length > 180) shortText = shortText.Substring(0, 180);
            return new QueryHistoryRecord { Id = id, Date = start, FinishTime = finish, ElapsedTime = elapsed ?? "", TotalRowsReturned = rows, ExecResult = result ?? "", QueryText = query, QueryTextShort = shortText, DataSource = server ?? "", DatabaseName = database ?? "", LoginName = login ?? "", WorkstationId = workstation ?? "" };
        }
        private void SetStatus(string message, string kind) { StatusMessage = message; StatusKind = kind; }
        private bool Set<T>(ref T field, T value, string name) { if (Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
