using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>固定区域读取结算标题，避免胜败背景影响模板；OCR 会话归调用方所有。</summary>
public sealed class MasterLeagueResultRecognizer(OnnxTextRecognizer ocr)
{
    public static readonly Roi TitleRegion = new(720, 275, 480, 80);

    /// <summary>提前验证离线模型，不在页面操作期间下载资源。</summary>
    public void Validate() => ocr.Validate();

    /// <summary>只提取中央标题的白色笔画；战斗左上角同名标签不在此区域内。</summary>
    public bool IsResult(ScreenImage screen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080) return false;
        var pixels = OnnxTextRecognizer.CreateWhiteMask(screen, TitleRegion, ct);
        return IsAccepted(ocr.Recognize(pixels, TitleRegion.Width,
            new Roi(0, 0, TitleRegion.Width, TitleRegion.Height), ct));
    }

    /// <summary>仅忽略空白与大小写；不补字、不模糊匹配 SCORE，低置信度不授权返回。</summary>
    public static bool IsAccepted(OcrTextRecognition text)
        => double.IsFinite(text.Confidence) && double.IsFinite(text.MinimumConfidence)
        && text.Confidence is >= .95 and <= 1 && text.MinimumConfidence is >= .90 and <= 1
        && string.Concat(text.Text.Where(c => !char.IsWhiteSpace(c)))
            .Equals("BATTLESCORE", StringComparison.OrdinalIgnoreCase);
}
