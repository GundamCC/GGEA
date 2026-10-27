using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>历史推图复用窗口任务、设备互斥与战斗编制；本文件只组装本作品的识别及推进依赖。</summary>
public partial class MainWindow
{
    /// <summary>每轮读取历史目录，与主线编号和队列键隔离。</summary>
    private static HistoryStageCatalog LoadHistoryCatalog() => HistoryStageCatalog.Load(
        ConfigService.ResolvePath("resources/history_stages.json"),
        ConfigService.ResolvePath("resources/templates/stage/history_stage"));

    /// <summary>一轮任务独占 Logo/节点缓存；取消或失败退出后统一释放，不保留跨作品的按钮授权。</summary>
    private async Task<AutoProgressResult> RunHistoryProgressAsync(string serial, HardStageEntry selected,
        AutoProgressSettings settings, AutoProgressBattleService stageBattle,
        Func<Func<ScreenImage, CancellationToken, bool>, AutoProgressBattleService> createBossBattle,
        Action<string> report, CancellationToken ct, bool attackBoss = true)
    {
        var automation = _automation ?? throw new InvalidOperationException(L.Text("自动化服务尚未初始化。"));
        var adb = _adb ?? throw new InvalidOperationException(L.Text("ADB 尚未初始化。"));
        var log = _logger ?? throw new InvalidOperationException(L.Text("日志服务尚未初始化。"));
        var stageService = _stageService ?? throw new InvalidOperationException(L.Text("关卡服务尚未初始化。"));
        var catalog = LoadHistoryCatalog();
        var work = catalog.Works.Single(item => item.Id == selected.Id);
        var textOcr = GetTextOcr();
        using var vision = await Task.Run(() => new HistoryStageVision(catalog,
            ConfigService.ResolvePath("resources/templates/stage"), textOcr), ct);
        using var nodes = new StageNodeRecognizer(adb, _matcher, log,
            ConfigService.ResolvePath(_config.StageNodeTemplateDirectory),
            numberRecognizer: new OnnxStageNumberRecognizer(GetTextOcr()), layout: StageNodeLayout.History);
        nodes.ValidateForProgress();
        var bossRecognizer = new HistoryBossProgressRecognizer(GetTextOcr());
        if (attackBoss) bossRecognizer.Validate();
        // 返回识别与导航共用本作品的资源生命周期；不借用主线节点，也不额外截图或点击。
        var bossBattle = attackBoss ? createBossBattle((screen, token) =>
            bossRecognizer.ConfirmReturn(screen, vision, work.Id, token)) : null;
        bossBattle?.Validate(settings);
        var navigation = new HistoryProgressNavigator(automation, vision, catalog, work, serial, log, async token =>
        {
            if (!await EnsureMainScreenAsync(serial, token)) throw new InvalidOperationException(L.Text("未确认主界面，停止历史导航。"));
            var menu = await new StageMenuEntryService(automation, log).OpenFromHomeAsync(serial, _mainLayout,
                Math.Max(0, _config.StagePageSettleMs), token);
            if (!menu.Success) throw new InvalidOperationException(menu.Message);
            var main = _stageLayout.Find("mainStage") ?? throw new InvalidOperationException(L.Text("关卡布局缺少主要关卡入口。"));
            await automation.TapAsync(serial, main.CenterX, main.CenterY, token);
            var title = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath("resources/templates/stage/main_stage.png"),
                new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .90, TimeoutMs = GameTiming.RecognitionTimeoutMs,
                    AllowGlobalContinuation = false }, token);
            if (title is null) throw new InvalidOperationException(L.Text("未确认主要关卡标题，停止历史导航。"));
        }, _downloadDialogService ?? throw new InvalidOperationException(L.Text("下载识别服务尚未初始化。")), _config.AllowGgeaDownload);
        var reader = stageService.CreateProgressReader(new HistoryStageNodeRecognizer(automation, nodes, navigation));
        async Task<StageNodeRecognition> ReadNodes(HardStageEntry target, CancellationToken token)
        {
            await navigation.WaitForPageAsync(HistoryPageKind.StageNodes, token);
            return await reader.ReadCurrentWorkForProgressAsync(serial, target, token);
        }
        // 每个作品的关卡与 BOSS 分别拥有首关记录，重入导航时沿用，不能彼此抵消。
        var stageInitialFormation = new AutoFormationSession();
        var bossInitialFormation = new AutoFormationSession();
        return await new HistoryWorkProgressRunner(navigation.OpenOverviewAsync, async token =>
        {
            await navigation.OpenPartAsync(HistoryProgressStep.Stage, token);
            var runner = new AutoProgressRunner(ReadNodes, ReadNodes, async (node, battleToken) =>
            {
                await navigation.WaitForPageAsync(HistoryPageKind.StageNodes, battleToken);
                var firstStage = node is { Kind: WorkStageKind.Normal, Number: 1, Position.OverallIndex: 1, HasPassed: false };
                return await stageBattle.RunAsync(serial, settings, node.Kind, report, battleToken,
                    initialFormation: firstStage ? stageInitialFormation : null);
            }, (target, completed, advanceToken) => reader.AdvanceAfterBattleAsync(serial, target, completed, advanceToken),
            async (_, restoreToken) =>
            {
                var overview = await navigation.OpenOverviewAsync(restoreToken);
                if (overview.Stage == HistoryCompletion.Complete) return true;
                await navigation.OpenPartAsync(HistoryProgressStep.Stage, restoreToken);
                return false;
            });
            return await runner.RunAsync([work], report, token);
        }, async token =>
        {
            await navigation.OpenPartAsync(HistoryProgressStep.Boss, token);
            return await new HistoryBossProgressService(automation, navigation, bossRecognizer, serial, log)
                .RunAsync(work.Work, (order, battleToken) => bossBattle!.RunAsync(serial, settings, WorkStageKind.Unknown,
                    report, battleToken, preparationAlreadyOpened: true,
                    initialFormation: order == 1 ? bossInitialFormation : null), report, token);
        }, navigation.ReturnOverviewAsync).RunAsync(work.Work, report, ct, attackBoss);
    }
}
