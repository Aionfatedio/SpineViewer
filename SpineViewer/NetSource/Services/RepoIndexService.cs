using NLog;
using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpineViewer.NetSource.Services
{
    public record RepoIndexProgress(int Done, int Total);

    // RepoIndexService 只负责把 GitHub 仓库树转换为可搜索的 SpineBundle 索引。
    // 它不触碰 UI，也不下载模型文件；这样索引、搜索、下载三层可以分开 review。
    public partial class RepoIndexService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const int GraphQLBatchSize = 100;

        private const int CompareFilesSoftLimit = 300;

        private const int CurrentSchemaVersion = 2;

        private static readonly (string Skel, string Atlas)[] _suffixPairs =
        [
            (".skel", ".atlas"),
            (".skel.bytes", ".atlas.txt"),
            (".json", ".atlas")
        ];

        private static readonly string[] _textureSuffixes =
        [
            ".png",
            ".webp",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tga"
        ];

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private readonly GitHubApiClient _api;
        private readonly string _cacheRoot;

        public RepoIndexService(GitHubApiClient api, string cacheRoot)
        {
            _api = api;
            _cacheRoot = cacheRoot;
        }

        public RepoIndexCache? TryLoadCache(string repoId)
        {
            try
            {
                var path = NetSourcePathProvider.GetRepoTreesCachePath(_cacheRoot, repoId);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<RepoIndexCache>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Warn("Failed to load repo index cache for {0}: {1}", repoId, ex.Message);
                return null;
            }
        }

        public void SaveCache(RepoIndexCache cache)
        {
            try
            {
                NetSourcePathProvider.EnsureDirectoryExists(NetSourcePathProvider.GetRepoCacheDir(_cacheRoot, cache.RepoId));
                var path = NetSourcePathProvider.GetRepoTreesCachePath(_cacheRoot, cache.RepoId);
                var json = JsonSerializer.Serialize(cache, _jsonOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Error("Failed to save repo index cache for {0}: {1}", cache.RepoId, ex.Message);
            }
        }

        public void DeleteCache(string repoId)
        {
            try
            {
                var dir = NetSourcePathProvider.GetRepoCacheDir(_cacheRoot, repoId);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Warn("Failed to delete repo cache {0}: {1}", repoId, ex.Message);
            }
        }

        public async Task<RepoIndexCache> RefreshAsync(
            RepoSourceConfig config,
            bool forceFull,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress = null)
        {
            // 刷新入口先确认默认分支和 HEAD；如果 HEAD 未变，只补齐可能缺失的元数据。
            // 如果 HEAD 已变，优先走 GitHub compare 做小范围更新，失败或变更多时再全量索引。
            var repoInfo = await _api.GetRepoAsync(config.Owner, config.Name, ct);
            if (string.IsNullOrWhiteSpace(config.Branch))
                config.Branch = string.IsNullOrWhiteSpace(repoInfo.DefaultBranch) ? "main" : repoInfo.DefaultBranch;

            ct.ThrowIfCancellationRequested();

            var (headSha, branchCommitDate) = await _api.GetBranchHeadAsync(config.Owner, config.Name, config.Branch!, ct);

            DateTime? finalDate = TryParseIso(repoInfo.PushedAt) ?? branchCommitDate;

            var cached = TryLoadCache(config.RepoId);
            if (!forceFull
                && cached is not null
                && cached.SchemaVersion == CurrentSchemaVersion
                && cached.Bundles.Count > 0)
            {
                if (string.Equals(cached.HeadCommit, headSha, StringComparison.OrdinalIgnoreCase))
                    return await UpdateUnchangedCacheAsync(config, cached, headSha, finalDate, ct, progress);

                var incremental = await TryRefreshIncrementalAsync(config, cached, headSha, finalDate, ct, progress);
                if (incremental is not null)
                    return incremental;
            }

            return await RefreshFullAsync(config, headSha, finalDate, ct, progress);
        }

        private async Task<RepoIndexCache> UpdateUnchangedCacheAsync(
            RepoSourceConfig config,
            RepoIndexCache cached,
            string headSha,
            DateTime? finalDate,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress)
        {
            bool dirty = false;
            if (!string.Equals(cached.HeadCommit, headSha, StringComparison.OrdinalIgnoreCase))
            {
                cached.HeadCommit = headSha;
                dirty = true;
            }

            var newIso = finalDate?.ToString("o") ?? string.Empty;
            if (!string.Equals(cached.HeadCommitDate, newIso, StringComparison.Ordinal))
            {
                cached.HeadCommitDate = newIso;
                dirty = true;
            }

            if (_api.HasToken
                && (!cached.CommitMetadataResolved || cached.Bundles.Any(b => string.IsNullOrEmpty(b.CommitSha))))
            {
                cached.CommitMetadataResolved = await ResolveBundleCommitsAsync(
                    cached.Bundles,
                    config.Owner,
                    config.Name,
                    headSha,
                    finalDate,
                    forceAll: !cached.CommitMetadataResolved,
                    ct,
                    progress);
                dirty = true;
            }

            if (dirty)
            {
                cached.IndexedAt = DateTime.UtcNow.ToString("o");
                SaveCache(cached);
            }

            return cached;
        }

        private async Task<RepoIndexCache?> TryRefreshIncrementalAsync(
            RepoSourceConfig config,
            RepoIndexCache cached,
            string headSha,
            DateTime? finalDate,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress)
        {
            // compare API 能告诉我们哪些路径发生变化；只重建受影响目录的 bundle，
            // 可以避免每次刷新都为所有模型重新解析提交元数据。
            if (string.IsNullOrWhiteSpace(cached.HeadCommit))
                return null;

            GitHubCompareResponse compare;
            try
            {
                compare = await _api.GetCompareAsync(config.Owner, config.Name, cached.HeadCommit, headSha, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Warn("Compare failed for {0}/{1}, falling back to full index: {2}", config.Owner, config.Name, ex.Message);
                return null;
            }

            if (string.Equals(compare.Status, "identical", StringComparison.OrdinalIgnoreCase))
                return await UpdateUnchangedCacheAsync(config, cached, headSha, finalDate, ct, progress);

            if (!string.Equals(compare.Status, "ahead", StringComparison.OrdinalIgnoreCase)
                || compare.BehindBy > 0)
                return null;

            if (compare.Files is null)
                return null;

            if (compare.Files.Count >= CompareFilesSoftLimit)
            {
                _logger.Info("Compare file list reached {0} items for {1}/{2}; falling back to full index", compare.Files.Count, config.Owner, config.Name);
                return null;
            }

            var changedDirs = GetChangedBundleDirs(compare.Files);
            if (changedDirs.Count == 0)
                return await UpdateUnchangedCacheAsync(config, cached, headSha, finalDate, ct, progress);

            progress?.Report(new RepoIndexProgress(0, changedDirs.Count));

            ct.ThrowIfCancellationRequested();
            var trees = await _api.GetTreeRecursiveAsync(config.Owner, config.Name, headSha, ct);
            var allBundles = AggregateBundles(config.RepoId, trees);
            var changedBundles = allBundles
                .Where(b => changedDirs.Contains(b.BundleDir))
                .ToList();

            var mergedBundles = cached.Bundles
                .Where(b => !changedDirs.Contains(b.BundleDir))
                .Concat(changedBundles)
                .ToList();
            SortBundles(mergedBundles);

            bool commitMetadataResolved = cached.CommitMetadataResolved;
            if (_api.HasToken)
            {
                var resolveTargets = cached.CommitMetadataResolved ? changedBundles : mergedBundles;
                commitMetadataResolved = await ResolveBundleCommitsAsync(
                    resolveTargets,
                    config.Owner,
                    config.Name,
                    headSha,
                    finalDate,
                    forceAll: !cached.CommitMetadataResolved,
                    ct,
                    progress);
            }
            else
            {
                if (changedBundles.Count > 0)
                    commitMetadataResolved = false;
                progress?.Report(new RepoIndexProgress(changedDirs.Count, changedDirs.Count));
            }

            var newCache = CreateCache(config, headSha, finalDate, commitMetadataResolved, mergedBundles, trees.Truncated);
            SaveCache(newCache);
            return newCache;
        }

        private async Task<RepoIndexCache> RefreshFullAsync(
            RepoSourceConfig config,
            string headSha,
            DateTime? finalDate,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress)
        {
            ct.ThrowIfCancellationRequested();
            var trees = await _api.GetTreeRecursiveAsync(config.Owner, config.Name, headSha, ct);

            var bundles = AggregateBundles(config.RepoId, trees);

            var commitMetadataResolved = await ResolveBundleCommitsAsync(
                bundles,
                config.Owner,
                config.Name,
                headSha,
                finalDate,
                forceAll: false,
                ct,
                progress);

            var newCache = CreateCache(config, headSha, finalDate, commitMetadataResolved, bundles, trees.Truncated);
            SaveCache(newCache);
            return newCache;
        }

        private static RepoIndexCache CreateCache(
            RepoSourceConfig config,
            string headSha,
            DateTime? finalDate,
            bool commitMetadataResolved,
            List<SpineBundle> bundles,
            bool truncated)
        {
            return new RepoIndexCache
            {
                SchemaVersion = CurrentSchemaVersion,
                RepoId = config.RepoId,
                Host = config.Host,
                Owner = config.Owner,
                Name = config.Name,
                Branch = config.Branch!,
                HeadCommit = headSha,
                HeadCommitDate = finalDate?.ToString("o") ?? string.Empty,
                IndexedAt = DateTime.UtcNow.ToString("o"),
                CommitMetadataResolved = commitMetadataResolved,
                Bundles = bundles,
                Truncated = truncated
            };
        }

        private static HashSet<string> GetChangedBundleDirs(IEnumerable<GitHubCompareFile> files)
        {
            var dirs = new HashSet<string>(StringComparer.Ordinal);

            void AddPath(string? path)
            {
                if (!IsBundleRelevantPath(path))
                    return;
                dirs.Add(GetParentDir(path!));
            }

            foreach (var file in files)
            {
                AddPath(file.Filename);
                AddPath(file.PreviousFilename);
            }

            return dirs;
        }
    }
}
