using System.IO;
using System.Text.Json;
using GGEA.App.Config;

namespace GGEA.App.Tasks;

internal sealed record DevelopmentOffer(string Id, string SeriesId, string UnitId, string UnitName,
    string Template, int Amount, int Capital);
internal sealed record DevelopmentOrder(DevelopmentOffer Offer, int Progress);

/// <summary>委托指定的作品与角色所属作品分别存储。只把目标作品下可计入进度的基准单位交给开发流程。</summary>
internal static class DevelopmentRequestIndex
{
    private sealed record Offer(string RoleId, string Title, string Kind, string? SeriesId, int Amount);
    private sealed record Unit(string Name, string[] SeriesIds, string Roadmap);
    private sealed record Catalog(Dictionary<string, Unit> Units, Dictionary<string, Offer> Offers);
    private sealed record Baseline(string UnitId, int Capital, Dictionary<string, int> Materials);
    private static readonly Lazy<Dictionary<(string, string), DevelopmentOffer>> Entries = new(Load);

    private static T LoadFile<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(
        ConfigService.ResolvePath("resources/data/" + name))) ?? throw new InvalidDataException(name);

    private static Dictionary<(string, string), DevelopmentOffer> Load()
    {
        var catalog = LoadFile<Catalog>("character_request_index.json");
        var bases = LoadFile<Dictionary<string, Baseline>>("series_development_bases.json");
        var entries = new Dictionary<(string, string), DevelopmentOffer>();
        foreach (var (id, offer) in catalog.Offers)
        {
            if (offer.Kind != "开发指定系列单位" || !offer.Title.StartsWith("委托开发", StringComparison.Ordinal)) continue;
            if (offer.SeriesId is null || !bases.TryGetValue(offer.SeriesId, out var basis) ||
                !catalog.Units.TryGetValue(basis.UnitId, out var unit) || !unit.SeriesIds.Contains(offer.SeriesId) ||
                basis.Capital <= 0 || basis.Materials is not { Count: 0 } || offer.Amount is < 1 or > 32)
                throw new InvalidDataException($"委托 {id} 的作品、基准机体或数量无效");
            entries.Add((offer.RoleId, offer.Title), new(id, offer.SeriesId, basis.UnitId, unit.Name, unit.Roadmap, offer.Amount, basis.Capital));
        }
        return entries;
    }

    internal static DevelopmentOffer? Find(string roleId, int number) =>
        Entries.Value.GetValueOrDefault((roleId, "委托开发" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
