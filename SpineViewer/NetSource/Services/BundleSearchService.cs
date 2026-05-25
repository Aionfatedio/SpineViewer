using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SpineViewer.NetSource.Services
{
    public record BundleSearchResult(
        SpineBundle Bundle,
        string RepoId,
        string RepoDisplayName,
        string CommitSha,
        DateTime? CommitDate,
        string DownloadCommitSha,
        int RepoOrderIndex);

    public class BundleSearchService
    {
        public IReadOnlyList<BundleSearchResult> Search(
            IReadOnlyDictionary<string, RepoIndexCache> caches,
            IReadOnlyDictionary<string, string> repoDisplayNames,
            IReadOnlyDictionary<string, int> repoOrder,
            string? query,
            IReadOnlyCollection<string>? filterRepoIds,
            int limit)
        {
            var results = new List<BundleSearchResult>();
            var hasQuery = !string.IsNullOrWhiteSpace(query);
            var trimmed = hasQuery ? query!.Trim() : string.Empty;

            bool Match(SpineBundle b)
            {
                if (!hasQuery) return true;
                return (b.ModelName?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (b.BundleDir?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (b.SkelPath?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false);
            }

            var orderedRepos = caches
                .OrderBy(kv => repoOrder.TryGetValue(kv.Key, out var idx) ? idx : int.MaxValue)
                .ToArray();

            foreach (var (repoId, cache) in orderedRepos)
            {
                if (filterRepoIds is { Count: > 0 } && !filterRepoIds.Contains(repoId)) continue;
                if (cache?.Bundles is null) continue;

                var displayName = repoDisplayNames.TryGetValue(repoId, out var dn) ? dn : repoId;
                var orderIdx = repoOrder.TryGetValue(repoId, out var oi) ? oi : int.MaxValue;
                var headDate = ParseCommitDate(cache.HeadCommitDate);

                foreach (var b in cache.Bundles)
                {
                    if (!Match(b)) continue;
                    var sha = string.IsNullOrEmpty(b.CommitSha) ? cache.HeadCommit : b.CommitSha!;
                    var date = string.IsNullOrEmpty(b.CommitDate) ? headDate : ParseCommitDate(b.CommitDate);
                    var downloadSha = string.IsNullOrEmpty(cache.HeadCommit) ? sha : cache.HeadCommit;
                    results.Add(new BundleSearchResult(b, repoId, displayName, sha, date, downloadSha, orderIdx));
                    if (limit > 0 && results.Count >= limit) return results;
                }
            }

            return results;
        }

        private static DateTime? ParseCommitDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }
    }
}
