using System.IO;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    // 标准截图中 AUTO 中央字形围绕实际点击点定位，避免旁边地图的相似图案授权固定坐标点击。
    private static Roi AutoToggleRoi => new(AutoButtonCenter.X - 60, AutoButtonCenter.Y - 35, 120, 70);
    private int _autoEvidenceSlot;
    private AutoButtonRecognizer? _battleAutoRecognizer;

    /// <summary>所有刷本入口共用字形与颜色判定，不把未识别到颜色解释成关闭。</summary>
    private AutoButtonRecognizer BattleAutoRecognizer()
    {
        var paths = AutoProgressFlowTemplates.FromDefaults();
        return _battleAutoRecognizer ??= new(_matcher, paths.AutoAll, paths.AutoEnemy, paths.AutoClose);
    }

    /// <summary>配置结果只承认新截图中的蓝色 AUTO；发送点击不代表配置成功。</summary>
    private sealed record AutoConfiguration(ScreenImage Screen, bool Confirmed, bool Acted,
        SortieFlowResult? Terminal = null);

    /// <summary>逐帧纠正 AUTO，最多三次点击；每次操作后先复查，未知、结算或终止画面均不继续切换。</summary>
    private async Task<AutoConfiguration> EnsureAllAutoAsync(string serial, ScreenImage screen,
        AutoButtonRecognizer recognizer, int intervalMs, CancellationToken ct)
    {
        var acted = false;
        AutoButtonState? previous = null;
        for (var clicks = 0; ; clicks++)
        {
            ct.ThrowIfCancellationRequested();
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal)
                return new(screen, false, acted, terminal);
            if (IsSettlementPage(screen)) return new(screen, false, acted);
            var auto = recognizer.Recognize(screen, AutoToggleRoi, ct);
            if (auto?.State == AutoButtonState.All)
            {
                _log.Info("已确认蓝色 AUTO（全部自动），无需继续切换。");
                return new(screen, true, acted);
            }
            if (auto is null || auto.State == AutoButtonState.Unknown)
            {
                _log.Info("AUTO 未显示或状态证据不足，不点击；保留等待并在停滞检查时复查。");
                return new(screen, false, acted);
            }
            if (auto.State == previous)
            {
                // 回执成功不等于状态已切换；同态重试交给两分钟恢复，避免延迟生效的点击叠加。
                _log.Warn($"AUTO 点击后仍为 {auto.State}，尚未确认全部自动，不追加盲点。");
                return new(screen, false, acted);
            }
            if (clicks >= 3)
            {
                _log.Warn("AUTO 有限切换后仍未确认蓝色全部自动，保留当前画面，等待停滞检查，不继续连点。");
                return new(screen, false, acted);
            }
            SaveAutoToggleEvidence(screen, auto);
            previous = auto.State;
            await _automation.TapAsync(serial, AutoButtonCenter.X, AutoButtonCenter.Y, ct);
            acted = true;
            _log.Info($"确认 AUTO 为 {auto.State}，切换一次，等待新截图复查全部自动。");
            await Task.Delay(Math.Max(1, intervalMs), ct);
            screen = await _automation.CaptureScreenAsync(serial, ct);
        }
    }

    /// <summary>保存点击所依据的原始局部像素；每个服务轮转六个槽位，磁盘失败不改变识别或点击规则。</summary>
    private void SaveAutoToggleEvidence(ScreenImage screen, AutoButtonMatch auto)
    {
        try
        {
            var roi = AutoToggleRoi;
            var pixels = new byte[roi.Width * roi.Height * 4];
            for (var y = 0; y < roi.Height; y++)
                Buffer.BlockCopy(screen.Bgra, ((roi.Y + y) * screen.Width + roi.X) * 4,
                    pixels, y * roi.Width * 4, roi.Width * 4);
            var directory = Path.Combine(AppContext.BaseDirectory, "logs", "auto");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"auto-{Environment.ProcessId}-{_autoEvidenceSlot++ % 6 + 1}.png");
            File.WriteAllBytes(path, ImageCodec.EncodePng(pixels, roi.Width, roi.Height));
            _log.Info($"AUTO 切换前局部画面已保存：{path}，状态 {auto.State}，区域 {auto.Match.Region}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"AUTO 局部画面保存失败（{ex.GetType().Name}），继续按本帧识别结果处理。");
        }
    }
}
