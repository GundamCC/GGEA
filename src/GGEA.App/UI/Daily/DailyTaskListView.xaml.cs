using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>列表内区分勾选、排序、打开设置和移除任务，把选中的任务交给窗口。</summary>
public partial class DailyTaskListView : Grid
{
    private ListReorder<DailyTaskItem>? _reorder;
    internal event Action<DailyTaskItem>? SettingsRequested;
    internal event Action<DailyTaskItem>? RemoveRequested;

    public DailyTaskListView() => InitializeComponent();

    internal void Bind(ObservableCollection<DailyTaskItem> tasks, Action<string> report, Action afterDrop)
    {
        DailyTaskList.ItemsSource = tasks;
        _reorder = new ListReorder<DailyTaskItem>(DailyTaskList, tasks, item => item.Name, report,
            ignoreButtons: true, afterDrop: afterDrop);
    }

    private void OnSettingsRequested(object sender, RoutedEventArgs e)
    {
        if (_reorder?.DragStarted == true) return;
        if ((sender as FrameworkElement)?.DataContext is not DailyTaskItem { CanOpenSettings: true } task) return;
        SettingsRequested?.Invoke(task);
        e.Handled = true;
    }

    private void OnTaskRowClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsCheckBoxAncestor(e.OriginalSource as DependencyObject)) OnSettingsRequested(sender, e);
    }

    private void OnRemoveRequested(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: DailyTaskItem item }) RemoveRequested?.Invoke(item);
    }

    /// <summary>判断鼠标原始命中点是否位于勾选框内部，避免点击勾选框时触发跳转。</summary>
    private static bool IsCheckBoxAncestor(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
