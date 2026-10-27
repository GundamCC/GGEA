using System.IO;
using System.Text.Json;
using GGEA.App.Logging;
using GGEA.Plugin.Abstractions;
using GGEA.Updater;

namespace GGEA.App.Plugins;

/// <summary>插件配置按标识隔离并原子落盘；不读取或修改主程序业务配置。</summary>
internal sealed class PluginContext(string directory, string settingsPath, string language, string id, IGgeaLog log) : IPluginContext
{
    public string PluginDirectory => directory;
    public string UiLanguage => language;
    public string? ReadSettings()
    {
        UpdateFiles.RejectLinks(settingsPath);
        if (!File.Exists(settingsPath)) return null;
        var json = File.ReadAllText(settingsPath);
        using var parsed = JsonDocument.Parse(json);
        return json;
    }
    public void WriteSettings(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        UpdateFiles.WriteJson(settingsPath, parsed.RootElement);
    }
    public void Log(PluginLogLevel level, string message)
    {
        var text = $"[插件 {id}] {message}";
        switch (level)
        {
            case PluginLogLevel.Debug: log.Debug(text); break;
            case PluginLogLevel.Info: log.Info(text); break;
            case PluginLogLevel.Warning: log.Warn(text); break;
            case PluginLogLevel.Error: log.Error(text); break;
            default: throw new ArgumentOutOfRangeException(nameof(level));
        }
    }
}
