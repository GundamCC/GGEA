using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>每个 GGEA 进程为 HD-Adb 持有独立服务，避免旧客户端反复替换默认 5037 服务。</summary>
internal sealed class BlueStacksAdbServer
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<BlueStacksAdbServer>>> Servers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LifecycleGate = new();
    private static readonly HashSet<BlueStacksAdbServer> Owned = [];
    private static bool _exiting;
    private readonly Process _process;
    private readonly int _port;
    private int _stopped;
    private SafeFileHandle? _job;

    /// <summary>服务以前台模式运行；所有权是实际进程句柄，不通过全局 kill-server 清理其他工具。</summary>
    private BlueStacksAdbServer(Process process, int port)
    { _process = process; _port = port; }

    /// <summary>退出事件只回收本应用已经创建完成的服务；调用方先结束业务与 插件 租约。</summary>
    static BlueStacksAdbServer() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        BlueStacksAdbServer[] servers;
        lock (LifecycleGate) { _exiting = true; servers = Owned.ToArray(); }
        foreach (var server in servers) server.Stop();
    };

    /// <summary>同安装的截图、输入、发现及 插件 共用服务；取消只取消当前等待，不取消其他调用的初始化。</summary>
    internal static async Task<IReadOnlyList<string>> RouteAsync(string path, IReadOnlyList<string> args, IGgeaLog log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.GetFileName(path).Equals("HD-Adb.exe", StringComparison.OrdinalIgnoreCase)) return args;
        var key = Path.GetFullPath(path);
        var entry = Servers.GetOrAdd(key, _ => new(() => StartAsync(key, log)));
        BlueStacksAdbServer server;
        try { server = await entry.Value.WaitAsync(ct); }
        catch
        {
            if (entry.Value.IsFaulted) Servers.TryRemove(new KeyValuePair<string, Lazy<Task<BlueStacksAdbServer>>>(key, entry));
            throw;
        }
        if (server._process.HasExited)
            throw new AdbException("HD-Adb 独立服务已退出，请重新启动 GGEA；未切换到其他 ADB 服务");
        return ["-P", server._port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. args];
    }

    /// <summary>分配回环空闲端口并核验 ADB 握手；端口竞争或启动失败时拒绝执行业务。</summary>
    private static async Task<BlueStacksAdbServer> StartAsync(string path, IGgeaLog log)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "nodaemon", "server" }) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        var server = new BlueStacksAdbServer(process, port);
        try
        {
            lock (LifecycleGate)
            {
                if (_exiting) throw new AdbException("GGEA 正在退出，未启动 HD-Adb 服务");
                if (!process.Start()) throw new AdbException("无法启动 HD-Adb 独立服务");
                // 握手仍在等待时退出也必须回收服务，不能只登记已成功初始化的任务。
                Owned.Add(server);
                server._job = CreateLifetimeJob(process);
            }
            process.StandardInput.Close();
            // 丢弃服务诊断内容并持续消费管道，不缓存设备数据或让服务因输出缓冲区满而挂起。
            _ = DrainAsync(process.StandardOutput.BaseStream);
            _ = DrainAsync(process.StandardError.BaseStream);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!process.HasExited)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    var stream = client.GetStream();
                    await stream.WriteAsync("000chost:version"u8.ToArray(), timeout.Token);
                    var reply = new byte[4];
                    await stream.ReadExactlyAsync(reply, timeout.Token);
                    if (Encoding.ASCII.GetString(reply) != "OKAY") throw new AdbException("HD-Adb 独立服务握手失败");
                    if (!OwnsListener(port, process.Id)) throw new AdbException("HD-Adb 服务端口被其他进程占用，已停止连接");
                    if (process.HasExited) break;
                    log.Info($"HD-Adb 独立服务已启动: port={port}；设备操作不使用默认 5037 服务");
                    return server;
                }
                catch (SocketException) { await Task.Delay(50, timeout.Token); }
            }
            throw new AdbException("HD-Adb 独立服务启动失败；未修改默认 ADB 服务");
        }
        catch (Exception ex)
        {
            server.Stop();
            throw new AdbException("HD-Adb 独立服务不可用；未切换客户端或执行设备操作", ex);
        }
    }

    /// <summary>服务退出会关闭管道；异常只结束诊断消费，不产生未观察的后台任务异常。</summary>
    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>仅结束持有句柄的前台服务，不按端口或进程名杀死外部服务。</summary>
    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(2000); } }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        finally { _job?.Dispose(); lock (LifecycleGate) Owned.Remove(this); _process.Dispose(); }
    }

    /// <summary>释放临时端口与启动服务之间可能有竞争；核验监听 PID，禁止把业务发送给抢占端口的进程。</summary>
    internal static bool OwnsListener(int port, int pid)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (result != 122 || size < 4) throw new Win32Exception((int)result);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (result != 0) throw new Win32Exception((int)result);
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            if (count < 0 || count > (size - 4) / rowSize) throw new AdbException("无法核验 HD-Adb 监听端口");
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(buffer + 4 + i * rowSize);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                if (localPort == port && row.Pid == pid && row.LocalAddress == 0x0100007f) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // TCP_TABLE_OWNER_PID_LISTENER 的 IPv4 行结构，用于确认服务归属而非枚举设备数据。
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    /// <summary>独立服务加入本应用持有的 Job；即使 GGEA 崩溃，内核也会结束它，避免遗留后台服务。</summary>
    internal static SafeFileHandle CreateLifetimeJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    // 与 Windows Job API 的 ABI 对应；使用结构布局而非硬编码平台相关偏移。
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
