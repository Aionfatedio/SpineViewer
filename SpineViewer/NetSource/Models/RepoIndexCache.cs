using System.Collections.Generic;

namespace SpineViewer.NetSource.Models
{
    public class RepoIndexCache
    {
        // 默认值必须保持 1: 旧缓存 JSON 缺失该字段时反序列化得到 1,
        // 与 RepoIndexService.CurrentSchemaVersion 不一致即触发全量重建。
        public int SchemaVersion { get; set; } = 1;

        public string RepoId { get; set; } = string.Empty;

        public string Host { get; set; } = "github.com";

        public string Owner { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Branch { get; set; } = string.Empty;

        public string HeadCommit { get; set; } = string.Empty;

        public string HeadCommitDate { get; set; } = string.Empty;

        public string IndexedAt { get; set; } = string.Empty;

        public bool CommitMetadataResolved { get; set; }

        public List<SpineBundle> Bundles { get; set; } = [];

        public bool Truncated { get; set; }
    }
}
