using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GGEA.App.Logging;

namespace GGEA.App.Config;

/// <summary>AP 消耗配置文件的根对象，保存多个命名方案及最后选择项。</summary>
public sealed class ApConsumptionProfilesData
{
    [JsonPropertyName("lastSelectedProfile")]
    public string LastSelectedProfile { get; set; } = string.Empty;

    [JsonPropertyName("profiles")]
    public List<ApConsumptionProfile> Profiles { get; set; } = new();
}

/// <summary>一个可命名的 AP 消耗方案，作品列表顺序就是执行顺序。</summary>
public sealed class ApConsumptionProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("works")]
    public List<ApConsumptionProfileWork> Works { get; set; } = new();
}

/// <summary>方案中的作品及已勾选 Hard 关卡编号。</summary>
public sealed class ApConsumptionProfileWork
{
    [JsonPropertyName("workId")]
    public int WorkId { get; set; }

    [JsonPropertyName("hardStages")]
    public List<int> HardStages { get; set; } = new();
}

/// <summary>读取、更新和保存 AP 消耗命名配置。</summary>
public sealed class ApConsumptionProfileService
{
    private readonly IGgeaLog _log;

    public ApConsumptionProfileService(IGgeaLog log)
    {
        _log = log;
    }

    /// <summary>加载配置；文件不存在或损坏时返回空集合。</summary>
    public ApConsumptionProfilesData Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ApConsumptionProfilesData();
            }

            string json;
            using (var file = new SharedConfigFile(path)) json = file.Read();
            return JsonSerializer.Deserialize<ApConsumptionProfilesData>(json)
                ?? new ApConsumptionProfilesData();
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 AP 消耗配置失败，使用空方案列表: {ex.Message}");
            return new ApConsumptionProfilesData();
        }
    }

    /// <summary>按名称覆盖或追加方案，并将其设为最后选择项。</summary>
    public void Upsert(ApConsumptionProfilesData data, ApConsumptionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(profile);
        var index = data.Profiles.FindIndex(item =>
            string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            data.Profiles[index] = profile;
        }
        else
        {
            data.Profiles.Add(profile);
        }

        data.LastSelectedProfile = profile.Name;
    }

    // 操作以方案名称为单位，在文件锁内读取最新列表。切换选中项不会改写方案内容。
    internal ApConsumptionProfilesData Edit(string path, Action<ApConsumptionProfilesData> change)
    {
        using var file = new SharedConfigFile(path);
        var data = file.Exists ? JsonSerializer.Deserialize<ApConsumptionProfilesData>(file.Read())
            ?? throw new InvalidDataException("AP 配置为空。") : new ApConsumptionProfilesData();
        change(data);
        file.Write(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return data;
    }

    internal static ApConsumptionProfile? Find(ApConsumptionProfilesData data, string name)
        => data.Profiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static void CheckProfile(ApConsumptionProfile? original, ApConsumptionProfile? current)
    {
        if (JsonSerializer.Serialize(original) != JsonSerializer.Serialize(current))
            throw new IOException(GGEA.App.Localization.L.Text("此设置已被其他窗口修改，请重新打开后再保存。"));
    }

    /// <summary>以缩进 JSON 保存全部方案，目录不存在时自动创建。</summary>
    public void Save(ApConsumptionProfilesData data, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        using var file = new SharedConfigFile(path);
        file.Write(JsonSerializer.Serialize(data, options));
        _log.Info($"AP 消耗配置已保存: {path}");
    }
}
