using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GGEA.App.Tasks;

/// <summary>任务列表行共用的变更通知。返回值供调用方决定是否刷新关联的显示属性。</summary>
internal static class TaskItemProperty
{
    public static bool Set<T>(object sender, PropertyChangedEventHandler? changed, ref T field, T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        changed?.Invoke(sender, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
