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
            NetSourcePathProvider.EnsureLayout(_cacheRoot);

            _credentialStore = new NetSourceCredentialStore(_cacheRoot);
            _api = new GitHubApiClient(token: _credentialStore.GetGitHubToken(), userAgent: $"SpineViewer/{App.Version}");
            _indexService = new RepoIndexService(_api, _cacheRoot);
            _downloadService = new BundleDownloadService(_api, _cacheRoot);

            _aggregateSearch = vmMain.NetSourceAggregateSearch;
            _searchQuery = vmMain.NetSourceSearchQuery;
            _activeSortKey = IsSortKeySupported(vmMain.NetSourceSortKey) ? vmMain.NetSourceSortKey : null;
            _sortDescending = vmMain.NetSourceSortDescending;

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
                var localInfo = HasToken
                    ? _downloadService.GetBundleInfo(r.RepoId, r.Bundle, r.CommitSha)
                    : new LocalBundleInfo(DownloadedBundleState.None, null);
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
            return sortKey == SortKeyRepo
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
            _ = RefreshRepoAsync(item, forceFull: false, resetSort: true);
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
                _ = RefreshRepoAsync(item, forceFull: false, resetSort: true);
            }
        });
        private RelayCommand? _cmd_RefreshAll;

        #endregion

        #region 下载命令

        public RelayCommand<IList?> Cmd_DownloadAndImport => _cmd_DownloadAndImport ??= new(DownloadAndImport_Execute, args => args is not null && args.Count > 0);
        private RelayCommand<IList?>? _cmd_DownloadAndImport;

        public RelayCommand<IList?> Cmd_SaveAs => _cmd_SaveAs ??= new(SaveAs_Execute, args => args is not null && args.Count > 0);
        private RelayCommand<IList?>? _cmd_SaveAs;

        public RelayCommand<IList?> Cmd_UpdateLocalFiles => _cmd_UpdateLocalFiles ??= new(UpdateLocalFiles_Execute, CanUpdateLocalFiles);
        private RelayCommand<IList?>? _cmd_UpdateLocalFiles;

        public RelayCommand<IList?> Cmd_RemoveLocalFiles => _cmd_RemoveLocalFiles ??= new(RemoveLocalFiles_Execute, CanRemoveLocalFiles);
        private RelayCommand<IList?>? _cmd_RemoveLocalFiles;

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
                reqs.Add(new BundleDownloadRequest(cfg, it.Bundle, it.Result.CommitSha, localDir, TrackInLibrary: false));
            }
            if (reqs.Count == 0) return;

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                SaveAsTask(reqs, reporter, linked.Token);
            }, Str("Str_NetSourceSaveAsTitle"));
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
                var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, cfg.RepoId, it.Result.CommitSha, it.Bundle.BundleDir);
                reqs.Add(new BundleDownloadRequest(
                    cfg,
                    it.Bundle,
                    it.Result.CommitSha,
                    localDir,
                    TrackInLibrary: true,
                    OverwriteExisting: true));
            }
            if (reqs.Count == 0) return;

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            ProgressService.RunAsync((reporter, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                UpdateLocalFilesTask(reqs, reporter, linked.Token);
            }, Str("Str_NetSourceUpdateFilesTitle"));
        }

        private void UpdateLocalFilesTask(List<BundleDownloadRequest> reqs, IProgressReporter reporter, CancellationToken ct)
        {
            int totalBundles = reqs.Count;
            int successBundle = 0;
            int failedBundle = 0;

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

                    _downloadService.DownloadAsync(req, fileProgress, ct).GetAwaiter().GetResult();
                    successBundle++;
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("GitHub repo file update canceled at {0}", req.Bundle.ModelName);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Error("Failed to update GitHub repo files for {0}: {1}", req.Bundle.ModelName, ex.Message);
                    failedBundle++;
                }

                reporter.Done = i + 1;
                _vmMain.ProgressValue = (i + 1f) / totalBundles;
            }

            _vmMain.ProgressState = TaskbarItemProgressState.None;

            Application.Current.Dispatcher.Invoke(() => RefreshSearch());

            if (failedBundle > 0)
                _logger.Warn("GitHub repo file update finished: {0} success, {1} failed", successBundle, failedBundle);
            else
                _logger.Info("GitHub repo file update finished: {0} bundle(s) updated", successBundle);
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
                    var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, it.Result.RepoId, it.Result.CommitSha, it.Bundle.BundleDir);
                    var localSkelPath = System.IO.Path.Combine(localDir, GetRepoFileName(it.Bundle.SkelPath));
                    unloadedModels += _vmMain.SpineObjectListViewModel.RemoveLoadedSpineObjectFromPathIfUnderRoot(localSkelPath, repoDownloadsRoot);
                    removedFiles += _downloadService.RemoveLocalFiles(it.Result.RepoId, it.Bundle, it.Result.CommitSha);
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

                var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, cfg.RepoId, it.Result.CommitSha, it.Bundle.BundleDir);
                var localTargetSkelPath = System.IO.Path.Combine(localDir, GetRepoFileName(it.Bundle.SkelPath));
                if (_vmMain.SpineObjectListViewModel.TrySelectLoadedSpineObject(localTargetSkelPath))
                {
                    alreadyLoaded++;
                    continue;
                }

                var localInfo = HasToken
                    ? _downloadService.GetBundleInfo(it.Result.RepoId, it.Bundle, it.Result.CommitSha)
                    : new LocalBundleInfo(DownloadedBundleState.None, null);
                var localState = localInfo.State;
                it.LocalState = localState;
                it.LocalUpdatedAt = localInfo.UpdatedAt;
                if (HasToken
                    && localState == DownloadedBundleState.Current
                    && _downloadService.TryGetLocalSkelPath(it.Result.RepoId, it.Bundle, it.Result.CommitSha, out var localSkelPath))
                {
                    localSkelPaths.Add(localSkelPath);
                    continue;
                }

                reqs.Add(new BundleDownloadRequest(
                    cfg,
                    it.Bundle,
                    it.Result.CommitSha,
                    localDir,
                    TrackInLibrary: true,
                    OverwriteExisting: !HasToken || localState == DownloadedBundleState.Outdated));
            }
            if (reqs.Count == 0)
            {
                if (localSkelPaths.Count > 0)
                {
                    var loadSummary = _vmMain.SpineObjectListViewModel.AddSpineObjectFilesImmediately(localSkelPaths);
                    var totalAlreadyLoaded = alreadyLoaded + loadSummary.Reused;
                    if (loadSummary.Failed > 0)
                        _logger.Warn("GitHub repo import finished: 0 bundle(s) downloaded, {0} loaded, {1} already loaded, {2} load failed",
                            loadSummary.Loaded, totalAlreadyLoaded, loadSummary.Failed);
                    else
                        _logger.Info("GitHub repo import finished: 0 bundle(s) downloaded, {0} loaded, {1} already loaded",
                            loadSummary.Loaded, totalAlreadyLoaded);
                }
                else if (alreadyLoaded > 0)
                {
                    _logger.Info("GitHub repo import finished: 0 bundle(s) downloaded, 0 loaded, {0} already loaded", alreadyLoaded);
                }
                return;
            }

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

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
            int totalBundles = reqs.Count;
            int successBundle = 0;
            int failedBundle = 0;
            var downloadedSkelPaths = new List<string>(localSkelPaths);

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

            SpineObjectLoadSummary loadSummary = default;
            if (downloadedSkelPaths.Count > 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    loadSummary = _vmMain.SpineObjectListViewModel.AddSpineObjectFilesImmediately(downloadedSkelPaths);
                    RefreshSearch();
                });
            }

            var totalAlreadyLoaded = alreadyLoaded + loadSummary.Reused;
            if (failedBundle > 0 || loadSummary.Failed > 0)
                _logger.Warn("GitHub repo import finished: {0} bundle(s) downloaded, {1} download failed, {2} loaded, {3} already loaded, {4} load failed",
                    successBundle, failedBundle, loadSummary.Loaded, totalAlreadyLoaded, loadSummary.Failed);
            else
                _logger.Info("GitHub repo import finished: {0} bundle(s) downloaded, {1} loaded, {2} already loaded",
                    successBundle, loadSummary.Loaded, totalAlreadyLoaded);
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
                await RefreshRepoAsync(item, forceFull: false);
            }
        }

        private async Task RefreshRepoAsync(NetSourceRepoItemViewModel item, bool forceFull, bool resetSort = false)
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
                    RefreshSearch(resetSort: resetSort);
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

        private static string Str(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        #endregion

        public void Dispose()
        {
            PersistSearchState();
            _vmMain.SaveNetSourceState();
            _indexCts.Cancel();
            _indexCts.Dispose();
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _api.Dispose();
        }
    }
}
