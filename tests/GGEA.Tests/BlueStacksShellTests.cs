using System.Text.RegularExpressions;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>模拟蓝叠能截图但 shell closed 的故障，验证协议选择和禁止重复业务操作。</summary>
public sealed class BlueStacksShellTests
{
    /// <summary>普通、兼容及 exec-out 三种通道均按探测结果选择，后续调用复用同设备选择。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SelectsWorkingChannelBeforeBusinessCommand(int failures)
    {
        var calls = new List<string[]>();
        var shell = new BlueStacksShell((args, _, _) =>
        {
            calls.Add(args.ToArray());
            if (calls.Count <= failures) throw new AdbException("error: closed");
            return Task.FromResult(Reply(args, args[^1].Contains("GGEA_PROBE") ? "GGEA_PROBE" : ""));
        }, new TestLog());
        await shell.ExecuteAsync(["-s", "device-a", "shell", "input", "tap", "1", "2"], 15000, default);
        await shell.ExecuteAsync(["-s", "device-a", "shell", "monkey", "-p", "game", "1"], 15000, default);
        Assert.Equal(failures + 3, calls.Count);
        Assert.Single(calls, a => a[^1].Contains("input tap"));
        Assert.Single(calls, a => a[^1].Contains("monkey"));
        Assert.Equal(failures == 2 ? "exec-out" : "shell", calls[^1][2]);
        if (failures == 1) Assert.Equal("-x", calls[^1][3]);
        Assert.Equal(failures != 2, calls[^1][^1].StartsWith("'"));
    }

    /// <summary>所有探测均失败时不能发送一次业务操作；零退出码垃圾输出也不是可用通道。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedProbesNeverSendInput(bool throws)
    {
        var calls = 0;
        var shell = new BlueStacksShell((args, _, _) =>
        {
            calls++;
            Assert.Contains("GGEA_PROBE", args[^1]);
            if (throws) throw new AdbException("error: closed");
            return Task.FromResult("unexpected output");
        }, new TestLog());
        await Assert.ThrowsAsync<AdbException>(() => shell.ExecuteAsync(["-s", "a", "shell", "input", "tap", "1", "2"], 15000, default));
        Assert.Equal(3, calls);
    }

    /// <summary>业务失败只清除缓存，下次独立调用重新探测，不能当场跨通道重放点击。</summary>
    [Fact]
    public async Task BusinessFailureIsNotRetriedAndInvalidatesSelection()
    {
        var probes = 0;
        var inputs = 0;
        var shell = new BlueStacksShell((args, _, _) =>
        {
            if (args[^1].Contains("GGEA_PROBE")) { probes++; return Task.FromResult(Reply(args, "GGEA_PROBE")); }
            inputs++;
            throw new AdbException("error: closed");
        }, new TestLog());
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<AdbException>(() => shell.ExecuteAsync(["-s", "a", "shell", "input", "tap", "1", "2"], 15000, default));
        Assert.Equal(2, probes);
        Assert.Equal(2, inputs);
    }

    /// <summary>设备不共享探测结果，取消必须直接传播，不能降级到其它通道。</summary>
    [Fact]
    public async Task DeviceSelectionIsIsolatedAndCancellationStopsProbing()
    {
        var probes = 0;
        var shell = new BlueStacksShell((args, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (args[^1].Contains("GGEA_PROBE")) probes++;
            return Task.FromResult(Reply(args, args[^1].Contains("GGEA_PROBE") ? "GGEA_PROBE" : ""));
        }, new TestLog());
        foreach (var serial in new[] { "a", "b" })
            await shell.ExecuteAsync(["-s", serial, "shell", "echo", "ok"], 15000, default);
        Assert.Equal(2, probes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.ExecuteAsync(
            ["-s", "a", "shell", "echo", "ok"], 15000, new CancellationToken(true)));
        var canceled = new BlueStacksShell((_, _, _) => throw new OperationCanceledException(), new TestLog());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.ExecuteAsync(["-s", "a", "shell", "echo", "ok"], 15000, default));
    }

    /// <summary>远程非零退出、标记缺失、截断或尾部污染不能因为客户端 exit=0 而报告成功。</summary>
    [Theory]
    [InlineData("result\nMARK:1\n")]
    [InlineData("result\nMARK:0")]
    [InlineData("result\nOTHER:0\n")]
    [InlineData("result\nMARK:0\nextra")]
    public void RejectsFailedOrIncompleteRemoteCommand(string output)
        => Assert.Throws<AdbException>(() => BlueStacksShell.ParseOutput(output, "MARK"));

    /// <summary>仅移除包装器自己的换行和标记，保留命令原输出；兼容 CRLF。</summary>
    [Theory]
    [InlineData("result\n\nMARK:0\n", "result\n")]
    [InlineData("result\r\nMARK:0\r\n", "result")]
    public void PreservesOriginalOutput(string output, string expected)
        => Assert.Equal(expected, BlueStacksShell.ParseOutput(output, "MARK"));

    /// <summary>启动提示和版本冲突提示不得变成设备。</summary>
    [Fact]
    public void DeviceListIgnoresDaemonMessages()
    {
        var devices = AdbService.ParseDevices("* daemon not running. starting it now *\n* daemon started successfully *\nadb server version (41) doesn't match this client (36); killing...\nemulator-5554 device model:BlueStacks\n");
        Assert.Single(devices);
        Assert.Equal("emulator-5554", devices[0].Serial);
    }

    /// <summary>测试替身从命令提取随机标记，模拟设备端完整返回。</summary>
    private static string Reply(IReadOnlyList<string> args, string output)
        => output + "\n" + Regex.Match(args[^1], "GGEA_EXIT_[a-f0-9]{32}").Value + ":0\n";
}
