using System.Globalization;
using System.IO;
using System.Windows;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>开发版截图、输入与模板采集工具。</summary>
public partial class MainWindow
{
    private void ConnectScreenshotView()
    {
        ScreenshotView.ScreenshotButton.Click += ScreenshotButton_Click;
        ScreenshotView.MissionEntryButton.Click += MissionEntryButton_Click;
        ScreenshotView.TapButton.Click += TapButton_Click;
        ScreenshotView.SwipeStartButton.Click += SwipeStartButton_Click;
        ScreenshotView.SwipeEndButton.Click += SwipeEndButton_Click;
        ScreenshotView.SwipeButton.Click += SwipeButton_Click;
        ScreenshotView.PreciseDragButton.Click += SwipeButton_Click;
        ScreenshotView.SaveTemplateButton.Click += SaveTemplateButton_Click;
        ScreenshotView.TestTemplateButton.Click += TestTemplateButton_Click;
        ScreenshotView.ScreenImage.MouseLeftButtonDown += ScreenImage_MouseLeftButtonDown;
        ScreenshotView.ScreenImage.MouseMove += ScreenImage_MouseMove;
        ScreenshotView.ScreenImage.MouseLeftButtonUp += ScreenImage_MouseLeftButtonUp;
    }

    private AutomationDevice.Target? _previewTarget;
    private ScreenImage? _currentScreen;

    /// <summary>截图并显示到界面。</summary>
    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => ScreenshotButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task ScreenshotButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (!TryGetDeviceSerial(out var serial))
        {
            return;
        }

        UpdateBusyState();
        try
        {
            _previewTarget = null;
            var preview = await _adb!.PreviewAsync(serial, _windowTasks.Token);
            _currentScreen = await Task.Run(() => ImageCodec.DecodePng(preview.Png));
            _previewTarget = preview.Target;

            ScreenshotView.ScreenImage.Source = WpfImageCodec.ToBitmap(_currentScreen);
            ScreenshotView.OverlayCanvas.Width = _currentScreen.Width;
            ScreenshotView.OverlayCanvas.Height = _currentScreen.Height;
            ClearSelection();
            _logger!.Info(L.Format($"截图完成: {_currentScreen.Width}x{_currentScreen.Height}"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"截图失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>开发工具与正常自动化共用设备互斥；目标页确认后将控制权交回用户。</summary>
    private async void MissionEntryButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(async () =>
        {
            using var execution = TryBeginExecution();
            if (execution is null) return;
            var serial = await GetOnlineTaskSerialAsync(execution, ConnectionStateText, _windowTasks.Token);
            if (serial is null) return;
            try
            {
                var result = await new MissionEntryProbe(_adb!, _automation!).OpenAsync(
                    serial, _config, _mainLayout, _missionLayout, _windowTasks.Token);
                ConnectionStateText.Text = result;
                _logger?.Info(result);
            }
            catch (OperationCanceledException) when (_windowTasks.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ConnectionStateText.Text = L.Format($"使命入口检查失败：{ex.Message}");
                _logger?.Warn(ConnectionStateText.Text);
            }
        });
    }

    private bool BindPreviewTarget(string serial)
    {
        if (_previewTarget is not { } target || target.Serial != serial)
        {
            _logger?.Warn(L.Text("请重新截取当前设备画面"));
            return false;
        }
        _adb!.BindPreview(target);
        return true;
    }

    /// <summary>点击截图中选中的坐标点。</summary>
    private async void TapButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => TapButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task TapButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_adb is null) return;
        var serial = await GetOnlineTaskSerialAsync(execution, ConnectionStateText, _windowTasks.Token);
        if (serial is null) return;

        if (_selectedPoint is not Point point)
        {
            _logger?.Warn(L.Text("请先在截图上单击选择坐标点"));
            return;
        }

        UpdateBusyState();
        try
        {
            if (!BindPreviewTarget(serial)) return;
            await _adb.TapAsync(serial, (int)Math.Round(point.X), (int)Math.Round(point.Y), _windowTasks.Token);
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"点击失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>把当前选中点设为滑动起点。</summary>
    private void SwipeStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPoint is not Point point)
        {
            _logger?.Warn(L.Text("请先在截图上单击选择坐标点"));
            return;
        }

        _swipeStart = point;
        ScreenshotView.SwipeStartButton.Content = L.Format($"起点 {point.X:0},{point.Y:0}");
        DrawOverlay();
        _logger?.Info(L.Format($"滑动起点已设置: ({point.X:0}, {point.Y:0})"));
    }

    /// <summary>把当前选中点设为滑动终点。</summary>
    private void SwipeEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPoint is not Point point)
        {
            _logger?.Warn(L.Text("请先在截图上单击选择坐标点"));
            return;
        }

        _swipeEnd = point;
        ScreenshotView.SwipeEndButton.Content = L.Format($"终点 {point.X:0},{point.Y:0}");
        DrawOverlay();
        _logger?.Info(L.Format($"滑动终点已设置: ({point.X:0}, {point.Y:0})"));
    }

    /// <summary>执行起点到终点的滑动。</summary>
    private async void SwipeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => SwipeButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task SwipeButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_adb is null) return;
        var serial = await GetOnlineTaskSerialAsync(execution, ConnectionStateText, _windowTasks.Token);
        if (serial is null) return;

        if (_swipeStart is not Point start || _swipeEnd is not Point end)
        {
            _logger?.Warn(L.Text("请先设置滑动起点与终点"));
            return;
        }

        if (!int.TryParse(ScreenshotView.SwipeDurationBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration))
        {
            duration = 500;
        }

        UpdateBusyState();
        try
        {
            if (!BindPreviewTarget(serial)) return;
            Func<string, int, int, int, int, int, CancellationToken, Task> gesture =
                ReferenceEquals(sender, ScreenshotView.PreciseDragButton) ? _adb.DragAsync : _adb.SwipeAsync;
            await gesture(
                serial,
                (int)Math.Round(start.X), (int)Math.Round(start.Y),
                (int)Math.Round(end.X), (int)Math.Round(end.Y),
                Math.Clamp(duration, 0, 60000), _windowTasks.Token);
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"滑动失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>把当前 ROI 保存为 PNG 模板。</summary>
    private void SaveTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScreen is null)
        {
            _logger?.Warn(L.Text("请先截图再保存模板"));
            return;
        }

        if (_selectedRoi is not Roi roi)
        {
            _logger?.Warn(L.Text("请先在截图上拖选 ROI"));
            return;
        }

        var name = ScreenshotView.TemplateNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _logger?.Warn(L.Text("请先输入模板名称"));
            return;
        }

        try
        {
            // 处理文件名中的非法字符
            var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var directory = ConfigService.Resources.ResolveWritePath(_config.TemplateDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, safeName + ".png");

            var cropped = WpfImageCodec.Crop(WpfImageCodec.ToBitmap(_currentScreen), roi);
            WpfImageCodec.SavePng(cropped, path);
            _logger!.Info(L.Format($"模板已保存: {path}"));

            RefreshTemplateList();
            ScreenshotView.TemplateNameBox.Clear();
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"保存模板失败: {ex.Message}"));
        }
    }

    /// <summary>用当前截图测试所选模板的匹配结果。</summary>
    private async void TestTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => TestTemplateButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task TestTemplateButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_currentScreen is null)
        {
            _logger?.Warn(L.Text("请先截图再测试模板"));
            return;
        }

        if (ScreenshotView.TemplateCombo.SelectedItem is not TemplateFileItem item)
        {
            _logger?.Warn(L.Text("请先选择模板文件"));
            return;
        }

        if (!double.TryParse(ScreenshotView.ThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            threshold = _config.DefaultMatchThreshold;
        }

        UpdateBusyState();
        try
        {
            var screen = _currentScreen;
            var template = await Task.Run(() => ImageCodec.DecodePng(File.ReadAllBytes(item.Path)));
            var result = await Task.Run(() => _matcher.Match(
                screen.Bgra, screen.Width, screen.Height,
                template.Bgra, template.Width, template.Height,
                threshold));

            _lastMatch = result;
            if (result is null)
            {
                ScreenshotView.SelectionStatusText.Text = L.Format($"匹配失败: 未找到置信度 ≥ {threshold:P1} 的目标");
                _logger?.Warn(ScreenshotView.SelectionStatusText.Text);
            }
            else
            {
                ScreenshotView.SelectionStatusText.Text = L.Format($"匹配成功: {result}");
                _logger?.Info(L.Format($"模板测试成功: {item.Name} -> {result}"));
            }

            DrawOverlay();
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"模板测试失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>刷新模板文件下拉列表。</summary>
    private void RefreshTemplateList()
    {
        var directory = ConfigService.ResolvePath(_config.TemplateDirectory);
        if (!ConfigService.Resources.DirectoryExists(directory))
        {
            ScreenshotView.TemplateCombo.ItemsSource = Array.Empty<TemplateFileItem>();
            return;
        }

        var items = ConfigService.Resources.GetFiles(directory, "*.png", SearchOption.AllDirectories)
            .Select(f => new TemplateFileItem(f, Path.GetFileName(f)))
            .OrderBy(i => i.Name)
            .ToArray();
        ScreenshotView.TemplateCombo.ItemsSource = items;
        ScreenshotView.TemplateCombo.SelectedItem = items.LastOrDefault();
    }

    /// <summary>模板下拉框条目。</summary>
    private sealed record TemplateFileItem(string Path, string Name)
    {
        public override string ToString() => Name;
    }
}
