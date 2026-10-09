using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MSSQLTool
{
    internal static class UpdateChecker
    {
        private const string ProductName = "MSSQLTool";
        private const string DisplayName = "MSSQL Tool";
        private const string ReleasesApiUrl = "https://api.github.com/repos/PuLuShen/MSSQLTOOL/releases/latest";
        private const string ReleasePageUrl = "https://github.com/PuLuShen/MSSQLTOOL/releases/latest";
        private const string StagedVsixFilePattern = "MSSQLTool-*.vsix";
        private const string StagedZipFilePattern = "MSSQLTool-*.zip";
        private const string ExpectedVsixName = "MSSQLTool.vsix";
        private const string Sha256DigestPrefix = "sha256:";

        /// <summary>
        /// Where a downloaded package waits for the install.  A folder of its own keeps it away from
        /// the temp-file sweep, which used to be able to delete a package the deferred install still
        /// needed.
        /// </summary>
        private const string StagingFolderName = "MSSQLToolUpdate";
        private const string HelperScriptName = "install-update.ps1";
        private const string HelperLogName = "update-install.log";
        private const string ReadyMarkerSuffix = ".ready";

        /// <summary>
        /// How long the detached helper waits for SSMS to exit before it gives up.  The install never
        /// waits inside SSMS: the helper outlives the process, so a slow download cannot cancel it.
        /// </summary>
        private const int HelperWaitMinutes = 5;
#if DEBUG
        private const string ForceUpdateAvailableEnvironmentVariable = "MSSQLTOOL_FORCE_UPDATE_AVAILABLE";
#endif

        private static int pendingStartupCheck;
        private static readonly object diagnosticsLock = new object();
        private static readonly object updateStateLock = new object();

        private static string lastUpdateResult = "No update check has run yet.";
        private static string stagedVsixPath;
        private static GitHubRelease stagedRelease;
        private static bool pendingUpdateOnClose;
        private static bool updateAvailable;
        private static bool stageDownloadInProgress;
        private static bool stageDownloadFailed;
        private static Task stageDownloadTask = Task.CompletedTask;
        private static Task cleanupDownloadedVsixFilesTask = Task.CompletedTask;
        private static UpdateInfoBar activeInfoBar;
        private static string stageVersion;
        private static string stageAssetUrl;
        private static string stageSha256;
        private static long stageAssetSize;
        private static string helperScriptText;

        /// <summary>
        /// The install helper, embedded from Resources\install-update.ps1.  It is written next to the
        /// downloaded package and started detached, so the install does not depend on this process.
        /// </summary>
        internal static string HelperScriptText
        {
            get
            {
                if (helperScriptText != null) return helperScriptText;

                try
                {
                    using (Stream stream = typeof(UpdateChecker).Assembly
                        .GetManifestResourceStream("MSSQLTool.Resources.install-update.ps1"))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                                return helperScriptText = reader.ReadToEnd();
                        }
                    }

                    Log("Update helper script not found in the assembly.");
                }
                catch (Exception ex)
                {
                    Log($"Load update helper script failed: {ex.Message}");
                }

                return helperScriptText = string.Empty;
            }
        }

        internal static event Action LastUpdateResultChanged;

        internal static string LastUpdateResult
        {
            get
            {
                lock (diagnosticsLock)
                {
                    return lastUpdateResult;
                }
            }
        }

        internal static void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                MSSQLToolPackage._logger?.Info(message);
            }
            catch
            {
            }
        }

        public static void ScheduleCheck(AsyncPackage package, bool enableUpdateChecks)
        {
            if (package == null)
            {
                return;
            }

            Task cleanupTask = ScheduleCleanupDownloadedVsixFiles();
            ReportLastInstallResult();

            if (!enableUpdateChecks)
            {
                Log("Update check skipped: disabled by settings.");
                SetLastUpdateResult(LocalizationManager.T("Update check skipped because it is disabled in settings."));
                return;
            }

            Interlocked.Exchange(ref pendingStartupCheck, 1);
            Log("Update check scheduled shortly after package initialization.");
            SetLastUpdateResult(LocalizationManager.T("Startup update check scheduled."));

            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var token = package.DisposalToken;
                    await cleanupTask;
                    await Task.Delay(TimeSpan.FromMilliseconds(900), token);

                    if (Interlocked.CompareExchange(ref pendingStartupCheck, 0, 1) != 1)
                    {
                        return;
                    }

                    Log("Running startup update check after initialization delay.");
                    SetLastUpdateResult(LocalizationManager.T("Running startup update check."));
                    await CheckForUpdatesAsync(package, token, showUpToDate: false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log($"Startup update check failed: {ex.Message}");
                    SetLastUpdateResult(LocalizationManager.Format("Startup update check failed: {0}", ex.Message));
                }
            });
        }

        public static void CheckNow(AsyncPackage package, bool ignoreSettings = true)
        {
            if (package == null)
            {
                return;
            }

            if (!ignoreSettings && !SettingsManager.GetEnableUpdateChecks())
            {
                Log("Manual update check skipped: disabled by settings.");
                SetLastUpdateResult(LocalizationManager.T("Manual update check skipped because it is disabled in settings."));
                return;
            }

            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    Log("Manual update check started.");
                    SetLastUpdateResult(LocalizationManager.T("Manual update check started."));
                    await CheckForUpdatesAsync(package, package.DisposalToken, showUpToDate: true);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log($"Manual update check failed: {ex.Message}");
                    SetLastUpdateResult(LocalizationManager.Format("Manual update check failed: {0}", ex.Message));
                }
            });
        }

        /// <summary>
        /// Hands the install to a detached helper so it survives this process.
        ///
        /// The previous version waited up to ten seconds for the background download and then gave up
        /// (and it made SSMS's close hang for those ten seconds).  A slow download therefore meant the
        /// update was never installed at all.  The helper waits for SSMS to exit, downloads the
        /// package itself when the staged one is not ready, verifies it and runs VSIXInstaller.
        /// </summary>
        internal static void LaunchDeferredUpdateOnClose()
        {
            string version;
            string assetUrl;
            string sha256;
            long assetSize;
            bool downloadFailed;
            GitHubRelease release;
            lock (updateStateLock)
            {
                if (!pendingUpdateOnClose)
                {
                    return;
                }

                pendingUpdateOnClose = false;
                version = stageVersion;
                assetUrl = stageAssetUrl;
                sha256 = stageSha256;
                assetSize = stageAssetSize;
                downloadFailed = stageDownloadFailed;
                release = stagedRelease;
            }

            string installerPath = GetVsixInstallerPath();
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                Log("Deferred update on close: VSIXInstaller.exe not found; opening the release page instead.");
                SetLastUpdateResult(LocalizationManager.T("Could not launch VSIXInstaller automatically; opened release page instead."));
                OpenUrl(release?.HtmlUrl ?? ReleasePageUrl);
                return;
            }

            string folder = StagingFolder;
            string vsixPath = string.IsNullOrWhiteSpace(version)
                ? Path.Combine(folder, "MSSQLTool-update.vsix")
                : Path.Combine(folder, StagedVsixName(version));
            string logPath = HelperLogPath;

            if (!TryStartUpdateHelper(folder, version, vsixPath, assetUrl, sha256, assetSize, installerPath, logPath, out string failure))
            {
                Log($"Deferred update on close: could not start the installer helper ({failure}).");
                SetLastUpdateResult(LocalizationManager.Format("Could not start the update installer ({0}); opened release page instead.", failure));
                OpenUrl(release?.HtmlUrl ?? ReleasePageUrl);
                return;
            }

            Log($"Deferred update on close: helper started for {version ?? "the latest release"}"
                + (downloadFailed ? " (the in-session download had failed; the helper downloads it)" : string.Empty)
                + "; it installs once SSMS has exited.");
            SetLastUpdateResult(downloadFailed
                ? LocalizationManager.T("The update is downloaded and installed once SSMS closes.")
                : LocalizationManager.T("The update runs the installer once SSMS closes."));
        }

        /// <summary>Writes the helper script and starts it detached from this process.</summary>
        internal static bool TryStartUpdateHelper(string folder, string version, string vsixPath, string assetUrl,
            string sha256, long assetSize, string installerPath, string logPath, out string failure)
        {
            failure = null;
            try
            {
                Directory.CreateDirectory(folder);
                string scriptPath = Path.Combine(folder, HelperScriptName);
                File.WriteAllText(scriptPath, HelperScriptText, new UTF8Encoding(false));

                string arguments = BuildHelperArguments(Process.GetCurrentProcess().Id, version, vsixPath, assetUrl,
                    sha256, assetSize, installerPath, ReleasePageUrl, logPath, HelperWaitMinutes);

                var start = new ProcessStartInfo
                {
                    FileName = GetPowerShellPath(),
                    Arguments = arguments,
                    // Detached: the helper must outlive SSMS, and this call must not block the close.
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(start);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Log($"Start update helper failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Quotes a value for the helper's command line.  The arguments after <c>-File</c> are split by
        /// the usual Windows rules, where single quotes are literal characters and an empty quoted
        /// value is dropped, so values use double quotes and empty ones are left out entirely.
        /// </summary>
        internal static string QuoteForPowerShell(string value)
            => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

        internal static string BuildHelperArguments(int processId, string version, string vsixPath, string assetUrl,
            string sha256, long assetSize, string installerPath, string releasePage, string logPath, int waitMinutes)
        {
            var builder = new StringBuilder();
            builder.Append("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ");
            builder.Append(QuoteForPowerShell(Path.Combine(Path.GetDirectoryName(logPath) ?? string.Empty, HelperScriptName)));
            builder.Append(" -TargetProcessId ").Append(processId.ToString(CultureInfo.InvariantCulture));
            builder.Append(" -WaitMinutes ").Append(waitMinutes.ToString(CultureInfo.InvariantCulture));
            // Empty values are omitted rather than passed as '': PowerShell's -File parser drops an
            // empty quoted argument, which fails the whole call with "missing an argument".
            AppendHelperArgument(builder, "Version", version);
            AppendHelperArgument(builder, "VsixPath", vsixPath);
            AppendHelperArgument(builder, "AssetUrl", assetUrl);
            AppendHelperArgument(builder, "Sha256", sha256);
            if (assetSize > 0) builder.Append(" -Size ").Append(assetSize.ToString(CultureInfo.InvariantCulture));
            AppendHelperArgument(builder, "Installer", installerPath);
            AppendHelperArgument(builder, "SsmsPath", GetSsmsDirectory());
            AppendHelperArgument(builder, "InstanceId", GetSsmsInstanceId());
            AppendHelperArgument(builder, "ReleasePage", releasePage);
            AppendHelperArgument(builder, "LogPath", logPath);
            return builder.ToString();
        }

        /// <summary>
        /// The SSMS instance id taken from this extension's own folder, which SSMS lays out as
        /// <c>%LOCALAPPDATA%\Microsoft\SSMS\&lt;version&gt;_&lt;instanceId&gt;\Extensions</c>.  Knowing the
        /// running instance means VSIXInstaller patches that one instead of guessing.
        /// </summary>
        internal static string GetSsmsInstanceId()
        {
            try
            {
                return ExtractInstanceId(AppDomain.CurrentDomain.BaseDirectory);
            }
            catch (Exception ex)
            {
                Log($"Resolve the SSMS instance id failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Pulls the instance id out of a path segment such as "22.0_6d5d8555".</summary>
        internal static string ExtractInstanceId(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            foreach (string segment in path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = segment.IndexOf('_');
                if (separator <= 0 || separator == segment.Length - 1) continue;

                string version = segment.Substring(0, separator);
                string id = segment.Substring(separator + 1);
                if (id.IndexOf('.') >= 0) continue;   // "22.0_6d5d8555" only; skip dotted file names
                if (version.Length < 2 || id.Length < 6) continue;

                bool looksLikeVersion = true;
                foreach (char c in version)
                {
                    if (char.IsDigit(c) || c == '.') continue;
                    looksLikeVersion = false;
                    break;
                }

                if (!looksLikeVersion) continue;

                bool looksLikeInstanceId = true;
                foreach (char c in id)
                {
                    if (Uri.IsHexDigit(c)) continue;
                    looksLikeInstanceId = false;
                    break;
                }

                if (!looksLikeInstanceId) continue;

                return id;
            }

            return null;
        }

        /// <summary>
        /// The folder that holds Ssms.exe, used by the helper to look up the instance id VSIXInstaller
        /// needs for this isolated shell.
        /// </summary>
        internal static string GetSsmsDirectory()
        {
            try
            {
                string exePath = Process.GetCurrentProcess()?.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    string directory = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrWhiteSpace(directory)) return directory;
                }
            }
            catch (Exception ex)
            {
                Log($"Resolve the SSMS folder failed: {ex.Message}");
            }

            try
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AppendHelperArgument(StringBuilder builder, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            builder.Append(" -").Append(name).Append(' ').Append(QuoteForPowerShell(value));
        }

        internal static string GetPowerShellPath()
        {
            try
            {
                string candidate = Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex)
            {
                Log($"Resolve powershell.exe failed: {ex.Message}");
            }

            return "powershell.exe";
        }

        internal static string StagingFolder => Path.Combine(Path.GetTempPath(), StagingFolderName);

        /// <summary>
        /// Where the install helper writes its log.  It lives with the other diagnostics instead of in
        /// the temp staging folder, which is swept at startup, so a failed install stays readable.
        /// </summary>
        internal static string HelperLogPath => Path.Combine(AppPaths.LogsFolder, HelperLogName);

        internal static string StagedVsixName(string version) => $"MSSQLTool-{version}.vsix";

        internal static string ReadyMarkerPath(string vsixPath) => vsixPath + ReadyMarkerSuffix;


        private static async Task CheckForUpdatesAsync(AsyncPackage package, CancellationToken token, bool showUpToDate)
        {
            var currentVersion = GetCurrentVersion();
            if (currentVersion == null)
            {
                Log("Update check failed: current version unavailable.");
                SetLastUpdateResult(LocalizationManager.T("Update check failed because current version could not be determined."));
                return;
            }

            var release = await GetLatestReleaseAsync(token);
            if (release == null || release.Draft)
            {
                string detail = string.IsNullOrWhiteSpace(lastFetchDetail) ? string.Empty : " (" + lastFetchDetail + ")";
                Log("Update check failed: release info unavailable.");
                SetLastUpdateResult(detail.Length == 0
                    ? LocalizationManager.T("Update check failed because latest release info was unavailable.")
                    : LocalizationManager.Format("Update check failed because latest release info was unavailable ({0}).", detail));
                return;
            }

            var latestVersion = ParseVersion(release.TagName);
            if (latestVersion == null)
            {
                Log("Update check failed: latest version parse failed.");
                SetLastUpdateResult(LocalizationManager.T("Update check failed because latest release version could not be parsed."));
                return;
            }

            LastLatestVersion = latestVersion;

            bool forceUpdateAvailable = false;
#if DEBUG
            forceUpdateAvailable = IsForceUpdateAvailableForDebug();
            if (forceUpdateAvailable)
            {
                Log($"Update check: debug force enabled by {ForceUpdateAvailableEnvironmentVariable}.");
            }
#endif

            if (latestVersion <= currentVersion && !forceUpdateAvailable)
            {
                lock (updateStateLock) { updateAvailable = false; }
                Log($"Update check: already on latest ({currentVersion}).");
                SetLastUpdateResult(LocalizationManager.Format("Up to date ({0}). Latest release is {1}.",
                    FormatVersion(currentVersion), FormatVersion(latestVersion)));
                if (showUpToDate)
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    ShowUpToDatePrompt(package, currentVersion);
                }

                return;
            }

            await package.JoinableTaskFactory.SwitchToMainThreadAsync(token);

            // The Updates page offers its own "install when SSMS closes" button, which is enabled by
            // this flag.
            lock (updateStateLock)
            {
                updateAvailable = true;
            }

            if (forceUpdateAvailable)
            {
                Log($"Update prompt forced for testing: latest {latestVersion}, current {currentVersion}.");
                SetLastUpdateResult(LocalizationManager.Format("Debug update test forced. Showing latest release {0} while current version is {1}.",
                    FormatVersion(latestVersion), FormatVersion(currentVersion)));
            }
            else
            {
                Log($"Update available: {latestVersion} (current {currentVersion}).");
                SetLastUpdateResult(LocalizationManager.Format("Update available: {0} -> {1}.",
                    FormatVersion(currentVersion), FormatVersion(latestVersion)));
            }

            ShowUpdatePrompt(package, release, latestVersion);
        }

#if DEBUG
        private static bool IsForceUpdateAvailableForDebug()
        {
            string value = Environment.GetEnvironmentVariable(ForceUpdateAvailableEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
#endif

        private static async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken token)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductName);
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                    using (var response = await client.GetAsync(ReleasesApiUrl, token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            // GitHub answers 404 for a private repository when the request carries no
                            // token, which is why the status has to name the response.
                            lastFetchDetail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                            Log($"Update check HTTP {response.StatusCode}");
                            return null;
                        }

                        lastFetchDetail = null;
                        string json = await response.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeObject<GitHubRelease>(json);
                    }
                }
            }
            catch (Exception ex)
            {
                lastFetchDetail = ex.Message;
                Log($"Update check fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Why the last release lookup failed, shown next to the failure message.</summary>
        private static volatile string lastFetchDetail;

        /// <summary>The newest release seen by a check, or null while no check has succeeded.</summary>
        internal static Version LastLatestVersion { get; private set; }

        private static Version currentVersion;

        internal static Version GetCurrentVersion()
        {
            // Cached: the Updates page asks for it on every progress tick, and logging it each time
            // buried the rest of the log.
            if (currentVersion != null) return currentVersion;

            try
            {
                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (assemblyVersion != null)
                {
                    currentVersion = assemblyVersion;
                    Log($"Update check current version (assembly): {assemblyVersion}");
                    return assemblyVersion;
                }
            }
            catch (Exception ex)
            {
                Log($"Update check version parse failed: {ex.Message}");
            }

            Log("Update check current version not found in assembly. Defaulting to 0.0.0.");
            return new Version(0, 0, 0, 0);
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string value = text.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            if (Version.TryParse(value, out Version parsed))
            {
                return parsed;
            }

            return null;
        }

        private static void ShowUpdatePrompt(AsyncPackage package, GitHubRelease release, Version latestVersion)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (activeInfoBar != null)
                {
                    activeInfoBar.Dismiss();
                    activeInfoBar = null;
                }

                activeInfoBar = new UpdateInfoBar(
                    package,
                    FormatVersion(latestVersion),
                    release?.HtmlUrl,
                    action => HandleInfoBarAction(action));

                if (activeInfoBar.TryShow())
                {
                    Log("Update prompt shown via InfoBar.");
                    StageDownloadInBackground(release);
                    return;
                }

                Log("InfoBar unavailable; opening release page as fallback.");
                activeInfoBar = null;
                SetLastUpdateResult(LocalizationManager.T("Update is available, but the InfoBar was unavailable. Opened release page."));
                OpenUrl(release?.HtmlUrl ?? ReleasePageUrl);
            }
            catch (Exception ex)
            {
                Log($"Update prompt failed: {ex.Message}");
            }
        }

        private static void HandleInfoBarAction(string action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.Equals(action, UpdateInfoBar.ActionUpdateOnClose, StringComparison.Ordinal))
            {
                return;
            }

            activeInfoBar = null;
            ArmDeferredUpdate();
        }

        /// <summary>
        /// Schedules the install for the moment SSMS closes.  Used by the InfoBar action and by the
        /// button on the Updates page, so the download alone never has to be the only hint.
        /// </summary>
        internal static void ArmDeferredUpdate()
        {
            string vsixPath;
            bool inProgress;
            lock (updateStateLock)
            {
                pendingUpdateOnClose = true;
                vsixPath = stagedVsixPath;
                inProgress = stageDownloadInProgress;
            }

            Log("Deferred update on close enabled.");

            if (!string.IsNullOrWhiteSpace(vsixPath) && File.Exists(vsixPath))
            {
                SetLastUpdateResult(LocalizationManager.T("Update will install when SSMS closes."));
                return;
            }

            if (inProgress)
            {
                SetLastUpdateResult(LocalizationManager.T("Update will install when SSMS closes; the download continues until then."));
                return;
            }

            // The close-time helper downloads the package itself, so an unfinished download here is
            // not a failure.
            SetLastUpdateResult(LocalizationManager.T("The update runs the installer once SSMS closes."));
        }

        /// <summary>True while an install is scheduled or a newer release was found.</summary>
        internal static bool IsUpdateAvailable
        {
            get
            {
                lock (updateStateLock)
                {
                    return pendingUpdateOnClose || updateAvailable;
                }
            }
        }

        /// <summary>True once the user scheduled the close-time install.</summary>
        internal static bool IsDeferredUpdateArmed
        {
            get
            {
                lock (updateStateLock)
                {
                    return pendingUpdateOnClose;
                }
            }
        }

        private static void ShowUpToDatePrompt(AsyncPackage package, Version currentVersion)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                VsShellUtilities.ShowMessageBox(
                    package,
                    LocalizationManager.Format("{0} is up to date ({1}).", DisplayName, FormatVersion(currentVersion)),
                    LocalizationManager.T("Information"),
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            catch (Exception ex)
            {
                Log($"Up-to-date prompt failed: {ex.Message}");
            }
        }

        private static void StageDownloadInBackground(GitHubRelease release)
        {
            if (release == null)
            {
                return;
            }

            GitHubAsset asset = GetInstallAsset(release);
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                lock (updateStateLock)
                {
                    stagedRelease = release;
                    stageDownloadFailed = true;
                }

                Log("Stage download skipped: no ZIP or VSIX asset found.");
                MarkStageDownloadFailed(LocalizationManager.T("Update download failed: no ZIP or VSIX release asset was found."));
                return;
            }

            bool hasDigest;
            string expectedSha256 = GetAssetSha256(asset, out hasDigest);
            if (hasDigest && string.IsNullOrWhiteSpace(expectedSha256))
            {
                lock (updateStateLock)
                {
                    stagedRelease = release;
                    stageDownloadFailed = true;
                }

                Log("Stage download aborted: GitHub release digest is invalid.");
                MarkStageDownloadFailed(LocalizationManager.T("Update download failed: invalid GitHub release digest."));
                return;
            }

            if (!hasDigest)
            {
                Log("Stage download: GitHub release digest was not provided; download will not be checksum verified.");
            }

            // Record what the helper needs before the download starts: even when this download never
            // finishes, the helper can still fetch the package after SSMS has closed.
            Version releaseVersion = ParseVersion(release.TagName);
            lock (updateStateLock)
            {
                stagedRelease = release;
                stageDownloadFailed = false;
                stageVersion = releaseVersion == null ? null : FormatVersion(releaseVersion);
                stageAssetUrl = asset.DownloadUrl;
                stageSha256 = expectedSha256;
                stageAssetSize = asset.Size;
            }

            Task cleanupTask = GetCleanupDownloadedVsixFilesTask();
            var completion = new TaskCompletionSource<bool>();
            lock (updateStateLock)
            {
                stageDownloadInProgress = true;
                stageDownloadTask = completion.Task;
            }

            _ = Task.Run(async () =>
            {
                string partialPath = null;
                string extractedVsixPath = null;
                bool staged = false;
                bool verified = false;

                try
                {
                    SetLastUpdateResult(LocalizationManager.T("Downloading update package in background."));

                    await cleanupTask;

                    string folder = StagingFolder;
                    Directory.CreateDirectory(folder);

                    bool isZip = asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    string finalPath = Path.Combine(folder, StagedVsixName(stageVersion ?? "latest"));
                    partialPath = finalPath + (isZip ? ".zip.part" : ".part");
                    BeginDownloadProgress(stageVersion);
                    await DownloadFileAsync(asset.DownloadUrl, partialPath, CancellationToken.None, ReportDownloadProgress);

                    if (!string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        string actualSha256 = ComputeSha256Hex(partialPath);
                        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"Stage download aborted: checksum mismatch. Expected={expectedSha256}, Actual={actualSha256}");
                            MarkStageDownloadFailed(LocalizationManager.T("Update download failed: checksum verification failed."));
                            return;
                        }

                        verified = true;
                    }

                    string preparedVsix = isZip ? ExtractVsixFromZip(partialPath) : partialPath;
                    if (!string.Equals(preparedVsix, finalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(finalPath);
                        File.Move(preparedVsix, finalPath);
                    }

                    partialPath = null;
                    extractedVsixPath = finalPath;
                    // The marker is what tells the helper (and the next start) that the file is complete.
                    WriteReadyMarker(finalPath, stageVersion, expectedSha256, asset.DownloadUrl);
                    ReplaceStagedVsixPath(finalPath);
                    staged = true;
                    Log($"Update package staged at: {finalPath}");

                    bool installPending;
                    lock (updateStateLock)
                    {
                        installPending = pendingUpdateOnClose;
                    }

                    string statusSuffix = verified
                        ? LocalizationManager.T("downloaded and verified")
                        : LocalizationManager.T("downloaded without checksum verification");

                    if (installPending)
                    {
                        SetLastUpdateResult(LocalizationManager.Format("Update package {0}. It will install when SSMS closes.", statusSuffix));
                    }
                    else
                    {
                        SetLastUpdateResult(LocalizationManager.Format("Update package {0}. Ready to install on close.", statusSuffix));
                    }
                }
                catch (Exception ex)
                {
                    Log($"Stage download failed: {ex.Message}");
                    MarkStageDownloadFailed(LocalizationManager.Format("Update download failed: {0}", ex.Message));
                }
                finally
                {
                    lock (updateStateLock)
                    {
                        stageDownloadInProgress = false;
                    }

                    EndDownloadProgress();

                    // A failed or superseded download must not leave a half file behind.
                    TryDeleteFile(partialPath);
                    if (!staged)
                    {
                        TryDeleteFile(extractedVsixPath);
                    }

                    completion.TrySetResult(true);
                }
            });
        }

        private static void MarkStageDownloadFailed(string message)
        {
            lock (updateStateLock)
            {
                stageDownloadFailed = true;
            }

            // No browser fallback here: the close-time helper retries the download by itself and opens
            // the release page only when it cannot install either.
            SetLastUpdateResult(message);
        }

        /// <summary>Marks a staged package as complete; the install helper trusts it only with this file.</summary>
        private static void WriteReadyMarker(string vsixPath, string version, string sha256, string url)
        {
            try
            {
                File.WriteAllText(ReadyMarkerPath(vsixPath),
                    $"version={version}\r\nsha256={sha256}\r\nurl={url}\r\ncompleted={DateTime.UtcNow:O}\r\n",
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log($"Write update ready marker failed: {ex.Message}");
            }
        }

        private static GitHubAsset GetInstallAsset(GitHubRelease release)
        {
            if (release?.Assets == null)
            {
                return null;
            }

            GitHubAsset firstZip = null;
            GitHubAsset firstVsix = null;

            foreach (var asset in release.Assets)
            {
                if (string.IsNullOrWhiteSpace(asset?.Name))
                {
                    continue;
                }

                if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (asset.Name.IndexOf(ProductName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return asset;
                    }

                    firstZip = firstZip ?? asset;
                }
                else if (asset.Name.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase))
                {
                    firstVsix = firstVsix ?? asset;
                }
            }

            return firstZip ?? firstVsix;
        }

        private static string ExtractVsixFromZip(string zipPath)
        {
            // Extracted inside the staging folder: the caller moves it into place, and the startup
            // sweep there is the only thing allowed to clean it up.
            string stagingFolder = StagingFolder;
            Directory.CreateDirectory(stagingFolder);
            string tempVsixPath = Path.Combine(stagingFolder, $"MSSQLTool-{Guid.NewGuid():N}.vsix.part");

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry selectedEntry = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string fileName = Path.GetFileName(entry.FullName);
                    if (string.Equals(fileName, ExpectedVsixName, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedEntry = entry;
                        break;
                    }

                    if (selectedEntry == null && fileName.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedEntry = entry;
                    }
                }

                if (selectedEntry == null)
                {
                    throw new InvalidOperationException("The release ZIP did not contain a VSIX file.");
                }

                using (Stream source = selectedEntry.Open())
                using (Stream target = File.Create(tempVsixPath))
                {
                    source.CopyTo(target);
                }
            }

            return tempVsixPath;
        }

        private static string GetAssetSha256(GitHubAsset asset, out bool hasDigest)
        {
            string digest = asset?.Digest;
            if (string.IsNullOrWhiteSpace(digest))
            {
                hasDigest = false;
                return null;
            }

            hasDigest = true;
            string trimmed = digest.Trim();
            if (!trimmed.StartsWith(Sha256DigestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string value = trimmed.Substring(Sha256DigestPrefix.Length).Trim();
            if (Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$"))
            {
                return value.ToLowerInvariant();
            }

            return null;
        }

        private static Task ScheduleCleanupDownloadedVsixFiles()
        {
            Task cleanupTask = Task.Run((Action)CleanupDownloadedVsixFiles);
            lock (updateStateLock)
            {
                cleanupDownloadedVsixFilesTask = cleanupTask;
            }

            return cleanupTask;
        }

        private static Task GetCleanupDownloadedVsixFilesTask()
        {
            lock (updateStateLock)
            {
                return cleanupDownloadedVsixFilesTask;
            }
        }

        private static void CleanupDownloadedVsixFiles()
        {
            string activePath;
            lock (updateStateLock)
            {
                if (stageDownloadInProgress)
                {
                    Log("Cleanup staged update files skipped because a download is in progress.");
                    return;
                }

                activePath = stagedVsixPath;
                stagedVsixPath = null;
                pendingUpdateOnClose = false;
                stageDownloadFailed = false;
                stagedRelease = null;
            }

            try
            {
                // Files from releases up to 4.62 lived directly in the temp folder.
                string tempDirectory = Path.GetTempPath();
                foreach (string filePath in Directory.GetFiles(tempDirectory, StagedVsixFilePattern))
                {
                    TryDeleteFile(filePath);
                }

                foreach (string filePath in Directory.GetFiles(tempDirectory, StagedZipFilePattern))
                {
                    TryDeleteFile(filePath);
                }

                // The staging folder is shared with the install helper, which may still be running
                // right after SSMS closed: only packages older than the helper's wait window are
                // stale enough to delete.
                string folder = StagingFolder;
                if (Directory.Exists(folder))
                {
                    DateTime cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(HelperWaitMinutes + 5);
                    foreach (string filePath in Directory.GetFiles(folder, "MSSQLTool-*"))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(filePath) > cutoff)
                            {
                                Log($"Cleanup kept a recent staged file: {filePath}");
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        TryDeleteFile(filePath);
                    }
                }

                if (!string.IsNullOrWhiteSpace(activePath) && File.Exists(activePath))
                {
                    TryDeleteFile(activePath);
                }
            }
            catch (Exception ex)
            {
                Log($"Cleanup staged update files failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reports what the detached install helper did after the previous SSMS session, so a failed
        /// install is visible instead of silent.
        /// </summary>
        private static void ReportLastInstallResult()
        {
            try
            {
                string logPath = HelperLogPath;
                if (!File.Exists(logPath)) return;

                string[] lines = File.ReadAllLines(logPath);
                if (lines.Length == 0) return;

                string lastLine = lines[lines.Length - 1];
                SetLastUpdateResult(lastLine);
                Log($"Previous update install: {lastLine}");
            }
            catch (Exception ex)
            {
                Log($"Read update install log failed: {ex.Message}");
            }
        }

        private static void ReplaceStagedVsixPath(string tempPath)
        {
            if (string.IsNullOrWhiteSpace(tempPath))
            {
                return;
            }

            lock (updateStateLock)
            {
                string previousPath = stagedVsixPath;
                stagedVsixPath = tempPath;

                if (!string.IsNullOrWhiteSpace(previousPath) &&
                    !string.Equals(previousPath, tempPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(previousPath);
                }
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Log($"Deleted staged update file: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Log($"Delete staged update file failed for '{filePath}': {ex.Message}");
            }
        }

        private static string ComputeSha256Hex(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static async Task DownloadFileAsync(string url, string path, CancellationToken token,
            Action<long, long> onProgress = null)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductName);
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? -1;
                    long receivedBytes = 0;
                    onProgress?.Invoke(0, totalBytes);

                    using (Stream source = await response.Content.ReadAsStreamAsync())
                    using (Stream target = File.Create(path))
                    {
                        // Read in chunks so the download can be reported while it runs; the previous
                        // CopyToAsync gave no chance to show any progress at all.
                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            await target.WriteAsync(buffer, 0, read, token);
                            receivedBytes += read;
                            onProgress?.Invoke(receivedBytes, totalBytes);
                        }
                    }
                }
            }
        }

        /// <summary>How far the background download got, as shown on the Updates page.</summary>
        internal sealed class DownloadProgress
        {
            public string Version { get; set; }
            public long BytesReceived { get; set; }
            public long TotalBytes { get; set; }
            public bool IsActive { get; set; }

            /// <summary>0-100, or -1 while the server does not report a length.</summary>
            public int Percent => TotalBytes <= 0
                ? -1
                : (int)Math.Min(100, Math.Max(0, BytesReceived * 100 / TotalBytes));
        }

        private static DownloadProgress downloadProgress;
        private static DateTime lastProgressReportUtc = DateTime.MinValue;

        /// <summary>The download in flight, or null when nothing is being downloaded.</summary>
        internal static DownloadProgress CurrentDownloadProgress
        {
            get
            {
                lock (updateStateLock)
                {
                    return downloadProgress;
                }
            }
        }

        internal static void BeginDownloadProgress(string version)
        {
            lock (updateStateLock)
            {
                downloadProgress = new DownloadProgress { Version = version, IsActive = true };
                lastProgressReportUtc = DateTime.MinValue;
            }
        }

        internal static void EndDownloadProgress()
        {
            lock (updateStateLock)
            {
                downloadProgress = null;
            }
        }

        /// <summary>Stores the byte counts and refreshes the status text a few times per second.</summary>
        internal static void ReportDownloadProgress(long receivedBytes, long totalBytes)
        {
            bool report;
            lock (updateStateLock)
            {
                if (downloadProgress == null || !downloadProgress.IsActive) return;

                downloadProgress.BytesReceived = receivedBytes;
                if (totalBytes > 0) downloadProgress.TotalBytes = totalBytes;

                // The status text drives the UI, so a few updates per second are plenty.
                report = (DateTime.UtcNow - lastProgressReportUtc).TotalMilliseconds >= 250;
                if (report) lastProgressReportUtc = DateTime.UtcNow;
            }

            if (!report) return;
            SetLastUpdateResult(DescribeDownloadProgress(CurrentDownloadProgress));
        }

        internal static string DescribeDownloadProgress(DownloadProgress progress)
        {
            if (progress == null) return string.Empty;
            if (progress.Percent >= 0)
                return LocalizationManager.Format("Downloading update package: {0}% ({1} of {2})",
                    progress.Percent, FormatBytes(progress.BytesReceived), FormatBytes(progress.TotalBytes));

            return LocalizationManager.Format("Downloading update package: {0}",
                FormatBytes(progress.BytesReceived));
        }

        /// <summary>Byte counts in the units a person reads them in.</summary>
        internal static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "-";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";

            double kilobytes = bytes / 1024.0;
            if (kilobytes < 1024) return kilobytes.ToString("0.0", CultureInfo.InvariantCulture) + " KB";

            double megabytes = kilobytes / 1024.0;
            if (megabytes < 1024) return megabytes.ToString("0.0", CultureInfo.InvariantCulture) + " MB";

            return (megabytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        private static string GetVsixInstallerPath()
        {
            try
            {
                string exePath = Process.GetCurrentProcess()?.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    string exeDir = Path.GetDirectoryName(exePath);
                    string candidate = Path.Combine(exeDir ?? string.Empty, "VSIXInstaller.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Resolve VSIXInstaller from process failed: {ex.Message}");
            }

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir ?? string.Empty, "VSIXInstaller.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Log($"Resolve VSIXInstaller from AppDomain failed: {ex.Message}");
            }

            return null;
        }

        internal static string FormatVersion(Version version)
        {
            if (version == null)
            {
                return "0.0.0";
            }

            if (version.Revision > 0)
            {
                return version.ToString(4);
            }

            if (version.Build > 0)
            {
                return version.ToString(3);
            }

            return version.ToString(2);
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log($"Open URL failed: {ex.Message}");
            }
        }

        private static void SetLastUpdateResult(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
            lock (diagnosticsLock)
            {
                lastUpdateResult = stamped;
            }

            try
            {
                LastUpdateResultChanged?.Invoke();
            }
            catch
            {
            }
        }

        private sealed class GitHubRelease
        {
            [JsonProperty("tag_name")]
            public string TagName { get; set; }

            [JsonProperty("html_url")]
            public string HtmlUrl { get; set; }

            [JsonProperty("draft")]
            public bool Draft { get; set; }

            [JsonProperty("prerelease")]
            public bool Prerelease { get; set; }

            [JsonProperty("assets")]
            public List<GitHubAsset> Assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("digest")]
            public string Digest { get; set; }

            /// <summary>Byte count of the asset, used to detect a download that was cut short.</summary>
            [JsonProperty("size")]
            public long Size { get; set; }

            [JsonProperty("browser_download_url")]
            public string DownloadUrl { get; set; }
        }
    }
}