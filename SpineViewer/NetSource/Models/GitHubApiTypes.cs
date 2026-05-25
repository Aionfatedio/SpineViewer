using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpineViewer.NetSource.Models
{
    public class GitHubRepoInfo
    {
        [JsonPropertyName("default_branch")]
        public string? DefaultBranch { get; set; }

        [JsonPropertyName("pushed_at")]
        public string? PushedAt { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("private")]
        public bool Private { get; set; }

        [JsonPropertyName("archived")]
        public bool Archived { get; set; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }
    }

    public class GitHubBranchInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("commit")]
        public GitHubBranchCommit? Commit { get; set; }
    }

    public class GitHubBranchCommit
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("commit")]
        public GitHubBranchCommitDetail? Commit { get; set; }
    }

    public class GitHubBranchCommitDetail
    {
        [JsonPropertyName("author")]
        public GitHubCommitSignature? Author { get; set; }

        [JsonPropertyName("committer")]
        public GitHubCommitSignature? Committer { get; set; }
    }

    public class GitHubCommitSignature
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }
    }

    public class GitHubTreesResponse
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("tree")]
        public List<GitHubTreeEntry> Tree { get; set; } = [];

        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }
    }

    public class GitHubTreeEntry
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }
    }

    public class GitHubCompareResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("ahead_by")]
        public int AheadBy { get; set; }

        [JsonPropertyName("behind_by")]
        public int BehindBy { get; set; }

        [JsonPropertyName("total_commits")]
        public int TotalCommits { get; set; }

        [JsonPropertyName("files")]
        public List<GitHubCompareFile> Files { get; set; } = [];
    }

    public class GitHubCompareFile
    {
        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("previous_filename")]
        public string? PreviousFilename { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }
    }
}
