using GGEA.App.Localization;
using System.IO;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>远程缓存与用户导入分开保存；离线或远程配置损坏时仍保留上次可用来源。</summary>
public sealed class UpdateSourceStore(string installation, Action<string>? report = null)
{
    public const string ImportFile = "config/update-sources.json";
    public const string CacheFile = ".ggea-update/sources-cache.json";
    // 此入口固定在本版本中；以后修改公开分发仓库的文件即可让已安装客户端学习新来源。
    public const string DiscoveryUrl = "https://raw.githubusercontent.com/GundamCC/GGEA-Release/main/update-sources.json";

    /// <summary>本地明确导入的同 ID 来源优先，剩余位置按远程配置顺序填充。</summary>
    public UpdateSource[] Load()
    {
        var manual = ReadOrEmpty(ImportFile);
        var cached = ReadOrEmpty(CacheFile);
        return manual.Sources.Concat(cached.Sources).DistinctBy(s => s.Id).Take(UpdateSourceCatalog.MaximumSources).ToArray();
    }

    /// <summary>只在整份配置验证通过后原子写入；未知格式不清空已有缓存。</summary>
    public void SaveCache(string json)
    {
        var catalog = UpdateSourceCatalog.Parse(json);
        UpdateFiles.WriteJson(UpdateFiles.SafePath(installation, CacheFile), catalog);
    }

    /// <summary>手动导入仅替换用户列表，不把它当作程序包或立即开始下载。</summary>
    public void Import(string json)
    {
        var catalog = UpdateSourceCatalog.Parse(json);
        UpdateFiles.WriteJson(UpdateFiles.SafePath(installation, ImportFile), catalog);
    }

    /// <summary>读取上限先于分配内容；损坏配置报告 WARN，允许继续使用其他来源。</summary>
    private UpdateSourceCatalog ReadOrEmpty(string relative)
    {
        try
        {
            var path = UpdateFiles.SafePath(installation, relative);
            if (!File.Exists(path)) return UpdateSourceCatalog.Empty;
            if (new FileInfo(path).Length > UpdateSourceCatalog.MaximumBytes) throw new InvalidDataException(L.Text("配置过大。"));
            return UpdateSourceCatalog.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                  or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            report?.Invoke(L.Format($"更新源配置不可用（{relative}，{ex.GetType().Name}），已保留文件并尝试其他来源。"));
            return UpdateSourceCatalog.Empty;
        }
    }
}
