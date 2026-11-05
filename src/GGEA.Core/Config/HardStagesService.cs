using System.IO;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.Config;

/// <summary>
/// Hard 关卡数据加载服务：读取 resources/hard_stages.json。
/// 非法条目（Hard 数量不在 1~5、支线数为负数、缺少作品名或截图）会被过滤并记录警告。
/// </summary>
public sealed class HardStagesService
{
    private readonly IGgeaLog _log;

    public HardStagesService(IGgeaLog log)
    {
        _log = log;
    }

    public HardStagesData Load(string path)
    {
        var data = new HardStagesData();
        try
        {
            if (!File.Exists(path))
            {
                _log.Warn($"Hard 关卡数据文件不存在: {path}");
                return data;
            }

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<HardStagesData>(json, options);
            if (parsed is null)
            {
                return data;
            }

            foreach (var entry in parsed.HardStages)
            {
                if (string.IsNullOrWhiteSpace(entry.Work) ||
                    string.IsNullOrWhiteSpace(entry.Screenshot) ||
                    entry.SideStories < 0 ||
                    entry.HardCount is < 1 or > 5)
                {
                    _log.Warn($"Hard 关卡条目无效，已跳过: {entry.Work}");
                    continue;
                }

                data.HardStages.Add(entry);
            }

            return data;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 Hard 关卡数据失败，使用已读取的有效条目（可能为空）: {ex.Message}");
            return data;
        }
    }
}
