using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GGEA.ReleaseManager;

/// <summary>仅保存维护者的本机路径。凭证不属于配置模型，未知字段拒绝读取，避免静默丢弃旧数据。</summary>
public sealed record ReleaseManagerSettings
{
    public int SchemaVersion { get; init; } = 1;
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

    /// <summary>配置仅接受本机目录路径，拒绝 URL 与不支持的版本。</summary>
    private static void Validate(ReleaseManagerSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException("不支持的本机配置版本。");
        foreach (string path in new[] { settings.SourceRepository })
            if (path is null || (path.Length > 0 && (!Path.IsPathFullyQualified(path) || path.Contains("://"))))
                throw new InvalidDataException("仓库必须是绝对目录路径。");
    }
}
