using System.Collections.Generic;

namespace SpineViewer.NetSource.Models
{
    public class SpineBundle
    {
        public string RepoId { get; set; } = string.Empty;

        public string BundleDir { get; set; } = string.Empty;

        public string ModelName { get; set; } = string.Empty;

        public string SkelPath { get; set; } = string.Empty;

        public string? AtlasPath { get; set; }

        public List<string> TexturePaths { get; set; } = [];

        public string? BundleHash { get; set; }

        public long TotalSize { get; set; }

        public int FileCount { get; set; }

        public string? CommitSha { get; set; }

        public string? CommitDate { get; set; }
    }
}
