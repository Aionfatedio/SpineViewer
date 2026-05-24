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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;

namespace SpineViewer.ViewModels.NetSourceDialog
{
    public class NetSourceDialogViewModel : ObservableObject, IDisposable
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

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

        private readonly CancellationTokenSource _indexCts = new();

        private CancellationTokenSource? _downloadCts;

        public NetSourceDialogViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
            _cacheRoot = NetSourcePathProvider.ResolveCacheRoot(vmMain.PreferenceViewModel.NetSourceCacheRoot);
            NetSourcePathProvider.EnsureDirectoryExists(_cacheRoot);

            _credentialStore = new NetSourceCredentialStore(_cacheRoot);
            _api = new GitHubApiClient(token: _credentialStore.GetGitHubToken(), userAgent: $"SpineViewer/{App.Version}");
            _indexService = new RepoIndexService(_api, _cacheRoot);
            _downloadService = new BundleDownloadService(_api, _cacheRoot);

            _aggregateSearch = vmMain.NetSourceAggregateSearch;

            foreach (var cfg in vmMain.NetSourceRepoConfigs ?? [])
                Repos.Add(new NetSourceRepoItemViewModel(cfg));

            _ = LoadAllAsync();
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
                    _vmMain.SaveNetSourceRepoConfigs();
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
                    RefreshSearch();
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

        private void RefreshSearch()
        {
            IReadOnlyCollection<string>? filterIds = null;
            if (!_aggregateSearch && _selectedRepo is not null)
                filterIds = [_selectedRepo.Config.RepoId];

            var repoOrder = new Dictionary<string, int>();
            for (int i = 0; i < Repos.Count; i++)
                repoOrder[Repos[i].Config.RepoId] = i;

            var results = _searchService.Search(_caches, _repoDisplayNames, repoOrder, _searchQuery, filterIds, SearchResultLimit);

            SearchResults.ReplaceAll(results.Select(r => new NetSourceBundleItemViewModel(r)));

            ActiveSortKey = null;
            SortDescending = false;

            var totalBundles = _caches.Values.Sum(c => c?.Bundles?.Count ?? 0);
            StatusText = $"共 {totalBundles} 个模型 · 当前显示 {SearchResults.Count} 条";
        }

        public void SortByColumn(string columnKey)
        {
            if (string.IsNullOrEmpty(columnKey)) return;
            if (columnKey != SortKeyRepo && columnKey != SortKeySize && columnKey != SortKeyFileCount && columnKey != SortKeyCommitDate) return;

            if (string.Equals(ActiveSortKey, columnKey, StringComparison.Ordinal))
            {
                SortDescending = !SortDescending;
            }
            else
            {
                ActiveSortKey = columnKey;
                SortDescending = false;
            }
            ApplySort();
        }

        private void ApplySort()
        {
            if (SearchResults.Count == 0 || string.IsNullOrEmpty(ActiveSortKey)) return;

            var current = SearchResults.ToList();
            IEnumerable<NetSourceBundleItemViewModel> ordered = ActiveSortKey switch
            {
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
                MessagePopupService.Warn("无法识别仓库地址, 支持格式: https://github.com/owner/repo 或 owner/repo");
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
                MessagePopupService.Info("该仓库已添加");
                return;
            }

            var item = new NetSourceRepoItemViewModel(cfg);
            Repos.Add(item);
            NewRepoInput = string.Empty;
            PersistRepoList();
            NotifyRepoCommandStates();

            _ = RefreshRepoAsync(item, forceFull: false);
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
            _ = RefreshRepoAsync(item, forceFull: true);
        }, CanRefreshRepo);
        private RelayCommand<NetSourceRepoItemViewModel?>? _cmd_RefreshRepo;

        public RelayCommand<NetSourceRepoItemViewModel?> Cmd_RemoveRepo => _cmd_RemoveRepo ??= new(item =>
        {
            if (!CanRemoveRepo(item)) return;
            if (!MessagePopupService.OKCancel($"确定要移除仓库 {item.DisplayName} 及其本地缓存吗?"))
                return;

            Repos.Remove(item);
            _caches.Remove(item.Config.RepoId);
            _repoDisplayNames.Remove(item.Config.RepoId);
            _indexService.DeleteCache(item.Config.RepoId);
            PersistRepoList();
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
                MessagePopupService.Warn("无法打开浏览器: " + ex.Message);
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
                _ = RefreshRepoAsync(item, forceFull: true);
            }
        });
        private RelayCommand? _cmd_RefreshAll;

        #endregion

        #region 下载命令

        public RelayCommand<IList?> Cmd_DownloadAndImport => _cmd_DownloadAndImport ??= new(DownloadAndImport_Execute, args => args is not null && args.Count > 0);
        private RelayCommand<IList?>? _cmd_DownloadAndImport;

        public RelayCommand<IList?> Cmd_SaveAs => _cmd_SaveAs ??= new(SaveAs_Execute, args => args is not null && args.Count > 0);
        private RelayCommand<IList?>? _cmd_SaveAs;

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
                reqs.Add(new BundleDownloadRequest(cfg, it.Bundle, it.Result.CommitSha, localDir));
            }
            if (reqs.Count == 0) return;

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                SaveAsTask(reqs, reporter, linked.Token);
            }, "保存到本地");
        }

        private void SaveAsTask(List<BundleDownloadRequest> reqs, IProgressReporter reporter, CancellationToken ct)
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
                    var fp = new Progress<BundleDownloadProgress>(p =>
                    {
                        reporter.ProgressText = $"[{i + 1}/{total}] {req.Bundle.ModelName}  ·  {p.CurrentFile} ({p.CompletedFiles}/{p.TotalFiles})";
                    });
                    _downloadService.DownloadAsync(req, fp, ct).GetAwaiter().GetResult();
                    success++;
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("Save-as canceled at {0}", req.Bundle.ModelName);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Error("Failed to save bundle {0}: {1}", req.Bundle.ModelName, ex.Message);
                    failed++;
                }

                reporter.Done = i + 1;
                _vmMain.ProgressValue = (i + 1f) / total;
            }
            _vmMain.ProgressState = TaskbarItemProgressState.None;

            if (failed > 0)
                _logger.Warn("Save-as finished: {0} success, {1} failed", success, failed);
            else
                _logger.Info("Save-as finished: {0} bundle(s) saved to disk", success);
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "bundle";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name) sb.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private void DownloadAndImport_Execute(IList? args)
        {
            if (args is null || args.Count <= 0) return;
            var items = args.OfType<NetSourceBundleItemViewModel>().ToArray();
            if (items.Length == 0) return;

            var reqs = new List<BundleDownloadRequest>();
            foreach (var it in items)
            {
                var cfg = Repos.FirstOrDefault(r => r.Config.RepoId == it.Result.RepoId)?.Config;
                if (cfg is null) continue;
                var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, cfg.RepoId, it.Result.CommitSha, it.Bundle.BundleDir);
                reqs.Add(new BundleDownloadRequest(cfg, it.Bundle, it.Result.CommitSha, localDir));
            }
            if (reqs.Count == 0) return;

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                DownloadAndImportTask(reqs, reporter, linked.Token);
            }, "下载模型……");
        }

        private void DownloadAndImportTask(
            List<BundleDownloadRequest> reqs,
            IProgressReporter reporter,
            CancellationToken ct)
        {
            int totalBundles = reqs.Count;
            int successBundle = 0;
            int failedBundle = 0;
            var downloadedSkelPaths = new List<string>();

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;
            reporter.Total = totalBundles;
            reporter.Done = 0;
            reporter.ProgressText = $"[0/{totalBundles}]";

            for (int i = 0; i < totalBundles; i++)
            {
                if (ct.IsCancellationRequested) break;

                var req = reqs[i];
                reporter.ProgressText = $"[{i}/{totalBundles}] {req.Bundle.ModelName}";

                try
                {
                    var fileProgress = new Progress<BundleDownloadProgress>(p =>
                    {
                        reporter.ProgressText = $"[{i + 1}/{totalBundles}] {req.Bundle.ModelName}  ·  {p.CurrentFile} ({p.CompletedFiles}/{p.TotalFiles})";
                    });

                    var result = _downloadService.DownloadAsync(req, fileProgress, ct).GetAwaiter().GetResult();
                    downloadedSkelPaths.Add(result.LocalSkelPath);
                    successBundle++;
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("Bundle download canceled at {0}", req.Bundle.ModelName);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Error("Failed to download bundle {0}: {1}", req.Bundle.ModelName, ex.Message);
                    failedBundle++;
                }

                reporter.Done = i + 1;
                _vmMain.ProgressValue = (i + 1f) / totalBundles;
            }

            _vmMain.ProgressState = TaskbarItemProgressState.None;

            if (downloadedSkelPaths.Count > 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _vmMain.SpineObjectListViewModel.AddSpineObjectFromFileList(downloadedSkelPaths);
                });
            }

            if (failedBundle > 0)
                _logger.Warn("Net source download finished: {0} success, {1} failed", successBundle, failedBundle);
            else
                _logger.Info("Net source download finished: {0} bundle(s) imported", successBundle);
        }

        #endregion

        #region 同步实现

        private async Task LoadAllAsync()
        {
            foreach (var item in Repos.ToArray())
            {
                var cached = _indexService.TryLoadCache(item.Config.RepoId);
                if (cached is not null && cached.Bundles.Count > 0)
                {
                    _caches[item.Config.RepoId] = cached;
                    _repoDisplayNames[item.Config.RepoId] = item.DisplayName;
                    item.Status = RepoIndexStatus.Stale;
                    item.HeadCommit = cached.HeadCommit;
                    item.HeadCommitDateDisplay = NetSourceRepoItemViewModel.FormatCommitDate(cached.HeadCommitDate);
                    item.BundleCount = cached.Bundles.Count;
                    item.Truncated = cached.Truncated;
                }
            }
            RefreshSearch();

            foreach (var item in Repos.ToArray())
            {
                await RefreshRepoAsync(item, forceFull: false);
            }
        }

        private async Task RefreshRepoAsync(NetSourceRepoItemViewModel item, bool forceFull)
        {
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

                _caches[item.Config.RepoId] = cache;
                _repoDisplayNames[item.Config.RepoId] = item.DisplayName;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.HeadCommit = cache.HeadCommit;
                    item.HeadCommitDateDisplay = NetSourceRepoItemViewModel.FormatCommitDate(cache.HeadCommitDate);
                    item.BundleCount = cache.Bundles.Count;
                    item.Truncated = cache.Truncated;
                    item.Status = cache.Truncated ? RepoIndexStatus.Stale : RepoIndexStatus.Ready;
                    PersistRepoList();
                    RefreshSearch();
                    NotifyRepoCommandStates();
                });
            }
            catch (OperationCanceledException)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Status = RepoIndexStatus.Pending;
                    NotifyRepoCommandStates();
                });
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
            _vmMain.SaveNetSourceRepoConfigs();
        }

        #endregion

        public void Dispose()
        {
            _indexCts.Cancel();
            _indexCts.Dispose();
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _api.Dispose();
        }
    }
}
