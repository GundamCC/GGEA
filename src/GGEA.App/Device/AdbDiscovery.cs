using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using GGEA.App.Config;
using Microsoft.Win32;

namespace GGEA.App.Device;

/// <summary>ADB 候选及可靠执行时间；未知时间不使用文件修改时间冒充。</summary>
public sealed record AdbCandidate(string Path, DateTime? LastRunUtc);

/// <summary>只读探测本机 ADB，不执行候选程序；目录扫描有时间、深度和数量上限。</summary>
public static class AdbDiscovery
{
    private static readonly string[] Brands = ["mumu", "hd-player", "dnplayer", "netease", "ldplayer", "leidian", "雷电", "bluestacks", "蓝叠", "nox", "夜神", "memu", "逍遥", "android", "emulator"];

    /// <summary>从安装信息、正在运行的程序和常见目录收集候选，再按执行时间排序。</summary>
    public static IReadOnlyList<AdbCandidate> Discover()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!IsBrand(process.ProcessName) && !process.ProcessName.Contains("adb", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) continue;
                    roots.Add(Path.GetDirectoryName(path)!);
                    if (EmulatorTypes.IsSupportedAdbPath(path)) runs[path] = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { /* 进程退出或访问受限，继续其它来源。 */ }
            }
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            TryRead(() =>
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                    TryRead(() =>
                    {
                        using var key = uninstall!.OpenSubKey(name);
                        if (!IsBrand(key?.GetValue("DisplayName") as string ?? name)) return;
                        var location = (key?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(location)) roots.Add(location);
                        var icon = (key?.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim().Trim('"');
                        if (File.Exists(icon)) roots.Add(Path.GetDirectoryName(icon)!);
                    });
                using var bs = baseKey.OpenSubKey(@"SOFTWARE\BlueStacks_nxt");
                if (bs?.GetValue("InstallDir") is string bsRoot) roots.Add(bsRoot);
            });
        }

        ReadExecutionHistory(runs);
        foreach (var special in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            TryRead(() =>
            {
                var parent = Environment.GetFolderPath(special);
                if (Directory.Exists(parent))
                    foreach (var dir in Directory.EnumerateDirectories(parent).Where(d => IsBrand(Path.GetFileName(d)))) roots.Add(dir);
            });
        foreach (var path in runs.Keys) roots.Add(Path.GetDirectoryName(path)!);
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            roots.Add(path.Trim().Trim('"'));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        foreach (var parent in new[] { drive.RootDirectory.FullName, Path.Combine(drive.RootDirectory.FullName, "Program Files"), Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)") })
            TryRead(() =>
            {
                if (!Directory.Exists(parent)) return;
                foreach (var dir in Directory.EnumerateDirectories(parent).Where(d => IsBrand(Path.GetFileName(d)))) roots.Add(dir);
            });
        return FindCandidates(roots, runs);
    }

    /// <summary>有限扫描与排序独立于 Windows 数据源，允许在临时目录验证发现行为。</summary>
    internal static IReadOnlyList<AdbCandidate> FindCandidates(IEnumerable<string> roots, IReadOnlyDictionary<string, DateTime> runs)
    {
        var found = new Dictionary<string, AdbCandidate>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>(roots.Select(r => (r, 0)));
        var timer = Stopwatch.StartNew();
        while (queue.Count > 0 && visited.Count < 12000 && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            var (dir, depth) = queue.Dequeue();
            TryRead(() =>
            {
                if (!Directory.Exists(dir) || !visited.Add(Path.GetFullPath(dir)) ||
                    (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var name in EmulatorTypes.AdbClientFileNames)
                {
                    var file = Path.GetFullPath(Path.Combine(dir, name));
                    if (File.Exists(file)) found[file] = new(file, runs.TryGetValue(file, out var time) ? time : null);
                }
                if (depth >= 4) return;
                foreach (var child in Directory.EnumerateDirectories(dir).Take(2000)) queue.Enqueue((child, depth + 1));
            });
        }
        return found.Values.OrderByDescending(c => c.LastRunUtc).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>首次迁移保留已有显式选择；新配置采用最近运行候选，未知时间时稳定回退。</summary>
    public static void Apply(AppConfig config, IReadOnlyList<AdbCandidate> candidates)
    {
        var explicitPath = Path.IsPathRooted(config.AdbPath) && File.Exists(config.AdbPath);
        config.AdbPaths = (config.AdbPaths ?? []).Concat(candidates.Select(c => c.Path))
            .Append(config.AdbPath).Where(p => !string.IsNullOrWhiteSpace(p) && p != "adb")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!explicitPath && candidates.Count > 0) config.AdbPath = candidates[0].Path;
        config.AdbDiscoveryCompleted = true;
    }

    /// <summary>仅在当前 ADB 附近配对启动器，切换安装时不沿用其它模拟器的路径。</summary>
    public static string? FindLauncher(string adbPath, string type)
    {
        if (!Path.IsPathRooted(adbPath)) return null;
        var directory = Path.GetDirectoryName(adbPath);
        var names = EmulatorTypes.GetProfile(type).LauncherExecutableNames;
        for (var level = 0; level < 3 && !string.IsNullOrEmpty(directory); level++)
        {
            foreach (var sub in EmulatorTypes.IsMuMu(type) ? new[] { "", "nx_main", "shell" } : new[] { "", "nx_main" })
            foreach (var name in names)
            {
                var path = Path.Combine(directory, sub, name);
                if (File.Exists(path)) return path;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    /// <summary>读取当前用户 BAM 执行记录；系统可能禁用或拒绝访问，因此时间允许未知。</summary>
    private static void ReadExecutionHistory(IDictionary<string, DateTime> runs)
    {
        var mappings = DriveInfo.GetDrives().Select(d => d.Name[..2]).Select(d =>
        {
            var buffer = new StringBuilder(1024);
            return (Drive: d, Device: QueryDosDevice(d, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "");
        }).Where(m => m.Device.Length > 0).ToArray();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        foreach (var prefix in new[] { @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings\", @"SYSTEM\CurrentControlSet\Services\bam\UserSettings\" })
            TryRead(() =>
            {
                using var key = Registry.LocalMachine.OpenSubKey(prefix + sid);
                foreach (var name in key?.GetValueNames() ?? [])
                {
                    if (!EmulatorTypes.IsSupportedAdbPath(name)) continue;
                    var mapping = mappings.FirstOrDefault(m => name.StartsWith(m.Device + "\\", StringComparison.OrdinalIgnoreCase));
                    if (mapping.Device is null || key!.GetValue(name) is not byte[] bytes || bytes.Length < 8) continue;
                    var value = BitConverter.ToInt64(bytes);
                    if (value <= 0 || value > DateTime.MaxValue.ToFileTimeUtc()) continue;
                    var path = mapping.Drive + name[mapping.Device.Length..];
                    var time = DateTime.FromFileTimeUtc(value);
                    if (!runs.TryGetValue(path, out var previous) || time > previous) runs[path] = time;
                }
            });
    }

    /// <summary>品牌匹配仅用于缩小检索范围，不决定模拟器业务类型。</summary>
    private static bool IsBrand(string value) => Brands.Any(b => value.Contains(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>一个不可读来源不能阻断其它来源；不捕获编程错误。</summary>
    private static void TryRead(Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { }
    }

    /// <summary>将 BAM 的设备路径映射为盘符路径。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maxLength);
}
