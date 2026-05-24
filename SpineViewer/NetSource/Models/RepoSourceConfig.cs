using System;
using System.Text.Json.Serialization;

namespace SpineViewer.NetSource.Models
{
    public class RepoSourceConfig
    {
        public string RawUrl { get; set; } = string.Empty;

        public string Host { get; set; } = "github.com";

        public string Owner { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Branch { get; set; }

        public string AddedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonIgnore]
        public string RepoId => ComputeRepoId(Host, Owner, Name, Branch);

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Branch)
            ? $"{Owner}/{Name}"
            : $"{Owner}/{Name}@{Branch}";

        public static string ComputeRepoId(string host, string owner, string name, string? branch)
        {
            var raw = $"{host}/{owner}/{name}@{branch ?? string.Empty}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(raw.ToLowerInvariant());
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }
}
