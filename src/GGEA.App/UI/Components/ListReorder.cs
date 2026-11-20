using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GGEA.App.Localization;

namespace GGEA.App.UI;

/// <summary>AP 与每日任务列表共用的排序行为和装饰。</summary>
internal sealed class ListReorder<T> where T : class
{
    private readonly ListBox _list;
    private readonly ObservableCollection<T> _items;
    private readonly Func<T, string> _itemName;
    private readonly Action<string> _report;
    private readonly bool _ignoreButtons;
    private readonly Action? _afterDrop;
    private Point _dragStart;
    private T? _draggedItem;
    private DragPreviewAdorner? _dragAdorner;
    private DropIndicatorAdorner? _dropIndicator;

    // 列表与排序组件同生共灭，每个列表单独持有本次手势的预览和插入位置。
    internal ListReorder(ListBox list, ObservableCollection<T> items, Func<T, string> itemName,
        Action<string> report, bool ignoreButtons = false, Action? afterDrop = null)
    {
        _list = list;
        _items = items;
        _itemName = itemName;
        _report = report;
        _ignoreButtons = ignoreButtons;
        _afterDrop = afterDrop;
        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewMouseMove += OnMouseMove;
        list.DragOver += OnDragOver;
        list.DragLeave += OnDragLeave;
        list.Drop += OnDrop;
    }

    internal bool DragStarted { get; private set; }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        DragStarted = false;
        if (_ignoreButtons)
        {
            for (var source = e.OriginalSource as DependencyObject; source is not null && source != _list;
                source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source))
            {
                if (source is System.Windows.Controls.Primitives.ButtonBase)
                {
                    _draggedItem = null;
                    return;
                }
            }
        }
        _dragStart = e.GetPosition(_list);
        var container = ItemsControl.ContainerFromElement(
            _list,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        _draggedItem = container?.DataContext as T;
    }

    /// <summary>鼠标移动超过系统拖拽阈值后启动列表项的移动排序操作。</summary>
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null)
        {
            return;
        }

        var current = e.GetPosition(_list);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _draggedItem;
        _draggedItem = null;
        var draggedContainer = _list.ItemContainerGenerator.ContainerFromItem(dragged) as ListBoxItem;
        var adornerLayer = AdornerLayer.GetAdornerLayer(_list);
        if (draggedContainer is not null && adornerLayer is not null)
        {
            draggedContainer.Opacity = 0.55;
            _dragAdorner = new DragPreviewAdorner(_list, draggedContainer);
            _dragAdorner.UpdatePosition(current);
            adornerLayer.Add(_dragAdorner);
        }

        try
        {
            DragStarted = true;
            DragDrop.DoDragDrop(_list, dragged, DragDropEffects.Move);
        }
        finally
        {
            if (_dragAdorner is not null && adornerLayer is not null)
            {
                adornerLayer.Remove(_dragAdorner);
            }

            if (draggedContainer is not null)
            {
                draggedContainer.Opacity = 1;
            }

            _dragAdorner = null;
            RemoveDropIndicator();
            _list.SelectedItem = null;
        }
    }

    /// <summary>
    /// 拖拽经过列表时，只允许同类型列表项移动，
    /// 并用虚线指示目标行上方或下方的插入位置。
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(T))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        _dragAdorner?.UpdatePosition(e.GetPosition(_list));

        var targetContainer = ItemsControl.ContainerFromElement(
            _list,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = targetContainer?.DataContext as T;
        var dragged = e.Data.GetData(typeof(T)) as T;
        if (target is null || dragged is null)
        {
            RemoveDropIndicator();
            e.Handled = true;
            return;
        }

        var dragIndex = _list.Items.IndexOf(dragged);
        var targetIndex = _list.Items.IndexOf(target);
        if (dragIndex < 0 || targetIndex < 0 || dragIndex == targetIndex)
        {
            RemoveDropIndicator();
        }
        else
        {
            // 目标在拖动行上方时画顶边线，下方时画底边线，与 AntD 示例一致。
            UpdateDropIndicator(targetContainer!, targetIndex < dragIndex);
        }

        e.Handled = true;
    }

    /// <summary>更新虚线插入位置指示线。</summary>
    private void UpdateDropIndicator(ListBoxItem targetContainer, bool showTop)
    {
        var adornerLayer = AdornerLayer.GetAdornerLayer(_list);
        if (adornerLayer is null)
        {
            return;
        }

        if (_dropIndicator is null)
        {
            _dropIndicator = new DropIndicatorAdorner(_list, adornerLayer);
            adornerLayer.Add(_dropIndicator);
        }

        _dropIndicator.UpdatePosition(targetContainer, showTop);
    }

    /// <summary>移除当前插入位置指示线。</summary>
    private void RemoveDropIndicator()
    {
        _dropIndicator?.Remove();
        _dropIndicator = null;
    }

    /// <summary>把拖拽项移动到鼠标所在项的位置，并更新对应可观察集合。</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        RemoveDropIndicator();
        MoveItem(e);
        _afterDrop?.Invoke();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _list.SelectedItem = null;
        RemoveDropIndicator();
    }

    private void MoveItem(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(T)) is not T dragged)
        {
            return;
        }

        var targetContainer = ItemsControl.ContainerFromElement(
            _list,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = targetContainer?.DataContext as T;
        var oldIndex = _items.IndexOf(dragged);
        var newIndex = target is null ? _items.Count - 1 : _items.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
        {
            _items.Move(oldIndex, newIndex);
            _report(L.Format($"已调整顺序：{_itemName(dragged)} 移动到第 {newIndex + 1} 位。"));
        }

        _list.SelectedItem = null;
        e.Handled = true;
    }

    /// <summary>跟随鼠标显示的列表项半透明拖拽预览。</summary>
    private sealed class DragPreviewAdorner : Adorner
    {
        private readonly VisualBrush _previewBrush;
        private readonly double _previewWidth;
        private readonly double _previewHeight;
        private Point _position;

        public DragPreviewAdorner(UIElement adornedElement, FrameworkElement previewSource)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            _previewBrush = new VisualBrush(previewSource) { Opacity = 0.88 };
            _previewWidth = previewSource.ActualWidth;
            _previewHeight = previewSource.ActualHeight;
        }

        /// <summary>更新预览左上角附近的鼠标位置并请求重绘。</summary>
        public void UpdatePosition(Point position)
        {
            _position = position;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var left = Math.Clamp(_position.X + 12, 0, Math.Max(0, AdornedElement.RenderSize.Width - _previewWidth));
            var top = Math.Clamp(_position.Y + 10, 0, Math.Max(0, AdornedElement.RenderSize.Height - _previewHeight));
            drawingContext.DrawRoundedRectangle(
                _previewBrush,
                new Pen((Brush)Application.Current.FindResource("Brush.Accent"), 2),
                new Rect(left, top, _previewWidth, _previewHeight),
                radiusX: 8,
                radiusY: 8);
        }
    }

    /// <summary>虚线插入位置指示线。</summary>
    private sealed class DropIndicatorAdorner : Adorner
    {
        private readonly AdornerLayer _layer;
        private readonly ListBox _listBox;
        private Rect? _line;
        private bool _removed;

        public DropIndicatorAdorner(ListBox listBox, AdornerLayer layer)
            : base(listBox)
        {
            _listBox = listBox;
            _layer = layer;
            IsHitTestVisible = false;
        }

        /// <summary>根据目标行更新指示线；showTop 为 true 时显示在目标行上方。</summary>
        public void UpdatePosition(ListBoxItem target, bool showTop)
        {
            var origin = target.TranslatePoint(new Point(0, 0), _listBox);
            var y = showTop ? origin.Y : origin.Y + target.ActualHeight;
            _line = new Rect(
                origin.X + 3,
                y - 1,
                Math.Max(0, target.ActualWidth - 6),
                2);
            InvalidateVisual();
        }

        /// <summary>从装饰层移除指示线，重复调用安全。</summary>
        public void Remove()
        {
            if (_removed)
            {
                return;
            }

            _removed = true;
            _layer.Remove(this);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_line is not Rect line)
            {
                return;
            }

            var pen = new Pen((Brush)Application.Current.FindResource("Brush.Accent"), 2)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            };
            drawingContext.DrawLine(
                pen,
                new Point(line.Left, line.Top + line.Height / 2),
                new Point(line.Right, line.Top + line.Height / 2));
        }
    }
}
