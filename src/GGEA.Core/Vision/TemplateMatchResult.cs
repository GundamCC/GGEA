namespace GGEA.App.Vision;

/// <summary>
/// 模板匹配结果：包含匹配区域、中心点与置信度。
/// </summary>
public sealed record TemplateMatchResult(Roi Region, double Confidence)
{
    public int X => Region.X;
    public int Y => Region.Y;
    public int Width => Region.Width;
    public int Height => Region.Height;
    public int CenterX => Region.CenterX;
    public int CenterY => Region.CenterY;

    public override string ToString() =>
        $"Confidence={Confidence:P1}, ROI=({Region}), Center=({CenterX},{CenterY})";
}
