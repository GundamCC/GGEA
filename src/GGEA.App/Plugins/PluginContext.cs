using System.IO;
using System.Text.Json.Nodes;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.Plugin.Abstractions;
using GGEA.Updater;

namespace GGEA.App.Plugins;

/// <summary>插件配置按标识隔离并原子落盘；不读取或修改主程序业务配置。</summary>
internal sealed class PluginContext(string directory, string settingsPath, string language, string id, IGgeaLog log) : IPluginContext
{
    public string PluginDirectory => directory;
    public string UiLanguage => language;
    private JsonNode? _baseline;
    private bool _hasBaseline;

    public string? ReadSettings()
    {
        using var file = new SharedConfigFile(settingsPath);
        UpdateFiles.RejectLinks(settingsPath);
        var json = file.Exists ? file.Read() : null;
        _baseline = json is null ? null : JsonNode.Parse(json);
        _hasBaseline = true;
        return json;
    }

    public void WriteSettings(string json)
    {
        var edited = JsonNode.Parse(json);
        using var file = new SharedConfigFile(settingsPath);
        UpdateFiles.RejectLinks(settingsPath);
        var latest = file.Exists ? JsonNode.Parse(file.Read()) : null;
        JsonNode? merged = edited;
        if (_hasBaseline && JsonNode.DeepEquals(edited, _baseline)) return;
        if (edited is JsonObject updated && latest is JsonObject current && (_baseline is null || _baseline is JsonObject))
        {
            JsonSettingChanges.Apply(_baseline as JsonObject ?? new JsonObject(), updated, current);
            merged = current;
        }
        else if (!JsonNode.DeepEquals(latest, _baseline) && !JsonNode.DeepEquals(latest, edited))
            throw new IOException(GGEA.App.Localization.L.Text("此设置已被其他窗口修改，请重新打开后再保存。"));
        UpdateFiles.WriteJson(settingsPath, merged);
        _baseline = edited;
        _hasBaseline = true;
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
