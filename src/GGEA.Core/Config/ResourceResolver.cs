using System.IO;

namespace GGEA.App.Config;

/// <summary>不可变的资源视图：语言文件覆盖通用文件，目录逐文件合并，外部自定义路径保持原样。</summary>
public sealed class ResourceResolver
{
    private readonly string _baseDirectory;
    private readonly string _root;
    public string Language { get; }

    /// <summary>每个应用会话固定根目录与语言，测试可使用独立目录而不修改全局状态。</summary>
    public ResourceResolver(string baseDirectory, string language)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _root = Path.Combine(_baseDirectory, "resources");
        Language = GameProfiles.Languages.Any(l => l.Id == language) ? language : "zh-CN";
    }

    /// <summary>兼容旧 resources 路径及解析后继续拼接的物理路径，不访问其他语言作为回退。</summary>
    private string? RelativeResource(string path)
    {
        var full = Path.GetFullPath(path, _baseDirectory);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = Path.GetRelativePath(_root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, 2);
        if (parts[0].Equals("common", StringComparison.OrdinalIgnoreCase) || GameProfiles.Languages.Any(l => l.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)))
            return parts.Length == 2 ? parts[1] : "";
        return relative;
    }

    /// <summary>文件优先取当前语言；目录返回稳定通用根，枚举必须使用 GetFiles 以保留逐文件回退。</summary>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var relative = RelativeResource(path);
        if (relative is null) return Path.GetFullPath(path, _baseDirectory);
        // 更新器保留旧版用户维护的作品坐标；不能用新版随包默认值遮蔽这份数据。
        var legacyPositions = Path.Combine(_root, "stage_work_positions.json");
        if (relative == "stage_work_positions.json" && File.Exists(legacyPositions)) return legacyPositions;
        var localized = Path.Combine(_root, Language, relative);
        return File.Exists(localized) ? localized : Path.Combine(_root, "common", relative);
    }

    /// <summary>识图候选按语言、通用顺序返回；通用只补试一次，外部路径不参与语言回退。</summary>
    public IEnumerable<string> TemplateCandidates(string path)
    {
        var primary = Resolve(path);
        yield return primary;
        var relative = RelativeResource(path);
        if (relative is null) yield break;
        var common = Path.Combine(_root, "common", relative);
        if (!primary.Equals(common, StringComparison.OrdinalIgnoreCase) && File.Exists(common))
            yield return common;
    }

    /// <summary>遮罩跟随实际候选；通用模板不能搭配语言遮罩，语言模板允许共用缺省遮罩。</summary>
    public string ResolveTemplateCompanion(string templatePath, string companionPath)
    {
        var relative = RelativeResource(companionPath);
        var commonRoot = Path.Combine(_root, "common") + Path.DirectorySeparatorChar;
        return relative is not null && Path.GetFullPath(templatePath, _baseDirectory)
            .StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(_root, "common", relative) : Resolve(companionPath);
    }

    /// <summary>采集只写入当前语言目录，避免覆盖通用素材；自定义目录仍尊重用户指定位置。</summary>
    public string ResolveWritePath(string path)
    {
        var relative = RelativeResource(path);
        return relative is null ? Path.GetFullPath(path, _baseDirectory) : Path.Combine(_root, Language, relative);
    }

    /// <summary>语言独有目录也属于有效资源目录。</summary>
    public bool DirectoryExists(string path)
    {
        var relative = RelativeResource(path);
        return relative is null ? Directory.Exists(Path.GetFullPath(path, _baseDirectory)) :
            Directory.Exists(Path.Combine(_root, Language, relative)) || Directory.Exists(Path.Combine(_root, "common", relative));
    }

    /// <summary>按相对子路径去重，同名文件只保留语言版本；禁止以整目录覆盖丢失通用素材。</summary>
    public string[] GetFiles(string path, string pattern, SearchOption search = SearchOption.TopDirectoryOnly)
    {
        var relative = RelativeResource(path);
        if (relative is null) return Directory.GetFiles(Path.GetFullPath(path, _baseDirectory), pattern, search);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in new[] { "common", Language })
        {
            var directory = Path.Combine(_root, category, relative);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.GetFiles(directory, pattern, search))
                files[Path.GetRelativePath(directory, file)] = file;
        }
        return files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value).ToArray();
    }
}
