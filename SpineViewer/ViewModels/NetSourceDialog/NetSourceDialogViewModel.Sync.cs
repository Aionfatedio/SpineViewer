using SpineViewer.NetSource.Models;
using SpineViewer.NetSource.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SpineViewer.ViewModels.NetSourceDialog
{
    public partial class NetSourceDialogViewModel
    {
        #region 同步实现

        private void RunBackground(Task task, string operationName)
        {
            _ = ObserveBackgroundTaskAsync(task, operationName);
        }

        private async Task ObserveBackgroundTaskAsync(Task task, string operationName)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
            catch (Exception ex)
            {
                // 后台刷新失败只写日志并把仓库项标记失败，避免未观察异常弹出全局错误窗口。
                _logger.Debug(ex.ToString());
                _logger.Warn("{0} failed: {1}", operationName, ex.Message);
            }
        }

        private async Task LoadAllAsync()
        {
            // 先展示本地缓存，保证离线启动也能看到上次索引；随后再尝试联网刷新。
            foreach (var item in Repos.ToArray())
            {
                if (_disposed || _indexCts.IsCancellationRequested)
                    return;

                var cached = _indexService.TryLoadCache(item.Config.RepoId);
                if (cached is not null && cached.Bundles.Count > 0)
                {
                    _caches[item.Config.RepoId] = cached;
                    _repoDisplayNames[item.Config.RepoId] = item.DisplayName;
                    item.Status = cached.Truncated ? RepoIndexStatus.Stale : RepoIndexStatus.Ready;
                    item.HeadCommit = cached.HeadCommit;
                    item.HeadCommitDateDisplay = NetSourceRepoItemViewModel.FormatCommitDate(cached.HeadCommitDate);
                    item.BundleCount = cached.Bundles.Count;
                    item.Truncated = cached.Truncated;
                }
            }
            RefreshSearch();

            foreach (var item in Repos.ToArray())
            {
                if (_disposed || _indexCts.IsCancellationRequested)
                    return;

                await RefreshRepoAsync(item, forceFull: false);
            }
        }

        private async Task RefreshRepoAsync(NetSourceRepoItemViewModel item, bool forceFull, bool resetSort = false)
        {
            if (_disposed || _indexCts.IsCancellationRequested)
                return;

            item.Status = RepoIndexStatus.Indexing;
            item.ErrorMessage = null;
            item.IndexDone = 0;
            item.IndexTotal = 0;
            NotifyRepoCommandStates();
            var ct = _indexCts.Token;

            try
            {
                var progress = new Progress<RepoIndexProgress>(p =>
                {
                    item.IndexDone = p.Done;
                    item.IndexTotal = p.Total;
                });
                var cache = await Task.Run(() => _indexService.RefreshAsync(item.Config, forceFull, ct, progress), ct);
                if (_disposed || ct.IsCancellationRequested)
                    return;

                // 索引在后台线程完成，缓存与状态回写统一切回 WPF UI 线程，
                // _caches 等字典只在 UI 线程读写，避免并发刷新时的竞态。
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _caches[item.Config.RepoId] = cache;
                    _repoDisplayNames[item.Config.RepoId] = item.DisplayName;

                    item.HeadCommit = cache.HeadCommit;
                    item.HeadCommitDateDisplay = NetSourceRepoItemViewModel.FormatCommitDate(cache.HeadCommitDate);
                    item.BundleCount = cache.Bundles.Count;
                    item.Truncated = cache.Truncated;
                    item.Status = cache.Truncated ? RepoIndexStatus.Stale : RepoIndexStatus.Ready;
                    PersistRepoList();
                    RefreshSearch(resetSort: resetSort);
                    NotifyRepoCommandStates();
                });
            }
            catch (OperationCanceledException)
            {
                if (_disposed)
                    return;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Status = RepoIndexStatus.Pending;
                    NotifyRepoCommandStates();
                });
            }
            catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.Status = RepoIndexStatus.Pending;
                        NotifyRepoCommandStates();
                    });
                    return;
                }

                _logger.Debug(ex.ToString());
                _logger.Warn("Refresh repo failed {0}: {1}", item.DisplayName, ex.Message);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Status = RepoIndexStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    NotifyRepoCommandStates();
                });
            }
        }

        private void PersistRepoList()
        {
            _vmMain.NetSourceRepoConfigs = Repos.Select(r => r.Config).ToList();
            _vmMain.SaveNetSourceState();
        }

        private static string Str(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        #endregion
    }
}
