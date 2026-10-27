using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GGEA.Updater;

/// <summary>读取现有正式发布清单，不另建版本来源或依赖可变的文件名猜测安装身份。</summary>
public sealed record UpdateManifest(string Version, string Commit, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>本地普通包没有正式清单；只读取版本，不猜测旧文件归属，升级时保留未知文件。</summary>
    public static UpdateManifest ReadInstallation(string root)
    {
        if (File.Exists(UpdateFiles.SafePath(root, "build-info.json"))) return Read(root);
        var path = UpdateFiles.SafePath(root, "local-build-info.json");
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("本地构建信息过大。");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var version = json.RootElement.GetProperty("version").GetString() ?? "";
        _ = UpdateFiles.ParseVersion(version);
        return new UpdateManifest(version, "", new Dictionary<string, string>());
    }

    /// <summary>清单大小和条目数有界；Windows 不区分大小写，重复路径一律拒绝。</summary>
    public static UpdateManifest Read(string root)
    {
        var path = UpdateFiles.SafePath(root, "build-info.json");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("发布清单过大。");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var item = json.RootElement;
        var version = item.GetProperty("version").GetString() ?? "";
        _ = UpdateFiles.ParseVersion(version);
        var commit = item.GetProperty("commit").GetString() ?? "";
        if (item.GetProperty("schemaVersion").GetInt32() != 1 ||
            item.GetProperty("channel").GetString() != "Release" ||
            item.GetProperty("runtime").GetString() != "win-x64" ||
            item.GetProperty("configuration").GetString() != "Release" ||
            !item.GetProperty("selfContained").GetBoolean() || !item.GetProperty("singleFile").GetBoolean() ||
            !Regex.IsMatch(commit, "^[a-f0-9]{40}$"))
            throw new InvalidDataException("仅支持正式 win-x64 自包含发布包。");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in item.GetProperty("files").EnumerateObject())
        {
            UpdateFiles.ValidateManagedPath(file.Name);
            var hash = file.Value.GetString() ?? "";
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || !files.TryAdd(file.Name, hash) || files.Count > 20000)
                throw new InvalidDataException("发布文件摘要或路径集合无效。");
        }
        if (!files.ContainsKey("GGEA.exe")) throw new InvalidDataException("发布包缺少主程序。");
        return new UpdateManifest(version, commit, files);
    }

    /// <summary>下载后验证完整默认包；安装后仅跳过明确属于用户的可变数据。</summary>
    public void Verify(string root, bool preserveUserFiles = false, CancellationToken cancellationToken = default)
    {
        foreach (var (relative, hash) in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = UpdateFiles.SafePath(root, relative);
            if (!File.Exists(path)) throw new InvalidDataException($"发布文件缺失：{relative}");
            if (preserveUserFiles && UpdateFiles.IsUserFile(relative)) continue;
            if (!UpdateFiles.Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"发布文件校验失败：{relative}");
        }
    }
}

/// <summary>完整 ZIP 的安全展开与逐文件验证；网络下载和安装都不能绕过此边界。</summary>
public static class UpdatePackage
{
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>只能展开到尚不存在的独立目录，且 ZIP 必须采用正式发布脚本的一层根目录。</summary>
    public static UpdateManifest Extract(string archive, string destination, string version, CancellationToken cancellationToken = default)
    {
        _ = UpdateFiles.ParseVersion(version);
        UpdateFiles.RejectLinks(destination);
        if (Directory.Exists(destination)) throw new IOException("更新暂存目录已经存在。");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 25000) throw new InvalidDataException("更新包文件数量超限。");
        var baseName = $"GGEA-v{version}-win-x64";
        string? rootName = null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // PowerShell Compress-Archive 的 Windows ZIP 可能使用反斜线；只在 ZIP 边界统一分隔符。
            var entryName = entry.FullName.Replace('\\', '/');
            rootName ??= entryName.Split('/')[0];
            if (!Regex.IsMatch(rootName, "^" + Regex.Escape(baseName) + "(?:-[a-f0-9]{12})?$"))
                throw new InvalidDataException("更新包根目录与版本不一致。");
            var prefix = rootName + "/";
            if (entryName == prefix) continue;
            if (!entryName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("更新包包含多个根目录。");
            var relative = entryName[prefix.Length..];
            var directory = relative.EndsWith('/');
            if (directory) relative = relative[..^1];
            var target = UpdateFiles.SafePath(destination, relative);
            // ZIP 的 Unix 文件类型与 Windows reparse 属性均不可用于创建链接。
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("更新包不能包含链接。");
            if (directory) { Directory.CreateDirectory(target); continue; }
            if (!paths.Add(relative) || entry.Length < 0 || (expandedBytes += entry.Length) > MaximumExpandedBytes)
                throw new InvalidDataException("更新包包含重复文件或解压大小超限。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += count;
                if (written > entry.Length) throw new InvalidDataException("更新包条目大小异常。");
                output.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("更新包条目不完整。");
        }
        var manifest = UpdateManifest.Read(destination);
        if (manifest.Version != version || !manifest.Files.ContainsKey("GGEA.Updater.exe") ||
            (rootName != baseName && rootName != baseName + "-" + manifest.Commit[..12]) ||
            !paths.SetEquals(manifest.Files.Keys.Append("build-info.json")))
            throw new InvalidDataException("更新包身份或文件集合不完整。");
        manifest.Verify(destination, cancellationToken: cancellationToken);
        return manifest;
    }
}
