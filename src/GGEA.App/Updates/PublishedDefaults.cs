using System.IO;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>保留已核验的出厂默认值。用户修改设置后仍能在本机组装完整的待安装包。</summary>
internal static class PublishedDefaults
{
    internal static string CacheRoot(string installation) =>
        UpdateFiles.SafePath(installation, UpdateFiles.StateDirectory + "/defaults");

    internal static void Remember(string installation, string source)
    {
        try
        {
            if (!File.Exists(UpdateFiles.SafePath(source, "build-info.json"))) return;
            var manifest = UpdateManifest.Read(source);
            var cache = CacheRoot(installation);
            foreach (var (name, hash) in manifest.Files)
            {
                if (!UpdateFiles.IsUserFile(name)) continue;
                var original = UpdateFiles.SafePath(source, name);
                var saved = UpdateFiles.SafePath(cache, name);
                if (File.Exists(saved) && UpdateFiles.Hash(saved).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(original)) continue;
                using var snapshot = File.OpenRead(original);
                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshot)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                UpdateFiles.CopyAtomically(original, saved, installation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // 缓存不可用仍可下载已认证的默认文件；不会把缓存当作版本或完整性的依据。
        }
    }
}
