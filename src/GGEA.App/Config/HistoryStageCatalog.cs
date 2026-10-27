using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GGEA.App.Config;

/// <summary>历史作品目录独立于主线；保留用户配置的 hardStages 字段，编号不跨目录复用。</summary>
public sealed class HistoryStageCatalog
{
    [JsonPropertyName("hardStages")]
    public List<HardStageEntry> Works { get; set; } = [];

    /// <summary>外观相近的作品必须比较副标题；分组由素材目录维护，不在导航中猜测作品编号。</summary>
    [JsonPropertyName("confusableGroups")]
    public List<int[]> ConfusableGroups { get; set; } = [];

    /// <summary>任何缺失、重复编号或非法路径均拒绝整个目录，避免部分加载丢掉竞争作品。</summary>
    public static HistoryStageCatalog Load(string path, string logoDirectory)
    {
        var catalog = JsonSerializer.Deserialize<HistoryStageCatalog>(File.ReadAllText(path))
            ?? throw new InvalidDataException("历史关卡配置为空。");
        if (catalog.Works is null || catalog.Works.Count == 0 || catalog.ConfusableGroups is null ||
            catalog.Works.Any(w => w is null || w.Id <= 0 || string.IsNullOrWhiteSpace(w.Work) ||
                string.IsNullOrWhiteSpace(w.Screenshot) || Path.GetFileName(w.Screenshot) != w.Screenshot ||
                !File.Exists(ConfigService.ResolvePath(Path.Combine(logoDirectory, w.Screenshot)))) ||
            catalog.Works.Select(w => w.Id).Distinct().Count() != catalog.Works.Count)
            throw new InvalidDataException("历史作品目录含无效/重复条目或缺失 Logo，未开始导航。");
        var ids = catalog.Works.Select(w => w.Id).ToHashSet();
        if (catalog.ConfusableGroups.Any(g => g is null || g.Length < 2 ||
                g.Distinct().Count() != g.Length || g.Any(id => !ids.Contains(id))) ||
            catalog.ConfusableGroups.SelectMany(g => g).GroupBy(id => id).Any(g => g.Count() > 1))
            throw new InvalidDataException("历史作品相似组配置无效，未开始导航。");
        return catalog;
    }

    /// <summary>返回目标的相似作品；未分组作品仍参加完整 Logo 的全目录竞争。</summary>
    public IReadOnlyList<int> ConfusableWith(int id)
        => ConfusableGroups.FirstOrDefault(group => group.Contains(id)) ?? [];
}
