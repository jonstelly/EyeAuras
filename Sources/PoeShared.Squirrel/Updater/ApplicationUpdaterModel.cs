using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Disposables;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using log4net;
using PoeShared.Scaffolding;
using PoeShared.Squirrel.Core;
using Squirrel;

namespace PoeShared.Squirrel.Updater
{
    internal sealed class ApplicationUpdaterModel : DisposableReactiveObject, IApplicationUpdaterModel
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ApplicationUpdaterModel));

        private static readonly string ApplicationName = Process.GetCurrentProcess().ProcessName + ".exe";

        private UpdateInfo latestVersion;
        private Version mostRecentVersion;
        private DirectoryInfo mostRecentVersionAppFolder;
        private UpdateSourceInfo updateSource;
        private int progressPercent;
        private bool isBusy;

        public ApplicationUpdaterModel()
        {
            SquirrelAwareApp.HandleEvents(
                OnInitialInstall,
                OnAppUpdate,
                onAppUninstall: OnAppUninstall,
                onFirstRun: OnFirstRun);

            MostRecentVersionAppFolder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            UpdatedVersion = null;
        }

        public DirectoryInfo MostRecentVersionAppFolder
        {
            get => mostRecentVersionAppFolder;
            set => RaiseAndSetIfChanged(ref mostRecentVersionAppFolder, value);
        }

        public UpdateSourceInfo UpdateSource
        {
            get => updateSource;
            set => RaiseAndSetIfChanged(ref updateSource, value);
        }

        public Version UpdatedVersion
        {
            get => mostRecentVersion;
            private set => RaiseAndSetIfChanged(ref mostRecentVersion, value);
        }

        public UpdateInfo LatestVersion
        {
            get => latestVersion;
            private set => RaiseAndSetIfChanged(ref latestVersion, value);
        }

        public int ProgressPercent
        {
            get => progressPercent;
            private set => RaiseAndSetIfChanged(ref progressPercent, value);
        }

        public bool IsBusy
        {
            get => isBusy;
            private set => RaiseAndSetIfChanged(ref isBusy, value);
        }

        public async Task ApplyRelease(UpdateInfo updateInfo)
        {
            Guard.ArgumentNotNull(updateInfo, nameof(updateInfo));

            Log.Debug($"Applying update {updateInfo.DumpToTextRaw()}");

            using var unused = CreateIsBusyAnchor();
            using var mgr = await CreateManager();
            
            Log.Debug("Downloading releases...");
            await mgr.DownloadReleases(updateInfo.ReleasesToApply, x => UpdateProgress(x, "DownloadRelease"));

            string newVersionFolder;
            if (string.IsNullOrWhiteSpace(GetSquirrelUpdateExe()))
            {
                Log.Warn("Not a Squirrel-app or debug mode detected, skipping update");
                newVersionFolder = AppDomain.CurrentDomain.BaseDirectory;
                for (var i = 0; i < 100; i++)
                {
                    UpdateProgress(i, "Debug");
                    await Task.Delay(5000);
                }
            }
            else
            {
                Log.Debug("Applying releases...");
                newVersionFolder = await mgr.ApplyReleases(updateInfo, x => UpdateProgress(x, "ApplyRelease"));
            }

            var lastAppliedRelease = updateInfo.ReleasesToApply.Last();

            Log.Debug(
                $"Update completed to v{lastAppliedRelease.Version}, result: {newVersionFolder}");

            if (string.IsNullOrWhiteSpace(newVersionFolder))
            {
                throw new ApplicationException("Expected non-empty new version folder path");
            }

            MostRecentVersionAppFolder = new DirectoryInfo(newVersionFolder);
            UpdatedVersion = lastAppliedRelease.Version.Version;
            LatestVersion = null;
            ProgressPercent = 0;
        }

        public void Reset()
        {
            LatestVersion = null;
            UpdatedVersion = null;
            IsBusy = false;
            ProgressPercent = 0;
        }

        /// <summary>
        ///     Checks whether update exist and if so, downloads it
        /// </summary>
        /// <returns>True if application was updated</returns>
        public async Task<UpdateInfo> CheckForUpdates()
        {
            Log.Debug("Update check requested");
            using var unused = CreateIsBusyAnchor();
            Reset();

            using var mgr = await CreateManager();
            Log.Debug("Checking for updates...");

            var updateInfo = await mgr.CheckForUpdate(true, CheckUpdateProgress);

            Log.Debug($"UpdateInfo:\r\n{updateInfo?.DumpToText()}");
            if (updateInfo == null || updateInfo.ReleasesToApply.Count == 0)
            {
                return null;
            }

            LatestVersion = updateInfo;
            return updateInfo;
        }

        public async Task RestartApplication()
        {
            using var unused = CreateIsBusyAnchor();
            
            var updatedExecutable = new FileInfo(Path.Combine(mostRecentVersionAppFolder.FullName, ApplicationName));
            Log.Debug(
                $"Restarting app, folder: {mostRecentVersionAppFolder}, appName: {ApplicationName}, exePath: {updatedExecutable}(exists: {updatedExecutable.Exists})...");

            if (!updatedExecutable.Exists)
            {
                throw new FileNotFoundException("Application executable was not found", updatedExecutable.FullName);
            }

            var squirrelUpdater = GetSquirrelUpdateExe();
            var squirrelArgs = $"--processStartAndWait {updatedExecutable.FullName}";

            Log.Debug($"Starting Squirrel updater @ '{squirrelUpdater}', args: {squirrelArgs} ...");
            var updaterProcess = Process.Start(squirrelUpdater, squirrelArgs);
            if (updaterProcess == null)
            {
                throw new FileNotFoundException($"Failed to start updater @ '{squirrelUpdater}'");
            }

            Log.Debug($"Process spawned, PID: {updaterProcess.Id}");
            await Task.Delay(2000);

            Log.Debug("Terminating application...");
            Application.Current.Shutdown(0);
        }

        private async Task<IUpdateManager> CreateManager()
        {
            var appName = Process.GetCurrentProcess().ProcessName;
            var rootDirectory = default(string);

            if (AppArguments.Instance.IsDebugMode || string.IsNullOrWhiteSpace(GetSquirrelUpdateExe()))
            {
                rootDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }

            Log.Debug($"AppName: {appName}, root directory: {rootDirectory}");

            Log.Debug($"Using update source: {updateSource.DumpToTextRaw()}");
            var downloader = new BasicAuthFileDownloader(
                new NetworkCredential(
                    updateSource.Username?.ToUnsecuredString(),
                    updateSource.Password?.ToUnsecuredString()));
            if (updateSource.Uri.Contains("github"))
            {
                Log.Debug($"Using GitHub source: {updateSource.DumpToTextRaw()}");

                var mgr = PoeUpdateManager.GitHubUpdateManager(
                    updateSource.Uri,
                    appName,
                    rootDirectory,
                    downloader);
                return await mgr;
            }
            else
            {
                Log.Debug($"Using BasicHTTP source: {updateSource.DumpToTextRaw()}");
                var mgr = new PoeUpdateManager(updateSource.Uri, appName, rootDirectory, downloader);
                return mgr;
            }
        }

        private void UpdateProgress(int progressPercent, string taskName)
        {
            Log.Debug($"[ApplicationUpdaterModel.UpdateProgress] {taskName} is in progress: {progressPercent}%");
            ProgressPercent = progressPercent;
        }

        private void CheckUpdateProgress(int progressPercent)
        {
            Log.Debug($"[ApplicationUpdaterModel.CheckUpdateProgress] Check update is in progress: {progressPercent}%");
        }

        private void OnAppUninstall(Version appVersion)
        {
            Log.Debug($"[ApplicationUpdaterModel.OnAppUninstall] Uninstalling v{appVersion}...");
        }

        private void OnAppUpdate(Version appVersion)
        {
            Log.Debug($"[ApplicationUpdaterModel.OnAppUpdate] Updating v{appVersion}...");
        }

        private void OnInitialInstall(Version appVersion)
        {
            Log.Debug($"[ApplicationUpdaterModel.OnInitialInstall] App v{appVersion} installed");
        }

        private void OnFirstRun()
        {
            Log.Debug("[ApplicationUpdaterModel.OnFirstRun] App started for the first time");
        }
        
        private static string GetSquirrelUpdateExe()
        {
            const string updaterExecutableName = "update.exe";

            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null &&
                Path.GetFileName(entryAssembly.Location).Equals(updaterExecutableName, StringComparison.OrdinalIgnoreCase) &&
                entryAssembly.Location.IndexOf("app-", StringComparison.OrdinalIgnoreCase) == -1 &&
                entryAssembly.Location.IndexOf("SquirrelTemp", StringComparison.OrdinalIgnoreCase) == -1)
            {
                return Path.GetFullPath(entryAssembly.Location);
            }

            var squirrelAssembly = typeof(PoeUpdateManager).Assembly;
            var executingAssembly = Path.GetDirectoryName(squirrelAssembly.Location);
            if (executingAssembly == null)
            {
                throw new ApplicationException($"Failed to get directory assembly {squirrelAssembly}");
            }

            var fileInfo = new FileInfo(Path.Combine(executingAssembly, "..", updaterExecutableName));
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException(
                    $"{updaterExecutableName} not found(path: {fileInfo.FullName}), not a Squirrel-installed app?",
                    fileInfo.FullName);
            }

            return fileInfo.FullName;
        }

        private IDisposable CreateIsBusyAnchor()
        {
            IsBusy = true;
            ProgressPercent = 0;

            return Disposable.Create(
                () =>
                {
                    IsBusy = false;
                    ProgressPercent = 0;
                });
        }
    }
}