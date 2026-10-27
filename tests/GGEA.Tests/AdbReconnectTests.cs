using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>复现保留型号的 MuMu 离线连接，验证定向恢复与失败边界，不访问真实模拟器。</summary>
public sealed class AdbReconnectTests
{
    private const string Serial = "127.0.0.1:16384";
    private static readonly AdbDevice Offline = new(Serial, "offline", "SM_S9210");

    /// <summary>即使型号存在也显示离线；USB 别名或无端口地址不能触发断开命令。</summary>
    [Fact]
    public void OfflineDisplayAndEligibility_AreExplicit()
    {
        Assert.Contains("SM_S9210", Offline.Display);
        Assert.Contains("离线 (offline)", Offline.Display);
        Assert.True(Offline.CanReconnect);
        Assert.False((Offline with { Serial = "emulator-5554" }).CanReconnect);
        Assert.False((Offline with { Serial = "127.0.0.1" }).CanReconnect);
        Assert.False((Offline with { State = "device" }).CanReconnect);
    }

    /// <summary>重连严格限定选中地址，connect 文本成功也须经过实际状态验证。</summary>
    [Theory]
    [InlineData("device", true)]
    [InlineData("offline", false)]
    [InlineData("missing", false)]
    public async Task Reconnect_VerifiesExactTarget(string finalState, bool success)
    {
        var commands = new List<string>();
        var reads = 0;
        var adb = new AdbService("must-not-run.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                commands.Add(string.Join(" ", args));
                var state = reads++ == 0 ? "offline" : finalState;
                return Task.FromResult(args[0] == "devices"
                    ? $"List of devices attached\nother device\n{(state == "missing" ? "" : $"{Serial} {state} model:SM_S9210")}\n"
                    : "connected successfully");
            }
        };
        if (success) await adb.ReconnectDeviceAsync(Offline);
        else await Assert.ThrowsAsync<AdbException>(() => adb.ReconnectDeviceAsync(Offline));
        Assert.Equal(new[] { "devices -l", $"disconnect {Serial}", $"connect {Serial}", "devices -l" }, commands);
    }

    /// <summary>预检发现设备已恢复、消失或未授权时，不断开它，也不操作其他设备。</summary>
    [Theory]
    [InlineData("device")]
    [InlineData("unauthorized")]
    [InlineData("missing")]
    public async Task ChangedState_DoesNotDisconnect(string state)
    {
        var adb = new AdbService("must-not-run.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                Assert.Equal(new[] { "devices", "-l" }, args);
                return Task.FromResult(state == "missing" ? "" : $"{Serial} {state}");
            }
        };
        if (state == "device") await adb.ReconnectDeviceAsync(Offline);
        else await Assert.ThrowsAsync<AdbException>(() => adb.ReconnectDeviceAsync(Offline));
    }

    /// <summary>断开后取消或失败均不得继续连接，异常不能改报成功。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedDisconnect_StopsRecovery(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var adb = new AdbService("must-not-run.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                if (args[0] == "devices") return Task.FromResult($"{Serial} offline");
                Assert.Equal(new[] { "disconnect", Serial }, args);
                if (!cancel) throw new AdbException("disconnect failed");
                cts.Cancel();
                return Task.FromResult("");
            }
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.ReconnectDeviceAsync(Offline, cts.Token));
        else await Assert.ThrowsAsync<AdbException>(() => adb.ReconnectDeviceAsync(Offline));
    }
}
