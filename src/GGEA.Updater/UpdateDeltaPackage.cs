using System.Text.Json;

namespace GGEA.Updater;

/// <summary>仅表示本机基线不可复用；网络包损坏、路径错误不能伪装成此异常。</summary>
public sealed class UpdateBaseUnavailableException(string message) : IOException(message);

/// <summary>差异包协议只影响下载准备，不改变更新器的完整包校验、用户文件保护与恢复协议。</summary>
public static class UpdateDeltaPackage
{
    public const string MetadataName = "delta-info.json";

    internal static void Complete(string package, string installation, string baseVersion, UpdateManifest next,
        HashSet<string> paths, long expandedBytes, CancellationToken cancellationToken)
    {
        var metadataPath = UpdateFiles.SafePath(package, MetadataName);
        if (!paths.Contains(MetadataName) || new FileInfo(metadataPath).Length > 4096)
            throw new InvalidDataException("差异包元数据缺失或过大。");
        using var json = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var metadata = json.RootElement;
        var baseHash = metadata.GetProperty("baseManifestSha256").GetString() ?? "";
        if (metadata.GetProperty("schemaVersion").GetInt32() != 1 ||
            metadata.GetProperty("baseVersion").GetString() != baseVersion ||
            UpdateFiles.ParseVersion(baseVersion) >= UpdateFiles.ParseVersion(next.Version) ||
            !System.Text.RegularExpressions.Regex.IsMatch(baseHash, "^[a-f0-9]{64}$") ||
            next.Files.ContainsKey(MetadataName) ||
            paths.Except(next.Files.Keys.Append("build-info.json").Append(MetadataName), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("差异包身份或文件集合无效。");

        // 先校验所有下载文件，即使本机基线损坏，也不能以回退掩盖远程内容校验失败。
        foreach (var (relative, hash) in next.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.Contains(relative) && !UpdateFiles.Hash(UpdateFiles.SafePath(package, relative)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"差异包文件校验失败：{relative}");
            // 用户默认文件必须随包提供，不能把用户修改的配置放入正式暂存包。
            if (UpdateFiles.IsUserFile(relative) && !paths.Contains(relative))
                throw new InvalidDataException("差异包缺少用户文件默认值。");
        }

        var basePath = UpdateFiles.SafePath(installation, "build-info.json");
        if (!File.Exists(basePath) || !UpdateFiles.Hash(basePath).Equals(baseHash, StringComparison.OrdinalIgnoreCase))
            throw new UpdateBaseUnavailableException("本机发布清单与差异包基线不一致。");
        var previous = UpdateManifest.Read(installation);
        if (previous.Version != baseVersion) throw new UpdateBaseUnavailableException("本机版本与差异包基线不一致。");
        foreach (var (relative, hash) in next.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.Contains(relative)) continue;
            if (!previous.Files.TryGetValue(relative, out var oldHash) || !oldHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("差异包遗漏了新增或变化的文件。");
            var source = UpdateFiles.SafePath(installation, relative);
            if (!File.Exists(source) || !UpdateFiles.Hash(source).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new UpdateBaseUnavailableException($"本机文件无法复用：{relative}");
            expandedBytes += new FileInfo(source).Length;
            if (expandedBytes > UpdatePackage.MaximumExpandedBytes) throw new InvalidDataException("更新暂存大小超限。");
            var destination = UpdateFiles.SafePath(package, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, false);
            if (!UpdateFiles.Hash(destination).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new UpdateBaseUnavailableException($"复制期间本机文件发生变化：{relative}");
        }
        File.Delete(metadataPath);
    }
}
