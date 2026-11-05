using System.Text.Json.Serialization;

namespace GGEA.App.Config;

/// <summary>
/// 作品 → 主要关卡画布坐标映射集合（维护文件 resources/stage_work_positions.json）。
/// 坐标均为“从画布最左端算起”的画布坐标，用于进入 MAIN STAGE 后直接滚动定位。
/// </summary>
public sealed class StageWorkPositionsData
{
    [JsonPropertyName("positions")]
    public List<StageWorkPosition> Positions { get; set; } = new();

    /// <summary>按作品 ID 查找位置；未配置返回 null。</summary>
    public StageWorkPosition? Find(int id)
        => Positions.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// 单个作品的位置：id 对应 hard_stages.json 中的作品 ID。
/// </summary>
public sealed class StageWorkPosition
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>画布 X 坐标（相对最左端）。</summary>
    [JsonPropertyName("canvasX")]
    public int CanvasX { get; set; }

    /// <summary>画布 Y 坐标（屏幕纵坐标，画布不纵向滚动）。</summary>
    [JsonPropertyName("canvasY")]
    public int CanvasY { get; set; }

    /// <summary>是否已在真机上验证过（可选，仅作人工核对标记）。</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; set; }
}
