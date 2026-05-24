using NLog;
using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpineViewer.NetSource.Services
{
    public class RepoIndexService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const int GraphQLBatchSize = 100;

        private static readonly (string Skel, string Atlas)[] _suffixPairs =
        [
            (".skel", ".atlas"),
            (".skel.bytes", ".atlas.txt"),
            (".json", ".atlas")
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

        public async Task<RepoIndexCache> RefreshAsync(RepoSourceConfig config, bool forceFull, CancellationToken ct)
        {
            var repoInfo = await _api.GetRepoAsync(config.Owner, config.Name, ct);
            if (string.IsNullOrWhiteSpace(config.Branch))
                config.Branch = string.IsNullOrWhiteSpace(repoInfo.DefaultBranch) ? "main" : repoInfo.DefaultBranch;

            ct.ThrowIfCancellationRequested();

            var (headSha, branchCommitDate) = await _api.GetBranchHeadAsync(config.Owner, config.Name, config.Branch!, ct);

            DateTime? finalDate = TryParseIso(repoInfo.PushedAt) ?? branchCommitDate;

            var cached = TryLoadCache(config.RepoId);
            if (!forceFull
                && cached is not null
                && string.Equals(cached.HeadCommit, headSha, StringComparison.OrdinalIgnoreCase)
                && cached.Bundles.Count > 0)
            {
                bool dirty = false;
                if (finalDate.HasValue)
                {
                    var newIso = finalDate.Value.ToString("o");
                    if (!string.Equals(cached.HeadCommitDate, newIso, StringComparison.Ordinal))
                    {
                        cached.HeadCommitDate = newIso;
                        dirty = true;
                    }
                }

                if (cached.Bundles.Any(b => string.IsNullOrEmpty(b.CommitSha)))
                {
                    await ResolveBundleCommitsAsync(cached.Bundles, config.Owner, config.Name, headSha, finalDate, ct);
                    dirty = true;
                }

                if (dirty) SaveCache(cached);
                return cached;
            }

            ct.ThrowIfCancellationRequested();
            var trees = await _api.GetTreeRecursiveAsync(config.Owner, config.Name, headSha, ct);

            var bundles = AggregateBundles(config.RepoId, trees);

            await ResolveBundleCommitsAsync(bundles, config.Owner, config.Name, headSha, finalDate, ct);

            var newCache = new RepoIndexCache
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
                Bundles = bundles,
                Truncated = trees.Truncated
            };
            SaveCache(newCache);
            return newCache;
        }

        private async Task ResolveBundleCommitsAsync(
            List<SpineBundle> bundles,
            string owner,
            string name,
            string headSha,
            DateTime? fallbackDate,
            CancellationToken ct)
        {
            var pending = bundles.Where(b => string.IsNullOrEmpty(b.CommitSha) && !string.IsNullOrEmpty(b.SkelPath)).ToList();
            if (pending.Count == 0) return;

            var fallbackIso = fallbackDate?.ToString("o") ?? string.Empty;

            if (!_api.HasToken)
            {
                foreach (var b in pending)
                {
                    b.CommitSha = headSha;
                    b.CommitDate = fallbackIso;
                }
                return;
            }

            for (int start = 0; start < pending.Count; start += GraphQLBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = pending.Skip(start).Take(GraphQLBatchSize).ToList();
                var query = BuildGraphQLBatchQuery(owner, name, headSha, batch);

                try
                {
                    var body = await _api.PostGraphQLAsync(query, ct);
                    ApplyGraphQLBatch(body, batch);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Warn("GraphQL batch failed for [{0}..{1}] in {2}/{3}: {4}",
                        start, start + batch.Count, owner, name, ex.Message);
                }
            }

            foreach (var b in pending)
            {
                if (string.IsNullOrEmpty(b.CommitSha))
                {
                    b.CommitSha = headSha;
                    b.CommitDate = fallbackIso;
                }
            }
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

        private static void ApplyGraphQLBatch(string body, List<SpineBundle> batch)
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            if (!data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object) return;
            if (!repo.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.Object) return;

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

                if (lowerName.EndsWith(".png"))
                {
                    bucket.PngFiles.Add(entry);
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

            bundles.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.BundleDir, b.BundleDir);
                return c != 0 ? c : string.CompareOrdinal(a.ModelName, b.ModelName);
            });

            return bundles;
        }

        private static SpineBundle BuildBundle(string repoId, string dir, GitHubTreeEntry skelEntry, string skelSuffix, DirEntry bucket)
        {
            var skelPath = skelEntry.Path!;
            var atlasSuffix = SkelToAtlasSuffix(skelSuffix);
            var atlasEntry = FindAtlasFor(skelPath, skelSuffix, atlasSuffix, bucket);

            var bundle = new SpineBundle
            {
                RepoId = repoId,
                BundleDir = dir,
                ModelName = StripSuffix(GetFileName(skelPath), skelSuffix),
                SkelPath = skelPath,
                AtlasPath = atlasEntry?.Path,
                PngPaths = bucket.PngFiles
                    .Where(p => p.Path is not null)
                    .Select(p => p.Path!)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList()
            };
            long total = skelEntry.Size ?? 0;
            int count = 1;
            if (atlasEntry is not null) { total += atlasEntry.Size ?? 0; count++; }
            foreach (var png in bucket.PngFiles) { total += png.Size ?? 0; count++; }
            bundle.TotalSize = total;
            bundle.FileCount = count;
            return bundle;
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
            public List<GitHubTreeEntry> PngFiles { get; } = [];
        }
    }
}
