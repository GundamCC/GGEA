using GGEA.App.Localization;
using System.IO;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.UI;

/// <summary>
/// 主界面布局加载服务：读取 resources/ui_layout/main.json。
/// 文件缺失或损坏时回退为空布局，保证程序仍可启动。
/// </summary>
public sealed class UiLayoutService
{
    private readonly IGgeaLog _log;

    public UiLayoutService(IGgeaLog log)
    {
        _log = log;
    }

    /// <summary>从 JSON 文件加载布局。</summary>
    public UiLayout Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _log.Warn(L.Format($"主界面布局文件不存在，使用空布局: {path}"));
                return new UiLayout();
            }

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<UiLayout>(json, options) ?? new UiLayout();
        }
        catch (Exception ex)
        {
            _log.Warn(L.Format($"读取主界面布局失败，使用空布局: {ex.Message}"));
            return new UiLayout();
        }
    }
}
