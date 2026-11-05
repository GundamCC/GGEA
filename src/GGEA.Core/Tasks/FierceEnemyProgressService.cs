using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>强敌自动推图适配：共享入口与单位导航，独立识别难度，不依赖略过次数。</summary>
public sealed class FierceEnemyProgressService(AutomationService automation, IGgeaLog log, OnnxTextRecognizer ocr)
{
    private readonly FierceEnemyProgressRecognizer _recognizer = new(ocr);

    /// <summary>已确认主界面后导航至指定单位；单位识别器的生命周期覆盖整轮战斗。</summary>
    public async Task<AutoProgressResult> RunAsync(string serial, UiLayout mainLayout, FierceEnemyUnit target,
        Func<CancellationToken, Task<AutoProgressBattleResult>> battle, Action<string> report, CancellationToken ct)
    {
        _recognizer.Validate();
        using var unit = target.IsLatest ? null : new FierceEnemyUnitRecognizer(target.TemplatePath);
        var navigation = new FierceEnemyNavigationService(automation, log, ocr);
        var opened = await new FierceEnemyAssaultService(automation, log).OpenFromHomeAsync(serial, mainLayout, ct: ct);
        if (!opened.Success) return new(false, 0, 0, opened.Message);
        var entry = new FierceEnemyAssaultEntryService(automation, log, new FierceEnemyAssaultSkipCountRecognizer(ocr));
        if (!await entry.EnterAsync(serial, ct))
            return new(false, 0, 0, L.Text("未确认前往事件按钮，停止强敌导航。"));
        // 原略过路径通过读取次数等待活动就绪；推图不读取次数，改为等待真实关卡选择按钮。
        var ready = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(FierceEnemyNavigationService.SelectStageTemplate),
            new WaitOptions { Roi = FierceEnemyAssaultEntryService.ButtonRegion, Threshold = .92,
                TimeoutMs = GameTiming.ShortRecognitionTimeoutMs, AllowGlobalContinuation = false }, ct);
        if (ready is null || !await navigation.NavigateAsync(serial, unit, ct))
            return new(false, 0, 0, L.Format($"未确认 {target.Name} 的强敌关卡详情。"));

        // 每轮重新扫描，不沿用导航前的截图；最多五个视口覆盖当前、底部和顶部。
        async Task<IReadOnlyList<FierceEnemyProgressObservation>> Read(CancellationToken token)
        {
            // 结算可能先显示选关标题再加载难度卡片，沿用导航的有限等待，不能把过渡帧当成单位错误。
            if (!await navigation.WaitForDetailAsync(serial, unit, token))
                throw new InvalidOperationException(L.Text("等待强敌难度页就绪超时，停止推进。"));
            var states = new Dictionary<FierceEnemyDifficulty, FierceEnemyProgressObservation>();
            for (var page = 0; page < 5; page++)
            {
                var screen = await automation.CaptureScreenAsync(serial, token);
                if (!await Task.Run(() => navigation.IsDetail(screen, unit, token), token))
                    throw new InvalidOperationException(L.Text("强敌单位或难度页复核失败，停止推进。"));
                var observations = await Task.Run(() => _recognizer.Read(screen, token), token);
                await Task.Delay(GameTiming.RecognitionIntervalMs, token);
                var fresh = await automation.CaptureScreenAsync(serial, token);
                if (!await Task.Run(() => navigation.IsDetail(fresh, unit, token), token))
                    throw new InvalidOperationException(L.Text("强敌详情复核时页面发生变化，停止推进。"));
                var confirmed = await Task.Run(() => _recognizer.Read(fresh, token), token);
                foreach (var observation in observations)
                    if (observation.State != FierceEnemyProgressState.Unknown &&
                        confirmed.Any(x => x.Difficulty == observation.Difficulty && x.State == observation.State))
                    {
                        if (!states.TryGetValue(observation.Difficulty, out var previous) || previous.State != observation.State)
                            log.Info($"强敌 {target.Name} / {observation.Difficulty}：" + (observation.State switch
                            {
                                FierceEnemyProgressState.Passed => "已通关（CLEAR / COMPLETE），跳过。",
                                FierceEnemyProgressState.Locked => "尚未解锁，不点击。",
                                _ => "未通关且挑战按钮可用。",
                            }));
                        states[observation.Difficulty] = observation;
                    }
                if (states.Count == 3 || states.Values.Any(x => x.Difficulty == FierceEnemyDifficulty.EXPERT &&
                    x.State == FierceEnemyProgressState.Passed)) return states.Values.ToArray();
                if (page < 4) await ScrollAsync(serial, page < 2, token);
            }
            return states.Values.ToArray();
        }

        async Task<AutoProgressBattleResult> Challenge(FierceEnemyDifficulty difficulty, CancellationToken token)
        {
            for (var page = 0; page < 5; page++)
            {
                var screen = await automation.CaptureScreenAsync(serial, token);
                if (!await Task.Run(() => navigation.IsDetail(screen, unit, token), token))
                    return new(false, L.Text("挑战前单位详情复核失败，未点击。"));
                var node = (await Task.Run(() => _recognizer.Read(screen, token), token))
                    .SingleOrDefault(x => x.Difficulty == difficulty);
                if (node is { State: FierceEnemyProgressState.Available, Challenge: not null })
                {
                    await Task.Delay(GameTiming.RecognitionIntervalMs, token);
                    var fresh = await automation.CaptureScreenAsync(serial, token);
                    if (!await Task.Run(() => navigation.IsDetail(fresh, unit, token), token))
                        return new(false, L.Text("挑战前单位详情发生变化，未点击。"));
                    var confirmed = (await Task.Run(() => _recognizer.Read(fresh, token), token))
                        .SingleOrDefault(x => x.Difficulty == difficulty);
                    if (confirmed is not { State: FierceEnemyProgressState.Available, Challenge: not null } ||
                        Math.Abs(confirmed.Challenge.CenterY - node.Challenge.CenterY) > 3)
                        return new(false, L.Text("挑战按钮未稳定，未点击。"));
                    await automation.TapAsync(serial, confirmed.Challenge.CenterX, confirmed.Challenge.CenterY, token);
                    return await battle(token);
                }
                if (page < 4) await ScrollAsync(serial, page < 2, token);
            }
            return new(false, L.Text("未重新确认目标难度的亮态挑战按钮，未点击。"));
        }
        return await new FierceEnemyProgressRunner(Read, Challenge).RunAsync(target.Name, report, ct);
    }

    /// <summary>仅在已确认详情页的右侧列表内有限滚动；下一次操作必须重新取帧。</summary>
    private async Task ScrollAsync(string serial, bool down, CancellationToken ct)
    {
        await automation.SwipeAsync(serial, 1500, down ? 870 : 340, 1500, down ? 340 : 870, 650, ct);
        await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
    }
}
