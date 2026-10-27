using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace GGEA.App.Logging;

/// <summary>独立本地环境快照：按字段白名单采集，不读取环境变量、命令行、账户或网络配置。</summary>
public sealed class EnvironmentDiagnostics
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<Dictionary<string, object?>> _collect;
    private DateTime _lastFailureUtc;
    private string? _lastFailureId;
    private string? _lastFailureKey;
    private readonly string _session = Guid.NewGuid().ToString("N")[..12];
    private int _sequence;

    /// <summary>不在构造时访问文件系统，诊断目录不可写也不能阻止程序启动。</summary>
    public EnvironmentDiagnostics(string directory) : this(directory, CollectHost) { }

    /// <summary>测试替换采集器，以验证采集异常、并发和敏感信息边界。</summary>
    internal EnvironmentDiagnostics(string directory, Func<Dictionary<string, object?>> collect)
    {
        _directory = directory;
        _collect = collect;
    }

    /// <summary>后台采集并等待完成，避免遗留后台任务；失败只返回状态，不覆盖原业务异常。</summary>
    public Task<string> CaptureAsync(string reason, string adbPath, string emulatorPath = "", string serial = "", string stage = "")
        => Task.Run(() => Capture(reason, adbPath, emulatorPath, serial, stage));

    /// <summary>每行一个 JSON 快照；故障一分钟内复用编号，文件达到 1 MiB 时保留一份轮转副本。</summary>
    private string Capture(string reason, string adbPath, string emulatorPath, string serial, string stage)
    {
        lock (_gate)
        {
            try
            {
                var now = DateTime.UtcNow;
                var failureKey = $"{adbPath}\n{emulatorPath}\n{serial}\n{stage}";
                if (reason == "screenshot-failure" && _lastFailureId is not null && _lastFailureKey == failureKey && now - _lastFailureUtc < TimeSpan.FromMinutes(1))
                    return $"environmentId={_lastFailureId}（一分钟内复用环境快照）";
                var id = $"{_session}-{++_sequence}";
                var fields = _collect();
                fields["id"] = id;
                fields["utc"] = now;
                fields["reason"] = reason;
                fields["serial"] = RedactPath(serial);
                fields["stage"] = stage;
                fields["adb"] = FileIdentity(adbPath);
                fields["emulator"] = FileIdentity(emulatorPath);
                fields["deviceProbe"] = "not-collected: 本日志不主动调用ADB，不代表Android权限或内存正常";
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"environment-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                // 成功写入本身验证的是日志目录权限，不能外推到 ADB 或 Android 权限。
                fields["logDirectoryWritable"] = true;
                File.AppendAllText(path, JsonSerializer.Serialize(fields) + Environment.NewLine);
                if (reason == "screenshot-failure") { _lastFailureUtc = now; _lastFailureId = id; _lastFailureKey = failureKey; }
                return $"environmentId={id}, file={Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                // 不记录异常消息，避免路径、账户名或外部采集内容绕过脱敏。
                return $"environment-unavailable={ex.GetType().Name}";
            }
        }
    }

    /// <summary>采集主机、运行时、权限、内存和已知模拟器进程；每项失败独立标记。</summary>
    private static Dictionary<string, object?> CollectHost()
    {
        var result = new Dictionary<string, object?>();
        void Read(string key, Func<object?> read)
        {
            try { result[key] = read(); }
            catch (Exception ex) { result[key] = $"unavailable:{ex.GetType().Name}"; }
        }
        Read("appVersion", () => typeof(EnvironmentDiagnostics).Assembly.GetName().Version?.ToString());
        Read("releaseCommit", () => typeof(EnvironmentDiagnostics).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GgeaReleaseCommit")?.Value ?? "unavailable");
        Read("os", () => RuntimeInformation.OSDescription);
        Read("osArchitecture", () => RuntimeInformation.OSArchitecture.ToString());
        Read("processArchitecture", () => RuntimeInformation.ProcessArchitecture.ToString());
        Read("runtime", () => RuntimeInformation.FrameworkDescription);
        Read("processorCount", () => Environment.ProcessorCount);
        Read("elevated", () => { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); });
        Read("processMemory", () => { using var process = Process.GetCurrentProcess(); return new { workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, handles = process.HandleCount }; });
        Read("systemMemory", () =>
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception();
            return new { loadPercent = memory.Load, totalPhysicalBytes = memory.TotalPhysical, availablePhysicalBytes = memory.AvailablePhysical,
                commitLimitBytes = memory.TotalPageFile, availableCommitBytes = memory.AvailablePageFile };
        });
        Read("appDisk", () => { var disk = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!); return new { availableBytes = disk.AvailableFreeSpace, totalBytes = disk.TotalSize, type = disk.DriveType.ToString() }; });
        Read("knownProcesses", CollectProcesses);
        return result;
    }

    /// <summary>只枚举明确相关的可执行文件名，不收集无关程序、完整路径或启动参数。</summary>
    private static object CollectProcesses()
    {
        string[] allowed = ["adb", "HD-Adb", "MuMuPlayer", "MuMuVMMHeadless", "MuMuManager", "dnplayer", "ldconsole", "HD-Player", "Nox", "NoxVMHandle"];
        var found = new List<object>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (allowed.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        found.Add(new { name = process.ProcessName, pid = process.Id, workingSetBytes = process.WorkingSet64 });
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return found.Take(64).ToArray();
    }

    /// <summary>读取已配置工具的文件版本；裸命令不会被误当成已解析的可执行文件。</summary>
    private static object FileIdentity(string path)
    {
        try
        {
            var exists = File.Exists(path);
            return new { path = RedactPath(path), exists, fileVersion = exists ? FileVersionInfo.GetVersionInfo(path).FileVersion : null };
        }
        catch (Exception ex) { return new { path = RedactPath(path), error = ex.GetType().Name }; }
    }

    /// <summary>替换标准个人目录中的用户名；不记录机器名或登录账户。</summary>
    internal static string RedactPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) path = path.Replace(profile, "<USERPROFILE>", StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(path, @"(?i)([a-z]:[\\/]Users[\\/])[^\\/]+", "$1<USER>");
    }

    /// <summary>Windows MEMORYSTATUSEX 布局，所有容量单位均为字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    /// <summary>读取物理内存及提交额度，不申请管理员权限。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
