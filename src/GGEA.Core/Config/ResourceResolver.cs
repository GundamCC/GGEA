using System.IO;
using GGEA.App.IO;

namespace GGEA.App.Config;

/// <summary>不可变的资源视图：语言文件覆盖通用文件，目录逐文件合并，外部自定义路径保持原样。</summary>
public sealed class ResourceResolver
{
    private readonly string _baseDirectory;
    private readonly string _root;
    private readonly string _userResources;
    private readonly IReadOnlyDictionary<string, string>? _originalFiles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Size, bool Custom)> _checked = new(FileSystemPaths.Comparer);
    public string Language { get; }

    /// <summary>每个应用会话固定根目录与语言，测试可使用独立目录而不修改全局状态。</summary>
    public ResourceResolver(string baseDirectory, string language, string? contentDirectory = null, IReadOnlyDictionary<string, string>? originalFiles = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _userResources = Path.Combine(_baseDirectory, "resources");
        _root = Path.Combine(contentDirectory ?? _baseDirectory, "resources");
        _originalFiles = originalFiles;
        Language = GameProfiles.Languages.Any(l => l.Id == language) ? language : "zh-CN";
    }

    /// <summary>仅规范化包内相对资源路径；导入 Windows 配置时识别反斜杠，外部自定义路径遵循本机文件系统。</summary>
    private string FullPath(string path)
    {
        var portable = path.Replace('\\', '/');
        if (!Path.IsPathRooted(path) && portable.StartsWith("resources/", FileSystemPaths.Comparison))
            path = portable;
        return Path.GetFullPath(path, _baseDirectory);
    }

    /// <summary>兼容旧 resources 路径及解析后继续拼接的物理路径，不访问其他语言作为回退。</summary>
    private string? RelativeResource(string path)
    {
        var full = FullPath(path);
        var root = full.StartsWith(_root + Path.DirectorySeparatorChar, FileSystemPaths.Comparison) ? _root : _userResources;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, FileSystemPaths.Comparison)) return null;
        var relative = Path.GetRelativePath(root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, 2);
        if (parts[0].Equals("common", FileSystemPaths.Comparison) || GameProfiles.Languages.Any(l => l.Id.Equals(parts[0], FileSystemPaths.Comparison)))
            return parts.Length == 2 ? parts[1] : "";
        return relative;
    }

    // 安装根保留用户采集的模板；旧版出厂图片不能遮住新版替换或删除的图片。
    private bool IsUserAsset(string file)
    {
        if (_root.Equals(_userResources, FileSystemPaths.Comparison) || !File.Exists(file)) return false;
        var name = Path.GetRelativePath(_baseDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
        if (_originalFiles is null) return !File.Exists(Path.Combine(_root, Path.GetRelativePath(_userResources, file)));
        if (!_originalFiles.TryGetValue(name, out var initial)) return true;
        var info = new FileInfo(file);
        if (_checked.TryGetValue(file, out var known) && known.Ticks == info.LastWriteTimeUtc.Ticks && known.Size == info.Length)
            return known.Custom;
        using var stream = File.OpenRead(file);
        var custom = !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(initial, StringComparison.OrdinalIgnoreCase);
        _checked[file] = (info.LastWriteTimeUtc.Ticks, info.Length, custom);
        return custom;
    }

    private string Asset(string category, string relative)
    {
        var custom = Path.Combine(_userResources, category, relative);
        return IsUserAsset(custom) ? custom : Path.Combine(_root, category, relative);
    }

    /// <summary>文件优先取当前语言；目录返回稳定通用根，枚举必须使用 GetFiles 以保留逐文件回退。</summary>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var relative = RelativeResource(path);
        if (relative is null) return FullPath(path);
        // 更新器保留旧版用户维护的作品坐标；不能用新版随包默认值遮蔽这份数据。
        var legacyPositions = Path.Combine(_userResources, "stage_work_positions.json");
        if (relative == "stage_work_positions.json" && File.Exists(legacyPositions)) return legacyPositions;
        var positions = Path.Combine(_userResources, "common", "stage_work_positions.json");
        if (relative == "stage_work_positions.json" && File.Exists(positions)) return positions;
        var localized = Asset(Language, relative);
        return File.Exists(localized) ? localized : Asset("common", relative);
    }

    /// <summary>识图候选按语言、通用顺序返回；通用只补试一次，外部路径不参与语言回退。</summary>
    public IEnumerable<string> TemplateCandidates(string path)
    {
        var primary = Resolve(path);
        yield return primary;
        var relative = RelativeResource(path);
        if (relative is null) yield break;
        var common = Asset("common", relative);
        if (!primary.Equals(common, FileSystemPaths.Comparison) && File.Exists(common))
            yield return common;
    }

    /// <summary>遮罩跟随实际候选；通用模板不能搭配语言遮罩，语言模板允许共用缺省遮罩。</summary>
    public string ResolveTemplateCompanion(string templatePath, string companionPath)
    {
        var relative = RelativeResource(companionPath);
        var commonRoot = Path.Combine(_root, "common") + Path.DirectorySeparatorChar;
        return relative is not null && FullPath(templatePath)
            .StartsWith(commonRoot, FileSystemPaths.Comparison)
            ? Asset("common", relative) : Resolve(companionPath);
    }

    /// <summary>采集只写入当前语言目录，避免覆盖通用素材；自定义目录仍尊重用户指定位置。</summary>
    public string ResolveWritePath(string path)
    {
        var relative = RelativeResource(path);
        return relative is null ? FullPath(path) : Path.Combine(_userResources, Language, relative);
    }

    /// <summary>语言独有目录也属于有效资源目录。</summary>
    public bool DirectoryExists(string path)
    {
        var relative = RelativeResource(path);
        return relative is null ? Directory.Exists(FullPath(path)) :
            Directory.Exists(Path.Combine(_root, Language, relative)) || Directory.Exists(Path.Combine(_root, "common", relative)) ||
            Directory.Exists(Path.Combine(_userResources, Language, relative)) || Directory.Exists(Path.Combine(_userResources, "common", relative));
    }

    /// <summary>按相对子路径去重，同名文件只保留语言版本；禁止以整目录覆盖丢失通用素材。</summary>
    public string[] GetFiles(string path, string pattern, SearchOption search = SearchOption.TopDirectoryOnly)
    {
        var relative = RelativeResource(path);
        if (relative is null) return Directory.GetFiles(FullPath(path), pattern, search);
        var files = new Dictionary<string, string>(FileSystemPaths.Comparer);
        foreach (var category in new[] { "common", Language })
        {
            var directory = Path.Combine(_root, category, relative);
            if (Directory.Exists(directory))
                foreach (var file in Directory.GetFiles(directory, pattern, search))
                    files[Path.GetRelativePath(directory, file)] = file;
            var custom = Path.Combine(_userResources, category, relative);
            if (!custom.Equals(directory, FileSystemPaths.Comparison) && Directory.Exists(custom))
                foreach (var file in Directory.GetFiles(custom, pattern, search).Where(IsUserAsset))
                    files[Path.GetRelativePath(custom, file)] = file;
        }
        return files.OrderBy(p => p.Key, FileSystemPaths.Comparer).Select(p => p.Value).ToArray();
    }
}
