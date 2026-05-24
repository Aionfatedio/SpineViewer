using CommunityToolkit.Mvvm.ComponentModel;
using SpineViewer.NetSource.Models;
using SpineViewer.NetSource.Services;
using System;

namespace SpineViewer.ViewModels.NetSourceDialog
{
    public class NetSourceBundleItemViewModel : ObservableObject
    {
        public BundleSearchResult Result { get; }

        public SpineBundle Bundle => Result.Bundle;

        public NetSourceBundleItemViewModel(BundleSearchResult result)
        {
            Result = result;
        }

        public string ModelName => Bundle.ModelName;
        public string BundleDir => Bundle.BundleDir;
        public string RepoDisplayName => Result.RepoDisplayName;
        public int FileCount => Bundle.FileCount;
        public long TotalSize => Bundle.TotalSize;
        public DateTime? CommitDate => Result.CommitDate;
        public int RepoOrderIndex => Result.RepoOrderIndex;
        public string CommitShort => string.IsNullOrEmpty(Result.CommitSha) ? string.Empty : Result.CommitSha[..Math.Min(7, Result.CommitSha.Length)];

        public string TotalSizeDisplay => FormatBytes(TotalSize);

        public string CommitDateDisplay => CommitDate.HasValue
            ? CommitDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : string.Empty;

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double v = bytes;
            string[] units = ["KB", "MB", "GB"];
            int idx = -1;
            do { v /= 1024; idx++; }
            while (v >= 1024 && idx < units.Length - 1);
            return $"{v:F2} {units[idx]}";
        }
    }
}
