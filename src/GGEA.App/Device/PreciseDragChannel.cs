using System.IO;
using GGEA.App.Logging;
using System.Security.Cryptography;

namespace GGEA.App.Device;

/// <summary>每次手势只启动一次设备端进程；远端程序按内容寻址，可跨任务复用。</summary>
internal sealed class PreciseDragChannel(AdbService adb, MuMuInputService route, IGgeaLog log)
{
    private const string Digest = "a5f18604bdd698bd2e929161d96b80038b431c8229c3f78762f3cb7025128581";
    private const string Remote = "/data/local/tmp/ggea-drag-" + Digest + ".dex";

    internal async Task DragAsync(string serial, int x1, int y1, int x2, int y2, int duration, CancellationToken ct,
        int? expectedDisplay = null, ulong? expectedPhysical = null)
    {
        if (duration is <= 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(duration));
        if (x1 < 0 || x2 < 0 || y1 < 0 || y2 < 0 || (x1 == x2 && y1 == y2))
            throw new ArgumentException("精确拖动需要有效且不同的起点和终点");
        var display = await route.TryResolveDisplayAsync(serial, ct);
        if (expectedDisplay.HasValue && expectedDisplay != (display ?? 0)) throw new AdbException("游戏显示屏已变化");
        var lease = "/data/local/tmp/ggea-gesture-" + Guid.NewGuid().ToString("N");
        Task<string> Shell(string command, int timeout, CancellationToken token) =>
            adb.DeviceTextAsync(["-s", serial, "shell", command], timeout, token);
        var check = $"sha256sum {Remote} 2>/dev/null";
        var cached = await Shell($"if [ -r {Remote} ]; then {check}; fi", 5000, ct);
        var staged = lease + ".dex";
        try
        {
            if (!cached.TrimStart().StartsWith(Digest + " ", StringComparison.OrdinalIgnoreCase))
            {
                var local = Path.Combine(AppContext.BaseDirectory, "resources", "common", "device", "precise-drag.dex");
                using (var file = File.OpenRead(local))
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(Digest, StringComparison.OrdinalIgnoreCase))
                        throw new AdbException("精确拖动组件摘要不匹配");
                await adb.DeviceTextAsync(["-s", serial, "push", local, staged], 15000, ct);
                // 临时文件仅属于本次调用。先验摘要，再将只读 DEX 原子替换到稳定名称。
                var install = $"set -- $(sha256sum {staged}); [ \"$1\" = {Digest} ] && chmod 444 {staged} && mv -f {staged} {Remote} && echo READY";
                if ((await Shell(install, 5000, ct)).Trim() != "READY") throw new AdbException("设备端精确拖动组件校验失败");
            }
            if ((await Shell($"printf 1 > {lease} && echo READY", 5000, ct)).Trim() != "READY")
                throw new AdbException("无法创建设备端手势会话");
            if (await route.TryResolveDisplayAsync(serial, ct) != display || (expectedDisplay is > 0 &&
                await route.ReadPhysicalDisplayAsync(serial, expectedDisplay.Value, ct) != expectedPhysical))
                throw new AdbException("游戏显示屏已变化，请重新确认画面");
            var args = FormattableString.Invariant($"{display ?? -1} {x1} {y1} {x2} {y2} {duration} {lease}");
            ct.ThrowIfCancellationRequested();
            // 保持启动手势的连接，直到设备执行 UP/CANCEL。取消标记走独立 ADB sync 通道，
            // 因而不会被蓝叠 shell 的串行锁挡在正在执行的手势后面。
            var running = Shell($"CLASSPATH={Remote} app_process /system/bin org.ggea.input.PreciseDrag {args}",
                duration + 15000, CancellationToken.None);
            string output;
            try { output = await running.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                string? empty = null;
                try
                {
                    empty = Path.GetTempFileName();
                    await adb.DeviceTextAsync(["-s", serial, "push", empty, lease], 5000, CancellationToken.None);
                }
                finally
                {
                    try { await running; }
                    catch (AdbException ex) { log.Info($"精确拖动设备端结束：{ex.Message}"); }
                    finally { if (empty != null) File.Delete(empty); }
                }
                throw;
            }
            if (!output.Split('\n').Any(line => line.Trim() == "GGEA_DRAG_OK"))
                throw new AdbException("设备未确认精确拖动完成");
        }
        finally
        {
            try { await Shell($"rm -f {lease} {staged}", 5000, CancellationToken.None); }
            catch (AdbException ex) { log.Warn($"设备端手势临时文件清理失败：{ex.Message}"); }
        }
    }
}
