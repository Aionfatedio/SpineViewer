using CommunityToolkit.Mvvm.ComponentModel;
using SpineViewer.NetSource.Models;
using System;
using System.Globalization;

namespace SpineViewer.ViewModels.NetSourceDialog
{
    public class NetSourceRepoItemViewModel : ObservableObject
    {
        public RepoSourceConfig Config { get; }

        public NetSourceRepoItemViewModel(RepoSourceConfig config)
        {
            Config = config;
        }

        public string DisplayName => Config.DisplayName;

        public string WebUrl => string.IsNullOrWhiteSpace(Config.Branch)
            ? $"https://{Config.Host}/{Config.Owner}/{Config.Name}"
            : $"https://{Config.Host}/{Config.Owner}/{Config.Name}/tree/{Uri.EscapeDataString(Config.Branch!)}";

        public RepoIndexStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsBusy));
                }
            }
        }
        private RepoIndexStatus _status = RepoIndexStatus.Pending;

        public string StatusText => _status switch
        {
            RepoIndexStatus.Pending => "等待刷新",
            RepoIndexStatus.Indexing => "正在索引",
            RepoIndexStatus.Ready => "已就绪",
            RepoIndexStatus.Stale => "可用 (有更新)",
            RepoIndexStatus.Failed => "失败",
            _ => string.Empty
        };

        public bool IsBusy => _status == RepoIndexStatus.Indexing;

        public string? HeadCommit
        {
            get => _headCommit;
            set => SetProperty(ref _headCommit, value);
        }
        private string? _headCommit;

        public string? HeadCommitDateDisplay
        {
            get => _headCommitDateDisplay;
            set => SetProperty(ref _headCommitDateDisplay, value);
        }
        private string? _headCommitDateDisplay;

        public int BundleCount
        {
            get => _bundleCount;
            set => SetProperty(ref _bundleCount, value);
        }
        private int _bundleCount;

        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }
        private string? _errorMessage;

        public bool Truncated
        {
            get => _truncated;
            set => SetProperty(ref _truncated, value);
        }
        private bool _truncated;

        public static string? FormatCommitDate(string? isoUtc)
        {
            if (string.IsNullOrWhiteSpace(isoUtc)) return null;
            if (DateTime.TryParse(isoUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                return d.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return null;
        }
    }
}
