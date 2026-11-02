using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GGEA.App.UI;

/// <summary>统一鼠标悬停的延迟状态；离开、禁用和卸载时取消，避免列表复用后出现迟到提示。</summary>
public static class HoverFeedback
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private static readonly DependencyPropertyKey IsActivePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsActive", typeof(bool), typeof(HoverFeedback), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsActiveProperty = IsActivePropertyKey.DependencyProperty;
    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    private static readonly ConditionalWeakTable<FrameworkElement, PendingHover> Pending = new();

    static HoverFeedback()
    {
        // 原生 ToolTip 也遵循相同延迟；禁用快速串联，防止相邻提示立即弹出。
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(300));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(0));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), Mouse.MouseEnterEvent, new MouseEventHandler(OnEnter), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), Mouse.MouseLeaveEvent, new MouseEventHandler(OnLeave), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnUnloaded), true);
    }

    /// <summary>在应用资源及控件创建前初始化属性元数据。</summary>
    public static void Initialize() { }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsEnabled) return;
        // 仅视觉交互控件与文字标记需要悬停状态，布局容器不创建计时器。
        if (element is not Control && element is not TextBlock && element is not System.Windows.Shapes.Shape) return;
        Cancel(element);
        Pending.Add(element, new PendingHover(element));
    }

    private static void OnLeave(object sender, MouseEventArgs e) => Cancel((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Cancel((FrameworkElement)sender);

    private static void Cancel(FrameworkElement element)
    {
        if (Pending.TryGetValue(element, out var pending)) pending.Stop();
        Pending.Remove(element);
        element.SetValue(IsActivePropertyKey, false);
    }

    /// <summary>计时器仅在等待期间运行；状态激活后仍监听禁用，卸载或离开统一释放。</summary>
    private sealed class PendingHover
    {
        private readonly FrameworkElement _element;
        private readonly DispatcherTimer _timer;
        public PendingHover(FrameworkElement element)
        {
            _element = element;
            _timer = new DispatcherTimer(DispatcherPriority.Input, element.Dispatcher) { Interval = Delay };
            _timer.Tick += OnTick;
            element.IsEnabledChanged += OnEnabledChanged;
            _timer.Start();
        }
        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();
            if (_element.IsEnabled) _element.SetValue(IsActivePropertyKey, true);
        }
        private void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue) Cancel(_element);
        }
        public void Stop()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _element.IsEnabledChanged -= OnEnabledChanged;
        }
    }
}
