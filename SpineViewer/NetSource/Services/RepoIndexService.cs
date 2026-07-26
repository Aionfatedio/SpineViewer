using NLog;
using Spine;
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

        // 单批别名数受服务端解析耗时约束, 过大易撞查询超时; 150 是收益与稳定的折中。
        private const int GraphQLBatchSize = 150;

        // 批次间无共享数据, 有限并发成倍缩短首次索引; 保持小值以避开 GitHub 二级限流。
        private const int GraphQLConcurrency = 4;

        private const int CompareFilesSoftLimit = 300;

        // v3: 聚合规则变更 (所有骨架一律要求同名 atlas) 且提交元数据改为空值语义, 旧缓存全量重建。
        private const int CurrentSchemaVersion = 3;

        // 骨架/atlas 后缀约定以 SpineObject.PossibleSuffixMapping 为唯一来源,
        // 按长度降序排列保证复合后缀 (.skel.bytes/.atlas.txt) 优先匹配。
        private static readonly string[] _skelSuffixes = SpineObject.PossibleSuffixMapping.Keys
            .OrderByDescending(s => s.Length)
            .ToArray();

        private static readonly string[] _atlasSuffixes = SpineObject.PossibleSuffixMapping.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length)
            .ToArray();

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
                // 只删除索引缓存文件; 已下载的模型文件与下载索引永久保留,
                // 重新添加同一仓库源 (RepoId 相同) 时本地蓝/橙状态可直接恢复。
                var path = NetSourcePathProvider.GetRepoTreesCachePath(_cacheRoot, repoId);
                if (File.Exists(path))
                    File.Delete(path);

                // 从未下载过任何模型时目录已空, 顺带移除; 非空 (含下载) 则保留。
                var dir = NetSourcePathProvider.GetRepoCacheDir(_cacheRoot, repoId);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Warn("Failed to delete repo index cache {0}: {1}", repoId, ex.Message);
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

            // 分支未指定时按默认分支解析, 但不写回 config: RepoId 依赖 Branch,
            // 仓库源身份必须自添加起保持稳定; 未指定分支的源始终跟随仓库默认分支。
            var branch = !string.IsNullOrWhiteSpace(config.Branch)
                ? config.Branch!
                : (string.IsNullOrWhiteSpace(repoInfo.DefaultBranch) ? "main" : repoInfo.DefaultBranch!);

            ct.ThrowIfCancellationRequested();

            var (headSha, branchCommitDate) = await _api.GetBranchHeadAsync(config.Owner, config.Name, branch, ct);

            DateTime? finalDate = TryParseIso(repoInfo.PushedAt) ?? branchCommitDate;

            var cached = TryLoadCache(config.RepoId);
            if (!forceFull
                && cached is not null
                && cached.SchemaVersion == CurrentSchemaVersion
                && cached.Bundles.Count > 0)
            {
                if (string.Equals(cached.HeadCommit, headSha, StringComparison.OrdinalIgnoreCase))
                    return await UpdateUnchangedCacheAsync(config, cached, headSha, finalDate, ct, progress);

                var incremental = await TryRefreshIncrementalAsync(config, branch, cached, headSha, finalDate, ct, progress);
                if (incremental is not null)
                    return incremental;
            }

            return await RefreshFullAsync(config, branch, headSha, finalDate, cached, ct, progress);
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
            string branch,
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
                // 变更目录的 bundle 是新对象 (CommitSha 为空), 旧的失败项也保持空值,
                // 直接传合并列表即可只补查空缺项。
                commitMetadataResolved = await ResolveBundleCommitsAsync(
                    mergedBundles,
                    config.Owner,
                    config.Name,
                    headSha,
                    ct,
                    progress);
            }
            else
            {
                if (changedBundles.Count > 0)
                    commitMetadataResolved = false;
                progress?.Report(new RepoIndexProgress(changedDirs.Count, changedDirs.Count));
            }

            var newCache = CreateCache(config, branch, headSha, finalDate, commitMetadataResolved, mergedBundles, trees.Truncated);
            SaveCache(newCache);
            return newCache;
        }

        private async Task<RepoIndexCache> RefreshFullAsync(
            RepoSourceConfig config,
            string branch,
            string headSha,
            DateTime? finalDate,
            RepoIndexCache? previous,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress)
        {
            ct.ThrowIfCancellationRequested();
            var trees = await _api.GetTreeRecursiveAsync(config.Owner, config.Name, headSha, ct);

            var bundles = AggregateBundles(config.RepoId, trees);
            InheritCommitMetadata(bundles, previous);

            var commitMetadataResolved = await ResolveBundleCommitsAsync(
                bundles,
                config.Owner,
                config.Name,
                headSha,
                ct,
                progress);

            var newCache = CreateCache(config, branch, headSha, finalDate, commitMetadataResolved, bundles, trees.Truncated);
            SaveCache(newCache);
            return newCache;
        }

        /// <summary>
        /// BundleHash 未变 ⇒ bundle 文件内容未变 ⇒ 其最后提交必然未变, 直接继承旧缓存的
        /// 提交元数据, 全量重建 (含 schema 升级) 只为真正变化的模型付 GraphQL 代价。
        /// 仅当旧缓存元数据完整可信 (CommitMetadataResolved) 时继承, 避免带入历史回填值。
        /// </summary>
        private static void InheritCommitMetadata(List<SpineBundle> bundles, RepoIndexCache? previous)
        {
            if (previous is null || !previous.CommitMetadataResolved || previous.Bundles.Count == 0)
                return;

            var oldByKey = new Dictionary<string, SpineBundle>(StringComparer.Ordinal);
            foreach (var old in previous.Bundles)
            {
                if (string.IsNullOrEmpty(old.CommitSha) || string.IsNullOrEmpty(old.BundleHash))
                    continue;
                oldByKey[$"{old.SkelPath}\n{old.BundleHash}"] = old;
            }

            foreach (var b in bundles)
            {
                if (string.IsNullOrEmpty(b.BundleHash))
                    continue;
                if (oldByKey.TryGetValue($"{b.SkelPath}\n{b.BundleHash}", out var old))
                {
                    b.CommitSha = old.CommitSha;
                    b.CommitDate = old.CommitDate;
                }
            }
        }

        private static RepoIndexCache CreateCache(
            RepoSourceConfig config,
            string branch,
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
                Branch = branch,
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
