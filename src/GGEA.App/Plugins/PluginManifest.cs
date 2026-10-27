using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GGEA.Updater;

namespace GGEA.App.Plugins;

/// <summary>插件声明。读取和兼容性检查不加载程序集，未启用的插件不执行任何代码。</summary>
public sealed record PluginManifest
{
    public int ApiVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string MinimumHostVersion { get; init; } = "";
    public string Assembly { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>限定单层包目录和入口文件，拒绝目录穿越、链接及未知协议版本。</summary>
    public static PluginManifest Read(string directory, Version hostVersion)
    {
        UpdateFiles.RejectLinks(directory);
        var path = UpdateFiles.SafePath(directory, "plugin.json");
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("插件清单过大。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), UpdateFiles.JsonOptions)
            ?? throw new InvalidDataException("插件清单为空。");
        if (!Regex.IsMatch(manifest.Id, @"\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\z") || manifest.Id.Length > 80 ||
            !string.Equals(Path.GetFileName(directory), manifest.Id, StringComparison.Ordinal))
            throw new InvalidDataException("插件标识必须与目录名一致。");
        if (manifest.ApiVersion != 1) throw new InvalidDataException("插件 API 版本不兼容。");
        if (!System.Version.TryParse(manifest.Version, out _) ||
            !System.Version.TryParse(manifest.MinimumHostVersion, out var minimum) || hostVersion < minimum)
            throw new InvalidDataException("插件版本无效或需要更新宿主。");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 ||
            string.IsNullOrWhiteSpace(manifest.EntryPoint) || manifest.EntryPoint.Length > 256 || manifest.Description.Length > 2000)
            throw new InvalidDataException("插件名称或入口声明无效。");
        if (manifest.Assembly.Contains('/') || manifest.Assembly.Contains('\\') ||
            !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(UpdateFiles.SafePath(directory, manifest.Assembly)))
            throw new InvalidDataException("插件入口 DLL 不存在或路径无效。");
        return manifest;
    }
}
