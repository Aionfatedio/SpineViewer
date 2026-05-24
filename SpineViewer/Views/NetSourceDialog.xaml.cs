using SpineViewer.Extensions;
using SpineViewer.Resources;
using SpineViewer.Utils;
using SpineViewer.ViewModels.NetSourceDialog;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SpineViewer.Views
{
    public partial class NetSourceDialog : Window
    {
        private ListBoxItem? _repoDragSourceItem;
        private Point _repoDragSourcePoint;

        public NetSourceDialog()
        {
            InitializeComponent();
            SourceInitialized += NetSourceDialog_SourceInitialized;
        }

        private void NetSourceDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }

        private void ButtonDownload_Click(object sender, RoutedEventArgs e)
        {
            var vm = (NetSourceDialogViewModel)DataContext;
            vm.Cmd_DownloadAndImport.Execute(_resultsListView.SelectedItems);
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView list) return;
            if (list.SelectedItems.Count <= 0) return;
            var vm = (NetSourceDialogViewModel)DataContext;
            vm.Cmd_DownloadAndImport.Execute(list.SelectedItems);
        }

        private void NetSourceDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NetSourceDialogViewModel { HasToken: false }) return;

            _resultsGridView.Columns.Remove(_commitColumn);
            _resultsGridView.Columns.Remove(_commitDateColumn);
        }

        private void ResultsListView_HeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header) return;
            if (header.Tag is not string key) return;
            var vm = (NetSourceDialogViewModel)DataContext;
            vm.SortByColumn(key);
        }

        private void NetSourceDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (DataContext is IDisposable d) d.Dispose();
        }

        #region 仓库列表拖拽排序

        private void RepoListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox list) return;

            _repoDragSourceItem = ((DependencyObject)e.OriginalSource)?.GetParent<ListBoxItem>(true);
            _repoDragSourcePoint = e.GetPosition(null);

            if (_repoDragSourceItem is null)
                list.Focus();
        }

        private void RepoListBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_repoDragSourceItem is null) return;
            if (sender is not ListBox list) return;

            var diff = _repoDragSourcePoint - e.GetPosition(null);
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var srcItem = list.ItemContainerGenerator.ItemFromContainer(_repoDragSourceItem) as NetSourceRepoItemViewModel;
            if (srcItem is null) return;

            DragDrop.DoDragDrop(_repoDragSourceItem, srcItem, DragDropEffects.Move);
        }

        private void RepoListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _repoDragSourceItem = null;
            _repoDragSourcePoint = default;
        }

        private void RepoListBox_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(NetSourceRepoItemViewModel))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void RepoListBox_Drop(object sender, DragEventArgs e)
        {
            if (sender is not ListBox list) return;
            if (!e.Data.GetDataPresent(typeof(NetSourceRepoItemViewModel))) return;

            var src = (NetSourceRepoItemViewModel)e.Data.GetData(typeof(NetSourceRepoItemViewModel))!;
            var vm = (NetSourceDialogViewModel)DataContext;

            int srcIdx = vm.Repos.IndexOf(src);
            if (srcIdx < 0) return;

            int dstIdx = vm.Repos.Count - 1;
            var pt = e.GetPosition(list);
            var hit = list.InputHitTest(pt) as DependencyObject;
            var dstContainer = hit?.GetParent<ListBoxItem>(true);
            if (dstContainer is not null)
            {
                var dstItem = list.ItemContainerGenerator.ItemFromContainer(dstContainer) as NetSourceRepoItemViewModel;
                if (dstItem is not null)
                    dstIdx = vm.Repos.IndexOf(dstItem);
            }

            vm.ReorderRepo(srcIdx, dstIdx);
            e.Handled = true;
        }

        #endregion
    }
}
