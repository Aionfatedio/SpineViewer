using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.NetSource.Models;
using SpineViewer.NetSource.Services;
using SpineViewer.Services;
using SpineViewer.Utils;
using SpineViewer.ViewModels.MainWindow;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace SpineViewer.ViewModels.NetSourceDialog
{
    // 该 ViewModel 是 GitHub 仓库导入窗口的编排层:
    // UI 状态留在这里，GitHub 通信、索引和下载分别下沉到 NetSource.Services。
    public partial class NetSourceDialogViewModel : ObservableObject, IDisposable
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public const string SortKeyModelName = "ModelName";
        public const string SortKeyRepo = "Repo";
        public const string SortKeySize = "Size";
        public const string SortKeyFileCount = "FileCount";
        public const string SortKeyCommitDate = "CommitDate";

        public const int SearchResultLimit = 1000;

        private readonly MainWindowViewModel _vmMain;
        private readonly string _cacheRoot;
        private readonly NetSourceCredentialStore _credentialStore;
        private readonly GitHubApiClient _api;
        private readonly RepoIndexService _indexService;
        private readonly BundleSearchService _searchService = new();
        private readonly BundleDownloadService _downloadService;

        private readonly Dictionary<string, RepoIndexCache> _caches = [];

        private readonly Dictionary<string, string> _repoDisplayNames = [];

        // 本地下载状态的内存缓存 (仅 UI 线程读写), 避免每次搜索刷新都对全部结果做磁盘存在性检查。
        // 键含 BundleHash, 索引刷新导致内容变化时旧键自然失效; 下载/更新/删除后整体清空。
        private readonly Dictionary<string, LocalBundleInfo> _localStateCache = [];

        private readonly CancellationTokenSource _indexCts = new();

        private CancellationTokenSource? _downloadCts;

        private bool _disposed;

        public NetSourceDialogViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
            _cacheRoot = NetSourcePathProvider.ResolveCacheRoot(vmMain.PreferenceViewModel.NetSourceCacheRoot);
            NetSourcePathProvider.EnsureLayout(_cacheRoot);

            _credentialStore = new NetSourceCredentialStore(_cacheRoot);
            var proxy = new GitHubProxyOptions(
                vmMain.PreferenceViewModel.NetSourceUseProxy,
                vmMain.PreferenceViewModel.NetSourceProxyHost,
                vmMain.PreferenceViewModel.NetSourceProxyPort);
            _api = new GitHubApiClient(token: _credentialStore.GetGitHubToken(), userAgent: $"SpineViewer/{App.Version}", proxy: proxy);
            _indexService = new RepoIndexService(_api, _cacheRoot);
            _downloadService = new BundleDownloadService(_api, _cacheRoot);

            _aggregateSearch = vmMain.NetSourceAggregateSearch;
            _searchQuery = vmMain.NetSourceSearchQuery;
            _activeSortKey = IsSortKeySupported(vmMain.NetSourceSortKey) ? vmMain.NetSourceSortKey : null;
            _sortDescending = vmMain.NetSourceSortDescending;

            foreach (var cfg in vmMain.NetSourceRepoConfigs ?? [])
                Repos.Add(new NetSourceRepoItemViewModel(cfg));

            RunBackground(LoadAllAsync(), "Load GitHub repo indexes");
        }

        public bool HasToken => _api.HasToken;

        #region 仓库列表

        public ObservableCollection<NetSourceRepoItemViewModel> Repos { get; } = [];

        public NetSourceRepoItemViewModel? SelectedRepo
        {
            get => _selectedRepo;
            set
            {
                if (SetProperty(ref _selectedRepo, value) && !_aggregateSearch)
                    RefreshSearch();
            }
        }
        private NetSourceRepoItemViewModel? _selectedRepo;

        public string? NewRepoInput
        {
            get => _newRepoInput;
            set => SetProperty(ref _newRepoInput, value);
        }
        private string? _newRepoInput;

        public bool AggregateSearch
        {
            get => _aggregateSearch;
            set
            {
                if (SetProperty(ref _aggregateSearch, value))
                {
                    _vmMain.NetSourceAggregateSearch = value;
                    _vmMain.SaveNetSourceState();
                    RefreshSearch();
                }
            }
        }
        private bool _aggregateSearch = true;

        #endregion

        #region 搜索

        public string? SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    _vmMain.NetSourceSearchQuery = value;
                    RefreshSearch();
                }
            }
        }
        private string? _searchQuery;

        public RangeObservableCollection<NetSourceBundleItemViewModel> SearchResults { get; } = [];

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }
        private string _statusText = string.Empty;

        public bool ShowUpdateLocalFiles
        {
            get => _showUpdateLocalFiles;
            private set => SetProperty(ref _showUpdateLocalFiles, value);
        }
        private bool _showUpdateLocalFiles;

        public bool ShowRemoveLocalFiles
        {
            get => _showRemoveLocalFiles;
            private set => SetProperty(ref _showRemoveLocalFiles, value);
        }
        private bool _showRemoveLocalFiles;

        public string? ActiveSortKey
        {
            get => _activeSortKey;
            private set => SetProperty(ref _activeSortKey, value);
        }
        private string? _activeSortKey;

        public bool SortDescending
        {
            get => _sortDescending;
            private set => SetProperty(ref _sortDescending, value);
        }
        private bool _sortDescending;

        private void RefreshSearch(bool resetSort = false)
        {
            if (resetSort)
                ResetSortState();

            IReadOnlyCollection<string>? filterIds = null;
            if (!_aggregateSearch && _selectedRepo is not null)
                filterIds = [_selectedRepo.Config.RepoId];

            var repoOrder = new Dictionary<string, int>();
            for (int i = 0; i < Repos.Count; i++)
                repoOrder[Repos[i].Config.RepoId] = i;

            var results = _searchService.Search(_caches, _repoDisplayNames, repoOrder, _searchQuery, filterIds, SearchResultLimit);

            SearchResults.ReplaceAll(results.Select(r =>
            {
                // 蓝/橙高亮只依赖 bundle hash 与本地文件, 均不需要 PAT;
                // PAT 只影响提交列精度, 无 PAT 时提交列由面板隐藏。
                var localInfo = GetCachedBundleInfo(r.RepoId, r.Bundle);
                var item = new NetSourceBundleItemViewModel(r)
                {
                    LocalState = localInfo.State,
                    LocalUpdatedAt = localInfo.UpdatedAt
                };
                return item;
            }));

            if (!string.IsNullOrEmpty(ActiveSortKey))
                ApplySort();

            var totalBundles = _caches.Values.Sum(c => c?.Bundles?.Count ?? 0);
            StatusText = string.Format(Str("Str_NetSourceStatusSummary"), totalBundles, SearchResults.Count);
        }

        public void SortByColumn(string columnKey)
        {
            if (string.IsNullOrEmpty(columnKey)) return;
            if (!IsSortKeySupported(columnKey)) return;

            // 模型名列是"置顶排序 ⇄ 默认顺序"开关: 已下载 (蓝/橙) 置顶按名称排, 再点恢复自然顺序。
            if (columnKey == SortKeyModelName && string.Equals(ActiveSortKey, SortKeyModelName, StringComparison.Ordinal))
            {
                ResetSortState();
                RefreshSearch();
                return;
            }

            if (string.Equals(ActiveSortKey, columnKey, StringComparison.Ordinal))
            {
                SortDescending = !SortDescending;
            }
            else
            {
                ActiveSortKey = columnKey;
                SortDescending = false;
            }
            PersistSearchState();
            ApplySort();
        }

        private void ResetSortState()
        {
            ActiveSortKey = null;
            SortDescending = false;
            PersistSearchState();
        }

        private void PersistSearchState()
        {
            _vmMain.NetSourceSearchQuery = SearchQuery;
            _vmMain.NetSourceSortKey = ActiveSortKey;
            _vmMain.NetSourceSortDescending = SortDescending;
        }

        private static bool IsSortKeySupported(string? sortKey)
        {
            return sortKey == SortKeyModelName
                || sortKey == SortKeyRepo
                || sortKey == SortKeySize
                || sortKey == SortKeyFileCount
                || sortKey == SortKeyCommitDate;
        }

        private void ApplySort()
        {
            if (SearchResults.Count == 0 || string.IsNullOrEmpty(ActiveSortKey)) return;

            var current = SearchResults.ToList();
            IEnumerable<NetSourceBundleItemViewModel> ordered = ActiveSortKey switch
            {
                // 已下载 (蓝/橙混排) 置顶, 置顶组和剩余组各自按模型名 A-Z 排序。
                SortKeyModelName => current
                    .OrderByDescending(b => b.LocalState is DownloadedBundleState.Current or DownloadedBundleState.Outdated)
                    .ThenBy(b => b.ModelName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(b => b.BundleDir, StringComparer.Ordinal),
                SortKeyRepo => SortDescending
                    ? current.OrderByDescending(b => b.RepoOrderIndex).ThenBy(b => b.BundleDir, StringComparer.Ordinal)
                    : current.OrderBy(b => b.RepoOrderIndex).ThenBy(b => b.BundleDir, StringComparer.Ordinal),
                SortKeySize => SortDescending
                    ? current.OrderByDescending(b => b.TotalSize)
                    : current.OrderBy(b => b.TotalSize),
                SortKeyFileCount => SortDescending
                    ? current.OrderByDescending(b => b.FileCount)
                    : current.OrderBy(b => b.FileCount),
                SortKeyCommitDate => SortDescending
                    ? current.OrderByDescending(b => b.CommitDate ?? DateTime.MinValue).ThenBy(b => b.BundleDir, StringComparer.Ordinal)
                    : current.OrderBy(b => b.CommitDate ?? DateTime.MinValue).ThenBy(b => b.BundleDir, StringComparer.Ordinal),
                _ => current
            };

            SearchResults.ReplaceAll(ordered);
        }

        private LocalBundleInfo GetCachedBundleInfo(string repoId, SpineBundle bundle)
        {
            var key = $"{repoId}\n{bundle.SkelPath}\n{bundle.BundleHash}";
            if (_localStateCache.TryGetValue(key, out var cached))
                return cached;

            var info = _downloadService.GetBundleInfo(repoId, bundle);
            _localStateCache[key] = info;
            return info;
        }

        private void InvalidateLocalStateCache() => _localStateCache.Clear();

        public void UpdateResultContextMenuState(IList? args)
        {
            var state = GetUniformHighlightedState(args);
            ShowUpdateLocalFiles = state == DownloadedBundleState.Outdated;
            ShowRemoveLocalFiles = state is DownloadedBundleState.Current or DownloadedBundleState.Outdated;
            _cmd_UpdateLocalFiles?.NotifyCanExecuteChanged();
            _cmd_RemoveLocalFiles?.NotifyCanExecuteChanged();
        }

        private static DownloadedBundleState? GetUniformHighlightedState(IList? args)
        {
            var items = args?.OfType<NetSourceBundleItemViewModel>().ToArray() ?? [];
            if (items.Length == 0)
                return null;

            var state = items[0].LocalState;
            if (state is not (DownloadedBundleState.Current or DownloadedBundleState.Outdated))
                return null;

            return items.All(it => it.LocalState == state) ? state : null;
        }

        #endregion

        #region 仓库管理命令

        public RelayCommand Cmd_AddRepo => _cmd_AddRepo ??= new(AddRepo_Execute);
        private RelayCommand? _cmd_AddRepo;

        private void AddRepo_Execute()
        {
            var input = NewRepoInput?.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return;

            var parsed = GitHubApiClient.TryParseRepoUrl(input);
            if (parsed is null)
            {
                MessagePopupService.Warn(Str("Str_NetSourceInvalidRepoUrl"));
                return;
            }

            var cfg = new RepoSourceConfig
            {
                RawUrl = input,
                Host = parsed.Host,
                Owner = parsed.Owner,
                Name = parsed.Name,
                Branch = parsed.Branch
            };

            if (IsDuplicateRepo(cfg))
            {
                MessagePopupService.Info(Str("Str_NetSourceRepoAlreadyAdded"));
                return;
            }

            var item = new NetSourceRepoItemViewModel(cfg);
            Repos.Add(item);
            NewRepoInput = string.Empty;
            PersistRepoList();
            NotifyRepoCommandStates();

            RunBackground(RefreshRepoAsync(item, forceFull: false), "Refresh GitHub repo");
        }

        private bool IsDuplicateRepo(RepoSourceConfig cfg)
        {
            static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

            return Repos.Any(r =>
                string.Equals(r.Config.Host, cfg.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Config.Owner, cfg.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Config.Name, cfg.Name, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(r.Config.Branch ?? string.Empty, cfg.Branch ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    || IsBlank(cfg.Branch)));
        }

        public RelayCommand<NetSourceRepoItemViewModel?> Cmd_RefreshRepo => _cmd_RefreshRepo ??= new(item =>
        {
            if (!CanRefreshRepo(item)) return;
            RunBackground(RefreshRepoAsync(item, forceFull: false, resetSort: true), "Refresh GitHub repo");
        }, CanRefreshRepo);
        private RelayCommand<NetSourceRepoItemViewModel?>? _cmd_RefreshRepo;

        public RelayCommand<NetSourceRepoItemViewModel?> Cmd_RemoveRepo => _cmd_RemoveRepo ??= new(item =>
        {
            if (!CanRemoveRepo(item)) return;
            if (!MessagePopupService.OKCancel(string.Format(Str("Str_NetSourceRemoveRepoQuest"), item.DisplayName)))
                return;

            Repos.Remove(item);
            _caches.Remove(item.Config.RepoId);
            _repoDisplayNames.Remove(item.Config.RepoId);

            // 只删除索引缓存, 已下载的模型文件永久保留 (含已加载模型), 无需卸载。
            _indexService.DeleteCache(item.Config.RepoId);
            PersistRepoList();
            InvalidateLocalStateCache();
            RefreshSearch();
            NotifyRepoCommandStates();
        }, CanRemoveRepo);
        private RelayCommand<NetSourceRepoItemViewModel?>? _cmd_RemoveRepo;

        public RelayCommand<NetSourceRepoItemViewModel?> Cmd_MoveUpRepo => _cmd_MoveUpRepo ??= new(item =>
        {
            if (!CanMoveUpRepo(item)) return;
            var idx = Repos.IndexOf(item);
            Repos.Move(idx, idx - 1);
            PersistRepoList();
            RefreshSearch();
            NotifyRepoCommandStates();
        }, CanMoveUpRepo);
        private RelayCommand<NetSourceRepoItemViewModel?>? _cmd_MoveUpRepo;

        public RelayCommand<NetSourceRepoItemViewModel?> Cmd_MoveDownRepo => _cmd_MoveDownRepo ??= new(item =>
        {
            if (!CanMoveDownRepo(item)) return;
            var idx = Repos.IndexOf(item);
            Repos.Move(idx, idx + 1);
            PersistRepoList();
            RefreshSearch();
            NotifyRepoCommandStates();
        }, CanMoveDownRepo);
        private RelayCommand<NetSourceRepoItemViewModel?>? _cmd_MoveDownRepo;

        public RelayCommand<NetSourceRepoItemViewModel?> Cmd_VisitRepoUrl => _cmd_VisitRepoUrl ??= new(item =>
        {
            if (!CanUseRepo(item)) return;
            try
            {
                Process.Start(new ProcessStartInfo(item.WebUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Error("Failed to open repo url {0}: {1}", item.WebUrl, ex.Message);
                MessagePopupService.Warn(string.Format(Str("Str_NetSourceOpenBrowserFailed"), ex.Message));
            }
        }, CanUseRepo);
        private RelayCommand<NetSourceRepoItemViewModel?>? _cmd_VisitRepoUrl;

        private bool CanUseRepo(NetSourceRepoItemViewModel? item)
            => item is not null && Repos.Contains(item);

        private bool CanRefreshRepo(NetSourceRepoItemViewModel? item)
            => CanUseRepo(item) && !item!.IsBusy;

        private bool CanRemoveRepo(NetSourceRepoItemViewModel? item)
            => CanUseRepo(item) && !item!.IsBusy;

        private bool CanMoveUpRepo(NetSourceRepoItemViewModel? item)
        {
            if (!CanUseRepo(item)) return false;
            return Repos.IndexOf(item!) > 0;
        }

        private bool CanMoveDownRepo(NetSourceRepoItemViewModel? item)
        {
            if (!CanUseRepo(item)) return false;
            var idx = Repos.IndexOf(item!);
            return idx >= 0 && idx < Repos.Count - 1;
        }

        private void NotifyRepoCommandStates()
        {
            _cmd_RefreshRepo?.NotifyCanExecuteChanged();
            _cmd_RemoveRepo?.NotifyCanExecuteChanged();
            _cmd_MoveUpRepo?.NotifyCanExecuteChanged();
            _cmd_MoveDownRepo?.NotifyCanExecuteChanged();
            _cmd_VisitRepoUrl?.NotifyCanExecuteChanged();
        }

        public void ReorderRepo(int srcIndex, int dstIndex)
        {
            if (srcIndex < 0 || srcIndex >= Repos.Count) return;
            if (dstIndex < 0 || dstIndex >= Repos.Count) return;
            if (srcIndex == dstIndex) return;
            Repos.Move(srcIndex, dstIndex);
            PersistRepoList();
            RefreshSearch();
            NotifyRepoCommandStates();
        }

        public RelayCommand Cmd_RefreshAll => _cmd_RefreshAll ??= new(() =>
        {
            foreach (var item in Repos.ToArray())
            {
                if (!CanRefreshRepo(item)) continue;
                RunBackground(RefreshRepoAsync(item, forceFull: false, resetSort: true), "Refresh GitHub repo");
            }
        });
        private RelayCommand? _cmd_RefreshAll;

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            // 搜索词/排序状态在各 setter 中已实时写回 _vmMain, 此处只需请求一次持久化。
            _disposed = true;
            _vmMain.SaveNetSourceState();
            _indexCts.Cancel();
            _downloadCts?.Cancel();
            _api.Dispose();
        }
    }
}
