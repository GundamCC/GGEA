using System.Windows.Documents;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>按列表顺序执行每日任务，记录业务结果，并按当前页面决定是否安全继续。</summary>
public partial class MainWindow
{
    private async Task<DailyQueueOutcome> ExecuteDailyPlanAsync(string serial, IReadOnlyList<DailyTaskItem> dailyPlan,
        ApConsumptionTaskChain? apTaskChain, IReadOnlyList<RewardTaskItem> rewardPlan, DailyRun run, CancellationToken ct)
    {
        // 中断原因属于队列，不借用会被收尾步骤覆盖的状态栏作为唯一存储。
        DailyQueueOutcome StopDailyQueue(DailyTaskItem task, string reason, DailyQueueOutcome outcome)
        {
            if (!run.ResultRecorded) run.Record(task, false, reason);
            run.Summary.SetInterruption(task.Name, reason);
            var message = run.Summary.GetInterruptionMessage()!;
            AccountStatusText.Text = message;
            _logger!.Error(message);
            return outcome;
        }

        var allowInitialAnnouncement = true;
        foreach (var task in dailyPlan.Where(task => task.Kind != DailyTaskKind.ClaimMissionReward))
        {
            run.Begin(task);
            ct.ThrowIfCancellationRequested();

            // 每个任务前都重新确认主界面，避免在上一任务的子页面直接操作下一任务。
            bool allowAnnouncement = allowInitialAnnouncement;
            if (task.Kind != DailyTaskKind.LimitedEvent && !await EnsureMainScreenAsync(serial, ct, allowAnnouncement))
            {
                AccountStatusText.Text = L.Text("未能确认主界面，任务中止");
                if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                {
                    AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                    return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
                }
                return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Interrupted);
            }

            AccountStatusText.Text = L.Format($"执行：{task.Name}…");
            // 默认失败，只有服务明确返回成功才能计入成功；奖励子项合并为一个队列项。
            var taskSucceeded = false;
            var failedSubtasks = new List<string>();
            var developmentResourcesInsufficient = false;
            allowInitialAnnouncement = false;

            switch (task.Kind)
            {
                case DailyTaskKind.HarvestBattleship:
                {
                    var harvestResult = await _personalBaseService!.HarvestBattleshipAsync(
                        serial,
                        _mainLayout,
                        _personalBaseLayout,
                        ConfigService.ResolvePath(_config.BattleshipTemplate),
                        string.IsNullOrWhiteSpace(_config.BattleshipMask)
                            ? null
                            : ConfigService.ResolvePath(_config.BattleshipMask),
                        ConfigService.ResolvePath(_config.RecycleAllTemplate),
                        ConfigService.ResolvePath(_config.RecycleAllDisabledTemplate),
                        ConfigService.ResolvePath(_config.HarvestOkTemplate),
                        ct: ct);
                    _logger!.Info(L.Format($"回收战舰: {harvestResult.Message}"));
                    // 出航中暂不可回收属于安全略过；只有定位或状态确认失败才计为失败。
                    taskSucceeded = harvestResult.Completed;
                    break;
                }

                case DailyTaskKind.ShopFreeGift:
                {
                    var shopOk = await _shopService!.BuyFreePremiumItemAsync(
                        serial,
                        _mainLayout,
                        _shopLayout,
                        ConfigService.ResolvePath(_config.ShopFreeBuyTemplate),
                        ConfigService.ResolvePath(_config.ShopFreeTextTemplate),
                        ConfigService.ResolvePath(_config.ShopConfirmBuyTemplate),
                        ConfigService.ResolvePath(_config.ShopResultCloseTemplate),
                        ConfigService.ResolvePath(_config.DiamondIconTemplate),
                        ct: ct);
                    if (!shopOk)
                    {
                        // 商店可能仍停在购买弹窗；禁止交给通用导航空等或继续其他任务。
                        run.Record(task, false, L.Text("商店未安全完成"));
                        // 仅失败后做只读弹窗诊断；网络/日期更新都不能授权购买或导航。
                        if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                        {
                            AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                            return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
                        }
                        AccountStatusText.Text = L.Text("商店未完成，每日任务链已停止；请检查并手动关闭当前弹窗后重新开始");
                        _logger!.Warn(AccountStatusText.Text);
                        return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
                    }
                    _logger!.Info(L.Text("商店购物: 完成或已安全略过"));
                    taskSucceeded = shopOk;
                    break;
                }

                case DailyTaskKind.ClaimRewards:
                {
                    taskSucceeded = await ClaimDailyRewardsAsync(serial, rewardPlan, failedSubtasks, ct);
                    break;
                }

                case DailyTaskKind.DevelopOnce:
                {
                    var developResult = await _developService!.ExecuteAsync(serial, CreateDevelopRequest(), ct);
                    // 保持每日任务语义：无可用开发正常跳过，不能用于工厂的产出计数。
                    var shortageReason = developResult.GetBlockedReason();
                    developmentResourcesInsufficient = shortageReason is not null;
                    if (developmentResourcesInsufficient)
                    {
                        if (!await RecoverDevelopmentResourcesAsync(serial, shortageReason!, message => AccountStatusText.Text = message, ct))
                        {
                            AccountStatusText.Text = L.Format($"{shortageReason}，取消或主界面恢复未完成，每日任务已停止。");
                            _logger!.Error(AccountStatusText.Text);
                            return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
                        }
                        failedSubtasks.Add(L.Format($"{shortageReason}，已停止本项并继续后续任务"));
                        _logger!.Warn(L.Format($"{shortageReason}，停止开发&强化一次，继续后续每日任务"));
                    }
                    var developOk = developResult is DevelopResult.Completed or DevelopResult.Unavailable;
                    _logger!.Info(L.Format($"开发&强化一次: {(developOk ? L.Text("完成") : L.Text("未完成"))}"));
                    taskSucceeded = developOk;
                    break;
                }

                case DailyTaskKind.UpgradeStages:
                {
                    AccountStatusText.Text = L.Text("执行：强化培育关卡…");
                    var upgradeResult = await _upgradeStagesService!.ExecuteFromMainAsync(
                        serial,
                        _mainLayout,
                        _stageLayout,
                        _upgradeStagesLayout,
                        ct: ct);
                    if (!upgradeResult.Success)
                    {
                        AccountStatusText.Text = L.Format($"强化培育关卡失败：{upgradeResult.Message}");
                        if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                        {
                            AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                            return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
                        }
                        return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Interrupted);
                    }

                    _logger!.Info(L.Text("强化培育关卡完成"));
                    taskSucceeded = true;
                    break;
                }

                case DailyTaskKind.LimitedEvent:
                    try
                    {
                        await RunLimitedDailyTaskAsync(serial, task.LimitedEventSelection!, ct, allowAnnouncement);
                        taskSucceeded = true;
                    }
                    catch (MedalRecognitionException ex)
                    {
                        AccountStatusText.Text = ex.Message;
                        failedSubtasks.Add(ex.Message);
                        // 标记本项失败，沿用队列的网络检查与下一项主界面导航。
                    }
                    catch (InvalidOperationException ex)
                    {
                        return StopDailyQueue(task, ex.Message, DailyQueueOutcome.Interrupted);
                    }
                    break;

                case DailyTaskKind.FierceEnemySkip:
                {
                    var selection = task.FierceEnemySelection ?? throw new InvalidOperationException(L.Text("强敌任务缺少选择"));
                    AccountStatusText.Text = L.Format($"强敌来袭略过：{selection.Unit.Name} / {selection.Difficulty}…");
                    var result = await new FierceEnemySkipService(_automation!, _logger!, GetTextOcr(),
                        confirm => CreateStageSkipService(false, confirm), GetRecognitionEvidence()).ExecuteAsync(serial, _mainLayout,
                            _stageLayout, selection, ct);
                    if (result.Success || result.IsUnavailable) _logger!.Info(result.Message);
                    if (result.IsUnavailable)
                    {
                        AccountStatusText.Text = result.Message;
                        failedSubtasks.Add(result.Message);
                        break;
                    }
                    if (!result.Success)
                    {
                        AccountStatusText.Text = result.Message;
                        return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Interrupted);
                    }
                    taskSucceeded = true;
                    break;
                }

                case DailyTaskKind.ConsumeAp:
                {
                    var chain = apTaskChain ?? throw new InvalidOperationException(L.Text("AP 消耗任务链未初始化"));
                    ApConsumptionView.ApPlanStatusText.Text = L.Format($"开始执行 AP 消耗略过，共 {chain.Count} 个作品…");
                    AccountStatusText.Text = L.Text("执行：消耗AP略过…");
                    var apResult = await _apConsumptionService!.ExecuteAsync(
                        serial,
                        chain,
                        _mainLayout,
                        _stageLayout,
                        _stagePositions,
                        _hardStages,
                        ct);
                    ApConsumptionView.ApPlanStatusText.Text = apResult.Message;
                    if (apResult.IsInterrupted)
                    {
                        AccountStatusText.Text = L.Format($"AP消耗中断：{apResult.Message}");
                        if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                        {
                            AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                            return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
                        }
                        if (!dailyPlan.Any(item => item.Kind == DailyTaskKind.ClaimMissionReward))
                        {
                            await EnsureMainScreenAsync(serial, ct);
                        }
                        return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Interrupted);
                    }

                    AccountStatusText.Text = apResult.Success
                        ? L.Text("AP消耗任务链完成")
                        : L.Text("AP消耗任务链完成，但存在失败作品");
                    taskSucceeded = apResult.Success;
                    break;
                }

                default:
                    _logger?.Warn(L.Format($"未实现的每日任务类型: {task.Kind}"));
                    break;
            }
            run.Record(task, taskSucceeded, string.Join("、", failedSubtasks));
            if (!taskSucceeded && !developmentResourcesInsufficient && await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
            {
                AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                return StopDailyQueue(task, AccountStatusText.Text, DailyQueueOutcome.Unsafe);
            }
        }

        return DailyQueueOutcome.Completed;
    }
}
