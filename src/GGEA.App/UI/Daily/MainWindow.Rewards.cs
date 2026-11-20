using System.Collections.ObjectModel;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>奖励选择、领取及队列收尾共用同一套使命领取配置。</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<RewardTaskItem> _rewardTasks = new();

    /// <summary>初始化奖励页默认选项；领取顺序即列表顺序。</summary>
    private void InitializeRewardTasks()
    {
        _rewardTasks.Add(new RewardTaskItem
        {
            Kind = RewardTaskKind.MailReward,
            Name = L.Text("领取邮件奖励"),
            IsChecked = true,
        });
        _rewardTasks.Add(new RewardTaskItem
        {
            Kind = RewardTaskKind.MissionReward,
            Name = L.Text("领取每日使命奖励"),
            IsChecked = true,
        });
    }

    private Task<bool> ClaimDailyMissionAsync(string serial, CancellationToken ct) =>
        _missionRewardService!.ClaimAsync(serial, _mainLayout, _missionLayout,
            ConfigService.ResolvePath(_config.MissionClaimAllTemplate),
            ConfigService.ResolvePath(_config.MissionClaimAllDisabledTemplate),
            ConfigService.ResolvePath(_config.MissionClaimResultOkTemplate), ct: ct);

    private async Task<bool> ClaimDailyRewardsAsync(string serial, IReadOnlyList<RewardTaskItem> rewardPlan,
        List<string> failedSubtasks, CancellationToken ct)
    {
        RewardView.RewardPlanStatusText.Text = L.Format($"开始领取 {rewardPlan.Count} 项奖励…");
        var taskSucceeded = rewardPlan.Count > 0;
        foreach (var reward in rewardPlan)
        {
            ct.ThrowIfCancellationRequested();
            AccountStatusText.Text = L.Format($"执行：{reward.Name}…");
            switch (reward.Kind)
            {
                case RewardTaskKind.MailReward:
                {
                    var giftOk = await _giftService!.CollectGiftsAsync(
                        serial,
                        _mainLayout,
                        _giftLayout,
                        ConfigService.ResolvePath(_config.GiftCollectAllTemplate),
                        ConfigService.ResolvePath(_config.GiftCollectAllDisabledTemplate),
                        ConfigService.ResolvePath(_config.GiftOkTemplate),
                        ConfigService.ResolvePath(_config.GiftCloseTemplate),
                        ct: ct);
                    _logger!.Info(L.Format($"领取邮件奖励: {(giftOk ? L.Text("完成") : L.Text("未完成"))}"));
                    taskSucceeded &= giftOk;
                    if (!giftOk) failedSubtasks.Add(reward.Name);
                    break;
                }

                case RewardTaskKind.MissionReward:
                {
                    var missionOk = await ClaimDailyMissionAsync(serial, ct);
                    _logger!.Info(L.Format($"领取每日使命奖励: {(missionOk ? L.Text("完成") : L.Text("未完成"))}"));
                    taskSucceeded &= missionOk;
                    if (!missionOk) failedSubtasks.Add(reward.Name);
                    break;
                }

                default:
                    _logger?.Warn(L.Format($"未实现的奖励类型: {reward.Kind}"));
                    taskSucceeded = false;
                    failedSubtasks.Add(reward.Name);
                    break;
            }
        }

        RewardView.RewardPlanStatusText.Text = L.Text("领取奖励流程结束。");
        return taskSucceeded;
    }

    private async Task FinalizeDailyRewardsAsync(string serial, IReadOnlyList<DailyTaskItem> dailyPlan, DailyRun run, CancellationToken ct)
    {
        var finalTask = dailyPlan.LastOrDefault(task => task.Kind == DailyTaskKind.ClaimMissionReward);
        if (finalTask is null) return;
        run.Begin(finalTask);

        var previousStatus = AccountStatusText.Text;
        AccountStatusText.Text = L.Text("收尾：领取每日使命奖励…");
        // 每次收尾重新验证主界面；失败时不进入使命页面、不执行领取点击。
        var missionOk = await EnsureMainScreenAsync(serial, ct, false)
            && await ClaimDailyMissionAsync(serial, ct);
        run.Record(finalTask, missionOk);
        var finalStatus = L.Format($"收尾领取每日使命奖励：{(missionOk ? L.Text("完成") : L.Text("未完成"))}");
        run.FinalizationStatus = finalStatus;
        _logger!.Info(finalStatus);
        AccountStatusText.Text = $"{previousStatus}；{finalStatus}";
        if (!missionOk && await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
        {
            AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
            run.Summary.SetInterruption(finalTask.Name, AccountStatusText.Text);
            run.FinalizationStatus = $"{finalStatus}；{AccountStatusText.Text}";
        }
    }
}
