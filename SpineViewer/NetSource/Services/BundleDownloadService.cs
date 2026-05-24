using NLog;
using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpineViewer.NetSource.Services
{
    public record BundleDownloadRequest(
        RepoSourceConfig RepoConfig,
        SpineBundle Bundle,
        string CommitSha,
        string LocalBundleDir);

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

    public class BundleDownloadService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly GitHubApiClient _api;
        private readonly string _cacheRoot;

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
                    if (File.Exists(job.LocalPath))
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
            return new BundleDownloadResult(req, skelLocal, downloadedBytes, allExist);
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

        private sealed record FileJob(string RepoPath, string LocalPath);
    }
}
