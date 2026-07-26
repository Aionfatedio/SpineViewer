using SpineViewer.NetSource.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpineViewer.NetSource.Services
{
    public partial class RepoIndexService
    {
        private async Task<bool> ResolveBundleCommitsAsync(
            List<SpineBundle> bundles,
            string owner,
            string name,
            string headSha,
            CancellationToken ct,
            IProgress<RepoIndexProgress>? progress)
        {
            // 单个 bundle 的最后提交需要 GraphQL history 查询, 无 PAT 时速率太低, 只保留仓库 HEAD。
            // 查询失败的批次保持 CommitSha 为空: 展示层回退到仓库 HEAD,
            // 下次刷新只补查空缺项, 不再全量重解析。
            var pending = bundles
                .Where(b => string.IsNullOrEmpty(b.CommitSha) && !string.IsNullOrEmpty(b.SkelPath))
                .ToList();
            if (pending.Count == 0) return _api.HasToken;

            progress?.Report(new RepoIndexProgress(0, pending.Count));

            if (!_api.HasToken)
            {
                progress?.Report(new RepoIndexProgress(pending.Count, pending.Count));
                return false;
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
    }
}
