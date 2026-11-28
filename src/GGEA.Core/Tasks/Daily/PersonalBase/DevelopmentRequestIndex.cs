using System.IO;
using System.Text.Json;
using GGEA.App.Config;

namespace GGEA.App.Tasks;

internal sealed record DevelopmentOffer(string Id, string SeriesId, string UnitId, string UnitName,
    string Template, int Amount, int Capital);
// 交付路线指定成品等级；强化基准路线由作品表生成，只允许数量一、纯 CAPITAL 的预览。
internal sealed record ProductionRoute(string SeriesId, string DiagramId, int Tab, int TabCount, int? Level, int Capital);
internal sealed record DevelopmentTarget(string UnitId, string Template, ProductionRoute Route);
internal sealed record SortieOffer(string RoleId, string Title, string SeriesId, int Amount);
internal sealed record SortieOrder(SortieOffer Offer, int Progress);
internal sealed record DevelopmentOrder(DevelopmentOffer Offer, int Progress);

/// <summary>委托指定的作品与角色所属作品分别存储。只把目标作品下可计入进度的基准单位交给开发流程。</summary>
internal static class DevelopmentRequestIndex
{
    private sealed record Offer(string RoleId, string Title, string Kind, string? SeriesId, int Amount, string? UnitId);
    private sealed record Unit(string Name, string[] SeriesIds, string Roadmap, ProductionRoute[]? DeliveryRoutes);
    private sealed record Catalog(Dictionary<string, Unit> Units, Dictionary<string, Offer> Offers);
    private sealed record Baseline(string UnitId, string DiagramId, int Capital, Dictionary<string, int> Materials);
    private static readonly Lazy<Catalog> Data = new(() => LoadFile<Catalog>("character_request_index.json"));
    private static readonly Lazy<Dictionary<string, Baseline>> Bases = new(() =>
        LoadFile<Dictionary<string, Baseline>>("series_development_bases.json"));
    private static readonly Lazy<Dictionary<(string, string), DevelopmentOffer>> Entries = new(Load);

    private static T LoadFile<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(
        ConfigService.ResolvePath("resources/data/" + name))) ?? throw new InvalidDataException(name);

    private static Dictionary<(string, string), DevelopmentOffer> Load()
    {
        var catalog = Data.Value;
        var entries = new Dictionary<(string, string), DevelopmentOffer>();
        foreach (var (id, offer) in catalog.Offers)
        {
            if (offer.Kind != "开发指定系列单位" || !offer.Title.StartsWith("委托开发", StringComparison.Ordinal)) continue;
            if (offer.SeriesId is null || !Bases.Value.TryGetValue(offer.SeriesId, out var basis) ||
                !catalog.Units.TryGetValue(basis.UnitId, out var unit) || !unit.SeriesIds.Contains(offer.SeriesId) ||
                basis.Capital <= 0 || basis.Materials is not { Count: 0 } || offer.Amount is < 1 or > 32)
                throw new InvalidDataException($"委托 {id} 的作品、基准机体或数量无效");
            entries.Add((offer.RoleId, offer.Title), new(id, offer.SeriesId, basis.UnitId, unit.Name, unit.Roadmap, offer.Amount, basis.Capital));
        }
        return entries;
    }

    // 强化委托使用它自己的作品 ID；部分基准单位没有「交付机体」路线记录。
    internal static DevelopmentTarget? ForStrength(StrengthRequest request)
    {
        if (!Data.Value.Offers.TryGetValue(request.Offer.Id, out var offer) || offer.RoleId != request.RoleId ||
            offer.Kind != "强化指定系列单位" || offer.Amount != request.Offer.Amount || offer.SeriesId is not { } series ||
            !Bases.Value.TryGetValue(series, out var basis) || basis.Capital <= 0 || basis.Materials is not { Count: 0 } ||
            !Data.Value.Units.TryGetValue(basis.UnitId, out var unit) || !unit.SeriesIds.Contains(series)) return null;
        var diagrams = DevelopmentMaps.Get(series).Diagrams;
        var diagram = diagrams.SingleOrDefault(d => d.Id == basis.DiagramId && !d.Ultimate);
        return diagram is null ? null : new(basis.UnitId, unit.Roadmap,
            new(series, diagram.Id, diagram.Tab, diagrams.Length, null, basis.Capital));
    }

    internal static string DeliveryTemplate(string unitId) => Data.Value.Units[unitId].Roadmap;

    internal static DevelopmentTarget? ForDelivery(DeliveryOffer offer, DeliveryProtection protection) =>
        DeliveryTargets(offer, protection).FirstOrDefault();

    internal static IReadOnlyList<DevelopmentTarget> DeliveryTargets(DeliveryOffer offer, DeliveryProtection protection)
    {
        var title = FormattableString.Invariant($"交付机体{offer.Number}");
        var link = Data.Value.Offers.Values.SingleOrDefault(o => o.RoleId == offer.RoleId && o.Title == title && o.Kind == "交付机体");
        if (link is not { Amount: 1 } || link.UnitId != offer.UnitId ||
            !Data.Value.Units.TryGetValue(offer.UnitId, out var unit) || unit.DeliveryRoutes is null) return [];
        return unit.DeliveryRoutes.Where(r => unit.SeriesIds.Contains(r.SeriesId) &&
            (link.SeriesId is null || link.SeriesId == r.SeriesId) &&
            r.Tab >= 1 && r.Tab <= r.TabCount && r.Level >= offer.MinimumLevel && r.Capital > 0 &&
            (!protection.ProtectHighLevel || r.Level < 60))
            .Select(route => new DevelopmentTarget(offer.UnitId, unit.Roadmap, route)).ToArray();
    }

    // 只有配置真正引用随包原生棋子时才根据 ID 选择作品，自定义素材沿用其入口配置。
    internal static ProductionRoute? RouteForTemplate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var resolved = ConfigService.ResolvePath(path);
        var name = Path.GetFileName(resolved);
        return Data.Value.Units.Values.FirstOrDefault(unit => unit.DeliveryRoutes is { Length: > 0 } &&
            Path.GetFileName(unit.Roadmap) == name &&
            string.Equals(ConfigService.ResolvePath(unit.Roadmap), resolved, GGEA.App.IO.FileSystemPaths.Comparison))
            ?.DeliveryRoutes?[0];
    }

    internal static SortieOffer? FindSortie(string roleId, int number, bool capture = false)
    {
        var title = (capture ? "夺取单位" : "请求出击") + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var offer = Data.Value.Offers.Values.SingleOrDefault(o => o.RoleId == roleId && o.Title == title &&
            o.Kind == (capture ? "夺取指定系列单位" : "完成指定系列关卡"));
        return offer is { SeriesId: not null, Amount: > 0 } ? new(roleId, title, offer.SeriesId, offer.Amount) : null;
    }

    internal static DevelopmentOffer? Find(string roleId, int number) =>
        Entries.Value.GetValueOrDefault((roleId, "委托开发" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
