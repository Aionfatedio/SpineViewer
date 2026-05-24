using NLog;
using SpineViewer.NetSource.Models;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpineViewer.NetSource.Services
{
    public class GitHubApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public GitHubApiException(HttpStatusCode statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        public GitHubApiException(string message, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = HttpStatusCode.ServiceUnavailable;
        }
    }

    public record GitHubRepoRef(string Host, string Owner, string Name, string? Branch);

    public sealed class GitHubApiClient : IDisposable
    {
        private const string ApiBase = "https://api.github.com";
        private const string RawBase = "https://raw.githubusercontent.com";
        private const string GraphQLEndpoint = "https://api.github.com/graphql";
        private const string AcceptHeader = "application/vnd.github+json";
        private const string ApiVersion = "2022-11-28";

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private string? _token;

        public GitHubApiClient(HttpClient? httpClient = null, string? token = null, string userAgent = "SpineViewer")
        {
            if (httpClient is null)
            {
                _http = new HttpClient(new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                });
                _ownsHttp = true;
            }
            else
            {
                _http = httpClient;
                _ownsHttp = false;
            }

            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptHeader));
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
            _http.Timeout = TimeSpan.FromMinutes(2);

            _token = token;
            ApplyAuthHeader();
        }

        public void UpdateToken(string? token)
        {
            _token = token;
            ApplyAuthHeader();
        }

        public bool HasToken => !string.IsNullOrWhiteSpace(_token);

        private void ApplyAuthHeader()
        {
            _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(_token)
                ? null
                : new AuthenticationHeaderValue("Bearer", _token);
        }

        #region URL 解析

        private static readonly Regex _httpsUrlPattern = new(
            @"^https?://(?<host>github\.com)/(?<owner>[^/\s]+)/(?<name>[^/\s\?#]+?)(?:\.git)?(?:/(?:tree|blob)/(?<branch>[^/\s\?#]+))?(?:[/?#].*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _sshUrlPattern = new(
            @"^git@(?<host>github\.com):(?<owner>[^/\s]+)/(?<name>[^/\s]+?)(?:\.git)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _shortPattern = new(
            @"^(?<owner>[A-Za-z0-9][A-Za-z0-9._-]*)/(?<name>[A-Za-z0-9._-]+)$",
            RegexOptions.Compiled);

        public static GitHubRepoRef? TryParseRepoUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            var m = _httpsUrlPattern.Match(s);
            if (m.Success)
            {
                return new GitHubRepoRef(
                    m.Groups["host"].Value.ToLowerInvariant(),
                    m.Groups["owner"].Value,
                    m.Groups["name"].Value,
                    m.Groups["branch"].Success ? m.Groups["branch"].Value : null);
            }

            m = _sshUrlPattern.Match(s);
            if (m.Success)
            {
                return new GitHubRepoRef(
                    m.Groups["host"].Value.ToLowerInvariant(),
                    m.Groups["owner"].Value,
                    m.Groups["name"].Value,
                    null);
            }

            m = _shortPattern.Match(s);
            if (m.Success)
            {
                return new GitHubRepoRef(
                    "github.com",
                    m.Groups["owner"].Value,
                    m.Groups["name"].Value,
                    null);
            }

            return null;
        }

        #endregion

        #region API 调用

        public async Task<GitHubRepoInfo> GetRepoAsync(string owner, string name, CancellationToken ct = default)
        {
            var url = $"{ApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
            return await GetJsonAsync<GitHubRepoInfo>(url, ct);
        }

        public async Task<(string Sha, DateTime? CommitDate)> GetBranchHeadAsync(string owner, string name, string branch, CancellationToken ct = default)
        {
            var url = $"{ApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/branches/{Uri.EscapeDataString(branch)}";
            var info = await GetJsonAsync<GitHubBranchInfo>(url, ct);
            if (info.Commit?.Sha is null)
                throw new GitHubApiException(HttpStatusCode.NotFound, $"Branch {branch} has no commit SHA");

            DateTime? date = null;
            var raw = info.Commit.Commit?.Committer?.Date ?? info.Commit.Commit?.Author?.Date;
            if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                date = parsed;

            return (info.Commit.Sha, date);
        }

        public async Task<GitHubTreesResponse> GetTreeRecursiveAsync(string owner, string name, string sha, CancellationToken ct = default)
        {
            var url = $"{ApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/git/trees/{Uri.EscapeDataString(sha)}?recursive=1";
            return await GetJsonAsync<GitHubTreesResponse>(url, ct);
        }

        public async Task DownloadRawAsync(
            string owner,
            string name,
            string commitSha,
            string repoRelativePath,
            string localDestPath,
            IProgress<long>? progress,
            CancellationToken ct = default)
        {
            var encodedPath = string.Join('/', repoRelativePath
                .Split('/', StringSplitOptions.None)
                .Select(Uri.EscapeDataString));

            var url = $"{RawBase}/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(commitSha)}/{encodedPath}";

            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await SafeReadStringAsync(resp.Content, ct);
                    throw new GitHubApiException(resp.StatusCode, FormatError(resp.StatusCode, body, url));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localDestPath)!);

                await using var dst = new FileStream(localDestPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    progress?.Report(total);
                }
            }
            catch (OperationCanceledException)
            {
                TrySafeDelete(localDestPath);
                throw;
            }
            catch (GitHubApiException)
            {
                TrySafeDelete(localDestPath);
                throw;
            }
            catch (Exception ex)
            {
                TrySafeDelete(localDestPath);
                throw new GitHubApiException("Download failed: " + ex.Message, ex);
            }
        }

        public async Task<string> PostGraphQLAsync(string query, CancellationToken ct = default)
        {
            var payloadJson = JsonSerializer.Serialize(new { query });
            using var content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json");
            try
            {
                using var resp = await _http.PostAsync(GraphQLEndpoint, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new GitHubApiException(resp.StatusCode, FormatError(resp.StatusCode, body, GraphQLEndpoint));
                return body;
            }
            catch (GitHubApiException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                throw new GitHubApiException("GraphQL request failed: " + ex.Message, ex);
            }
        }

        #endregion

        #region 内部工具

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    throw new GitHubApiException(resp.StatusCode, FormatError(resp.StatusCode, body, url));

                var result = JsonSerializer.Deserialize<T>(body, _jsonOptions);
                if (result is null)
                    throw new GitHubApiException(resp.StatusCode, "GitHub response is empty: " + url);
                return result;
            }
            catch (GitHubApiException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                throw new GitHubApiException("GitHub request failed: " + ex.Message, ex);
            }
        }

        private static async Task<string> SafeReadStringAsync(HttpContent content, CancellationToken ct)
        {
            try { return await content.ReadAsStringAsync(ct); }
            catch { return string.Empty; }
        }

        private static string FormatError(HttpStatusCode code, string body, string url)
        {
            var snippet = string.IsNullOrEmpty(body) ? string.Empty : (body.Length > 200 ? body[..200] + "..." : body);
            return code switch
            {
                HttpStatusCode.Unauthorized => $"GitHub 认证失败 (401), 请检查 PAT 是否正确或已过期. {snippet}",
                HttpStatusCode.Forbidden => $"GitHub 拒绝访问 (403), 可能触发了 API 速率限制 (未认证 60/小时, 认证 5000/小时). {snippet}",
                HttpStatusCode.NotFound => $"GitHub 资源不存在 (404), 请检查仓库/分支地址. URL: {url}",
                HttpStatusCode.UnprocessableEntity => $"GitHub 拒绝请求 (422), 请检查参数. {snippet}",
                _ => $"GitHub 调用失败 ({(int)code} {code}): {snippet}"
            };
        }

        private static void TrySafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        #endregion

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }
    }
}
