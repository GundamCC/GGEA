using GGEA.App.Config;

namespace GGEA.App.Vision;

/// <summary>作品 Logo 的真实屏幕位置；作品 ID 来自配置，不由文件名排序推断。</summary>
public sealed record WorkLogoMatch(int WorkId, double Confidence, Roi Bounds)
{
    public int X => Bounds.X + Bounds.Width / 2;
    public int Y => Bounds.Y + Bounds.Height / 2;
}

/// <summary>一张截图的识别结果，标题与选择按钮必须在同一帧内同时成立。</summary>
public sealed record WorkLogoObservation(
    bool TargetTitleVisible,
    TemplateMatchResult? SelectButton,
    bool SelectClosed,
    bool MainStageVisible,
    IReadOnlyList<WorkLogoMatch> Works)
{
    /// <summary>本次观察的原始截图，供同帧继续提示识别与失败取证；不跨轮复用。</summary>
    public byte[]? ScreenshotPng { get; init; }
    /// <summary>仅用于本帧诊断的候选分数和素材状态，不参与导航点击授权。</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>隔离截图和识图，使导航恢复可用确定性的页面序列验证。</summary>
public interface IWorkLogoVision
{
    Task<WorkLogoObservation> ObserveAsync(string serial, HardStageEntry target,
        IReadOnlyList<HardStageEntry> works, CancellationToken ct);
}
