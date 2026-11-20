using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>截图预览中的框选、坐标标记和手势显示。</summary>
public partial class MainWindow
{
    // 覆盖层配色
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x7B));
    private static readonly Brush PreviewBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private Roi? _selectedRoi;
    private TemplateMatchResult? _lastMatch;
    private Point? _selectedPoint;
    private Point? _swipeStart;
    private Point? _swipeEnd;
    private bool _dragging;
    private Point _dragStart;
    private Point _dragCurrent;

    /// <summary>按下鼠标：记录拖选起点。</summary>
    private void ScreenImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentScreen is null)
        {
            return;
        }

        _dragging = true;
        _dragStart = _dragCurrent = e.GetPosition(ScreenshotView.ScreenImage);
        ScreenshotView.ScreenImage.CaptureMouse();
    }

    /// <summary>移动鼠标：拖选时实时刷新预览框。</summary>
    private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragCurrent = e.GetPosition(ScreenshotView.ScreenImage);
        DrawOverlay();
    }

    /// <summary>松开鼠标：小距离视为选点，否则视为框选 ROI。</summary>
    private void ScreenImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ScreenshotView.ScreenImage.ReleaseMouseCapture();

        var point = e.GetPosition(ScreenshotView.ScreenImage);
        if (Math.Abs(point.X - _dragStart.X) < 6 && Math.Abs(point.Y - _dragStart.Y) < 6)
        {
            // 单击：选择坐标点
            _selectedPoint = point;
            _selectedRoi = null;
            _lastMatch = null;
            ScreenshotView.SelectionStatusText.Text = L.Format($"已选择点: ({point.X:0}, {point.Y:0})");
        }
        else
        {
            // 拖拽：框选 ROI
            var roi = Roi.FromPoints(_dragStart.X, _dragStart.Y, point.X, point.Y);
            _selectedRoi = roi;
            _selectedPoint = null;
            _lastMatch = null;
            ScreenshotView.SelectionStatusText.Text = L.Format($"ROI: {roi}  中心: ({roi.CenterX}, {roi.CenterY})");
        }

        DrawOverlay();
    }

    /// <summary>清空截图上的所有选择标记。</summary>
    private void ClearSelection()
    {
        _selectedRoi = null;
        _selectedPoint = null;
        _swipeStart = null;
        _swipeEnd = null;
        _lastMatch = null;
        ScreenshotView.SwipeStartButton.Content = L.Text("设起点");
        ScreenshotView.SwipeEndButton.Content = L.Text("设终点");
        DrawOverlay();
    }

    /// <summary>重绘截图覆盖层（ROI、匹配结果、起点/终点）。</summary>
    private void DrawOverlay()
    {
        ScreenshotView.OverlayCanvas.Children.Clear();

        // 拖拽过程中的实时预览
        if (_dragging)
        {
            var preview = Roi.FromPoints(_dragStart.X, _dragStart.Y, _dragCurrent.X, _dragCurrent.Y);
            AddRect(preview, PreviewBrush, 1, new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), dash: true);
        }

        // 已确认的 ROI
        if (_selectedRoi is Roi roi)
        {
            AddRect(roi, AccentBrush, 2, new SolidColorBrush(Color.FromArgb(40, 76, 141, 255)), dash: true);
        }

        // 匹配成功的高亮框
        if (_lastMatch is TemplateMatchResult match)
        {
            AddRect(match.Region, SuccessBrush, 2, new SolidColorBrush(Color.FromArgb(40, 52, 199, 123)), dash: true);
        }

        // 滑动起点与终点
        if (_swipeStart is Point start)
        {
            AddPoint(start, Brushes.DeepSkyBlue);
        }

        if (_swipeEnd is Point end)
        {
            AddPoint(end, Brushes.Orange);
        }

        // 当前选中点
        if (_selectedPoint is Point point)
        {
            AddPoint(point, AccentBrush);
        }
    }

    /// <summary>在覆盖层上添加矩形。</summary>
    private void AddRect(Roi roi, Brush stroke, double thickness, Brush fill, bool dash)
    {
        var rect = new Rectangle
        {
            Width = roi.Width,
            Height = roi.Height,
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = fill,
        };
        if (dash)
        {
            rect.StrokeDashArray = new DoubleCollection { 5, 3 };
        }

        Canvas.SetLeft(rect, roi.X);
        Canvas.SetTop(rect, roi.Y);
        ScreenshotView.OverlayCanvas.Children.Add(rect);
    }

    /// <summary>在覆盖层上添加小圆点标记。</summary>
    private void AddPoint(Point point, Brush brush)
    {
        const double size = 10;
        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            Stroke = Brushes.White,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(ellipse, point.X - size / 2);
        Canvas.SetTop(ellipse, point.Y - size / 2);
        ScreenshotView.OverlayCanvas.Children.Add(ellipse);
    }
}
