using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GGEA.ReleaseManager;

/// <summary>仅保存维护者的本机路径。凭证不属于配置模型，未知字段拒绝读取，避免静默丢弃旧数据。</summary>
public sealed record ReleaseManagerSettings
{
    public int SchemaVersion { get; init; } = 2;
    public string SourceRepository { get; init; } = "";
}

/// <summary>统一在 LocalApplicationData 保存；原子替换失败时保留旧文件，不回退到源码或 EXE 目录。</summary>
public sealed class ReleaseManagerSettingsStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GGEA", "ReleaseManager");
    public string FilePath { get; }
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ReleaseManagerSettingsStore(string? directory = null) => FilePath = Path.Combine(directory ?? DefaultDirectory, "settings.json");

    /// <summary>首次运行使用现有路径探测；损坏或未来版本必须报告，不能自动覆盖。</summary>
    public ReleaseManagerSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<ReleaseManagerSettings>(File.ReadAllText(FilePath), Options)
            ?? throw new InvalidDataException("本机配置为空。");
        Validate(settings);
        return settings;
    }

    /// <summary>临时文件与目标同目录，失败时不留下残缺 settings.json。</summary>
    public void Save(ReleaseManagerSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>相对路径始终以 EXE 目录为基准，不受快捷方式或子进程工作目录影响。</summary>
    public static string ResolveSource(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("源码仓库路径不能为空。");
        Validate(new() { SourceRepository = path });
        return Path.GetFullPath(path, baseDirectory);
    }

    /// <summary>同盘保存相对路径，跨盘保留绝对路径，避免生成无法解析的盘符相对路径。</summary>
    public static string StoreSource(string path, string baseDirectory) =>
        Path.GetRelativePath(baseDirectory, ResolveSource(path, baseDirectory));

    /// <summary>旧绝对配置优先迁移到工具所在仓库；已经保存的相对选择保持用户意图。</summary>
    public static string InitialSource(string saved, string? discovered, string baseDirectory, bool migrateAbsolute = true)
    {
        string selected = string.IsNullOrEmpty(saved) || (migrateAbsolute && Path.IsPathFullyQualified(saved))
            ? discovered ?? saved : saved;
        return string.IsNullOrEmpty(selected) ? string.Empty : StoreSource(selected, baseDirectory);
    }

    /// <summary>允许明确的相对路径和绝对路径，拒绝 URL、盘符相对路径与不支持的版本。</summary>
    private static void Validate(ReleaseManagerSettings settings)
    {
        if (settings.SchemaVersion is not (1 or 2)) throw new InvalidDataException("不支持的本机配置版本。");
        foreach (string path in new[] { settings.SourceRepository })
            if (path is null || (path.Length > 0 && (path.Contains("://") || (!Path.IsPathFullyQualified(path) && (Path.IsPathRooted(path) || path.Contains(':'))))))
                throw new InvalidDataException("仓库必须是本机绝对路径或相对路径。");
    }
}
