using System;
using System.IO;

namespace SpineViewer.NetSource.Services
{
    // 网络源的路径约定集中在这里，避免下载、索引和凭证代码各自拼路径。
    // Resources/netcache 保存通用缓存和凭证，Resources/Spine 保存模型文件与模型索引。
    public static class NetSourcePathProvider
    {
        public const string ResourcesFolderName = "Resources";

        public const string NetCacheFolderName = "netcache";

        public const string SpineFolderName = "Spine";

        public const string ReposFolderName = "repos";

        public const string TreesCacheFileName = "trees.json";

        public const string DownloadIndexFileName = "downloaded-bundles.json";

        public const string DownloadsFolderName = "downloads";

        public const string CredentialsFileName = "credentials.dat";

        public static string GetDefaultCacheRoot()
        {
            return Path.Combine(GetExeDir(), ResourcesFolderName);
        }

        public static string ResolveCacheRoot(string? userConfigured)
        {
            if (string.IsNullOrWhiteSpace(userConfigured))
                return GetDefaultCacheRoot();
            // 相对路径相对 exe 目录解析, 避免随启动方式不同的工作目录漂移。
            return Path.GetFullPath(userConfigured, GetExeDir());
        }

        public static string GetNetCacheDir(string cacheRoot)
            => Path.Combine(cacheRoot, NetCacheFolderName);

        public static string GetSpineRoot(string cacheRoot)
            => Path.Combine(cacheRoot, SpineFolderName);

        public static string GetReposRoot(string cacheRoot)
            => Path.Combine(GetSpineRoot(cacheRoot), ReposFolderName);

        public static string GetRepoCacheDir(string cacheRoot, string repoId)
            => Path.Combine(GetReposRoot(cacheRoot), repoId);

        public static string GetRepoTreesCachePath(string cacheRoot, string repoId)
            => Path.Combine(GetRepoCacheDir(cacheRoot, repoId), TreesCacheFileName);

        public static string GetDownloadIndexPath(string cacheRoot, string repoId)
            => Path.Combine(GetRepoCacheDir(cacheRoot, repoId), DownloadIndexFileName);

        public static string GetRepoDownloadsDir(string cacheRoot, string repoId)
            => Path.Combine(GetRepoCacheDir(cacheRoot, repoId), DownloadsFolderName);

        public static string GetBundleLocalDir(string cacheRoot, string repoId, string bundleDir)
        {
            // 同一 bundle 只保留最新一次下载, 本地路径不按 commit 区分。
            var safeBundleDir = (bundleDir ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(GetRepoDownloadsDir(cacheRoot, repoId), safeBundleDir);
        }

        public static string GetCredentialsFilePath(string cacheRoot)
            => Path.Combine(GetNetCacheDir(cacheRoot), CredentialsFileName);

        public static void EnsureLayout(string cacheRoot)
        {
            EnsureDirectoryExists(GetNetCacheDir(cacheRoot));
            EnsureDirectoryExists(GetSpineRoot(cacheRoot));
            EnsureDirectoryExists(GetReposRoot(cacheRoot));
        }

        public static void EnsureDirectoryExists(string dir)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static string GetExeDir()
        {
            return Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
