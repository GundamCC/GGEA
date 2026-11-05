using System.IO;

namespace GGEA.App.Updates;

/// <summary>Windows 更新偏好使用安装器的原子写入规则，与平台无关配置加载分离。</summary>
public static class UpdatePreferences
{
    /// <summary>更新选项即时保存，只改该字段，避免把设置窗口尚未保存的编辑或旧快照写回磁盘。</summary>
    public static void SaveAutoInstallUpdates(bool enabled, string path)
    {
        var root = File.Exists(path)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidDataException("配置文件不是 JSON 对象，未保存更新选项。")
            : new System.Text.Json.Nodes.JsonObject();
        root["autoInstallUpdates"] = enabled;
        // 共用刷盘后重命名的原子写入，失败时保留原配置；不使用 Load 的默认值回退覆盖损坏文件。
        GGEA.Updater.UpdateFiles.WriteJson(path, root);
    }
}
