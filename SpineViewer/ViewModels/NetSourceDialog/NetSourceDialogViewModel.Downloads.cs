using CommunityToolkit.Mvvm.Input;
using SpineViewer.NetSource.Models;
using SpineViewer.NetSource.Services;
using SpineViewer.Services;
using SpineViewer.ViewModels.MainWindow;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Shell;

namespace SpineViewer.ViewModels.NetSourceDialog
{
    public partial class NetSourceDialogViewModel
    {
        #region 下载命令

        public RelayCommand<IList?> Cmd_DownloadAndImport => _cmd_DownloadAndImport ??= new(DownloadAndImport_Execute, args => args is not null && args.Count > 0);
        private RelayCommand<IList?>? _cmd_DownloadAndImport;

        public RelayCommand<IList?> Cmd_SaveAs => _cmd_SaveAs ??= new(SaveAs_Execute, args => args is not null && args.Count > 0);
        private RelayCommand<IList?>? _cmd_SaveAs;

        public RelayCommand<IList?> Cmd_UpdateLocalFiles => _cmd_UpdateLocalFiles ??= new(UpdateLocalFiles_Execute, CanUpdateLocalFiles);
        private RelayCommand<IList?>? _cmd_UpdateLocalFiles;

        public RelayCommand<IList?> Cmd_RemoveLocalFiles => _cmd_RemoveLocalFiles ??= new(RemoveLocalFiles_Execute, CanRemoveLocalFiles);
        private RelayCommand<IList?>? _cmd_RemoveLocalFiles;

        /// <summary>
        /// 逐个 bundle 串行下载, 是三种下载入口 (导入/另存为/更新) 共享的进度循环。
        /// </summary>
        private (int Success, int Failed) RunBundleDownloadLoop(
            IReadOnlyList<BundleDownloadRequest> reqs,
            IProgressReporter reporter,
            CancellationToken ct,
            string logContext,
            Action<BundleDownloadResult>? onDownloaded = null)
        {
            int total = reqs.Count;
            int success = 0;
            int failed = 0;

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;
            reporter.Total = total;
            reporter.Done = 0;
            reporter.ProgressText = $"[0/{total}]";

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) break;

                var req = reqs[i];
                reporter.ProgressText = $"[{i}/{total}] {req.Bundle.ModelName}";

                try
                {
                    var fileProgress = new Progress<BundleDownloadProgress>(p =>
                    {
                        reporter.ProgressText = $"[{i + 1}/{total}] {req.Bundle.ModelName}  ·  {p.CurrentFile} ({p.CompletedFiles}/{p.TotalFiles})";
                    });

                    var result = _downloadService.DownloadAsync(req, fileProgress, ct).GetAwaiter().GetResult();
                    onDownloaded?.Invoke(result);
                    success++;
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("{0} canceled at {1}", logContext, req.Bundle.ModelName);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Error("{0} failed for {1}: {2}", logContext, req.Bundle.ModelName, ex.Message);
                    failed++;
                }

                reporter.Done = i + 1;
                _vmMain.ProgressValue = (i + 1f) / total;
            }
            _vmMain.ProgressState = TaskbarItemProgressState.None;

            return (success, failed);
        }

        private void SaveAs_Execute(IList? args)
        {
            if (args is null || args.Count <= 0) return;
            var items = args.OfType<NetSourceBundleItemViewModel>().ToArray();
            if (items.Length == 0) return;

            if (!DialogService.ShowOpenFolderDialog(out var targetDir) || string.IsNullOrWhiteSpace(targetDir))
                return;

            bool saveToSelectedDir = items.Length == 1;
            var reqs = new List<BundleDownloadRequest>();
            foreach (var it in items)
            {
                var cfg = Repos.FirstOrDefault(r => r.Config.RepoId == it.Result.RepoId)?.Config;
                if (cfg is null) continue;
                var localDir = saveToSelectedDir
                    ? targetDir!
                    : System.IO.Path.Combine(targetDir!, SanitizeName(it.Bundle.ModelName));
                reqs.Add(new BundleDownloadRequest(cfg, it.Bundle, it.Result.DownloadCommitSha, localDir, TrackInLibrary: false));
            }
            if (reqs.Count == 0) return;

            RestartDownloadCts(out var token);

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                var (success, failed) = RunBundleDownloadLoop(reqs, reporter, linked.Token, "Save-as");
                if (failed > 0)
                    _logger.Warn("Save-as finished: {0} success, {1} failed", success, failed);
                else
                    _logger.Info("Save-as finished: {0} bundle(s) saved to disk", success);
            }, Str("Str_NetSourceSaveAsTitle"));
        }

        private bool CanUpdateLocalFiles(IList? args)
            => GetUniformHighlightedState(args) == DownloadedBundleState.Outdated;

        private void UpdateLocalFiles_Execute(IList? args)
        {
            if (!CanUpdateLocalFiles(args)) return;
            var items = args!.OfType<NetSourceBundleItemViewModel>().ToArray();

            var reqs = new List<BundleDownloadRequest>();
            foreach (var it in items)
            {
                var cfg = Repos.FirstOrDefault(r => r.Config.RepoId == it.Result.RepoId)?.Config;
                if (cfg is null) continue;
                var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, cfg.RepoId, it.Bundle.BundleDir);
                reqs.Add(new BundleDownloadRequest(
                    cfg,
                    it.Bundle,
                    it.Result.DownloadCommitSha,
                    localDir,
                    TrackInLibrary: true,
                    OverwriteExisting: true));
            }
            if (reqs.Count == 0) return;

            RestartDownloadCts(out var token);

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                var (success, failed) = RunBundleDownloadLoop(reqs, reporter, linked.Token, "GitHub repo file update");

                Application.Current.Dispatcher.Invoke(() => RefreshSearch());

                if (failed > 0)
                    _logger.Warn("GitHub repo file update finished: {0} success, {1} failed", success, failed);
                else
                    _logger.Info("GitHub repo file update finished: {0} bundle(s) updated", success);
            }, Str("Str_NetSourceUpdateFilesTitle"));
        }

        private bool CanRemoveLocalFiles(IList? args)
            => GetUniformHighlightedState(args) is DownloadedBundleState.Current or DownloadedBundleState.Outdated;

        private void RemoveLocalFiles_Execute(IList? args)
        {
            if (!CanRemoveLocalFiles(args)) return;
            var items = args!.OfType<NetSourceBundleItemViewModel>().ToArray();
            if (!MessagePopupService.OKCancel(string.Format(Str("Str_NetSourceRemoveLocalFilesQuest"), items.Length)))
                return;

            int removedFiles = 0;
            int failedBundles = 0;
            int unloadedModels = 0;
            var repoDownloadsRoot = NetSourcePathProvider.GetReposRoot(_cacheRoot);
            foreach (var it in items)
            {
                try
                {
                    var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, it.Result.RepoId, it.Bundle.BundleDir);
                    var localSkelPath = System.IO.Path.Combine(localDir, GetRepoFileName(it.Bundle.SkelPath));
                    unloadedModels += _vmMain.SpineObjectListViewModel.RemoveLoadedSpineObjectFromPathIfUnderRoot(localSkelPath, repoDownloadsRoot);
                    removedFiles += _downloadService.RemoveLocalFiles(it.Result.RepoId, it.Bundle);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Error("Failed to remove GitHub repo local files for {0}: {1}", it.ModelName, ex.Message);
                    failedBundles++;
                }
            }

            RefreshSearch();

            if (failedBundles > 0)
                _logger.Warn("GitHub repo local file removal finished: {0} file(s) removed, {1} loaded model(s) unloaded, {2} bundle(s) failed", removedFiles, unloadedModels, failedBundles);
            else
                _logger.Info("GitHub repo local file removal finished: {0} file(s) removed, {1} loaded model(s) unloaded", removedFiles, unloadedModels);
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "bundle";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name) sb.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private static string GetRepoFileName(string repoPath)
        {
            var idx = repoPath.LastIndexOf('/');
            return idx < 0 ? repoPath : repoPath[(idx + 1)..];
        }

        private void RestartDownloadCts(out CancellationToken token)
        {
            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            token = _downloadCts.Token;
        }

        private void DownloadAndImport_Execute(IList? args)
        {
            if (args is null || args.Count <= 0) return;
            var items = args.OfType<NetSourceBundleItemViewModel>().ToArray();
            if (items.Length == 0) return;

            var reqs = new List<BundleDownloadRequest>();
            var localSkelPaths = new List<string>();
            int alreadyLoaded = 0;
            foreach (var it in items)
            {
                var cfg = Repos.FirstOrDefault(r => r.Config.RepoId == it.Result.RepoId)?.Config;
                if (cfg is null) continue;

                // 先按 GitHub repo 下载路径查重；已载入时只唤醒主列表，不重复添加模型。
                var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, cfg.RepoId, it.Bundle.BundleDir);
                var localTargetSkelPath = System.IO.Path.Combine(localDir, GetRepoFileName(it.Bundle.SkelPath));
                if (_vmMain.SpineObjectListViewModel.TrySelectLoadedSpineObject(localTargetSkelPath))
                {
                    alreadyLoaded++;
                    continue;
                }

                var localInfo = HasToken
                    ? _downloadService.GetBundleInfo(it.Result.RepoId, it.Bundle)
                    : new LocalBundleInfo(DownloadedBundleState.None, null);
                var localState = localInfo.State;
                it.LocalState = localState;
                it.LocalUpdatedAt = localInfo.UpdatedAt;
                // 蓝色高亮表示本地文件可直接载入；黄色或默认状态需要重新下载后再载入。
                if (HasToken
                    && localState == DownloadedBundleState.Current
                    && _downloadService.TryGetLocalSkelPath(it.Result.RepoId, it.Bundle, out var localSkelPath))
                {
                    localSkelPaths.Add(localSkelPath);
                    continue;
                }

                // 到达这里的 bundle 都不是可信的最新状态, 统一整包重新下载。
                reqs.Add(new BundleDownloadRequest(
                    cfg,
                    it.Bundle,
                    it.Result.DownloadCommitSha,
                    localDir,
                    TrackInLibrary: true,
                    OverwriteExisting: true));
            }
            if (reqs.Count == 0)
            {
                if (localSkelPaths.Count > 0)
                {
                    var loadSummary = _vmMain.SpineObjectListViewModel.AddSpineObjectFilesImmediately(localSkelPaths);
                    var totalLoaded = loadSummary.Loaded + alreadyLoaded + loadSummary.Reused;
                    if (loadSummary.Failed > 0)
                        _logger.Warn("GitHub repo import finished: 0 bundle(s) downloaded, {0} loaded, {1} load failed",
                            totalLoaded, loadSummary.Failed);
                    else
                        _logger.Info("GitHub repo import finished: 0 bundle(s) downloaded, {0} loaded",
                            totalLoaded);
                }
                else if (alreadyLoaded > 0)
                {
                    _logger.Info("GitHub repo import finished: 0 bundle(s) downloaded, {0} loaded", alreadyLoaded);
                }
                return;
            }

            RestartDownloadCts(out var token);

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                DownloadAndImportTask(reqs, localSkelPaths, alreadyLoaded, reporter, linked.Token);
            }, Str("Str_NetSourceDownloadTitle"));
        }

        private void DownloadAndImportTask(
            List<BundleDownloadRequest> reqs,
            List<string> localSkelPaths,
            int alreadyLoaded,
            IProgressReporter reporter,
            CancellationToken ct)
        {
            var downloadedSkelPaths = new List<string>(localSkelPaths);
            var (successBundle, failedBundle) = RunBundleDownloadLoop(
                reqs, reporter, ct, "Bundle download",
                result => downloadedSkelPaths.Add(result.LocalSkelPath));

            SpineObjectLoadSummary loadSummary = default;
            if (downloadedSkelPaths.Count > 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    loadSummary = _vmMain.SpineObjectListViewModel.AddSpineObjectFilesImmediately(downloadedSkelPaths);
                    RefreshSearch();
                });
            }

            var totalLoaded = loadSummary.Loaded + alreadyLoaded + loadSummary.Reused;
            if (failedBundle > 0 || loadSummary.Failed > 0)
                _logger.Warn("GitHub repo import finished: {0} bundle(s) downloaded, {1} download failed, {2} loaded, {3} load failed",
                    successBundle, failedBundle, totalLoaded, loadSummary.Failed);
            else
                _logger.Info("GitHub repo import finished: {0} bundle(s) downloaded, {1} loaded",
                    successBundle, totalLoaded);
        }

        #endregion
    }
}
