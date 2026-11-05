using System.Text.Json.Serialization;

namespace GGEA.App.Config;

/// <summary>
/// Hard 关卡数据集合（维护文件 resources/hard_stages.json）。
/// </summary>
public sealed class HardStagesData
{
    [JsonPropertyName("hardStages")]
    public List<HardStageEntry> HardStages { get; set; } = new();
}

/// <summary>
/// 单个作品条目：
/// id = 作品 ID，work = 作品名，normalCount = 普通关卡数量（最后一关为 BOSS），
/// sideStories = 支线关卡数量，hardCount = Hard 关卡数量（1~5），
/// screenshot = 该作品的识别素材文件名，相对于 resources/templates/stage/main_stage；
/// 可登记 PNG 截图或 WebP Logo，实际解码与匹配支持由识别器负责。
/// </summary>
public sealed class HardStageEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("work")]
    public string Work { get; set; } = "";

    [JsonPropertyName("normalCount")]
    public int NormalCount { get; set; }

    /// <summary>位于 NORMAL/BOSS 与 HARD 之间的 SIDE STORY 关卡数量。</summary>
    [JsonPropertyName("sideStories")]
    public int SideStories { get; set; }

    [JsonPropertyName("hardCount")]
    public int HardCount { get; set; }

    [JsonPropertyName("screenshot")]
    public string Screenshot { get; set; } = "";
}
