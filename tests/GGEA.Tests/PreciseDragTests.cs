using System.IO;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>替换进程传输，验证生产入口的上传、路由、单次投递与取消收尾。</summary>
public class PreciseDragTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Drag_BindsDisplayAndNeverReplays(bool switchDisplay, bool loseReceipt)
    {
        var adb = new AdbService("adb.exe", new TestLog());
        int probes = 0, uploads = 0, injections = 0, cleanup = 0;
        adb.DeviceTextAsync = (args, _, _) =>
        {
            if (args.Contains("dumpsys"))
            {
                int display = ++probes == 2 && switchDisplay ? 11 : 10;
                return Task.FromResult($"Display #{display} (activities from top to bottom):\n topResumedActivity=ActivityRecord{{b u0 com.bandainamcoent.gget_WW/.Game t1}}");
            }
            if (args[2] == "push") { Assert.True(File.Exists(args[3])); uploads++; }
            string cmd = args[^1];
            if (cmd.StartsWith("CLASSPATH="))
            {
                Assert.Contains("PreciseDrag 10 110 201 500 600 700 ", cmd);
                injections++;
                return Task.FromResult(loseReceipt ? "" : "GGEA_DRAG_OK\n");
            }
            if (cmd.StartsWith("rm -f")) cleanup++;
            return Task.FromResult(cmd.EndsWith("echo READY") ? "READY" : "");
        };
        Task Gesture() => adb.DragAsync("serial", 110, 201, 500, 600, 700);
        if (switchDisplay || loseReceipt) await Assert.ThrowsAsync<AdbException>(Gesture);
        else await Gesture();
        Assert.Equal(1, uploads);
        Assert.Equal(switchDisplay ? 0 : 1, injections);
        Assert.Equal(1, cleanup);
    }

    [Fact]
    public async Task Cancellation_RevokesLeaseAndWaitsForDevice()
    {
        var adb = new AdbService("adb.exe", new TestLog());
        using var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        adb.DeviceTextAsync = (args, _, token) =>
        {
            var cmd = args[^1];
            if (cmd.StartsWith("CLASSPATH=")) { cts.Cancel(); return done.Task; }
            if (args[2] == "push" && !cmd.EndsWith(".dex"))
            {
                Assert.False(token.IsCancellationRequested);
                Assert.Equal(0, new FileInfo(args[3]).Length);
                done.SetException(new AdbException("cancelled"));
            }
            if (cmd.StartsWith("rm -f")) cleaned = done.Task.IsCompleted;
            return Task.FromResult(cmd.EndsWith("echo READY") ? "READY" : "");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.DragAsync("s", 1, 1, 40, 60, 500, cts.Token));
        Assert.True(cleaned);
    }
}
