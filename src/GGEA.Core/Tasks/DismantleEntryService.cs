using GGEA.App.Localization;
using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>单位达到上限后经强化页面进入解体页面；不选择或解体单位。</summary>
public sealed class DismantleEntryService(AutomationService automation)
{
    /// <summary>用户提供的强化界面标题素材。</summary>
    public const string TitleTemplate = "resources/templates/upgrade/title_upgrade.png";
    /// <summary>强化页解体入口与解体页标题。</summary>
    public const string EntryTemplate = "resources/templates/upgrade/button_dismantle_gray.png";
    public const string DismantleTitleTemplate = "resources/templates/upgrade/title_dismantle.png";

    /// <summary>仅单位上限且已恢复主界面时允许点击入口；金币不足和恢复失败均不进入。</summary>
    public async Task<bool> OpenAfterRecoveryAsync(string serial, DevelopResult reason, bool homeRecovered,
        CancellationToken ct = default, int timeoutMs = GameTiming.RecognitionTimeoutMs)
    {
        ct.ThrowIfCancellationRequested();
        if (reason != DevelopResult.UnitLimitReached || !homeRecovered) return false;
        return await OpenFromHomeAsync(serial, ct, timeoutMs);
    }

    /// <summary>调用方已确认主界面后进入解体；用于单位上限清理和全部开发完成后的最终清理。</summary>
    public async Task<bool> OpenFromHomeAsync(string serial, CancellationToken ct = default, int timeoutMs = GameTiming.RecognitionTimeoutMs)
    {
        ct.ThrowIfCancellationRequested();
        var path = ConfigService.ResolvePath(TitleTemplate);
        if (!File.Exists(path)) throw new FileNotFoundException(L.Text("缺少强化界面标题素材，未点击入口。"), path);
        foreach (var template in new[] { EntryTemplate, DismantleTitleTemplate })
            if (!File.Exists(ConfigService.ResolvePath(template)))
                throw new FileNotFoundException(L.Text("缺少解体入口素材，未点击入口。"), template);
        await automation.TapAsync(serial, 415, 1034, ct);
        if (await automation.WaitForImageAsync(serial, path,
            new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85,
                TimeoutMs = timeoutMs, PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct) is null) return false;
        var entry = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(EntryTemplate),
            new WaitOptions { Region = RecognitionRegion.DismantleEntry, Threshold = .90,
                TimeoutMs = timeoutMs, PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);
        if (entry is null) return false;
        await automation.TapAsync(serial, entry.CenterX, entry.CenterY, ct);
        return await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(DismantleTitleTemplate),
            new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85,
                TimeoutMs = timeoutMs, PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct) is not null;
    }
}
