using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GGEA.App.Config;

namespace GGEA.App.Tasks;

/// <summary>强敌目录中的单位；编号只用于选择，不用于推算列表点击位置。</summary>
public sealed record FierceEnemyUnit(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name_en")] string Name,
    [property: JsonPropertyName("screenshot")] string Screenshot)
{
    /// <summary>特殊选项沿用游戏当前默认单位，不读取单位素材；不写入真实单位目录。</summary>
    public static FierceEnemyUnit Latest { get; } = new(0, "最新", "");
    public bool IsLatest => Id == 0;

    public string TemplatePath => ConfigService.ResolvePath("resources/templates/unit/" + Screenshot);

    /// <summary>加载随程序分发的目录，拒绝重复编号、空名称和越界素材路径。</summary>
    public static IReadOnlyList<FierceEnemyUnit> Load()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(ConfigService.ResolvePath("resources/fierce_enemy_assault.json")));
        var units = json.RootElement.GetProperty("fierce_enemy").Deserialize<List<FierceEnemyUnit>>()
            ?? throw new InvalidDataException("强敌目录为空。");
        if (units.Count == 0 || units.Select(u => u.Id).Distinct().Count() != units.Count ||
            units.Any(u => u.Id <= 0 || string.IsNullOrWhiteSpace(u.Name) || string.IsNullOrWhiteSpace(u.Screenshot) ||
                Path.GetFileName(u.Screenshot) != u.Screenshot || !File.Exists(u.TemplatePath)))
            throw new InvalidDataException("强敌目录或单位素材不完整。");
        return units.OrderByDescending(u => u.Id).ToArray();
    }
}

/// <summary>可略过的难度；不提供 CHALLENGE，执行层同样验证枚举边界。</summary>
public enum FierceEnemyDifficulty { EXPERT, HARD, NORMAL }

/// <summary>添加任务时冻结选择，之后修改下拉框不会悄悄改变已加入的任务。</summary>
public sealed record FierceEnemySkipSelection(FierceEnemyUnit Unit, FierceEnemyDifficulty Difficulty);
