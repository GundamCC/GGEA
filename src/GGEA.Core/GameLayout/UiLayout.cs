using System.Text.Json.Serialization;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>
/// 主界面固定功能区域布局。
/// 功能区域位置固定，因此保存坐标后不依赖游戏语言，更换语言仍可正常点击。
/// </summary>
public sealed class UiLayout
{
    /// <summary>截图基准宽度。</summary>
    [JsonPropertyName("screenWidth")]
    public int ScreenWidth { get; set; } = 1920;

    /// <summary>截图基准高度。</summary>
    [JsonPropertyName("screenHeight")]
    public int ScreenHeight { get; set; } = 1080;

    /// <summary>全部锚点。</summary>
    [JsonPropertyName("anchors")]
    public List<UiAnchor> Anchors { get; set; } = new();

    /// <summary>按名称查找锚点。</summary>
    public UiAnchor? Find(string name)
        => Anchors.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 单个界面锚点：名称、矩形区域与可选红点模板。
/// </summary>
public sealed class UiAnchor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>可选：该区域对应的识别模板（如红点）。</summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>区域中心 X，供点击使用。</summary>
    [JsonIgnore]
    public int CenterX => X + Width / 2;

    /// <summary>区域中心 Y，供点击使用。</summary>
    [JsonIgnore]
    public int CenterY => Y + Height / 2;

    /// <summary>转换为视觉模块的 ROI。</summary>
    [JsonIgnore]
    public Roi Roi => new(X, Y, Width, Height);
}
