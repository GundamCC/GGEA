using System.IO;
using System.Text.Json;

namespace GGEA.App.Config;

/// <summary>活动配置没有开放日期，以递增的活动编号确定最新活动。</summary>
internal sealed record LimitedEventDefinition(int Id, string Work, int NormalCount, int SideStories,
    int HardCount, int SpecialHardCount, string Screenshot)
{
    internal static LimitedEventDefinition[] Load()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(ConfigService.ResolvePath("resources/common/limited_time_event.json")));
        var events = json.RootElement.GetProperty("event").Deserialize<LimitedEventDefinition[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (events is null || events.Any(e => e is null || e.Id <= 0 || string.IsNullOrWhiteSpace(e.Work) ||
            string.IsNullOrWhiteSpace(e.Screenshot) || e.Screenshot.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            e.NormalCount <= 0 || e.HardCount <= 0 || e.SideStories < 0 || e.SpecialHardCount < 0) ||
            events.Select(e => e.Id).Distinct().Count() != events.Length)
            throw new InvalidDataException("限时活动配置无效。");
        return events.OrderByDescending(e => e.Id).ToArray();
    }

    internal HardStageEntry CreateWork() => new() { Id = Id, Work = Work, InferFinalBoss = false,
        NormalCount = NormalCount, SideStories = SideStories, HardCount = HardCount + SpecialHardCount };
}
