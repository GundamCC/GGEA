using System.Diagnostics;
using System.Windows;
using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>每日任务队列的启动、执行、收尾与中断。</summary>
public partial class MainWindow
{
    private bool _dailyRunning;
    private CancellationTokenSource? _dailyCts;

    /// <summary>每日任务：按用户拖拽后的列表顺序，依次执行已勾选的任务。</summary>
    private async void DailyStartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => DailyStartButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task DailyStartButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        // 任务运行中：点击按钮 = 中止当前任务
        if (_dailyRunning)
        {
            _logger?.Info(L.Text("用户请求中止每日任务…"));
            DailyView.DailyStartButton.Content = L.Text("正在停止…");
            DailyView.DailyStartButton.IsEnabled = false;
            _dailyCts?.Cancel();
            return;
        }

        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_automation is null ||
            _personalBaseService is null ||
            _shopService is null ||
            _giftService is null ||
            _developService is null ||
            _stageService is null ||
            _stageSkipService is null ||
            _apConsumptionService is null ||
            _upgradeStagesService is null ||
            _missionRewardService is null)
        {
            return;
        }

        var serial = await GetOnlineTaskSerialAsync(execution, AccountStatusText, _windowTasks.Token);
        if (serial is null) return;

        // 领取每日使命奖励：勾选后随“领取奖励”任务按队列顺序执行一次，
        // 并在每日任务队列最后再执行一次（共两次）；不勾选“领取奖励”时不自动执行。
        var missionRewardChecked = _rewardTasks.Any(reward =>
            reward.Kind == RewardTaskKind.MissionReward && reward.IsChecked);

        // 固化本次执行顺序：按当前列表顺序过滤已勾选任务，防止运行期间拖拽影响导航上下文。
        var dailyPlan = DailyTaskPlanner.BuildDailyPlan(_dailyTasks.Where(IsDailyTaskAvailable), missionRewardChecked);
        if (dailyPlan.Count == 0)
        {
            _logger?.Warn(L.Text("请至少勾选一个每日任务"));
            return;
        }

        var hasApTask = dailyPlan.Any(task => task.Kind == DailyTaskKind.ConsumeAp);
        var apPlan = hasApTask
            ? ApConsumptionPlanner.BuildExecutionPlan(_apWorkItems)
            : Array.Empty<ApConsumptionWorkItem>();
        if (hasApTask && apPlan.Count == 0)
        {
            _logger?.Warn(L.Text("消耗AP：请在 AP消耗 TAB 中至少勾选一个 Hard 关卡"));
            ApConsumptionView.ApPlanStatusText.Text = L.Text("尚未选择需要执行的 Hard 关卡。");
            return;
        }

        // 每次点击“任务开始”都建立独立 AP 链；前一次完成或中止的游标不得带入新任务。
        var apTaskChain = hasApTask
            ? new ApConsumptionTaskChain(serial, apPlan)
            : null;

        var hasRewardTask = dailyPlan.Any(task => task.Kind == DailyTaskKind.ClaimRewards);
        IReadOnlyList<RewardTaskItem> rewardPlan = hasRewardTask
            ? RewardTaskPlanner.BuildExecutionPlan(_rewardTasks)
            : Array.Empty<RewardTaskItem>();
        if (hasRewardTask && rewardPlan.Count == 0 && !missionRewardChecked)
        {
            _logger?.Warn(L.Text("领取奖励：请在 奖励 TAB 中至少勾选一项奖励"));
            RewardView.RewardPlanStatusText.Text = L.Text("尚未选择需要领取的奖励。");
            return;
        }

        _dailyRunning = true;
        _dailyCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _dailyCts.Token;
        DailyView.DailyStartButton.Content = L.Text("中止任务   ■");
        UpdateBusyState();
        AccountStatusText.Text = L.Text("开始执行每日任务…");
        var elapsed = Stopwatch.StartNew();
        var run = new DailyRun(dailyPlan.Count);
        var summary = run.Summary;
        // AP 只在独立执行时发送专属提醒；组合每日队列仍由原有队列汇总统一提醒。
        var endNotification = dailyPlan.Count == 1 && hasApTask
            ? BeginTaskEndNotification(L.Text("AP 消耗"))
            : null;
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            {
                return;
            }

            // 普通队列提前结束也进入收尾；安全停止和异常仍保留原有停止边界。
            var queueOutcome = await DailyQueueRunner.RunAsync(
                () => ExecuteDailyPlanAsync(serial, dailyPlan, apTaskChain, rewardPlan, run, ct),
                () => FinalizeDailyRewardsAsync(serial, dailyPlan, run, ct), ct);
            if (queueOutcome != DailyQueueOutcome.Completed || summary.GetInterruptionMessage() is not null) return;

            ct.ThrowIfCancellationRequested();
            elapsed.Stop();
            var completionMessage = summary.GetCompletionMessage(elapsed.Elapsed);
            var completionDetails = apTaskChain is not null && completionMessage is not null
                ? $"{completionMessage}；{apTaskChain.GetExecutionSummary()}"
                : completionMessage;
            AccountStatusText.Text = L.Format($"每日任务执行完成：{completionDetails}");
            _logger!.Info(AccountStatusText.Text);
            if (endNotification is not null)
            {
                endNotification.Succeeded = completionMessage is not null && summary.FailureCount == 0;
            }
            else if (!_apStopNotification.Reported && _config.EnableWindowsNotifications && completionMessage is not null)
            {
                // 系统通知优先完整展示每日失败清单；较长的 AP 执行统计仍保留在状态和日志中。
                var lockedSummary = apTaskChain?.GetLockedSummary();
                var notificationMessage = string.IsNullOrEmpty(lockedSummary)
                    ? completionMessage
                    : $"{completionMessage}；{lockedSummary}";
                ShowQueueCompletionNotification(notificationMessage, summary.FailureCount > 0);
            }
        }
        catch (OperationCanceledException)
        {
            AccountStatusText.Text = L.Text("任务已中止");
            summary.SetInterruption(run.Current?.Name ?? L.Text("每日任务"), L.Text("每日任务已被用户中止"));
            _logger!.Warn(L.Text("每日任务已被用户中止"));
        }
        catch (Exception ex)
        {
            var reason = L.Format($"每日任务执行失败: {ex.Message}");
            if (run.Current is not null && !run.ResultRecorded) summary.Record(run.Current.Name, false, reason);
            summary.SetInterruption(run.Current?.Name ?? L.Text("每日任务"), reason);
            _logger?.Error(reason);
            // 明确区分捕获失败与业务未命中，且不再启动可能点击遮罩的恢复逻辑。
            if (ex is ScreenshotCaptureException)
            {
                AccountStatusText.Text = L.Text("截图捕获失败，每日任务已停止");
                return;
            }
            AccountStatusText.Text = L.Text("每日任务执行失败");
            if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
            {
                AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
            }
        }
        finally
        {
            var interruption = summary.GetInterruptionMessage();
            if (interruption is not null)
                AccountStatusText.Text = run.FinalizationStatus is null ? interruption : $"{interruption}；{run.FinalizationStatus}";
            if (apTaskChain is not null)
            {
                var apSummary = apTaskChain.GetExecutionSummary();
                ApConsumptionView.ApPlanStatusText.Text = apSummary;
                _logger?.Info(apSummary);
                if (!AccountStatusText.Text.Contains(apSummary, StringComparison.Ordinal))
                {
                    AccountStatusText.Text = $"{AccountStatusText.Text}；{apSummary}";
                }
            }

            // 最后一条日志保留每日中断及收尾状态，不能仅剩“AP 成功0、失败0”。
            if (interruption is not null) _logger?.Warn(AccountStatusText.Text);
            _dailyRunning = false;
            _dailyCts?.Dispose();
            _dailyCts = null;
            DailyView.DailyStartButton.Content = L.Text("任务开始");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, AccountStatusText.Text);
        }
    }

    // 每次启动独立持有当前项和记账状态，收尾任务或异常不会重复记录中断项。
    private sealed class DailyRun(int plannedCount)
    {
        internal TaskQueueSummary Summary { get; } = new(plannedCount);
        internal DailyTaskItem? Current { get; private set; }
        internal bool ResultRecorded { get; private set; }
        internal string? FinalizationStatus { get; set; }

        internal void Begin(DailyTaskItem task)
        {
            Current = task;
            ResultRecorded = false;
        }

        internal void Record(DailyTaskItem task, bool success, string details = "")
        {
            Summary.Record(task.Name, success, details);
            ResultRecorded = true;
        }
    }
}
