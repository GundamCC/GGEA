using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>次数观察与流程决策；未知值不得当作仍有次数，零次属于正常结束。</summary>
public sealed record FierceEnemyAssaultSkipCount(int? Remaining, string Message)
{
    public bool CanSkip => Remaining is > 0;
}

/// <summary>识别强敌来袭右上角次数；只接受高置信度的 0/6 至 6/6，不纠正 OCR 字符。</summary>
public sealed class FierceEnemyAssaultSkipCountRecognizer(OnnxTextRecognizer ocr)
{
    public static readonly Roi CountRegion = new(1775, 120, 80, 40);

    /// <summary>模型归窗口所有，导航前预检，避免到达目标页后才发现运行资源缺失。</summary>
    public void Validate() => ocr.Validate();

    /// <summary>裁剪次数数值，排除左侧中文标签、上方 AP 和面板边框。</summary>
    public FierceEnemyAssaultSkipCount Recognize(ScreenImage screen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080)
            return new(null, "强敌来袭次数识别仅支持 1920×1080，已停止任务。");
        var mask = OnnxTextRecognizer.CreateWhiteMask(screen, CountRegion, ct);
        var text = ocr.Recognize(mask, CountRegion.Width, new Roi(0, 0, CountRegion.Width, CountRegion.Height), ct);
        return Decode(text);
    }

    /// <summary>统一格式与置信度门禁；仅去除空白，禁止将 O 猜为 0 或补写斜杠。</summary>
    public static FierceEnemyAssaultSkipCount Decode(OcrTextRecognition text)
    {
        var value = string.Concat(text.Text.Where(c => !char.IsWhiteSpace(c)));
        if (!double.IsFinite(text.Confidence) || !double.IsFinite(text.MinimumConfidence) ||
            text.Confidence is < .90 or > 1 || text.MinimumConfidence is < .85 or > 1 ||
            value.Length != 3 || value[0] is < '0' or > '6' || value[1] != '/' || value[2] != '6')
            return new(null, $"未可靠识别强敌来袭略过次数，已停止任务。{text.Diagnostic}");
        var remaining = value[0] - '0';
        return new(remaining, remaining == 0 ? "强敌来袭略过次数为0" : $"强敌来袭剩余略过次数：{remaining}/6");
    }
}
