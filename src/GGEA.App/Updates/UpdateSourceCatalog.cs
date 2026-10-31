using GGEA.App.Localization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>来源切换的可读状态；区分正常回退与可恢复网络异常。</summary>
public sealed record UpdateSourceStatus(string Message, bool IsWarning = false);

/// <summary>已验证的更新描述；来源域名随描述传递，不能把任意重定向当作官方附件。</summary>
public sealed record UpdateRelease(Version Version, string Notes, string PackageName, Uri PackageUrl,
    long PackageSize, Uri? ChecksumUrl, string? Sha256)
{
    public string SourceId { get; init; } = "github";
    public string SourceName { get; init; } = "GitHub";
    public IReadOnlyCollection<string> AllowedHosts { get; init; } = UpdateHttpTransport.GitHubHosts;
    public UpdateDeltaRelease? Delta { get; init; }
}

/// <summary>可选差异附件绑定检查时的旧版本；完整包始终是发布与镜像回退的必备项。</summary>
public sealed record UpdateDeltaRelease(string BaseVersion, string PackageName, Uri PackageUrl,
    long PackageSize, Uri ChecksumUrl, string? Sha256);

/// <summary>一个备用源提供静态版本清单及其可用的 HTTPS 下载域名。</summary>
public sealed record UpdateSource(string Id, string Name, string ManifestUrl, string[] AllowedHosts);

/// <summary>稳定的 v1 配置协议；空列表表示尚无备用源。</summary>
public sealed record UpdateSourceCatalog(int SchemaVersion, UpdateSource[] Sources)
{
    public const int MaximumBytes = 64 * 1024;
    public const int MaximumSources = 4;
    public static UpdateSourceCatalog Empty => new(1, []);

    /// <summary>只接受有界、无凭证的公开 HTTPS 地址；禁止本机、IP、非标准端口和片段。</summary>
    internal static bool IsPublicHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && IsPublicHost(uri.IdnHost);

    /// <summary>域名必须明确列出，不接受通配符或本机短名称。</summary>
    private static bool IsPublicHost(string host) =>
        Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.') &&
        !host.EndsWith('.') && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
        !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !host.Any(char.IsWhiteSpace);

    internal static bool IsHash(string hash) => Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$");

    /// <summary>格式或地址错误时整份拒绝；调用方保留上一份可用缓存，不部分覆盖。</summary>
    public static UpdateSourceCatalog Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException(L.Text("更新源配置过大。"));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException(L.Text("不支持此更新源配置版本。"));
        var entries = root.GetProperty("sources").EnumerateArray().ToArray();
        if (entries.Length > MaximumSources) throw new InvalidDataException(L.Text("备用源数量超限。"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<UpdateSource>();
        foreach (var item in entries)
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? "";
            var address = item.GetProperty("manifestUrl").GetString() ?? "";
            var hosts = item.GetProperty("allowedHosts").EnumerateArray().Select(h => h.GetString() ?? "").ToArray();
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,39}$") || id == "github" || !ids.Add(id) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl) ||
                hosts.Length is < 1 or > 8 || hosts.Any(h => !IsPublicHost(h)) ||
                hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hosts.Length)
                throw new InvalidDataException(L.Text("更新源名称、标识或域名列表无效。"));
            _ = ReadAuthorizedUri(address, hosts);
            sources.Add(new(id, name, address, hosts.Select(h => h.ToLowerInvariant()).ToArray()));
        }
        return new(1, sources.ToArray());
    }

    /// <summary>静态备用版本清单明确绑定平台、完整包名、长度与摘要，不能重用来源名称猜测 URL。</summary>
    internal static UpdateRelease ParseRelease(string json, UpdateSource source)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        if (item.GetProperty("schemaVersion").GetInt32() != 1 || item.GetProperty("runtime").GetString() != "win-x64")
            throw new InvalidDataException(L.Text("备用源版本清单的平台或格式不受支持。"));
        var version = UpdateFiles.ParseVersion(item.GetProperty("version").GetString() ?? "");
        var name = item.GetProperty("packageName").GetString() ?? "";
        var hash = item.GetProperty("sha256").GetString() ?? "";
        var size = item.GetProperty("packageSize").GetInt64();
        if (name != $"GGEA-v{version}-win-x64.zip" || !IsHash(hash) || size <= 0 || size > UpdatePackage.MaximumPackageBytes)
            throw new InvalidDataException(L.Text("备用源包名、大小或 SHA-256 无效。"));
        var url = ReadAuthorizedUri(item.GetProperty("packageUrl").GetString() ?? "", source.AllowedHosts);
        var notes = item.TryGetProperty("notes", out var body) ? body.GetString() ?? "" : "";
        if (notes.Length > 12000) notes = notes[..12000] + "\n…";
        return new(version, notes, name, url, size, null, hash.ToLowerInvariant())
        {
            SourceId = source.Id, SourceName = source.Name, AllowedHosts = source.AllowedHosts
        };
    }

    /// <summary>清单及附件只能使用当前来源明确授权的域名，不扩大其他来源的访问范围。</summary>
    private static Uri ReadAuthorizedUri(string address, IReadOnlyCollection<string> hosts)
    {
        if (address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            !IsPublicHttps(uri) || !hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(L.Text("更新源地址必须使用其授权域名中的公开 HTTPS 地址。"));
        return uri;
    }
}
