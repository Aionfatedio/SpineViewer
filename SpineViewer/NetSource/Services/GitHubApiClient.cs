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
using System.Windows;

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

    public record GitHubProxyOptions(bool Enabled, string Host, int Port);

    // GitHubApiClient 是网络源的最低层封装: URL 解析、REST/GraphQL/raw 请求、
    // 认证、代理和轻量重试都收敛在这里，避免 ViewModel 直接感知 HTTP 细节。
    public sealed class GitHubApiClient : IDisposable
    {
        private const string ApiBase = "https://api.github.com";
        private const string RawBase = "https://raw.githubusercontent.com";
        private const string GraphQLEndpoint = "https://api.github.com/graphql";
        private const string AcceptHeader = "application/vnd.github+json";
        private const string ApiVersion = "2022-11-28";
        private const int MaxTransientRetries = 2;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(400);

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _http;
        private string? _token;

        public GitHubApiClient(string? token = null, string userAgent = "SpineViewer", GitHubProxyOptions? proxy = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            if (proxy is { Enabled: true } && !string.IsNullOrWhiteSpace(proxy.Host) && proxy.Port is > 0 and <= 65535)
            {
                handler.Proxy = new WebProxy(proxy.Host, proxy.Port);
                handler.UseProxy = true;
            }

            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptHeader));
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
            _http.Timeout = TimeSpan.FromMinutes(2);

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

        // tree/blob 后的分支只取第一段: URL 无法区分"含 / 的分支"和"分支下的子目录"
        // (tree/main/some/dir 远比 tree/dev/wpf 常见)。需要含 / 的分支时用 owner/repo@branch 简写。
        private static readonly Regex _httpsUrlPattern = new(
            @"^https?://(?<host>github\.com)/(?<owner>[^/\s]+)/(?<name>[^/\s\?#]+?)(?:\.git)?(?:/(?:tree|blob)/(?<branch>[^/\s\?#]+))?(?:[/?#].*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _sshUrlPattern = new(
            @"^git@(?<host>github\.com):(?<owner>[^/\s]+)/(?<name>[^/\s]+?)(?:\.git)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _shortPattern = new(
            @"^(?<owner>[A-Za-z0-9][A-Za-z0-9._-]*)/(?<name>[A-Za-z0-9._-]+)(?:@(?<branch>\S+))?$",
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
                    m.Groups["branch"].Success ? m.Groups["branch"].Value : null);
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

        public async Task<GitHubCompareResponse> GetCompareAsync(string owner, string name, string baseSha, string headSha, CancellationToken ct = default)
        {
            var url = $"{ApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/compare/{Uri.EscapeDataString(baseSha)}...{Uri.EscapeDataString(headSha)}";
            return await GetJsonAsync<GitHubCompareResponse>(url, ct);
        }

        public async Task DownloadRawAsync(
            string owner,
            string name,
            string commitSha,
            string repoRelativePath,
            string localDestPath,
            CancellationToken ct = default)
        {
            var encodedPath = string.Join('/', repoRelativePath
                .Split('/', StringSplitOptions.None)
                .Select(Uri.EscapeDataString));

            var url = $"{RawBase}/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(commitSha)}/{encodedPath}";

            try
            {
                using var resp = await SendWithRetryAsync(
                    token => _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token),
                    ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await SafeReadStringAsync(resp.Content, ct);
                    throw new GitHubApiException(resp.StatusCode, FormatError(resp.StatusCode, body, url));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localDestPath)!);

                await using var dst = new FileStream(localDestPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await src.CopyToAsync(dst, 81920, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
            try
            {
                using var resp = await SendWithRetryAsync(async token =>
                {
                    using var content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json");
                    return await _http.PostAsync(GraphQLEndpoint, content, token);
                }, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new GitHubApiException(resp.StatusCode, FormatError(resp.StatusCode, body, GraphQLEndpoint));
                return body;
            }
            catch (GitHubApiException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                using var resp = await SendWithRetryAsync(
                    token => _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token),
                    ct);
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
            // 只放行用户主动取消; HttpClient 超时同样抛 TaskCanceledException,
            // 必须包装成 GitHubApiException 才能让 UI 显示失败原因而不是静默回到 Pending。
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                throw new GitHubApiException("GitHub request failed: " + ex.Message, ex);
            }
        }

        private static async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> send,
            CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var resp = await send(ct);
                    if (!IsTransientStatus(resp.StatusCode) || attempt >= MaxTransientRetries)
                        return resp;

                    resp.Dispose();
                }
                catch (HttpRequestException) when (attempt < MaxTransientRetries)
                {
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxTransientRetries)
                {
                }

                // 只重试网络抖动和 5xx/408，认证、限流和 404 立即返回给上层展示。
                await Task.Delay(RetryBaseDelay * (attempt + 1), ct);
            }
        }

        private static bool IsTransientStatus(HttpStatusCode code)
            => code is HttpStatusCode.RequestTimeout
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

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
                HttpStatusCode.Unauthorized => string.Format(Str("Str_NetSourceGitHubUnauthorized"), snippet),
                HttpStatusCode.Forbidden => string.Format(Str("Str_NetSourceGitHubForbidden"), snippet),
                HttpStatusCode.NotFound => string.Format(Str("Str_NetSourceGitHubNotFound"), url),
                HttpStatusCode.UnprocessableEntity => string.Format(Str("Str_NetSourceGitHubUnprocessable"), snippet),
                _ => string.Format(Str("Str_NetSourceGitHubGenericError"), (int)code, code, snippet)
            };
        }

        private static string Str(string key)
            => Application.Current?.TryFindResource(key) as string ?? key;

        private static void TrySafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        #endregion

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
