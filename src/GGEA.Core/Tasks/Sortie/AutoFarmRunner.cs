using GGEA.App.Localization;

namespace GGEA.App.Tasks;

internal sealed record AutoFarmOptions(int Rounds, string SortieTemplate, bool AllowDownload,
    bool AllowEmptySlotSortie, bool AllowTerrainUnsuitableSortie);

internal sealed record AutoFarmResult(bool Succeeded, string Message, bool CheckConnection = false);

/// <summary>首次出击只点击一次，后续轮次接手续战；只有确认结算后才推进轮次。</summary>
internal static class AutoFarmRunner
{
    internal static async Task<AutoFarmResult> RunAsync(int rounds, Func<CancellationToken, Task<bool>> start,
        Func<bool, CancellationToken, Task<SortieFlowResult>> firstRound,
        Func<bool, CancellationToken, Task<SortieFlowResult>> nextRound, Action<string> report, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        ct.ThrowIfCancellationRequested();
        report(L.Format($"第 {1}/{rounds} 轮：正在识别出击按钮…"));
        if (!await start(ct)) return new(false, L.Text("未识别到出击按钮，请确认处于出击准备页面。"), true);

        for (int round = 1; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            report(L.Format($"第 {round}/{rounds} 轮：等待关卡完成 × {rounds}"));
            bool more = round < rounds;
            var result = await (round == 1 ? firstRound : nextRound)(more, ct);
            if (result == SortieFlowResult.AllRoundsCompleted)
                return new(true, L.Format($"全部 {rounds} 轮刷取完成，已退出。"));
            if (result != SortieFlowResult.RoundCompleted) return Stopped(result);
            if (!more) return new(true, L.Format($"全部 {rounds} 轮刷取完成。"));
            report(L.Format($"第 {round}/{rounds} 轮完成，已点击再次出击，等待下一轮…"));
        }
    }

    private static AutoFarmResult Stopped(SortieFlowResult result) => result switch
    {
        SortieFlowResult.DownloadCancelled => new(false, L.Text("检测到下载界面，未允许下载，已点击取消。"), false),
        SortieFlowResult.UnexpectedPreparation => new(false, L.Text("结算尚未确认却回到出击准备页，已停止刷本；本轮未计完成，也未重新出击。"), true),
        SortieFlowResult.Stalled => new(false, L.Text("连续两次 2 分钟检查仍无正常流程进展，自动刷本已停止，保留当前画面。"), false),
        SortieFlowResult.TapToNextTimeout => new(false, L.Text("等待出击加载或关卡开场超时，流程中断；请检查下载进度、游戏画面及出击是否成功。"), true),
        SortieFlowResult.ContinueTimeout => new(false, L.Text("等待继续按钮超时，流程中断。"), true),
        SortieFlowResult.AgainTimeout => new(false, L.Text("等待再次出击按钮超时，流程中断。"), true),
        SortieFlowResult.FailureDetected => new(false, L.Text("自动刷本失败，已回到选择关卡，请调整部队配置。"), true),
        SortieFlowResult.EmptySlotCancelled => new(false, L.Text("出击阵容存在空位且未勾选空位出击，已点击取消。"), false),
        SortieFlowResult.EmptySlotTimeout => new(false, L.Text("未识别到空位出击对话框按钮，流程中断。"), true),
        SortieFlowResult.TerrainCancelled => new(false, L.Text("检测到地形适性提示且未勾选强制出击，已点击取消。"), false),
        SortieFlowResult.TerrainTimeout => new(false, L.Text("未识别到地形适性对话框按钮，流程中断。"), true),
        SortieFlowResult.NoAp => new(false, L.Text("AP 不足，无法继续刷本，已回到选择关卡。"), false),
        SortieFlowResult.NoApCancelled => new(false, L.Text("AP 不足，无法出击，已点击取消。"), false),
        SortieFlowResult.NoApTimeout => new(false, L.Text("未识别到 AP 不足对话框取消按钮，流程中断。"), true),
        _ => new(false, L.Text("出击后未识别到地形、空位、AP 不足、下载提示或 TAP TO NEXT，流程中断。"), true),
    };
}
