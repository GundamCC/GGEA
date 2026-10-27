using GGEA.App.Localization;
using System.Windows;
using GGEA.App.Tasks;
using GGEA.App.Config;

namespace GGEA.App.UI;

/// <summary>强敌来袭入口共用窗口任务生命周期与设备互斥，关闭窗口或中止均可取消。</summary>
public partial class MainWindow
{
    /// <summary>启动配置加载后恢复动态任务；失败保留原配置并告知用户，不替换为默认单位。</summary>
    private void RestoreFierceEnemyDailyTask()
    {
        if (_config.FierceEnemyDaily is not { } saved) return;
        try
        {
            var item = saved.Restore(FierceEnemyUnit.Load());
            item.PropertyChanged += DailyTask_StateChanged;
            _dailyTasks.Insert(Math.Clamp(saved.Index, 0, _dailyTasks.Count), item);
            UpdateDailySelectAllState();
        }
        catch (Exception ex)
        {
            FierceEnemyAssaultStatusText.Text = L.Text("恢复强敌每日任务失败，请检查单位目录或重新添加任务。");
            _logger?.Warn(L.Format($"恢复强敌每日任务失败: {ex.GetType().Name}"));
        }
    }

    /// <summary>增删、勾选和拖拽后立即保存；写入失败明确提示当前变更尚未落盘。</summary>
    private void SaveFierceEnemyDailyTask()
    {
        var item = _dailyTasks.FirstOrDefault(task => task.Kind == DailyTaskKind.FierceEnemySkip);
        _config.FierceEnemyDaily = item?.FierceEnemySelection is { } choice
            ? new FierceEnemyDailySettings { UnitId = choice.Unit.Id, Difficulty = choice.Difficulty,
                IsChecked = item.IsChecked, Index = _dailyTasks.IndexOf(item) }
            : null;
        // 未加载日志的设计与测试窗口只更新内存，不写入用户配置。
        if (_logger is null) return;
        try { new ConfigService(_logger).Save(SnapshotConfig(), ConfigService.ResolvePath(DefaultConfigFile)); }
        catch (Exception ex)
        {
            FierceEnemyAssaultStatusText.Text = L.Text("每日任务已在当前窗口更新，但保存失败，重启后可能丢失，请检查配置目录。");
            _logger.Warn(L.Format($"强敌每日任务保存失败: {ex.GetType().Name}"));
        }
    }
    private CancellationTokenSource? _fierceEnemyAssaultCts;

    /// <summary>目录错误在当前页显示，不影响其他每日任务使用。</summary>
    private void InitializeFierceEnemySelection()
    {
        FierceEnemyDifficultyBox.ItemsSource = Enum.GetValues<FierceEnemyDifficulty>();
        FierceEnemyDifficultyBox.SelectedIndex = 0;
        try
        {
            var units = FierceEnemyUnit.Load();
            FierceEnemyUnitBox.ItemsSource = new[] { FierceEnemyUnit.Latest }.Concat(units).ToArray();
            FierceEnemyUnitBox.SelectedItem = units.MaxBy(unit => unit.Id);
        }
        catch (Exception ex)
        {
            FierceEnemySelectionPanel.IsEnabled = false;
            FierceEnemyAssaultStatusText.Text = L.Format($"无法加载强敌单位：{ex.Message}");
        }
    }

    /// <summary>强敌任务保持唯一；替换时保留原排序，重新冻结选择并默认勾选。</summary>
    private void FierceEnemyAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!FierceEnemyAvailable) return;
        if (_busy || _windowTasks.IsStopping) return;
        if (FierceEnemyUnitBox.SelectedItem is not FierceEnemyUnit unit ||
            FierceEnemyDifficultyBox.SelectedItem is not FierceEnemyDifficulty difficulty) return;
        var existing = _dailyTasks.FirstOrDefault(t => t.Kind == DailyTaskKind.FierceEnemySkip);
        var item = new DailyTaskItem { Kind = DailyTaskKind.FierceEnemySkip, Name = L.Text("强敌来袭略过"),
            FierceEnemySelection = new(unit, difficulty), IsChecked = true };
        item.PropertyChanged += DailyTask_StateChanged;
        if (existing is not null)
        {
            var index = _dailyTasks.IndexOf(existing);
            existing.PropertyChanged -= DailyTask_StateChanged;
            _dailyTasks[index] = item;
        }
        else
        {
            var ap = _dailyTasks.FirstOrDefault(t => t.Kind == DailyTaskKind.ConsumeAp);
            _dailyTasks.Insert(ap is null ? _dailyTasks.Count : _dailyTasks.IndexOf(ap), item);
        }
        FierceEnemyAssaultStatusText.Text = $"{(existing is null ? L.Text("已添加") : L.Text("已替换"))}：{unit.Name} / {difficulty}。";
        SaveFierceEnemyDailyTask();
        UpdateDailySelectAllState();
        UpdateBusyState();
    }

    /// <summary>仅允许移除动态强敌任务；解除事件订阅，执行期间不允许改动队列。</summary>
    private void FierceEnemyRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy || _windowTasks.IsStopping || (sender as FrameworkElement)?.DataContext is not DailyTaskItem item ||
            item.Kind != DailyTaskKind.FierceEnemySkip || !_dailyTasks.Remove(item)) return;
        item.PropertyChanged -= DailyTask_StateChanged;
        FierceEnemyAssaultStatusText.Text = L.Text("已删除每日任务中的强敌来袭略过。");
        SaveFierceEnemyDailyTask();
        UpdateDailySelectAllState();
        UpdateBusyState();
    }

    /// <summary>开始或中止单次略过，不在 UI 线程执行图像匹配。</summary>
    private async void FierceEnemyAssaultStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartFierceEnemyAssaultAsync);

    /// <summary>独占设备执行当前选择的一次完整略过；与每日任务共用识别、暗态和资源保护。</summary>
    private async Task StartFierceEnemyAssaultAsync()
    {
        if (!FierceEnemyAvailable && _fierceEnemyAssaultCts is null) return;
        if (_fierceEnemyAssaultCts is not null)
        {
            _fierceEnemyAssaultCts.Cancel();
            FierceEnemyAssaultStartButton.Content = L.Text("正在停止…");
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        if (_automation is null || _logger is null || !TryGetDeviceSerial(out var serial))
        { FierceEnemyAssaultStatusText.Text = L.Text("请先连接并选择模拟器。"); return; }
        if (FierceEnemyUnitBox.SelectedItem is not FierceEnemyUnit unit ||
            FierceEnemyDifficultyBox.SelectedItem is not FierceEnemyDifficulty difficulty)
        { FierceEnemyAssaultStatusText.Text = L.Text("请先选择强敌单位和难度。"); return; }
        var selection = new FierceEnemySkipSelection(unit, difficulty);
        _fierceEnemyAssaultCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _fierceEnemyAssaultCts.Token;
        FierceEnemyAssaultStartButton.Content = L.Text("中止略过");
        FierceEnemyAssaultStatusText.Text = L.Text("正在确认主界面并进入事件…");
        UpdateBusyState();
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            { FierceEnemyAssaultStatusText.Text = L.Text("设备画面不兼容，已停止略过。"); return; }
            if (!await EnsureMainScreenAsync(serial, ct))
            { FierceEnemyAssaultStatusText.Text = L.Text("未确认主界面，已停止略过。"); return; }
            var result = await new FierceEnemySkipService(_automation, _logger, GetTextOcr(),
                confirm => CreateStageSkipService(false, confirm)).ExecuteAsync(serial, _mainLayout,
                    _stageLayout, selection, ct);
            FierceEnemyAssaultStatusText.Text = result.Message;
            if (result.Success || result.IsUnavailable) _logger.Info(result.Message);
            else _logger.Warn(result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { FierceEnemyAssaultStatusText.Text = L.Text("强敌来袭略过已中止。"); _logger.Info(FierceEnemyAssaultStatusText.Text); }
        catch (Exception ex)
        { FierceEnemyAssaultStatusText.Text = L.Format($"强敌来袭略过已停止：{ex.Message}"); _logger.Error(FierceEnemyAssaultStatusText.Text); }
        finally
        {
            _fierceEnemyAssaultCts.Dispose();
            _fierceEnemyAssaultCts = null;
            FierceEnemyAssaultStartButton.Content = L.Text("执行单次略过");
            UpdateBusyState();
        }
    }
}
