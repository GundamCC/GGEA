namespace GGEA.App.Vision;

/// <summary>HARD STAGE 标题识别结果。</summary>
public sealed record StageTitleMatch(
    int HardIndex,
    double Confidence,
    int CenterX,
    int CenterY,
    Roi Region);

/// <summary>HARD STAGE 标题识别接口，便于使用真实截图或测试替身。</summary>
public interface IStageTitleRecognizer
{
    /// <summary>在固定标题栏 ROI 内识别 HARD STAGE n，未识别到返回 null。</summary>
    Task<StageTitleMatch?> RecognizeAsync(
        string serial,
        Roi titleRoi,
        double threshold = 0.90,
        CancellationToken ct = default);
}
