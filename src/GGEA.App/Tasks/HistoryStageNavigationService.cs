using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>历史作品导航只到作品关卡首页；绝不点击关卡、Boss、故事或出击，不消费 AP。</summary>
public sealed class HistoryStageNavigationService
{
    private readonly Func<int, CancellationToken, Task<HistoryStageObservation>> _observe;
    private readonly Func<int, int, CancellationToken, Task> _tap;
    private readonly Func<bool, CancellationToken, Task> _scroll;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly IGgeaLog _log;

    /// <summary>生产入口沿用统一截图安全检查；识别在后台线程执行，缓存只由本次导航串行使用。</summary>
    public HistoryStageNavigationService(AutomationService automation, IHistoryStageVision vision, string serial, IGgeaLog log)
        : this(async (id, ct) =>
            {
                var screen = await automation.CaptureScreenAsync(serial, ct);
                return await Task.Run(() => vision.Inspect(screen, id, ct), ct);
            }, (x, y, ct) => automation.TapAsync(serial, x, y, ct),
            (down, ct) => automation.SwipeAsync(serial, 180, down ? 850 : 350, 180, down ? 350 : 850, 650, ct),
            log, Task.Delay) { }

    /// <summary>页面序列测试注入 I/O 与延时，验证误识别、取消和超时不会授权后续点击。</summary>
    internal HistoryStageNavigationService(Func<int, CancellationToken, Task<HistoryStageObservation>> observe,
        Func<int, int, CancellationToken, Task> tap, Func<bool, CancellationToken, Task> scroll,
        IGgeaLog log, Func<int, CancellationToken, Task> delay)
    { _observe = observe; _tap = tap; _scroll = scroll; _log = log; _delay = delay; }

    /// <summary>每一动作前读取新帧，关键点击需连续两帧授权；错作品只能重新定位，不能沿用候选身份。</summary>
    public async Task<StageNavigationResult> OpenAsync(HardStageEntry target, int workCount, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        try
        {
            HistoryProgressObservation? confirmedProgress = null;
            var state = await Observe();
            if (state.Page == HistoryPageKind.Overview && state.DetailWorkId == target.Id && await DetailConfirmed(state)) return Success();
            // 作品首页与具体节点页都保留左下 Logo，最多返回两层；每次返回都重新确认身份及箭头。
            for (var depth = 0; depth < 2 && state.DetailWorkId is not null && state.Back is not null; depth++)
            {
                var back = state.Back;
                var next = await Observe();
                if (next.DetailWorkId != state.DetailWorkId || !Stable(back, next.Back))
                    return Fail(L.Text("作品关卡页状态发生变化，未返回或继续点击。"));
                await Tap(next.Back!);
                state = await Wait(s => s.Page is HistoryPageKind.Overview or HistoryPageKind.List);
                if (state.Page == HistoryPageKind.Overview && state.DetailWorkId == target.Id && await DetailConfirmed(state)) return Success();
            }
            if (!state.HistoryTitle && (state.Entry is not null || state.MainStageTitle))
            {
                var next = await Observe();
                if (Stable(state.Entry, next.Entry))
                    await Tap(next.Entry!);
                else if (state.MainStageTitle && next.MainStageTitle && !next.HistoryTitle && next.Entry is null)
                {
                    // 只有连续两帧确认主要关卡、最新帧入口未命中时才使用用户指定补偿点。
                    // 每次导航最多补偿一次，仍须确认历史列表后才能选作品或点击前往关卡。
                    _log.Info("已确认主要关卡标题，历史入口未命中，点击固定位置 (551,872) 进入历史关卡。");
                    token.ThrowIfCancellationRequested();
                    await _tap(551, 872, token);
                    await _delay(GameTiming.RecognitionIntervalMs, token);
                }
                else return Fail(L.Text("HISTORY STAGE 入口或主要关卡标题未连续确认，未点击。"));
                state = await Wait(s => s.HistoryTitle && s.Go is not null);
            }
            if (!IsList(state)) return Fail(L.Text("未确认历史作品列表。请先进入主要关卡或历史关卡界面。"));

            // 优先处理当前可见目标；选择失败只撤销候选，不把同系列误命中当作进入成功。
            if (await TryCurrent(state)) return await Finish();
            var maxScrolls = Math.Clamp((int)Math.Ceiling(workCount * 180.0 / 500), 1, 8);
            for (var i = 0; i < maxScrolls; i++)
            {
                if (!IsList(await Observe())) return Fail(L.Text("历史列表标题或前往关卡按钮消失，已停止拖动。"));
                await _scroll(false, token); await _delay(GameTiming.RecognitionIntervalMs, token);
            }
            for (var page = 0; page <= maxScrolls; page++)
            {
                state = await Observe();
                if (!IsList(state)) return Fail(L.Text("未确认历史作品列表，已停止作品搜索。"));
                if (await TryCurrent(state)) return await Finish();
                if (page == maxScrolls) break;
                // 识别与动作之间再检查页面，不能用旧帧在其他页面滚动。
                if (!IsList(await Observe())) return Fail(L.Text("页面已改变，未继续拖动。"));
                _log.Info("当前历史列表未确认目标作品，继续向下浏览。");
                await _scroll(true, token); await _delay(GameTiming.RecognitionIntervalMs, token);
            }
            return Fail(L.Format($"有限浏览后仍未确认历史作品 {target.Work}，未点击前往关卡。"));

            async Task<HistoryStageObservation> Observe()
            {
                token.ThrowIfCancellationRequested();
                var observation = await _observe(target.Id, token);
                token.ThrowIfCancellationRequested();
                return observation;
            }
            async Task Tap(TemplateMatchResult hit)
            { token.ThrowIfCancellationRequested(); await _tap(hit.CenterX, hit.CenterY, token); await _delay(GameTiming.RecognitionIntervalMs, token); }
            async Task<HistoryStageObservation> Wait(Func<HistoryStageObservation, bool> predicate)
            {
                HistoryStageObservation last = state;
                // 页面等待使用短识别预算，避免沿用旧高频采样次数造成一分钟停留。
                for (var i = 0; i < GameTiming.ShortRecognitionTimeoutMs / GameTiming.RecognitionIntervalMs; i++)
                { last = await Observe(); if (predicate(last)) return last; await _delay(GameTiming.RecognitionIntervalMs, token); }
                return last;
            }
            async Task<bool> DetailConfirmed(HistoryStageObservation first)
            {
                await _delay(GameTiming.ConfirmationIntervalMs, token);
                var next = await Observe();
                var confirmed = next.Page == HistoryPageKind.Overview && next.HistoryTitle && next.Go is null && next.DetailWorkId == target.Id;
                // 不跨作品或跨帧拼凑攻略度；动画中的差异只影响计划，不伪造未完成状态。
                confirmedProgress = confirmed && first.Progress == next.Progress ? next.Progress : null;
                return confirmed;
            }
            async Task<bool> TryCurrent(HistoryStageObservation current)
            {
                if (current.SelectedWorkId != target.Id)
                {
                    if (current.Candidate is not { } candidate) return false;
                    await _delay(GameTiming.ConfirmationIntervalMs, token);
                    var next = await Observe();
                    if (!IsList(next) || next.Candidate is not { } second ||
                        Math.Abs(candidate.CenterX - second.CenterX) > 8 || Math.Abs(candidate.CenterY - second.CenterY) > 8) return false;
                    token.ThrowIfCancellationRequested();
                    await _tap(second.CenterX, second.CenterY, token); await _delay(GameTiming.RecognitionIntervalMs, token);
                    current = await Wait(s => IsList(s) && s.SelectedWorkId is not null);
                }
                if (!IsList(current) || current.SelectedWorkId != target.Id)
                { _log.Info("候选选中后未通过完整 Logo/副标题复核，撤销进入授权。"); return false; }
                await _delay(GameTiming.ConfirmationIntervalMs, token);
                var confirmed = await Observe();
                if (!IsList(confirmed) || confirmed.SelectedWorkId != target.Id || !Stable(current.Go, confirmed.Go)) return false;
                _log.Info($"已连续确认历史作品 {target.Work}，点击前往关卡后复核左下 Logo。");
                await Tap(confirmed.Go!);
                return true;
            }
            async Task<StageNavigationResult> Finish()
            {
                var detail = await Wait(s => s.Page == HistoryPageKind.Overview && s.HistoryTitle && s.Go is null && s.DetailWorkId is not null);
                if (!detail.HistoryTitle) return Fail(L.Text("进入后未确认历史关卡标题，尚未进行作品 Logo 复核，已停止。"));
                if (detail.Page != HistoryPageKind.Overview) return Fail(L.Text("进入后未确认作品总览页面，已停止；未选择任何关卡或出击。"));
                return detail.Page == HistoryPageKind.Overview && detail.HistoryTitle && detail.Go is null && detail.DetailWorkId == target.Id && await DetailConfirmed(detail)
                    ? Success() : Fail(L.Text("进入后左下角作品 Logo 未连续确认或与目标不同，已停止；未选择任何关卡或出击。"));
            }
            StageNavigationResult Success() => StageNavigationResult.Ok(L.Format($"已进入并复核历史作品：{target.Work}。{HistoryProgressDecision.Describe(confirmedProgress)}导航完成，未开始战斗。"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Fail(L.Text("历史作品导航超过 90 秒，已停止操作。")); }
    }

    private static bool IsList(HistoryStageObservation state) => state.HistoryTitle && state.Go is not null && state.DetailWorkId is null;
    private static bool Stable(TemplateMatchResult? first, TemplateMatchResult? second)
        => first is not null && second is not null && Math.Abs(first.CenterX - second.CenterX) <= 8 && Math.Abs(first.CenterY - second.CenterY) <= 8;
    /// <summary>识别证据不足作为可解释的导航失败交给调用方，不修改阈值或继续尝试出击。</summary>
    private StageNavigationResult Fail(string message)
    { _log.Warn(message); return StageNavigationResult.Fail(StageNavigationFailure.TitleNotRecognized, message); }
}
