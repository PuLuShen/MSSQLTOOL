using Aurora;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio;
using System.Collections;
using NLog;
using NLog.Targets;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Linq;
using MSSQLTool.Properties;

namespace MSSQLTool
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(MSSQLToolPackage.PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasMultipleProjects_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasSingleProject_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(SettingsWindow))]
    [ProvideToolWindow(typeof(AboutWindow))]
    [ProvideToolWindow(typeof(HealthDashboard_Server))]
    [ProvideToolWindow(typeof(DataTransferWindow))]
    [ProvideToolWindow(typeof(SqlServerBuildsWindow))]
    [ProvideToolWindow(typeof(QueryHistoryWindow))]
    [ProvideToolWindow(typeof(SchemaCompareWindow))]
    [ProvideToolWindow(typeof(StatisticsSummaryWindow))]
    [ProvideToolWindow(typeof(DatabaseScripterToolWindow))]
    [ProvideToolWindow(typeof(DataImportWindow))]
    [ProvideToolWindow(typeof(QuickSearchWindow))]
    [ProvideToolWindow(typeof(SnippetManagerWindow))]
    [ProvideToolWindow(typeof(QueryTemplateWindow))]
    public sealed class MSSQLToolPackage : AsyncPackage
    {

        public class SQLVersionInfo
        {
            public string SqlVersion { get; set; }    // e.g. "SQL Server 2022"
            public Version BuildNumber { get; set; }   // e.g. "16.0.1000"
            public DateTime ReleaseDate { get; set; }
            public string UpdateName { get; set; }    // e.g. "CU5" or "Security Update XYZ"
            public string KbNumber { get; set; }    // e.g. "CU5" or "Security Update XYZ"
            public string Url { get; set; }
        }

        public class SQLBuildsData
        {
            public Dictionary<string, List<SQLVersionInfo>> Builds { get; set; } = new Dictionary<string, List<SQLVersionInfo>>();
        }

        public SQLBuildsData SQLBuildsDataInfo;

        #region QueryHistory

        private const string QueryHistoryStorageModeTextFiles = "TextFiles";
        private const string QueryHistoryStorageModeDisabled = "Disabled";

        private static ConcurrentQueue<QueryHistoryEntry> _queryHistoryQueue = new ConcurrentQueue<QueryHistoryEntry>();
        private static int _queryHistoryProcessorRunning;
        private static int _queryHistoryQueueCount;
        private static Task _queryHistoryProcessorTask = Task.CompletedTask;
        private static QueryHistoryEntry _queryHistoryInFlight;
        private const int QueryHistoryQueueLimit = 5000;
        private static readonly object QueryHistoryRecoveryFileLock = new object();
        private static readonly object QueryHistoryTextFileLock = new object();
        public static string QueryHistoryLastPersistenceError { get; private set; }
        public static DateTime? QueryHistoryLastPersistenceSuccess { get; private set; }
        private static DateTime _queryHistoryLastRetentionCleanupUtc;
        private static int _statisticsCaptureVersion;
        private static int _pendingStatisticsCaptureVersion;
        private static readonly object _statisticsCaptureSyncRoot = new object();
        private static CancellationTokenSource _statisticsCaptureCancellationTokenSource;
        private System.Windows.Threading.DispatcherTimer _connectionColorRetryTimer;
        private System.Windows.Threading.DispatcherTimer _activeConnectionMonitorTimer;
        private EnvDTE.WindowEvents _windowEvents;
        private int _connectionColorRetryCount;
        private string _lastObservedConnectionColorKey;
        private string _lastObservedConnectionWindowKey;
        public static Logger _logger;

        private void InitializeLogging()
        {

            var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MSSQLTool",
                        "MSSQLToolLog"
                );
            Directory.CreateDirectory(logDirectory);

            // If using the NLog.config approach:
            LogManager.Setup()
                  .LoadConfiguration(builder =>
                  {
                      // Create a file target
                      var fileTarget = new FileTarget("fileLog")
                      {
                          FileName = Path.Combine(logDirectory, "log_${shortdate}.log"),
                          Layout = "${longdate}|${level}|${logger}|${message}${exception:format=ToString}",

                          // Optionally, configure archive settings, etc.
                          ArchiveFileName = Path.Combine(logDirectory, "archive/log.{###}.txt"),
                          ArchiveAboveSize = 1024 * 1024 * 5, // 5 MB, for example
                          MaxArchiveFiles = 5
                      };

                      // Add the file target to the builder
                      // builder.AddTarget(fileTarget);

                      // Create a rule: "Write all logs from Info to Fatal to fileTarget"
                      builder.ForLogger()
                             .FilterMinLevel(LogLevel.Info)
                             .WriteTo(fileTarget);
                  });

            _logger = LogManager.GetCurrentClassLogger();
            // If needed, create directories here if they do not exist
            // Or do nothing if the config is specifying a folder that NLog will create automatically
        }

        private static void EnqueueDataForProcessing(QueryHistoryEntry data)
        {
            if (Interlocked.Increment(ref _queryHistoryQueueCount) > QueryHistoryQueueLimit)
            {
                Interlocked.Decrement(ref _queryHistoryQueueCount);
                QueryHistoryLastPersistenceError = "Query history queue is full; the newest entry was written to the recovery log.";
                PersistRecoveryCopy(data, "queue-full");
                return;
            }
            _queryHistoryQueue.Enqueue(data);
            StartQueryHistoryProcessor();
        }

        private static void StartQueryHistoryProcessor()
        {
            if (Interlocked.CompareExchange(ref _queryHistoryProcessorRunning, 1, 0) == 0)
            {
                _queryHistoryProcessorTask = Task.Run(() => ProcessDataAsync());
            }
        }

        private static async Task ProcessDataAsync()
        {
            try
            {
                while (_queryHistoryQueue.TryDequeue(out QueryHistoryEntry data))
                {
                    Interlocked.Decrement(ref _queryHistoryQueueCount);
                    Interlocked.Exchange(ref _queryHistoryInFlight, data);
                    bool persisted = false;
                    for (int attempt = 0; attempt < 3 && !persisted; attempt++)
                    {
                        data.RetryCount = attempt;
                        persisted = await PersistDataAsync(data);
                        if (!persisted && attempt < 2) await Task.Delay(250 * (1 << attempt));
                    }
                    if (!persisted) PersistRecoveryCopy(data, "persistence-failed");
                    Interlocked.CompareExchange(ref _queryHistoryInFlight, null, data);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _queryHistoryProcessorRunning, 0);
                if (!_queryHistoryQueue.IsEmpty)
                {
                    StartQueryHistoryProcessor();
                }
            }
        }

        private static async System.Threading.Tasks.Task<bool> PersistDataAsync(QueryHistoryEntry data)
        {
            try
            {
                data.QueryText = QueryHistoryPrivacy.Protect(data.QueryText);
                string storageMode = SettingsManager.GetQueryHistoryStorageMode();
                if (string.Equals(storageMode, QueryHistoryStorageModeDisabled, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(storageMode, QueryHistoryStorageModeTextFiles, StringComparison.OrdinalIgnoreCase))
                {
                    QueryHistoryPrivacy.CleanupTextFiles();
                    await PersistDataAsJsonLineAsync(data);
                    QueryHistoryLastPersistenceError = null;
                    QueryHistoryLastPersistenceSuccess = DateTime.Now;
                    return true;
                }

                // Local SQLite storage: the history never touches the server the user is
                // connected to, and it keeps working while that server is unreachable.
                bool inserted = await System.Threading.Tasks.Task.Run(() =>
                {
                    bool ok = QueryHistorySqliteStore.TryInsert(data, out string insertError);
                    if (!ok) QueryHistoryLastPersistenceError = insertError;
                    return ok;
                });

                if (!inserted) return false;

                QueryHistoryLastPersistenceError = null;
                QueryHistoryLastPersistenceSuccess = DateTime.Now;
                RunSqliteHistoryRetentionCleanup();
                return true;
            }
            catch (Exception ex)
            {
                QueryHistoryLastPersistenceError = ex.Message;
                FeatureDiagnostics.Report("QueryHistory", "Background persistence failed", ex);
                _logger.Error(ex, "[QueryHistory-PersistDataAsync]: An exception occurred");
                return false;
            }
        }

        /// <summary>
        /// Applies the retention window to the local SQLite store at most twice a day, so a long
        /// editing session does not pay for the cleanup on every statement.
        /// </summary>
        private static void RunSqliteHistoryRetentionCleanup()
        {
            int retentionDays = SettingsManager.GetQueryHistoryRetentionDays();
            if (retentionDays <= 0) return;
            if (DateTime.UtcNow - _queryHistoryLastRetentionCleanupUtc <= TimeSpan.FromHours(12)) return;

            _queryHistoryLastRetentionCleanupUtc = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    int removed = QueryHistorySqliteStore.DeleteOlderThan(retentionDays, out string cleanupError);
                    if (cleanupError != null)
                        FeatureDiagnostics.Report("QueryHistory", "Retention cleanup failed: " + cleanupError, null);
                    else if (removed > 0)
                        _logger.Info("[QueryHistory] Retention cleanup removed " + removed + " expired records.");
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("QueryHistory", "Retention cleanup failed after history was saved", ex);
                }
            });
        }

        private static Task PersistDataAsJsonLineAsync(QueryHistoryEntry data)
        {
            string folderPath = SettingsManager.GetQueryHistoryTextFileFolder();
            Directory.CreateDirectory(folderPath);

            string fileName = $"query-history-{DateTime.UtcNow:yyyy-MM-dd}.jsonl";
            string filePath = Path.Combine(folderPath, fileName);
            string json = JsonConvert.SerializeObject(data);
            lock (QueryHistoryTextFileLock)
            {
                // An append can reach disk and still report an I/O failure. On a
                // retry, detect that record by its stable client id before appending.
                if (data.RetryCount > 0 && File.Exists(filePath))
                {
                    string id = data.ClientExecutionId.ToString("D");
                    if (File.ReadLines(filePath).Any(line => line.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0))
                        return Task.CompletedTask;
                }

                File.AppendAllText(filePath, json + Environment.NewLine);
            }
            return Task.CompletedTask;
        }

        private static void PersistRecoveryCopy(QueryHistoryEntry data, string reason)
        {
            try
            {
                data.QueryText = QueryHistoryPrivacy.Protect(data.QueryText);
                string folder = Path.Combine(SettingsManager.GetQueryHistoryTextFileFolder(), "recovery");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "query-history-recovery-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
                lock (QueryHistoryRecoveryFileLock)
                {
                    string id = data.ClientExecutionId.ToString("D");
                    if (File.Exists(path) && File.ReadLines(path).Any(line => line.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0)) return;
                    File.AppendAllText(path, JsonConvert.SerializeObject(new { Reason = reason, CapturedUtc = DateTime.UtcNow, Entry = data }) + Environment.NewLine);
                }
            }
            catch (Exception ex) { FeatureDiagnostics.Report("QueryHistory", "Could not write recovery history", ex); }
        }

        private static void FlushQueryHistory(TimeSpan timeout)
        {
            StartQueryHistoryProcessor();
            try { _queryHistoryProcessorTask?.Wait(timeout); }
            catch (Exception ex) { FeatureDiagnostics.Report("QueryHistory", "Shutdown flush did not complete cleanly", ex); }
            if (_queryHistoryProcessorTask != null && !_queryHistoryProcessorTask.IsCompleted)
            {
                QueryHistoryEntry inFlight = Volatile.Read(ref _queryHistoryInFlight);
                if (inFlight != null) PersistRecoveryCopy(inFlight, "shutdown-in-flight");
            }
            while (_queryHistoryQueue.TryDequeue(out QueryHistoryEntry pending))
            {
                Interlocked.Decrement(ref _queryHistoryQueueCount);
                PersistRecoveryCopy(pending, "shutdown-flush");
            }
        }
        #endregion

        public const string PackageGuidString = "82ff597d-c4bc-469f-b990-637219074984";
        public const string PackageGuidGroup = "d8ef26a8-e88c-4ad1-85fd-ddc48a207530";

        private Plugin m_plugin = null;
        private CommandRegistry m_commandRegistry = null;

        public CommandEvents m_queryExecuteEvent { get; private set; }

        private int numberOfWindowsOpen = 0;

        public int GetNextToolWindowId()
        {
            numberOfWindowsOpen += 1;

            return numberOfWindowsOpen;
        }

        public Dictionary<string, string> globalSnippets = new Dictionary<string, string>();
        private readonly List<KeypressCommandFilter> _commandFilters = new List<KeypressCommandFilter>();
        private readonly HashSet<IVsTextView> _registeredTextViews = new HashSet<IVsTextView>();

        public static MSSQLToolPackage PackageInstance { get; private set; }

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            PackageInstance = this;

            LocalizationManager.Initialize();
            InitializeLogging();

            try
            {
                await FormatQueryCommand.InitializeAsync(this);
                await RefreshTemplatesCommand.InitializeAsync(this);
                await RefreshCompletionMetadataCommand.InitializeAsync(this);
                await OpenTemplatesFolderCommand.InitializeAsync(this);
                await SettingsWindowCommand.InitializeAsync(this);
                await AboutWindowCommand.InitializeAsync(this);
                await ScriptSelectedObject.InitializeAsync(this);
                await ExportGridToAsInsertsCommand.InitializeAsync(this);
                await HealthDashboard_ServerCommand.InitializeAsync(this);
                await DataTransferWindowCommand.InitializeAsync(this);
                await DataImportWindowCommand.InitializeAsync(this);
                await ResultGridCopyAsInsertCommand.InitializeAsync(this);
                await SqlServerBuildsWindowCommand.InitializeAsync(this);
                await QueryHistoryWindowCommand.InitializeAsync(this);
                await SchemaCompareWindowCommand.InitializeAsync(this);
                ShortcutManager.ApplyQueryHistoryShortcut(SettingsManager.GetQueryHistoryShortcut(), out _);
                ShortcutManager.ApplyScriptObjectShortcut(SettingsManager.GetScriptObjectShortcut(), out _);
                await StatisticsSummaryWindowCommand.InitializeAsync(this);
                await DatabaseScripterToolWindowCommand.InitializeAsync(this);
                await QuickSearchWindowCommand.InitializeAsync(this);
                await SnippetManagerWindowCommand.InitializeAsync(this);
                await SelectCurrentStatementCommand.InitializeAsync(this);
                await ToggleBlockCommentCommand.InitializeAsync(this);
                await QueryTemplateWindowCommand.InitializeAsync(this);

                UpdateChecker.ScheduleCheck(this, SettingsManager.GetEnableUpdateChecks());

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            try
            {

                DTE2 application = GetGlobalService(typeof(DTE)) as DTE2;
                IVsProfferCommands3 profferCommands3 = await base.GetServiceAsync(typeof(SVsProfferCommands)) as IVsProfferCommands3;
                OleMenuCommandService oleMenuCommandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

                var command = application.Commands.Item("Query.Execute");
                m_queryExecuteEvent = application.Events.get_CommandEvents(command.Guid, command.ID);
                m_queryExecuteEvent.BeforeExecute += this.CommandEvents_BeforeExecute;
                m_queryExecuteEvent.AfterExecute += this.CommandEvents_AfterExecute;

                EnvDTE80.Events2 events = (EnvDTE80.Events2)application.Events;
                _windowEvents = events.WindowEvents;

                _windowEvents.WindowCreated += new _dispWindowEvents_WindowCreatedEventHandler(WindowCreated_Event);
                _windowEvents.WindowActivated += new _dispWindowEvents_WindowActivatedEventHandler(WindowActivated_Event);
                _windowEvents.WindowClosing += new _dispWindowEvents_WindowClosingEventHandler(WindowClosing_Event);

                StartActiveWindowConnectionMonitor();

                // "File.ConnectObjectExplorer"
                // "Query.Connect"
                // 

                //---------------------------------------------------------------------------
                // Query Templates
                ImageList icons = new ImageList();
                icons.Images.Add(Resources.script);

                m_plugin = new Plugin(application, profferCommands3, icons, oleMenuCommandService, "MSSQLTool", "Aurora.Connect");

                CommandBar commandBar = m_plugin.AddCommandBar("MSSQL Tool", MsoBarPosition.msoBarTop);
                m_commandRegistry = new CommandRegistry(m_plugin, commandBar, new Guid(PackageGuidString), new Guid(PackageGuidGroup));

                //---------------------------------------------------------------------------
                // Legacy snippet folder load: the dictionary has no other reader in
                // the extension, so keep it off the startup path.
                _ = Task.Run(LoadGlobalSnippets);
                SnippetService.ReloadSnippets();

                //---------------------------------------------------------------------------
                // Templates live on disk: the toolbar "模板语句" button opens the
                // picker dialog directly, so no dynamic per-file menu is built here.
                QueryTemplateLibrary.Instance.Initialize();

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");

                LocalizedMessageBox.Show(
                    ex.Message + Environment.NewLine + Environment.NewLine
                        + LocalizationManager.T("MSSQL Tool could not fully initialize; some features may be unavailable. Details were written to the activity log (Settings → Diagnostics)."),
                    "MSSQL Tool",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }

            try
            {
                // The build catalog comes from the network; do not hold package
                // initialization open for it. Show the menu entry whenever the
                // download finishes (or stay hidden when it fails).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        SQLBuildsDataInfo = SQLBuilds.DownloadSqlServerBuildInfo();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                        MenuCommand CmdSqlServerBuilds = m_plugin?.MenuCommandService?.FindCommand(new CommandID(SqlServerBuildsWindowCommand.CommandSet, SqlServerBuildsWindowCommand.CommandId));
                        if (CmdSqlServerBuilds != null) CmdSqlServerBuilds.Visible = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "An exception occurred");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            // needed for the OxyPlot library
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (KeypressCommandFilter filter in _commandFilters.ToArray())
                    filter.Dispose();
                _commandFilters.Clear();
                _registeredTextViews.Clear();
                _activeConnectionMonitorTimer?.Stop();
                _connectionColorRetryTimer?.Stop();
                if (_windowEvents != null)
                {
                    _windowEvents.WindowCreated -= WindowCreated_Event;
                    _windowEvents.WindowActivated -= WindowActivated_Event;
                    _windowEvents.WindowClosing -= WindowClosing_Event;
                    _windowEvents = null;
                }
                if (m_queryExecuteEvent != null)
                {
                    m_queryExecuteEvent.BeforeExecute -= CommandEvents_BeforeExecute;
                    m_queryExecuteEvent.AfterExecute -= CommandEvents_AfterExecute;
                    m_queryExecuteEvent = null;
                }
                QueryTemplateLibrary.Instance.Dispose();
                AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
                GridAccess.RestoreNativeAppearance();
                FlushQueryHistory(TimeSpan.FromSeconds(3));
                UpdateChecker.LaunchDeferredUpdateOnClose();
            }

            base.Dispose(disposing);
        }

        #endregion

        private bool TryApplyConnectionColorForWindow(EnvDTE.Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string windowKind = null;
            try { windowKind = window?.Kind; } catch { }

            if (window == null || windowKind != "Document")
            {
                return false;
            }

            var connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.ServerName))
            {
                return false;
            }

            GridAccess.ApplyConnectionColor(connectionInfo.ServerName, connectionInfo.Database);
            return true;
        }

        private void StartActiveWindowConnectionMonitor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_activeConnectionMonitorTimer != null)
            {
                return;
            }

            _activeConnectionMonitorTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };

            _activeConnectionMonitorTimer.Tick += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                try
                {
                    RefreshActiveWindowConnectionColorIfChanged();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to monitor active window connection changes.");
                }
            };

            _activeConnectionMonitorTimer.Start();
        }

        private ScriptFactoryAccess.ConnectionInfo _monitorConnectionInfo;
        private DateTime _monitorConnectionFetchedUtc;
        private string _monitorConnectionWindowKey;

        private void RefreshActiveWindowConnectionColorIfChanged(bool force = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var activeWindow = ServiceCache.ExtensibilityModel?.ActiveWindow;
            string windowKind = null;
            try { windowKind = activeWindow?.Kind; } catch { }

            if (activeWindow == null || windowKind != "Document")
            {
                _lastObservedConnectionColorKey = null;
                _lastObservedConnectionWindowKey = null;
                _monitorConnectionWindowKey = null;
                return;
            }

            string windowKey = GetWindowTrackingKey(activeWindow);

            // The live connection belongs to the active window. While the same
            // window stays active, reuse the cached info instead of walking
            // COM objects and reflection on every 750ms tick.
            ScriptFactoryAccess.ConnectionInfo connectionInfo;
            if (!force && _monitorConnectionInfo != null
                && string.Equals(windowKey, _monitorConnectionWindowKey, StringComparison.Ordinal)
                && (DateTime.UtcNow - _monitorConnectionFetchedUtc).TotalSeconds < 5)
            {
                connectionInfo = _monitorConnectionInfo;
            }
            else
            {
                connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
                _monitorConnectionInfo = connectionInfo;
                _monitorConnectionFetchedUtc = DateTime.UtcNow;
                _monitorConnectionWindowKey = windowKey;
            }

            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.ServerName))
            {
                _lastObservedConnectionColorKey = null;
                _lastObservedConnectionWindowKey = null;
                return;
            }

            string connectionKey = $"{connectionInfo.ServerName}|{connectionInfo.Database}";

            if (!force &&
                string.Equals(connectionKey, _lastObservedConnectionColorKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(windowKey, _lastObservedConnectionWindowKey, StringComparison.Ordinal))
            {
                return;
            }

            GridAccess.ApplyConnectionColor(connectionInfo.ServerName, connectionInfo.Database);

            // Warm the completion metadata cache for this connection so the
            // first IntelliSense request does not pay the schema-load latency.
            _ = Completion.SqlMetadataCache.PrefetchAsync(connectionInfo);

            _lastObservedConnectionColorKey = connectionKey;
            _lastObservedConnectionWindowKey = windowKey;
        }

        private static string GetWindowTrackingKey(EnvDTE.Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (window == null)
            {
                return string.Empty;
            }

            try
            {
                if (window.Document != null)
                {
                    return window.Document.FullName ?? window.Caption ?? string.Empty;
                }
            }
            catch
            {
            }

            try
            {
                return window.Caption ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void ScheduleActiveWindowConnectionColorRefresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_connectionColorRetryTimer == null)
            {
                _connectionColorRetryTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };

                _connectionColorRetryTimer.Tick += (sender, args) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    _connectionColorRetryCount++;

                    bool applied = false;
                    try
                    {
                        applied = TryApplyConnectionColorForWindow(ServiceCache.ExtensibilityModel?.ActiveWindow);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "Failed to refresh connection color.");
                    }

                    if (applied || _connectionColorRetryCount >= 6)
                    {
                        _connectionColorRetryTimer.Stop();
                    }
                };
            }

            RefreshActiveWindowConnectionColorIfChanged(force: true);
            _connectionColorRetryCount = 0;
            _connectionColorRetryTimer.Stop();
            _connectionColorRetryTimer.Start();
        }

        private void WindowActivated_Event(EnvDTE.Window GotFocus, EnvDTE.Window LostFocus)
        {

            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                EnsureStatisticsExecutionHookForActiveWindow("window-activated");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reattach statistics handler during window activation.");
            }

            // The editor command filter owns completion as well as optional snippets,
            // so it must be attached independently of the snippet setting.
            if (GotFocus != null)
            {
                try
                {
                    // snippet processor
                    var DocData = GridAccess.GetProperty(GotFocus.Object, "DocData");
                    if (DocData != null)
                    {
                        var txtMgr = (IVsTextManager)GridAccess.GetProperty(DocData, "TextManager");

                        IVsTextView textView;
                        if (txtMgr != null && txtMgr.GetActiveView(0, null, out textView) == VSConstants.S_OK)
                        {
                            foreach (KeypressCommandFilter existing in _commandFilters)
                            {
                                if (!existing.IsFor(textView)) existing.DismissCompletion();
                            }
                            EnsureCommandFilter(textView);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "An exception occurred");
                }
            }
            else
            {
                foreach (KeypressCommandFilter existing in _commandFilters) existing.DismissCompletion();
            }

            // Apply connection-based status-bar coloring through SSMS's native API.
            try
            {
                if (GotFocus != null)
                {
                    TryApplyConnectionColorForWindow(GotFocus);
                    ScheduleActiveWindowConnectionColorRefresh();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred applying connection color");
            }

        }

        internal KeypressCommandFilter EnsureCommandFilter(IVsTextView textView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (textView == null) return null;

            foreach (KeypressCommandFilter existing in _commandFilters)
                if (existing.IsFor(textView)) return existing;

            _registeredTextViews.Add(textView);
            var commandFilter = new KeypressCommandFilter(this, textView);
            commandFilter.AddToChain();
            _commandFilters.Add(commandFilter);
            return commandFilter;
        }

        private void WindowClosing_Event(EnvDTE.Window Window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Re-color remaining tabs after a tab closes
            try
            {
                var docData = Window == null ? null : GridAccess.GetProperty(Window.Object, "DocData");
                var textManager = docData == null ? null : GridAccess.GetProperty(docData, "TextManager") as IVsTextManager;
                if (textManager != null && textManager.GetActiveView(0, null, out IVsTextView closingView) == VSConstants.S_OK)
                {
                    for (int i = _commandFilters.Count - 1; i >= 0; i--)
                    {
                        if (!_commandFilters[i].IsFor(closingView)) continue;
                        _commandFilters[i].Dispose();
                        _commandFilters.RemoveAt(i);
                    }
                    _registeredTextViews.Remove(closingView);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred in WindowClosing_Event");
            }
        }

        private void WindowCreated_Event(EnvDTE.Window Window)
        {

            ThreadHelper.ThrowIfNotOnUIThread();

            // subscribe to the execution completed event
            try
            {
                var sqlResultsControl = GridAccess.GetNonPublicField(Window.Object, "m_sqlResultsControl");
                AttachStatisticsExecutionCompletedHandler(sqlResultsControl);
                TryApplyConnectionColorForWindow(Window);
                ScheduleActiveWindowConnectionColorRefresh();

            }
            catch (Exception ex) 
            {
                _logger.Error(ex, "An exception occurred");
            }
        }

        private static void AttachStatisticsExecutionCompletedHandler(object sqlResultsControl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (sqlResultsControl == null)
            {
                return;
            }

            EventHandler eventHandler = SQLResultsControl_ScriptExecutionCompleted;

            EventInfo eventInfo = sqlResultsControl.GetType().GetEvent("ScriptExecutionCompleted");
            if (eventInfo == null)
            {
                return;
            }

            Delegate handlerDelegate = Delegate.CreateDelegate(eventInfo.EventHandlerType, eventHandler.Target, eventHandler.Method);
            eventInfo.RemoveEventHandler(sqlResultsControl, handlerDelegate);
            eventInfo.AddEventHandler(sqlResultsControl, handlerDelegate);
        }

        public static void EnsureStatisticsExecutionHookForActiveWindow(string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var sqlResultsControl = GridAccess.GetSQLResultsControl();
                AttachStatisticsExecutionCompletedHandler(sqlResultsControl);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to ensure statistics execution hook ({reason}).");
            }
        }

        public void LoadGlobalSnippets()
        {

            if (SettingsManager.GetUseSnippets())
            {

                var snippetFolder = SettingsManager.GetSnippetFolder();

                if (Directory.Exists(snippetFolder))
                {
                    var allFiles = Directory.EnumerateFiles(snippetFolder, "*.sql");

                    foreach (var file in allFiles)
                    {

                        FileInfo fi = new FileInfo(file);

                        if (fi.Length < 1024 * 1024)
                        {
                            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fi.Name);

                            globalSnippets.Add(fileNameWithoutExtension.ToUpper(), System.IO.File.ReadAllText(fi.FullName));
                        }

                    }


                }

            }



        }

        // I don't understand the purpose, but it works
        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            // add this into main module -> AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            if (args.Name.Contains("OxyPlot"))
                return AppDomain.CurrentDomain.Load(args.Name);
            else return null;
        }
        //----------------


        // This method aligns all numeric values to the right
        public static void SQLResultsControl_ScriptExecutionCompleted(object QEOLESQLExec, object b)
        {

            ThreadHelper.ThrowIfNotOnUIThread();

            // Metadata refresh is independent from query-history collection. A
            // failure in optional history fields must not leave completion stale
            // after a successful schema change.
            try
            {
                var executedTextSpan = GridAccess.GetNonPublicField(QEOLESQLExec, "textSpan");
                string executedSql = (string)GridAccess.GetProperty(executedTextSpan, "Text");
                if (SettingsManager.GetSqlCompletionSettings().autoRefreshMetadata
                    && Completion.SqlMetadataCache.ShouldInvalidateAfterExecution(executedSql))
                    Completion.SqlMetadataCache.Invalidate(ScriptFactoryAccess.GetCurrentConnectionInfo());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to refresh SQL completion metadata after schema change");
            }

            try
            {
                //1. Align numeric types to the right
                CollectionBase gridContainers = GridAccess.GetGridContainers();

                foreach (var gridContainer in gridContainers)
                {
                    var grid = GridAccess.GetNonPublicField(gridContainer, "m_grid") as GridControl;
                    var gridStorage = grid.GridStorage;
                    var schemaTable = GridAccess.GetNonPublicField(gridStorage, "m_schemaTable") as DataTable;

                    var gridColumns = GridAccess.GetNonPublicField(grid, "m_Columns") as GridColumnCollection;
                    if (gridColumns != null)
                    {
                        //Why no "flot"? Because it cannot be aligned "good" due to the varying number of digits in the decimal part.
                        string[] typeToAlignRight = new string[] { "tinyint", "smallint", "int", "bigint", "money", "smallmoney", "decimal", "numeric" };

                        List<int> columnsToAlignRight = new List<int> { };

                        for (int c = 0; c < schemaTable.Rows.Count; c++)
                        {
                            int columnOrdinal = (int)schemaTable.Rows[c][1];
                            var sqlDataTypeName = schemaTable.Rows[c][24];

                            if (typeToAlignRight.Contains(sqlDataTypeName))
                            {
                                columnsToAlignRight.Add(columnOrdinal);
                            }
                        }

                        foreach (Microsoft.SqlServer.Management.UI.Grid.GridColumn gridColumn in gridColumns)
                        {

                            if (columnsToAlignRight.Contains(gridColumn.ColumnIndex - 1) || gridColumn.ColumnIndex == 0)
                            {
                                // not needed
                                //var textAlignField = GridAccess.GetNonPublicFieldInfo(gridColumn, "TextAlign");
                                //if (textAlignField != null)
                                //{
                                //    textAlignField.SetValue(gridColumn, System.Windows.Forms.HorizontalAlignment.Right);
                                //}

                                // applies to the row number column 
                                var textAlignField2 = GridAccess.GetNonPublicFieldInfo(gridColumn, "m_myAlign");
                                if (textAlignField2 != null)
                                {
                                    textAlignField2.SetValue(gridColumn, System.Windows.Forms.HorizontalAlignment.Right);
                                }

                                var textAlignField3 = GridAccess.GetNonPublicFieldInfo(gridColumn, "m_textFormat");
                                if (textAlignField3 != null)
                                {
                                    System.Windows.Forms.TextFormatFlags flags = (System.Windows.Forms.TextFormatFlags)GridAccess.GetNonPublicField(gridColumn, "m_textFormat");
                                    textAlignField3.SetValue(gridColumn, flags | System.Windows.Forms.TextFormatFlags.Right);
                                }
                            }

                        }
                    }

                    grid.Refresh();

                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            try
            {
                // 2. Get open transaction info
                int openTranCount = 0;

                var SQLResultsControl = GridAccess.GetSQLResultsControl();
                var m_SqlExec = GridAccess.GetNonPublicField(SQLResultsControl, "m_sqlExec");

                Microsoft.Data.SqlClient.SqlConnection connection = GridAccess.GetNonPublicField(m_SqlExec, "m_conn") as Microsoft.Data.SqlClient.SqlConnection;
                if (connection.State == ConnectionState.Open)
                {
                    using (Microsoft.Data.SqlClient.SqlCommand command = new Microsoft.Data.SqlClient.SqlCommand("SELECT @@TRANCOUNT", connection))
                    {
                        var result = command.ExecuteScalar();
                        openTranCount = result != DBNull.Value ? Convert.ToInt32(result) : 0;
                    }
                }

                SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connection.ConnectionString);
                bool isColumnEncryptionSettingOn = builder.ColumnEncryptionSetting == SqlConnectionColumnEncryptionSetting.Enabled;

                var editorProperties = GridAccess.GetNonPublicField(m_SqlExec, "editorProperties");
                var editorProperties_ElapsedTime = (string)GridAccess.GetProperty(editorProperties, "ElapsedTime");

                GridAccess.ChangeStatusBarContent(openTranCount, isColumnEncryptionSettingOn, editorProperties_ElapsedTime);

                // Re-apply connection color after status bar update
                string dataSource = (string)GridAccess.GetProperty(connection, "DataSource");
                string database = (string)GridAccess.GetProperty(connection, "Database");
                GridAccess.ApplyConnectionColor(dataSource, database);

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            // query history
            try
            {

                var editorProperties = GridAccess.GetNonPublicField(QEOLESQLExec, "editorProperties");

                var textSpan = GridAccess.GetNonPublicField(QEOLESQLExec, "textSpan");

                var mConn = GridAccess.GetNonPublicField(QEOLESQLExec, "m_conn");

                var QueryHistoryObj = new QueryHistoryEntry();
                QueryHistoryObj.StartTime = (DateTime)GridAccess.GetNonPublicField(editorProperties, "startTime");
                QueryHistoryObj.FinishTime = (DateTime)GridAccess.GetNonPublicField(editorProperties, "finishTime");
                QueryHistoryObj.ElapsedTime = (string)GridAccess.GetProperty(editorProperties, "ElapsedTime");
                QueryHistoryObj.TotalRowsReturned = (long)GridAccess.GetProperty(editorProperties, "TotalRowsReturned");

                // Success, Failure -> not really clear how to track batch execution results...
                QueryHistoryObj.ExecResult = GridAccess.GetNonPublicField(QEOLESQLExec, "m_execResult").ToString();

                QueryHistoryObj.QueryText = (string)GridAccess.GetProperty(textSpan, "Text");
                QueryHistoryObj.DataSource = (string)GridAccess.GetProperty(mConn, "DataSource");
                QueryHistoryObj.DatabaseName = (string)GridAccess.GetProperty(mConn, "Database");
                QueryHistoryObj.LoginName = (string)GridAccess.GetProperty(editorProperties, "ChildLoginName");
                QueryHistoryObj.WorkstationId = (string)GridAccess.GetProperty(mConn, "WorkstationId");

                EnqueueDataForProcessing(QueryHistoryObj);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            if (!StatisticsSummaryStore.IsWindowOpen())
            {
                ThreadHelper.Generic.BeginInvoke(() => EnsureStatisticsExecutionHookForActiveWindow("post-skip"));

                return;
            }

            var captureVersion = Interlocked.Exchange(ref _pendingStatisticsCaptureVersion, 0);
            if (captureVersion == 0)
            {
                captureVersion = Interlocked.Increment(ref _statisticsCaptureVersion);
            }

            if (!StatisticsSummaryStore.BeginCapture(captureVersion))
            {
                return;
            }

            var captureCancellationTokenSource = CreateStatisticsCaptureCancellationTokenSource();

            _ = Task.Run(async delegate
            {
                try
                {
                    await CaptureStatisticsSummaryAsync(QEOLESQLExec, captureVersion, captureCancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    StatisticsSummaryStore.MarkUnavailable(captureVersion);
                    _logger.Error(ex, "Failed to capture statistics summary.");
                }
                finally
                {
                    ReleaseStatisticsCaptureCancellationTokenSource(captureCancellationTokenSource);
                }
            });


        }

        public static void CancelStatisticsCapture(bool updateStore = true)
        {
            CancellationTokenSource captureCancellationTokenSource;

            Interlocked.Exchange(ref _pendingStatisticsCaptureVersion, 0);

            lock (_statisticsCaptureSyncRoot)
            {
                captureCancellationTokenSource = _statisticsCaptureCancellationTokenSource;
                _statisticsCaptureCancellationTokenSource = null;
            }

            captureCancellationTokenSource?.Cancel();

            if (updateStore)
            {
                StatisticsSummaryStore.CancelCapture();
            }
        }

        private static CancellationTokenSource CreateStatisticsCaptureCancellationTokenSource()
        {
            CancellationTokenSource previousCancellationTokenSource;
            CancellationTokenSource nextCancellationTokenSource;

            lock (_statisticsCaptureSyncRoot)
            {
                previousCancellationTokenSource = _statisticsCaptureCancellationTokenSource;
                nextCancellationTokenSource = new CancellationTokenSource();
                _statisticsCaptureCancellationTokenSource = nextCancellationTokenSource;
            }

            previousCancellationTokenSource?.Cancel();
            return nextCancellationTokenSource;
        }

        private static void ReleaseStatisticsCaptureCancellationTokenSource(CancellationTokenSource captureCancellationTokenSource)
        {
            lock (_statisticsCaptureSyncRoot)
            {
                if (ReferenceEquals(_statisticsCaptureCancellationTokenSource, captureCancellationTokenSource))
                {
                    _statisticsCaptureCancellationTokenSource = null;
                }
            }

            captureCancellationTokenSource.Dispose();
        }

        private static async Task CaptureStatisticsSummaryAsync(object sqlExecutionContext, int captureVersion, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;

            cancellationToken.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var statisticsOutputOptions = GetStatisticsSummaryOutputOptions(sqlExecutionContext);
            if (!statisticsOutputOptions.HasStatisticsOutput)
            {
                StatisticsSummaryStore.MarkUnavailable(captureVersion, StatisticsSummaryCaptureStatus.StatisticsDisabled);
                return;
            }

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!StatisticsSummaryStore.IsWindowOpen())
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                GridAccess.TryFlushStatisticsMessages();

                var statisticsText = GridAccess.TryGetStatisticsMessagesText();
                if (TryStoreStatisticsSummary(sqlExecutionContext, statisticsText, captureVersion, out var summary))
                {
                    return;
                }

                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(150, cancellationToken);
                }
            }

            StatisticsSummaryStore.MarkUnavailable(captureVersion);
        }

        private sealed class StatisticsSummaryOutputOptions
        {
            public bool StatisticsIoEnabled { get; set; }
            public bool StatisticsTimeEnabled { get; set; }
            public bool HasStatisticsOutput => StatisticsIoEnabled || StatisticsTimeEnabled;
        }

        private static StatisticsSummaryOutputOptions GetStatisticsSummaryOutputOptions(object sqlExecutionContext)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(GridAccess.GetNonPublicField(sqlExecutionContext, "m_conn") is SqlConnection connection)
                    || connection.State != ConnectionState.Open)
                {
                    return new StatisticsSummaryOutputOptions
                    {
                        StatisticsIoEnabled = true,
                        StatisticsTimeEnabled = true,
                    };
                }

                using (var command = new SqlCommand("DBCC USEROPTIONS WITH NO_INFOMSGS;", connection))
                using (var reader = command.ExecuteReader())
                {
                    var options = new StatisticsSummaryOutputOptions();

                    while (reader.Read())
                    {
                        if (reader.FieldCount < 2)
                        {
                            continue;
                        }

                        var optionName = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var optionValue = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (string.IsNullOrWhiteSpace(optionName))
                        {
                            continue;
                        }

                        if (optionName.Equals("statistics io", StringComparison.OrdinalIgnoreCase))
                        {
                            options.StatisticsIoEnabled = IsUserOptionEnabled(optionValue);
                        }
                        else if (optionName.Equals("statistics time", StringComparison.OrdinalIgnoreCase))
                        {
                            options.StatisticsTimeEnabled = IsUserOptionEnabled(optionValue);
                        }
                    }

                    return options;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to inspect session statistics options.");
                return new StatisticsSummaryOutputOptions
                {
                    StatisticsIoEnabled = true,
                    StatisticsTimeEnabled = true,
                };
            }
        }

        private static bool IsUserOptionEnabled(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("on", StringComparison.OrdinalIgnoreCase)
                || value.Equals("set", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryStoreStatisticsSummary(object sqlExecutionContext, string statisticsText, int captureVersion, out StatisticsSummary summary)
        {
            summary = null;

            if (string.IsNullOrWhiteSpace(statisticsText))
            {
                return false;
            }

            summary = StatisticsSummaryParser.Parse(statisticsText);
            if (summary == null)
            {
                return false;
            }

            var textSpan = GridAccess.GetNonPublicField(sqlExecutionContext, "textSpan");
            var mConn = GridAccess.GetNonPublicField(sqlExecutionContext, "m_conn");

            summary.QueryText = GridAccess.GetProperty(textSpan, "Text") as string;
            summary.DataSource = GridAccess.GetProperty(mConn, "DataSource") as string;
            summary.DatabaseName = GridAccess.GetProperty(mConn, "Database") as string;

            StatisticsSummaryStore.Set(summary, captureVersion);
            return true;
        }

        private void CommandEvents_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                EnsureStatisticsExecutionHookForActiveWindow("query-execute-before");

                if (!StatisticsSummaryStore.IsWindowOpen())
                {
                    return;
                }

                var captureVersion = Interlocked.Increment(ref _statisticsCaptureVersion);
                Interlocked.Exchange(ref _pendingStatisticsCaptureVersion, captureVersion);

                StatisticsSummaryStore.BeginCapture(captureVersion);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to prepare statistics capture before query execution.");
            }
        }

        //it has been executed, but the Grid hasn't been created yet...
        private void CommandEvents_AfterExecute(string Guid, int ID, object CustomIn, object CustomOut)
        {
            //ThreadHelper.ThrowIfNotOnUIThread();
        }

    }
}
