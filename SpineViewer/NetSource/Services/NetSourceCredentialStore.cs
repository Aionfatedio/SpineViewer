using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpineViewer.NetSource.Services
{
    // PAT 不写入 preference.json，而是通过 DPAPI 绑定当前 Windows 用户后保存到 netcache。
    public class NetSourceCredentialStore
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly string _cacheRoot;

        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("SpineViewer.NetSource.v1");

        public NetSourceCredentialStore(string cacheRoot)
        {
            _cacheRoot = cacheRoot;
        }

        public Dictionary<string, string> LoadAll()
        {
            var path = NetSourcePathProvider.GetCredentialsFilePath(_cacheRoot);
            if (!File.Exists(path)) return [];

            try
            {
                var ciphertext = File.ReadAllBytes(path);
                if (ciphertext.Length == 0) return [];

                var plaintext = ProtectedData.Unprotect(ciphertext, _entropy, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plaintext);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Warn("Failed to load GitHub repo credentials: {0}", ex.Message);
                return [];
            }
        }

        public void SaveAll(Dictionary<string, string> credentials)
        {
            try
            {
                NetSourcePathProvider.EnsureDirectoryExists(Path.GetDirectoryName(NetSourcePathProvider.GetCredentialsFilePath(_cacheRoot))!);
                var json = JsonSerializer.Serialize(credentials);
                var plaintext = Encoding.UTF8.GetBytes(json);
                var ciphertext = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(NetSourcePathProvider.GetCredentialsFilePath(_cacheRoot), ciphertext);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                _logger.Error("Failed to save GitHub repo credentials: {0}", ex.Message);
            }
        }

        public string? GetGitHubToken() => LoadAll().TryGetValue("github", out var v) ? v : null;

        public void SetGitHubToken(string? token)
        {
            var dict = LoadAll();
            if (string.IsNullOrWhiteSpace(token))
                dict.Remove("github");
            else
                dict["github"] = token;
            SaveAll(dict);
        }
    }
}
