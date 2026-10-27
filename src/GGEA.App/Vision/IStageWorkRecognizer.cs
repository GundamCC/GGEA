namespace GGEA.App.Vision;

/// <summary>主要关卡作品识别接口，便于导航流程使用真实截图或测试替身。</summary>
public interface IStageWorkRecognizer
{
    Task<IReadOnlyList<StageWorkMatch>> RecognizeAsync(
        string serial,
        double threshold = 0.85,
        CancellationToken ct = default);

    /// <summary>在预计区域内只匹配一个指定作品，降低全屏多模板误识别概率。</summary>
    Task<StageWorkMatch?> FindAsync(
        string serial,
        string templateName,
        Roi roi,
        double threshold = 0.78,
        CancellationToken ct = default);
}
