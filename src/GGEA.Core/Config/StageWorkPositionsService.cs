using System.IO;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.Config;

/// <summary>
/// 作品画布位置加载服务：读取 resources/stage_work_positions.json。
/// 文件缺失或损坏时回退为空数据，保证程序仍可启动。
/// </summary>
public sealed class StageWorkPositionsService
{
    private readonly IGgeaLog _log;

    public StageWorkPositionsService(IGgeaLog log)
    {
        _log = log;
    }

    /// <summary>从 JSON 文件加载作品位置映射。</summary>
    public StageWorkPositionsData Load(string path)
    {
        var data = new StageWorkPositionsData();
        try
        {
            if (!File.Exists(path))
            {
                _log.Warn($"作品位置文件不存在: {path}");
                return data;
            }

            string json;
            using (var file = new SharedConfigFile(path)) json = file.Read();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<StageWorkPositionsData>(json, options);
            if (parsed is null)
            {
                return data;
            }

            foreach (var entry in parsed.Positions)
            {
                if (entry.Id <= 0 || entry.CanvasX <= 0 || entry.CanvasY <= 0)
                {
                    _log.Warn($"作品位置条目无效，已跳过: id={entry.Id}");
                    continue;
                }

                data.Positions.Add(entry);
            }

            return data;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取作品位置失败，使用已读取的有效位置（可能为空）: {ex.Message}");
            return data;
        }
    }

    internal StageWorkPositionsData SaveEdits(StageWorkPositionsData original, StageWorkPositionsData edited, string path)
    {
        using var file = new SharedConfigFile(path);
        var latest = JsonSerializer.Deserialize<StageWorkPositionsData>(file.Read(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException();
        foreach (var item in edited.Positions)
        {
            var before = original.Find(item.Id);
            if (before is null || (before.CanvasX == item.CanvasX && before.CanvasY == item.CanvasY)) continue;
            var current = latest.Find(item.Id);
            if (current is null) throw new IOException(GGEA.App.Localization.L.Text("此设置已被其他窗口修改，请重新打开后再保存。"));
            current.CanvasX = MergeCoordinate(before.CanvasX, item.CanvasX, current.CanvasX);
            current.CanvasY = MergeCoordinate(before.CanvasY, item.CanvasY, current.CanvasY);
        }
        file.Write(JsonSerializer.Serialize(latest, new JsonSerializerOptions { WriteIndented = true }));
        return latest;
    }

    private static int MergeCoordinate(int original, int edited, int latest)
    {
        if (original == edited) return latest;
        if (latest == original || latest == edited) return edited;
        throw new IOException(GGEA.App.Localization.L.Text("此设置已被其他窗口修改，请重新打开后再保存。"));
    }

    /// <summary>将作品位置映射写回 JSON 文件（camelCase，缩进格式），目录不存在时自动创建。</summary>
    public void Save(StageWorkPositionsData data, string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        using var file = new SharedConfigFile(path);
        file.Write(JsonSerializer.Serialize(data, options));
    }
}
