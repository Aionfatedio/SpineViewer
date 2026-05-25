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
    public record BundleDownloadRequest(
        RepoSourceConfig RepoConfig,
        SpineBundle Bundle,
        string CommitSha,
        string LocalBundleDir,
        bool TrackInLibrary = true,
        bool OverwriteExisting = false);

    public record BundleDownloadResult(
        BundleDownloadRequest Request,
        string LocalSkelPath,
        long TotalBytes,
        bool AlreadyExists);

    public record BundleDownloadProgress(
        string CurrentFile,
        int CompletedFiles,
        int TotalFiles,
        long DownloadedBytes,
        long TotalBytes);

    public record LocalBundleInfo(DownloadedBundleState State, DateTime? UpdatedAt);

    public class BundleDownloadService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly GitHubApiClient _api;
        private readonly string _cacheRoot;
        private readonly Dictionary<string, DownloadIndex> _downloadIndexCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _downloadIndexLock = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        public int FileConcurrency { get; set; } = 4;

        public BundleDownloadService(GitHubApiClient api, string cacheRoot)
        {
            _api = api;
            _cacheRoot = cacheRoot;
        }

        public async Task<BundleDownloadResult> DownloadAsync(
            BundleDownloadRequest req,
            IProgress<BundleDownloadProgress>? progress,
            CancellationToken ct)
        {
            var b = req.Bundle;
            var commit = req.CommitSha;

            var jobs = new List<FileJob>();
            void AddJob(string repoPath)
            {
                var localPath = Path.Combine(req.LocalBundleDir, GetFileName(repoPath));
                jobs.Add(new FileJob(repoPath, localPath));
            }
            AddJob(b.SkelPath);
            if (!string.IsNullOrEmpty(b.AtlasPath))
                AddJob(b.AtlasPath!);
            foreach (var png in b.PngPaths)
                AddJob(png);

            NetSourcePathProvider.EnsureDirectoryExists(req.LocalBundleDir);

            int totalFiles = jobs.Count;
            int completedFiles = 0;
            long downloadedBytes = 0;
            var doneLock = new object();
            bool allExist = true;

            using var sem = new SemaphoreSlim(Math.Max(1, FileConcurrency));

            var tasks = jobs.Select(async job =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    if (!req.OverwriteExisting && File.Exists(job.LocalPath))
                    {
                        var fi = new FileInfo(job.LocalPath);
                        lock (doneLock)
                        {
                            completedFiles++;
                            downloadedBytes += fi.Length;
                            progress?.Report(new BundleDownloadProgress(GetFileName(job.RepoPath), completedFiles, totalFiles, downloadedBytes, 0));
                        }
                        return;
                    }

                    allExist = false;

                    long perFile = 0;
                    var fileProgress = new Progress<long>(bytes =>
                    {
                        var delta = bytes - perFile;
                        perFile = bytes;
                        lock (doneLock)
                        {
                            downloadedBytes += delta;
                            progress?.Report(new BundleDownloadProgress(GetFileName(job.RepoPath), completedFiles, totalFiles, downloadedBytes, 0));
                        }
                    });

                    await _api.DownloadRawAsync(
                        req.RepoConfig.Owner,
                        req.RepoConfig.Name,
                        commit,
                        job.RepoPath,
                        job.LocalPath,
                        fileProgress,
                        ct);

                    lock (doneLock)
                    {
                        completedFiles++;
                        progress?.Report(new BundleDownloadProgress(GetFileName(job.RepoPath), completedFiles, totalFiles, downloadedBytes, 0));
                    }
                }
                finally
                {
                    sem.Release();
                }
            }).ToArray();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Error("Bundle download failed: {0} | {1}", b.SkelPath, ex.Message);
                throw;
            }

            var skelLocal = Path.Combine(req.LocalBundleDir, GetFileName(b.SkelPath));
            if (req.TrackInLibrary)
                SaveDownloadIndex(req, skelLocal);

            return new BundleDownloadResult(req, skelLocal, downloadedBytes, allExist);
        }

        public DownloadedBundleState GetBundleState(string repoId, SpineBundle bundle, string commitSha)
            => GetBundleInfo(repoId, bundle, commitSha).State;

        public LocalBundleInfo GetBundleInfo(string repoId, SpineBundle bundle, string commitSha)
        {
            var index = LoadDownloadIndex(repoId);
            var key = BuildBundleKey(bundle);
            if (index.Bundles.TryGetValue(key, out var entry))
            {
                var updatedAt = ParseIso(entry.UpdatedAt);
                if (EntryMatchesBundle(entry, bundle, commitSha))
                {
                    if (EntryFilesExist(entry, NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, repoId, commitSha, bundle.BundleDir)))
                        return new LocalBundleInfo(DownloadedBundleState.Current, updatedAt);
                }

                return new LocalBundleInfo(DownloadedBundleState.Outdated, updatedAt);
            }

            if (BundleFilesExist(bundle, NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, repoId, commitSha, bundle.BundleDir)))
                return new LocalBundleInfo(DownloadedBundleState.Outdated, null);

            return new LocalBundleInfo(DownloadedBundleState.None, null);
        }

        public bool TryGetLocalSkelPath(string repoId, SpineBundle bundle, string commitSha, out string localSkelPath)
        {
            var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, repoId, commitSha, bundle.BundleDir);
            if (BundleFilesExist(bundle, localDir))
            {
                localSkelPath = Path.Combine(localDir, GetFileName(bundle.SkelPath));
                return true;
            }

            localSkelPath = string.Empty;
            return false;
        }

        public int RemoveLocalFiles(string repoId, SpineBundle bundle, string commitSha)
        {
            lock (_downloadIndexLock)
            {
                var index = LoadDownloadIndex(repoId);
                var key = BuildBundleKey(bundle);
                index.Bundles.TryGetValue(key, out var entry);
                var indexChanged = entry is not null && index.Bundles.Remove(key);

                var localDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, repoId, commitSha, bundle.BundleDir);
                var candidates = entry is not null
                    ? BuildEntryFilePaths(entry, localDir)
                    : BuildBundleFilePaths(bundle, localDir);

                var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var other in index.Bundles.Values)
                {
                    var otherLocalDir = NetSourcePathProvider.GetBundleLocalDir(_cacheRoot, repoId, commitSha, other.BundleDir);
                    foreach (var path in BuildEntryFilePaths(other, otherLocalDir))
                        protectedFiles.Add(path);
                }

                int removed = 0;
                foreach (var path in candidates)
                {
                    if (protectedFiles.Contains(path))
                        continue;

                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            removed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex.ToString());
                        _logger.Warn("Failed to remove GitHub repo local file {0}: {1}", path, ex.Message);
                    }
                }

                if (indexChanged)
                    SaveDownloadIndex(repoId, index);

                return removed;
            }
        }

        public async Task<List<BundleDownloadResult>> DownloadManyAsync(
            IEnumerable<BundleDownloadRequest> requests,
            IProgress<BundleDownloadProgress>? progress,
            CancellationToken ct)
        {
            var results = new List<BundleDownloadResult>();
            foreach (var req in requests)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await DownloadAsync(req, progress, ct));
            }
            return results;
        }

        private static string GetFileName(string repoPath)
        {
            var idx = repoPath.LastIndexOf('/');
            return idx < 0 ? repoPath : repoPath[(idx + 1)..];
        }

        private static HashSet<string> BuildBundleFilePaths(SpineBundle bundle, string localDir)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? repoPath)
            {
                if (!string.IsNullOrWhiteSpace(repoPath))
                    paths.Add(Path.Combine(localDir, GetFileName(repoPath)));
            }

            Add(bundle.SkelPath);
            Add(bundle.AtlasPath);
            foreach (var png in bundle.PngPaths)
                Add(png);
            return paths;
        }

        private static HashSet<string> BuildEntryFilePaths(DownloadIndexEntry entry, string localDir)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? repoPath)
            {
                if (!string.IsNullOrWhiteSpace(repoPath))
                    paths.Add(Path.Combine(localDir, GetFileName(repoPath)));
            }

            Add(entry.SkelPath);
            Add(entry.AtlasPath);
            foreach (var png in entry.PngPaths)
                Add(png);
            return paths;
        }

        private sealed record FileJob(string RepoPath, string LocalPath);

        private DownloadIndex LoadDownloadIndex(string repoId)
        {
            lock (_downloadIndexLock)
            {
                if (_downloadIndexCache.TryGetValue(repoId, out var cached))
                    return cached;

                var path = NetSourcePathProvider.GetDownloadIndexPath(_cacheRoot, repoId);
                DownloadIndex index;
                try
                {
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        index = JsonSerializer.Deserialize<DownloadIndex>(json, _jsonOptions) ?? new DownloadIndex();
                    }
                    else
                    {
                        index = new DownloadIndex();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Warn("Failed to load download index {0}: {1}", repoId, ex.Message);
                    index = new DownloadIndex();
                }

                _downloadIndexCache[repoId] = index;
                return index;
            }
        }

        private void SaveDownloadIndex(BundleDownloadRequest req, string localSkelPath)
        {
            lock (_downloadIndexLock)
            {
                var index = LoadDownloadIndex(req.RepoConfig.RepoId);
                index.Bundles[BuildBundleKey(req.Bundle)] = new DownloadIndexEntry
                {
                    BundleDir = req.Bundle.BundleDir,
                    ModelName = req.Bundle.ModelName,
                    CommitSha = req.CommitSha,
                    SkelPath = req.Bundle.SkelPath,
                    AtlasPath = req.Bundle.AtlasPath,
                    PngPaths = req.Bundle.PngPaths.ToList(),
                    BundleHash = req.Bundle.BundleHash,
                    TotalSize = req.Bundle.TotalSize,
                    FileCount = req.Bundle.FileCount,
                    LocalSkelPath = localSkelPath,
                    UpdatedAt = DateTime.UtcNow.ToString("o")
                };

                try
                {
                    SaveDownloadIndex(req.RepoConfig.RepoId, index);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Warn("Failed to save download index {0}: {1}", req.RepoConfig.RepoId, ex.Message);
                }
            }
        }

        private void SaveDownloadIndex(string repoId, DownloadIndex index)
        {
            var path = NetSourcePathProvider.GetDownloadIndexPath(_cacheRoot, repoId);
            NetSourcePathProvider.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(index, _jsonOptions));
        }

        private static string BuildBundleKey(SpineBundle bundle)
            => $"{bundle.BundleDir}\n{bundle.ModelName}";

        private static bool EntryMatchesBundle(DownloadIndexEntry entry, SpineBundle bundle, string commitSha)
        {
            var versionMatches = !string.IsNullOrEmpty(entry.BundleHash) && !string.IsNullOrEmpty(bundle.BundleHash)
                ? string.Equals(entry.BundleHash, bundle.BundleHash, StringComparison.OrdinalIgnoreCase)
                : string.Equals(entry.CommitSha, commitSha, StringComparison.OrdinalIgnoreCase);

            return versionMatches
                && string.Equals(entry.SkelPath, bundle.SkelPath, StringComparison.Ordinal)
                && string.Equals(entry.AtlasPath ?? string.Empty, bundle.AtlasPath ?? string.Empty, StringComparison.Ordinal)
                && entry.FileCount == bundle.FileCount
                && entry.TotalSize == bundle.TotalSize
                && TexturePathSetEquals(entry.PngPaths, bundle.PngPaths);
        }

        private static bool TexturePathSetEquals(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
        {
            return left.Count == right.Count
                && left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
        }

        private static bool EntryFilesExist(DownloadIndexEntry entry, string localDir)
        {
            if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(localDir)) return false;
            if (string.IsNullOrEmpty(entry.SkelPath) || !File.Exists(Path.Combine(localDir, GetFileName(entry.SkelPath)))) return false;
            if (!string.IsNullOrEmpty(entry.AtlasPath) && !File.Exists(Path.Combine(localDir, GetFileName(entry.AtlasPath)))) return false;
            return entry.PngPaths.All(p => File.Exists(Path.Combine(localDir, GetFileName(p))));
        }

        private static bool BundleFilesExist(SpineBundle bundle, string localDir)
        {
            if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(localDir)) return false;
            if (string.IsNullOrEmpty(bundle.SkelPath) || !File.Exists(Path.Combine(localDir, GetFileName(bundle.SkelPath)))) return false;
            if (!string.IsNullOrEmpty(bundle.AtlasPath) && !File.Exists(Path.Combine(localDir, GetFileName(bundle.AtlasPath!)))) return false;
            return bundle.PngPaths.All(p => File.Exists(Path.Combine(localDir, GetFileName(p))));
        }

        private static DateTime? ParseIso(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
                ? d
                : null;
        }

        private sealed class DownloadIndex
        {
            public Dictionary<string, DownloadIndexEntry> Bundles { get; set; } = [];
        }

        private sealed class DownloadIndexEntry
        {
            public string BundleDir { get; set; } = string.Empty;
            public string ModelName { get; set; } = string.Empty;
            public string CommitSha { get; set; } = string.Empty;
            public string SkelPath { get; set; } = string.Empty;
            public string? AtlasPath { get; set; }
            public List<string> PngPaths { get; set; } = [];
            public string? BundleHash { get; set; }
            public long TotalSize { get; set; }
            public int FileCount { get; set; }
            public string LocalSkelPath { get; set; } = string.Empty;
            public string UpdatedAt { get; set; } = string.Empty;
        }
    }
}
