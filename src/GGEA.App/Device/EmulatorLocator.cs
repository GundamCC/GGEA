using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace GGEA.App.Device;

/// <summary>一次模拟器安装中相互配套的 ADB 客户端与启动程序。</summary>
public sealed record EmulatorInstallation(string RootPath, string AdbPath, string LauncherPath);

/// <summary>
/// 模拟器安装位置探测。
/// 已知模拟器优先从注册表读取安装目录，再检查官方默认目录；探测失败时仍允许用户手动选择。
/// </summary>
public static class EmulatorLocator
{
    /// <summary>优先使用用户选定的管理器或配套 ADB 附近的管理器，兼容非默认安装目录。</summary>
    public static string? ResolveMuMuManager(string adbPath, string launcherPath)
    {
        if (string.Equals(Path.GetFileName(launcherPath), "MuMuManager.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(launcherPath))
            return launcherPath;
        return AdbDiscovery.FindLauncher(launcherPath, EmulatorTypes.MuMu)
            ?? AdbDiscovery.FindLauncher(adbPath, EmulatorTypes.MuMu) ?? FindMuMuManager();
    }

    /// <summary>
    /// 查找 MuMu 管理器程序 MuMuManager.exe 的完整路径；找不到返回 null。
    /// </summary>
    public static string? FindMuMuManager()
    {
        // 外置通用 ADB 与便携安装不一定有卸载信息；运行路径仅帮助定位管理器，不能证明设备品牌。
        var running = FindMuMuManagerNearRunningProcesses();
        if (running is not null) return running;
        // 1. 注册表卸载项（本机安装路径以 MuMu 命名的软件）
        var registryRoots = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach (var (hive, view) in registryRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    var displayName = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName) ||
                        !displayName.Contains("MuMu", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var installLocation = key?.GetValue("InstallLocation") as string;
                    if (installLocation is { Length: > 0 } && Directory.Exists(installLocation))
                    {
                        var found = FindMuMuManagerUnderRoot(installLocation);
                        if (found is not null)
                        {
                            return found;
                        }
                    }
                }
            }
            catch
            {
                // 注册表被占用或权限不足时继续尝试其他来源
            }
        }

        // 2. 常见安装目录（通用路径，不含本机私有配置）
        var commonRoots = new[]
        {
            @"C:\Program Files\Netease\MuMu Player 12",
            @"C:\Program Files\Netease\MuMuPlayer-12.0",
            @"C:\Program Files\MuMu",
            @"D:\Program Files\Netease\MuMu Player 12",
            @"D:\Program Files\Netease\MuMuPlayer-12.0",
            @"D:\Program Files\MuMu",
        };

        foreach (var root in commonRoots)
        {
            if (Directory.Exists(root))
            {
                var found = FindMuMuManagerUnderRoot(root);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>只检查已知 MuMu 进程的相邻目录，不扫描磁盘、不启动候选播放器。</summary>
    private static string? FindMuMuManagerNearRunningProcesses()
    {
        foreach (var name in new[] { "MuMuPlayer", "MuMuNxDevice", "MuMuMultiPlayer", "MuMuManager" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is not null && AdbDiscovery.FindLauncher(path, EmulatorTypes.MuMu) is { } manager)
                            return manager;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    { /* 进程退出或路径无权读取时继续注册表及其它来源。 */ }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return null;
    }

    /// <summary>
    /// 查找 BlueStacks 5 安装；只有同一安装目录中同时存在 HD-Adb.exe 与 HD-Player.exe 才返回，
    /// 防止把残留文件或不完整安装当成可用运行环境。
    /// </summary>
    public static EmulatorInstallation? FindBlueStacksInstallation()
    {
        foreach (var root in EnumerateBlueStacksRegistryRoots())
        {
            var installation = FindBlueStacksUnderRoot(root);
            if (installation is not null)
            {
                return installation;
            }
        }

        var commonRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BlueStacks_nxt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BlueStacks_nxt"),
        };

        foreach (var root in commonRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var installation = FindBlueStacksUnderRoot(root);
            if (installation is not null)
            {
                return installation;
            }
        }

        return null;
    }

    /// <summary>
    /// 从用户已选择的 ADB 或启动程序附近补全 BlueStacks 安装信息。
    /// BlueStacks 的两个程序通常位于同一目录，因此无需扫描其它磁盘。
    /// </summary>
    public static EmulatorInstallation? FindBlueStacksNearPaths(string? adbPath, string? launcherPath)
    {
        foreach (var path in new[] { adbPath, launcherPath })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                continue;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var installation = FindBlueStacksUnderRoot(directory);
                if (installation is not null)
                {
                    return installation;
                }
            }
        }

        return null;
    }

    /// <summary>在指定目录的有限深度内查找配套的 BlueStacks ADB 与播放器。</summary>
    internal static EmulatorInstallation? FindBlueStacksUnderRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                var adbPath = Path.Combine(dir, "HD-Adb.exe");
                var launcherPath = Path.Combine(dir, "HD-Player.exe");
                if (File.Exists(adbPath) && File.Exists(launcherPath))
                {
                    return new EmulatorInstallation(dir, adbPath, launcherPath);
                }

                if (depth >= 2)
                {
                    continue;
                }

                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    queue.Enqueue((subDir, depth + 1));
                }
            }
            catch
            {
                // 安装目录的个别子目录可能无权访问，跳过后继续探测其它候选。
            }
        }

        return null;
    }

    /// <summary>枚举 BlueStacks 卸载信息与产品注册表中记录的安装目录。</summary>
    private static IEnumerable<string> EnumerateBlueStacksRegistryRoots()
    {
        var registryRoots = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in registryRoots)
        {
            RegistryKey? baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var productKey = baseKey.OpenSubKey(@"SOFTWARE\BlueStacks_nxt");
                AddExistingDirectory(found, productKey?.GetValue("InstallDir") as string);

                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    var displayName = key?.GetValue("DisplayName") as string;
                    if (displayName?.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        AddExistingDirectory(found, key?.GetValue("InstallLocation") as string);
                    }
                }
            }
            catch
            {
                // 注册表项缺失或权限不足时使用其它来源。
            }
            finally
            {
                baseKey?.Dispose();
            }
        }

        return found;
    }

    /// <summary>将有效目录规范化后加入候选集合。</summary>
    private static void AddExistingDirectory(ISet<string> roots, string? path)
    {
        var normalized = path?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized))
        {
            roots.Add(normalized);
        }
    }

    /// <summary>
    /// 在指定安装根目录下查找 MuMuManager.exe（优先 nx_main 目录，其次向下搜索有限深度）。
    /// 单独提取便于单元测试。
    /// </summary>
    internal static string? FindMuMuManagerUnderRoot(string root)
    {
        var direct = Path.Combine(root, "nx_main", "MuMuManager.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        // 广度优先搜索，限制深度 3 层，避免扫描整个磁盘
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > 3)
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "MuMuManager.exe"))
                {
                    return file;
                }

                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    queue.Enqueue((subDir, depth + 1));
                }
            }
            catch
            {
                // 无权限目录跳过
            }
        }

        return null;
    }
}
