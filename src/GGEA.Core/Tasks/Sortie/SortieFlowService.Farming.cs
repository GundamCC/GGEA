using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    internal Task<AutoFarmResult> RunFarmAsync(string serial, AutoFarmOptions options,
        Action<string> report, CancellationToken ct)
        => AutoFarmRunner.RunAsync(options.Rounds,
            token => TryStartFarmAsync(serial, options, report, token),
            (more, token) => ExecuteAfterSortieAsync(serial, options.AllowDownload, options.AllowEmptySlotSortie,
                more, options.AllowTerrainUnsuitableSortie, ct: token, recoverySortieTemplate: options.SortieTemplate),
            (more, token) => ExecuteNextRoundAsync(serial, more, ct: token, allowDownload: options.AllowDownload,
                allowEmptySlotSortie: options.AllowEmptySlotSortie, allowTerrainUnsuitableSortie: options.AllowTerrainUnsuitableSortie,
                recoverySortieTemplate: options.SortieTemplate), report, ct);

    // 首轮沿用右下四分之一区域与出击阈值。后台匹配结束后再次检查取消，再点击实际识别中心。
    private async Task<bool> TryStartFarmAsync(string serial, AutoFarmOptions options,
        Action<string> report, CancellationToken ct)
    {
        var screen = await _automation.CaptureScreenAsync(serial, ct);
        var roi = new Roi(screen.Width * 3 / 4, screen.Height * 3 / 4, screen.Width / 4, screen.Height / 4);
        var sortie = await Task.Run(() => Match(screen, options.SortieTemplate, roi, StageSortieTemplateThreshold), ct);
        ct.ThrowIfCancellationRequested();
        if (sortie is null)
        {
            _log.Warn(L.Text("未识别到出击按钮，请确认当前处于出击准备页面"));
            return false;
        }
        _log.Info(L.Format($"识别到出击按钮: 中心 ({sortie.CenterX},{sortie.CenterY})，置信度 {sortie.Confidence:P1}"));
        report(L.Format($"第 {1}/{options.Rounds} 轮：已识别出击按钮，正在点击…"));
        ct.ThrowIfCancellationRequested();
        await _automation.TapAsync(serial, sortie.CenterX, sortie.CenterY, ct);
        return true;
    }
}
