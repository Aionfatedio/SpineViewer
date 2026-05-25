using NLog;
using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpineViewer.NetSource.Services
{
    public record RepoIndexProgress(int Done, int Total);

    public class RepoIndexService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const int GraphQLBatchSize = 100;

        private const int CompareFilesSoftLimit = 300;

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
            ".jpeg"
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
            var repoInfo = await _api.GetRepoAsync(config.Owner, config.Name, ct);
            if (string.IsNullOrWhiteSpace(config.Branch))
                config.Branch = string.IsNullOrWhiteSpace(repoInfo.DefaultBranch) ? "main" : repoInfo.DefaultBranch;

            ct.ThrowIfCancellationRequested();

            var (headSha, branchCommitDate) = await _api.GetBranchHeadAsync(config.Owner, config.Name, config.Branch!, ct);

            DateTime? finalDate = TryParseIso(repoInfo.PushedAt) ?? branchCommitDate;

            var cached = TryLoadCache(config.RepoId);
            if (!forceFull && cached is not null && cached.Bundles.Count > 0)
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
                SchemaVersion = 1,
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

        private static bool IsBundleRelevantPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var name = GetFileName(path).ToLowerInvariant();
            return name.EndsWith(".skel.bytes")
                || name.EndsWith(".skel")
                || name.EndsWith(".atlas.txt")
                || name.EndsWith(".atlas")
                || name.EndsWith(".json")
                || IsTextureFileName(name);
        }

        private async Task<bool> ResolveBundleCommitsAsync(
            List<SpineBundle> bundles,
            string owner,
            string name,
            string headSha,
            DateTime? fallbackDate,
            bool forceAll,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress)
        {
            var pending = bundles
                .Where(b => (forceAll || string.IsNullOrEmpty(b.CommitSha)) && !string.IsNullOrEmpty(b.SkelPath))
                .ToList();
            if (pending.Count == 0) return _api.HasToken;

            var fallbackIso = fallbackDate?.ToString("o") ?? string.Empty;
            progress?.Report(new RepoIndexProgress(0, pending.Count));

            if (!_api.HasToken)
            {
                progress?.Report(new RepoIndexProgress(pending.Count, pending.Count));
                return false;
            }

            foreach (var b in pending)
            {
                b.CommitSha = null;
                b.CommitDate = null;
            }

            bool allBatchesSucceeded = true;
            for (int start = 0; start < pending.Count; start += GraphQLBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = pending.Skip(start).Take(GraphQLBatchSize).ToList();
                var query = BuildGraphQLBatchQuery(owner, name, headSha, batch);

                try
                {
                    var body = await _api.PostGraphQLAsync(query, ct);
                    if (!ApplyGraphQLBatch(body, batch))
                        allBatchesSucceeded = false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allBatchesSucceeded = false;
                    _logger.Debug(ex.ToString());
                    _logger.Warn("GraphQL batch failed for [{0}..{1}] in {2}/{3}: {4}",
                        start, start + batch.Count, owner, name, ex.Message);
                }

                progress?.Report(new RepoIndexProgress(Math.Min(start + batch.Count, pending.Count), pending.Count));
            }

            foreach (var b in pending)
            {
                if (string.IsNullOrEmpty(b.CommitSha))
                {
                    b.CommitSha = headSha;
                    b.CommitDate = fallbackIso;
                }
            }

            return allBatchesSucceeded;
        }

        private static string BuildGraphQLBatchQuery(string owner, string name, string headSha, List<SpineBundle> batch)
        {
            var sb = new StringBuilder(256 + batch.Count * 200);
            sb.Append("query{repository(owner:\"").Append(EscapeGraphQLString(owner));
            sb.Append("\",name:\"").Append(EscapeGraphQLString(name));
            sb.Append("\"){object(expression:\"").Append(EscapeGraphQLString(headSha));
            sb.Append("\"){... on Commit{");
            for (int i = 0; i < batch.Count; i++)
            {
                sb.Append('b').Append(i).Append(":history(first:1,path:\"");
                sb.Append(EscapeGraphQLString(batch[i].SkelPath));
                sb.Append("\"){nodes{oid committedDate}}");
            }
            sb.Append("}}}}");
            return sb.ToString();
        }

        private static bool ApplyGraphQLBatch(string body, List<SpineBundle> batch)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
                return false;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            if (!data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object) return false;
            if (!repo.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.Object) return false;

            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Name.Length < 2 || prop.Name[0] != 'b') continue;
                if (!int.TryParse(prop.Name.AsSpan(1), out var idx)) continue;
                if (idx < 0 || idx >= batch.Count) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                if (!prop.Value.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
                if (nodes.GetArrayLength() == 0) continue;

                var first = nodes[0];
                if (first.ValueKind != JsonValueKind.Object) continue;

                string? oid = first.TryGetProperty("oid", out var oidEl) && oidEl.ValueKind == JsonValueKind.String ? oidEl.GetString() : null;
                string? date = first.TryGetProperty("committedDate", out var dateEl) && dateEl.ValueKind == JsonValueKind.String ? dateEl.GetString() : null;

                if (!string.IsNullOrEmpty(oid))
                {
                    var bundle = batch[idx];
                    bundle.CommitSha = oid;
                    bundle.CommitDate = date;
                }
            }

            return batch.All(b => !string.IsNullOrEmpty(b.CommitSha));
        }

        private static string EscapeGraphQLString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static DateTime? TryParseIso(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }

        private static List<SpineBundle> AggregateBundles(string repoId, GitHubTreesResponse trees)
        {
            var dirIndex = new Dictionary<string, DirEntry>(StringComparer.Ordinal);

            foreach (var entry in trees.Tree)
            {
                if (entry.Type != "blob" || string.IsNullOrEmpty(entry.Path)) continue;
                var path = entry.Path;
                var dir = GetParentDir(path);
                if (!dirIndex.TryGetValue(dir, out var bucket))
                {
                    bucket = new DirEntry();
                    dirIndex[dir] = bucket;
                }

                var lowerName = GetFileName(path).ToLowerInvariant();

                if (IsTextureFileName(lowerName))
                {
                    bucket.TextureFiles.Add(entry);
                }
                else if (lowerName.EndsWith(".skel.bytes"))
                {
                    bucket.SkelFiles.Add((entry, ".skel.bytes"));
                }
                else if (lowerName.EndsWith(".skel"))
                {
                    bucket.SkelFiles.Add((entry, ".skel"));
                }
                else if (lowerName.EndsWith(".atlas.txt"))
                {
                    bucket.AtlasFiles[lowerName] = entry;
                }
                else if (lowerName.EndsWith(".atlas"))
                {
                    bucket.AtlasFiles[lowerName] = entry;
                }
                else if (lowerName.EndsWith(".json"))
                {
                    bucket.JsonFiles.Add(entry);
                }
            }

            var bundles = new List<SpineBundle>();

            foreach (var (dir, bucket) in dirIndex)
            {
                foreach (var (skelEntry, skelSuffix) in bucket.SkelFiles)
                {
                    bundles.Add(BuildBundle(repoId, dir, skelEntry, skelSuffix, bucket));
                }

                foreach (var jsonEntry in bucket.JsonFiles)
                {
                    var matchedAtlas = FindAtlasFor(jsonEntry.Path!, ".json", ".atlas", bucket);
                    if (matchedAtlas is null) continue;
                    bundles.Add(BuildBundle(repoId, dir, jsonEntry, ".json", bucket));
                }
            }

            SortBundles(bundles);

            return bundles;
        }

        private static SpineBundle BuildBundle(string repoId, string dir, GitHubTreeEntry skelEntry, string skelSuffix, DirEntry bucket)
        {
            var skelPath = skelEntry.Path!;
            var atlasSuffix = SkelToAtlasSuffix(skelSuffix);
            var atlasEntry = FindAtlasFor(skelPath, skelSuffix, atlasSuffix, bucket);
            var textureFiles = SelectTextureFiles(skelPath, atlasEntry, bucket.TextureFiles)
                .Where(p => p.Path is not null)
                .OrderBy(p => p.Path, StringComparer.Ordinal)
                .ToList();

            var bundle = new SpineBundle
            {
                RepoId = repoId,
                BundleDir = dir,
                ModelName = StripSuffix(GetFileName(skelPath), skelSuffix),
                SkelPath = skelPath,
                AtlasPath = atlasEntry?.Path,
                PngPaths = textureFiles.Select(p => p.Path!).ToList(),
                BundleHash = BuildBundleHash(skelEntry, atlasEntry, textureFiles)
            };
            long total = skelEntry.Size ?? 0;
            int count = 1;
            if (atlasEntry is not null) { total += atlasEntry.Size ?? 0; count++; }
            foreach (var texture in textureFiles) { total += texture.Size ?? 0; count++; }
            bundle.TotalSize = total;
            bundle.FileCount = count;
            return bundle;
        }

        private static List<GitHubTreeEntry> SelectTextureFiles(
            string skelPath,
            GitHubTreeEntry? atlasEntry,
            List<GitHubTreeEntry> textureFiles)
        {
            var validTextures = textureFiles
                .Where(t => !string.IsNullOrEmpty(t.Path))
                .ToList();
            if (validTextures.Count <= 1)
                return validTextures;

            var groups = validTextures
                .GroupBy(t => GetTextureFormatKey(t.Path!))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .Select(g => g.OrderBy(t => t.Path, StringComparer.Ordinal).ToList())
                .ToList();
            if (groups.Count <= 1)
                return groups.FirstOrDefault() ?? validTextures;

            var candidates = BuildTextureBaseCandidates(skelPath, atlasEntry).ToArray();
            var scoredGroups = groups
                .Select(g => new
                {
                    Files = g,
                    FormatKey = GetTextureFormatKey(g[0].Path!),
                    BaseMatchCount = g.Count(t => TextureStemMatchesAnyCandidate(GetTextureStem(t.Path!), candidates)),
                    StemKey = string.Join("\n", g.Select(t => GetTextureStem(t.Path!)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                })
                .ToList();

            var bestMatch = scoredGroups.Max(g => g.BaseMatchCount);
            if (bestMatch > 0)
            {
                return scoredGroups
                    .Where(g => g.BaseMatchCount == bestMatch)
                    .OrderByDescending(g => g.Files.Count)
                    .ThenBy(g => GetTextureFormatPriority(g.FormatKey))
                    .First()
                    .Files;
            }

            if (scoredGroups.Select(g => g.StemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                return scoredGroups
                    .OrderBy(g => GetTextureFormatPriority(g.FormatKey))
                    .First()
                    .Files;
            }

            return validTextures
                .OrderBy(t => t.Path, StringComparer.Ordinal)
                .ToList();
        }

        private static IEnumerable<string> BuildTextureBaseCandidates(string skelPath, GitHubTreeEntry? atlasEntry)
        {
            var skelName = GetFileName(skelPath);
            foreach (var (skelSuffix, _) in _suffixPairs)
            {
                if (skelName.EndsWith(skelSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return StripSuffix(skelName, skelSuffix);
                    break;
                }
            }

            if (!string.IsNullOrEmpty(atlasEntry?.Path))
            {
                var atlasName = GetFileName(atlasEntry.Path!);
                if (atlasName.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase))
                    yield return StripSuffix(atlasName, ".atlas.txt");
                else if (atlasName.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                    yield return StripSuffix(atlasName, ".atlas");
            }
        }

        private static bool TextureStemMatchesAnyCandidate(string stem, IReadOnlyCollection<string> candidates)
        {
            return candidates.Any(candidate =>
                stem.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTextureFileName(string lowerName)
            => _textureSuffixes.Any(lowerName.EndsWith);

        private static string GetTextureFormatKey(string path)
        {
            var lowerName = GetFileName(path).ToLowerInvariant();
            if (lowerName.EndsWith(".png")) return "png";
            if (lowerName.EndsWith(".webp")) return "webp";
            if (lowerName.EndsWith(".jpg") || lowerName.EndsWith(".jpeg")) return "jpg";
            return string.Empty;
        }

        private static int GetTextureFormatPriority(string formatKey) => formatKey switch
        {
            "png" => 0,
            "webp" => 1,
            "jpg" => 2,
            _ => 10
        };

        private static string GetTextureStem(string path)
        {
            var fileName = GetFileName(path);
            foreach (var suffix in _textureSuffixes)
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return fileName[..^suffix.Length];
            }

            return fileName;
        }

        private static void SortBundles(List<SpineBundle> bundles)
        {
            bundles.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.BundleDir, b.BundleDir);
                return c != 0 ? c : string.CompareOrdinal(a.ModelName, b.ModelName);
            });
        }

        private static string BuildBundleHash(GitHubTreeEntry skelEntry, GitHubTreeEntry? atlasEntry, IReadOnlyList<GitHubTreeEntry> pngFiles)
        {
            var sb = new StringBuilder();

            void Append(GitHubTreeEntry entry)
            {
                sb.Append(entry.Path).Append('|')
                    .Append(entry.Sha).Append('|')
                    .Append(entry.Size?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                    .Append('\n');
            }

            Append(skelEntry);
            if (atlasEntry is not null)
                Append(atlasEntry);
            foreach (var png in pngFiles)
                Append(png);

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static string SkelToAtlasSuffix(string skelSuffix) => skelSuffix switch
        {
            ".skel" => ".atlas",
            ".skel.bytes" => ".atlas.txt",
            ".json" => ".atlas",
            _ => ".atlas"
        };

        private static GitHubTreeEntry? FindAtlasFor(string skelPath, string skelSuffix, string atlasSuffix, DirEntry bucket)
        {
            var skelName = GetFileName(skelPath);
            if (!skelName.EndsWith(skelSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            var basePrefix = skelName[..^skelSuffix.Length];
            var targetAtlasName = (basePrefix + atlasSuffix).ToLowerInvariant();
            return bucket.AtlasFiles.TryGetValue(targetAtlasName, out var hit) ? hit : null;
        }

        private static string GetParentDir(string path)
        {
            var idx = path.LastIndexOf('/');
            return idx <= 0 ? string.Empty : path[..idx];
        }

        private static string GetFileName(string path)
        {
            var idx = path.LastIndexOf('/');
            return idx < 0 ? path : path[(idx + 1)..];
        }

        private static string StripSuffix(string fileName, string suffix)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
            return fileName;
        }

        private sealed class DirEntry
        {
            public List<(GitHubTreeEntry Entry, string Suffix)> SkelFiles { get; } = [];
            public List<GitHubTreeEntry> JsonFiles { get; } = [];
            public Dictionary<string, GitHubTreeEntry> AtlasFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<GitHubTreeEntry> TextureFiles { get; } = [];
        }
    }
}
