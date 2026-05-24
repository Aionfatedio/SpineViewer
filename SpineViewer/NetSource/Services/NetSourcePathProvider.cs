using System;
using System.IO;

namespace SpineViewer.NetSource.Services
{
    public static class NetSourcePathProvider
    {
        public const string DefaultCacheFolderName = "netcache";

        public const string ReposFolderName = "repos";

        public const string TreesCacheFileName = "trees.json";

        public const string DownloadsFolderName = "downloads";

        public const string CredentialsFileName = "credentials.dat";

        public static string GetDefaultCacheRoot()
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(exeDir, DefaultCacheFolderName);
        }

        public static string ResolveCacheRoot(string? userConfigured)
        {
            if (string.IsNullOrWhiteSpace(userConfigured))
                return GetDefaultCacheRoot();
            return Path.GetFullPath(userConfigured);
        }

        public static string GetRepoCacheDir(string cacheRoot, string repoId)
            => Path.Combine(cacheRoot, ReposFolderName, repoId);

        public static string GetRepoTreesCachePath(string cacheRoot, string repoId)
            => Path.Combine(GetRepoCacheDir(cacheRoot, repoId), TreesCacheFileName);

        public static string GetRepoDownloadsDir(string cacheRoot, string repoId)
            => Path.Combine(GetRepoCacheDir(cacheRoot, repoId), DownloadsFolderName);

        public static string GetBundleLocalDir(string cacheRoot, string repoId, string commitSha, string bundleDir)
        {
            var commitShort = string.IsNullOrEmpty(commitSha) ? "head" : commitSha[..Math.Min(7, commitSha.Length)];
            var safeBundleDir = (bundleDir ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(GetRepoDownloadsDir(cacheRoot, repoId), commitShort, safeBundleDir);
        }

        public static string GetCredentialsFilePath(string cacheRoot)
            => Path.Combine(cacheRoot, CredentialsFileName);

        public static void EnsureDirectoryExists(string dir)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
