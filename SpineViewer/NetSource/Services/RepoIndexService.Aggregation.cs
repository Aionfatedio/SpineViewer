using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SpineViewer.NetSource.Services
{
    public partial class RepoIndexService
    {
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
                TexturePaths = textureFiles.Select(p => p.Path!).ToList(),
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
            // 同一目录可能同时包含 png/webp/jpg 等多套纹理。优先选名称与 skel/atlas 同 stem 的组；
            // 如果多种格式 stem 完全一致，则全部保留，交给 atlas 原文决定实际引用哪一张。
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
                    BaseMatchCount = g.Count(t => TextureStemMatchesAnyCandidate(GetTextureStem(t.Path!), candidates)),
                    StemKey = string.Join("\n", g.Select(t => GetTextureStem(t.Path!)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                })
                .ToList();

            var bestMatch = scoredGroups.Max(g => g.BaseMatchCount);
            if (bestMatch > 0)
            {
                var matchedFiles = scoredGroups
                    .Where(g => g.BaseMatchCount == bestMatch)
                    .SelectMany(g => g.Files)
                    .OrderBy(t => t.Path, StringComparer.Ordinal)
                    .ToList();
                if (matchedFiles.Count > 0)
                    return matchedFiles;
            }

            if (scoredGroups.Select(g => g.StemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                return scoredGroups
                    .SelectMany(g => g.Files)
                    .OrderBy(t => t.Path, StringComparer.Ordinal)
                    .ToList();
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

        private static bool IsTextureFileName(string lowerName)
            => _textureSuffixes.Any(lowerName.EndsWith);

        private static string GetTextureFormatKey(string path)
        {
            var lowerName = GetFileName(path).ToLowerInvariant();
            foreach (var suffix in _textureSuffixes)
            {
                if (lowerName.EndsWith(suffix))
                    return suffix.TrimStart('.');
            }
            return string.Empty;
        }

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

        private static string BuildBundleHash(GitHubTreeEntry skelEntry, GitHubTreeEntry? atlasEntry, IReadOnlyList<GitHubTreeEntry> textureFiles)
        {
            // 本地是否过期只看当前索引里的文件路径、blob sha 和大小；任一资源变化都会改变 bundle hash。
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
            foreach (var texture in textureFiles)
                Append(texture);

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
