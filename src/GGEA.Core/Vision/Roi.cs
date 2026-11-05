namespace GGEA.App.Vision;

/// <summary>
/// 图像上的矩形区域（ROI），坐标单位为像素。
/// </summary>
public readonly record struct Roi(int X, int Y, int Width, int Height)
{
    /// <summary>区域中心 X。</summary>
    public int CenterX => X + Width / 2;

    /// <summary>区域中心 Y。</summary>
    public int CenterY => Y + Height / 2;

    /// <summary>由两个鼠标点构造规范化矩形。</summary>
    public static Roi FromPoints(double x1, double y1, double x2, double y2)
    {
        var left = (int)Math.Round(Math.Min(x1, x2));
        var top = (int)Math.Round(Math.Min(y1, y2));
        var right = (int)Math.Round(Math.Max(x1, x2));
        var bottom = (int)Math.Round(Math.Max(y1, y2));
        return new Roi(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
