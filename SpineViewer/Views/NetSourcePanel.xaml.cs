using SpineViewer.Utils;
using SpineViewer.ViewModels.NetSourceDialog;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SpineViewer.Views
{
    public partial class NetSourcePanel : UserControl
    {
        private ListBoxItem? _repoDragSourceItem;
        private Point _repoDragSourcePoint;

        public NetSourcePanel()
        {
            InitializeComponent();
            DataContextChanged += NetSourcePanel_DataContextChanged;
        }

        private void NetSourcePanel_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigureCommitColumns();
        }

        private void NetSourcePanel_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ConfigureCommitColumns();
        }

        private void ConfigureCommitColumns()
        {
            if (DataContext is not NetSourceDialogViewModel vm) return;

            SetColumnVisible(_commitColumn, vm.HasToken, 5);
            SetColumnVisible(_commitDateColumn, vm.HasToken, 6);
        }

        private void SetColumnVisible(GridViewColumn column, bool visible, int index)
        {
            var columns = _resultsGridView.Columns;
            var exists = columns.Contains(column);

            if (visible && !exists)
                columns.Insert(Math.Min(index, columns.Count), column);
            else if (!visible && exists)
                columns.Remove(column);
        }

        private void ButtonDownload_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NetSourceDialogViewModel vm) return;
            vm.Cmd_DownloadAndImport.Execute(_resultsListView.SelectedItems);
        }

        private void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView list) return;
            if (list.SelectedItems.Count <= 0) return;
            if (DataContext is not NetSourceDialogViewModel vm) return;
            vm.Cmd_DownloadAndImport.Execute(list.SelectedItems);
        }

        private void ResultsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView list) return;

            var container = ((DependencyObject)e.OriginalSource)?.GetParent<ListViewItem>(true);
            if (container is null)
                return;

            if (!container.IsSelected)
            {
                list.SelectedItems.Clear();
                container.IsSelected = true;
            }
        }

        private void ResultsListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not ListView list) return;
            if (DataContext is not NetSourceDialogViewModel vm) return;
            vm.UpdateResultContextMenuState(list.SelectedItems);
        }

        private void ResultsListView_HeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header) return;
            if (header.Tag is not string key) return;
            if (DataContext is not NetSourceDialogViewModel vm) return;
            vm.SortByColumn(key);
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

        private void RepoListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox list) return;

            var container = ((DependencyObject)e.OriginalSource)?.GetParent<ListBoxItem>(true);
            if (container is null)
            {
                list.Tag = null;
                return;
            }

            var item = list.ItemContainerGenerator.ItemFromContainer(container) as NetSourceRepoItemViewModel;
            list.Tag = item;
            if (item is not null)
                list.SelectedItem = item;
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
            if (DataContext is not NetSourceDialogViewModel vm) return;

            var src = (NetSourceRepoItemViewModel)e.Data.GetData(typeof(NetSourceRepoItemViewModel))!;

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
