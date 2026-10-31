using GGEA.App.Localization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>GitHub 正式 Release 更新源，匿名请求固定的官方仓库。</summary>
public sealed class GitHubUpdateClient : IDisposable
{
    // 新版只信任源码仓库的正式附件；旧客户端由旧仓库中的过渡包完成迁移。
    public const string Repository = "GundamCC/GGEA";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    private const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private readonly UpdateHttpTransport _http;
    private readonly bool _ownsTransport;

    /// <summary>单独使用时拥有网络连接；协调器传入共享传输时统一负责释放。</summary>
    public GitHubUpdateClient(HttpMessageHandler? handler = null) { _http = new(handler); _ownsTransport = true; }
    internal GitHubUpdateClient(UpdateHttpTransport http) => _http = http;

    /// <summary>latest 只取正式发布；仍显式校验草稿、预发布和附件，处理正在上传的不完整 Release。</summary>
    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var json = await _http.ReadSmallAsync(new Uri(LatestUrl), 1024 * 1024, UpdateHttpTransport.GitHubHosts, timeout.Token);
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException(L.Text("GitHub 返回的不是正式发布版本。"));
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new InvalidDataException(L.Text("GitHub 发布版本格式无效。"));
        var version = UpdateFiles.ParseVersion(tag[1..]);
        if (version <= current) return null;
        var name = $"GGEA-v{version}-win-x64.zip";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var zip = FindAsset(assets, name);
        var checksum = FindAsset(assets, name + ".sha256");
        var size = zip.GetProperty("size").GetInt64();
        if (size <= 0 || size > UpdatePackage.MaximumPackageBytes) throw new InvalidDataException(L.Text("更新包大小超出支持范围。"));
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        if (notes.Length > 12000) notes = notes[..12000] + L.Text("\n…更多内容请查看 GitHub 发布页。");
        return new UpdateRelease(version, notes, name, AssetUri(zip, tag, name), size,
            AssetUri(checksum, tag, name + ".sha256"), ReadDigest(zip))
        { Delta = ReadDelta(assets, tag, version, current, size) };
    }

    /// <summary>差异附件尚未发布完整或不比全量包小则直接使用全量包；已发布的附件仍须通过地址和摘要校验。</summary>
    private static UpdateDeltaRelease? ReadDelta(JsonElement[] assets, string tag, Version version, Version current, long fullSize)
    {
        var name = $"GGEA-v{version}-from-v{current.ToString(4)}-win-x64.delta.zip";
        var matches = assets.Where(a => a.GetProperty("name").GetString() == name && a.GetProperty("state").GetString() == "uploaded").ToArray();
        var checksums = assets.Where(a => a.GetProperty("name").GetString() == name + ".sha256" && a.GetProperty("state").GetString() == "uploaded").ToArray();
        if (matches.Length == 0 || checksums.Length == 0) return null;
        if (matches.Length != 1 || checksums.Length != 1) throw new InvalidDataException(L.Text("差异更新附件重复。"));
        var size = matches[0].GetProperty("size").GetInt64();
        if (size <= 0 || size >= fullSize) return null;
        return new(current.ToString(4), name, AssetUri(matches[0], tag, name), size,
            AssetUri(checksums[0], tag, name + ".sha256"), ReadDigest(matches[0]));
    }

    /// <summary>GitHub 的摘要为可选字段；存在时必须明确使用 SHA-256。</summary>
    private static string? ReadDigest(JsonElement zip)
    {
        if (!zip.TryGetProperty("digest", out var item) || item.ValueKind == JsonValueKind.Null) return null;
        var digest = item.GetString() ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateSourceCatalog.IsHash(digest[7..]))
            throw new InvalidDataException(L.Text("GitHub 附件摘要格式无效。"));
        return digest[7..].ToLowerInvariant();
    }

    /// <summary>单一 GitHub 客户端也复用同一套下载完整性边界。</summary>
    public async Task<string> DownloadAsync(UpdateRelease update, string installation, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var downloader = new UpdatePackageDownloader(_http);
        var hash = await downloader.ResolveHashAsync(update, cancellationToken);
        return await downloader.DownloadAsync(update with { Sha256 = hash }, installation, progress, cancellationToken);
    }

    /// <summary>缺附件或重复附件时停止，避免在发布上传过程中误报可安装。</summary>
    private static JsonElement FindAsset(JsonElement[] assets, string name)
    {
        var matches = assets.Where(a => a.GetProperty("name").GetString() == name && a.GetProperty("state").GetString() == "uploaded").ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(L.Text("正式版附件尚未完整发布，请稍后重试或查看 GitHub 发布页。"));
    }

    /// <summary>只接受固定仓库、固定 tag 下的精确附件链接。</summary>
    private static Uri AssetUri(JsonElement asset, string tag, string name)
    {
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        if (asset.GetProperty("browser_download_url").GetString() != expected)
            throw new InvalidDataException(L.Text("更新附件不属于官方发布仓库。"));
        return new Uri(expected);
    }

    /// <summary>共享连接仅由协调器释放。</summary>
    public void Dispose() { if (_ownsTransport) _http.Dispose(); }
}
