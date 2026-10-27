using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using GGEA.Plugin.Abstractions;
using GGEA.Updater;

namespace GGEA.App.Plugins;

/// <summary>每个插件隔离托管依赖，唯一共享宿主协议；不支持热卸载，不将隔离误称为权限沙箱。</summary>
internal sealed class PluginLoadContext(string entryPath) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryPath);
    private readonly string _root = Path.GetDirectoryName(entryPath)!;

    protected override Assembly? Load(AssemblyName name)
    {
        var contract = typeof(IGgeaPlugin).Assembly;
        if (name.Name == contract.GetName().Name)
        {
            if (name.Version != contract.GetName().Version) throw new FileLoadException("插件协议程序集版本不兼容。");
            return contract;
        }
        if (name.Name is "GGEA" or "GGEA.Updater")
            throw new FileLoadException("插件只能引用公开协议，不能引用宿主内部程序集。");
        if (name.Name?.StartsWith("GGEA", StringComparison.OrdinalIgnoreCase) == true)
        {
            // 插件不能引用宿主内部实现；自身以 GGEA 命名的依赖仍必须从包内解析。
            var local = _resolver.ResolveAssemblyToPath(name);
            if (local is null) throw new FileLoadException("插件依赖必须随包分发，不能引用宿主内部程序集。");
            return LoadManaged(local);
        }
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadManaged(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(CheckPath(path));
    }

    /// <summary>依赖只能从当前插件目录加载，防止 deps.json 将解析目标指向包外。</summary>
    private string CheckPath(string path)
    {
        var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
        return UpdateFiles.SafePath(_root, relative);
    }

    /// <summary>托管程序集读取为快照，不长期锁住安装文件；运行实例仍只在重启后替换。</summary>
    internal Assembly LoadManaged(string path)
    {
        using var stream = File.OpenRead(CheckPath(path));
        return LoadFromStream(stream);
    }
}
