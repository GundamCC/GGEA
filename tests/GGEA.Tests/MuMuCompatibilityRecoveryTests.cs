using System.Net;
using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>覆盖用户无需执行命令的 MuMu 多开发现和残留 Activity 恢复，保留账号隔离保护。</summary>
public sealed class MuMuCompatibilityRecoveryTests
{
    private const string Package = "com.bandainamcoent.gget_WW";
    private const string Instances = """
        [{"index":"9","is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16672,"error_code":0}]
        """;

    /// <summary>短参数返回错误或非 JSON 时自动重试长参数，发现与原生截图使用同一实例结构。</summary>
    [Theory]
    [InlineData("unknown option -v")]
    [InlineData("{\"error_code\":1}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"0\":{\"is_android_started\":false}}")]
    public async Task ManagerQuery_RetriesReadOnlyLongOption(string failure)
    {
        var calls = new List<string[]>();
        var json = await MuMuDeviceIdentity.QueryAllAsync((args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args[1] == "-v" ? failure : Instances);
        }, default);
        Assert.Equal(new[] { "info", "-v", "all" }, calls[0]);
        Assert.Equal(new[] { "info", "--vmindex", "all" }, calls[1]);
        Assert.Equal("127.0.0.1:16672", Assert.Single(MuMuDeviceIdentity.ParseRunningSerials(json)));
        Assert.Equal(9, Assert.Single(MuMuScreenshotService.ParseInstances(json)).Index);
    }

    /// <summary>两次查询失败仍失败，取消不能触发第二次查询，不泄露未知响应。</summary>
    [Fact]
    public async Task ManagerQuery_FailureAndCancellationRemainFailures()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<AdbException>(() => MuMuDeviceIdentity.QueryAllAsync((_, _) =>
        { calls++; return Task.FromResult("private response"); }, default));
        Assert.Equal(2, calls);
        Assert.DoesNotContain("private response", error.Message);
        using var cts = new CancellationTokenSource();
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MuMuDeviceIdentity.QueryAllAsync((_, _) =>
        { calls++; cts.Cancel(); return Task.FromResult("broken"); }, cts.Token));
        Assert.Equal(1, calls);
    }

    /// <summary>实际监听才能成为候选，高编号不受前三个实例限制；远程地址和 ADB 服务端口不参与。</summary>
    [Fact]
    public void ListenerCandidates_UseOnlyBoundedLocalActiveEndpoints()
    {
        IPEndPoint[] listeners = [new(IPAddress.Loopback, 16416), new(IPAddress.Any, 16672),
            new(IPAddress.Loopback, 16672), new(IPAddress.Parse("192.0.2.1"), 16384),
            new(IPAddress.Loopback, 5037), new(IPAddress.Loopback, 16417), new(IPAddress.Loopback, 40000)];
        Assert.Equal(new[] { 16416, 16672 }, LocalAdbDiscovery.FindMuMuPorts(listeners));
        Assert.Equal(40, LocalAdbDiscovery.FindMuMuPorts(
            Enumerable.Range(0, 40).Select(index => new IPEndPoint(IPAddress.Loopback, 16384 + 32 * index))).Count);
    }

    /// <summary>合法空列表保持为空；第二种参数失败也不抹掉首次合法的空结果。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("unsupported")]
    public async Task ManagerQuery_EmptyResultIsNotInvented(string second)
    {
        var calls = 0;
        var result = await MuMuDeviceIdentity.QueryAllAsync((_, _) =>
            Task.FromResult(++calls == 1 ? "[]" : second), default);
        Assert.Equal(2, calls);
        Assert.Empty(MuMuDeviceIdentity.ParseRunningSerials(result));
    }

    /// <summary>管理器只列出部分实例时仍补查；在线实例不耗尽补查名额，也不自动选中未知设备。</summary>
    [Fact]
    public async Task Discovery_PartialManagerAndOnlinePrefixStillFindMissingInstance()
    {
        var online = Enumerable.Range(0, 16).Select(i => $"127.0.0.1:{16384 + i * 32}").ToArray();
        var adb = new FakeAdbService([], online);
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.MuMu), adb, new TestLog(), (_, _) =>
            throw new Exception("不得启动模拟器"))
        {
            ReadMuMuSerials = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { online[0] }),
            ReadMuMuListeningPorts = () => Enumerable.Range(0, 17).Select(i => 16384 + i * 32).ToArray()
        };
        var devices = await adapter.DiscoverDevicesAsync();
        Assert.Equal(17, devices.Count);
        Assert.Equal("MuMu", devices.Single(d => d.Serial == online[0]).Brand);
        Assert.Null(devices.Single(d => d.Serial == "127.0.0.1:16896").Brand);
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh(devices, null));
        adb.ConnectOverride = (_, _, _) => throw new Exception("在线设备不得重复连接");
        Assert.Equal(17, (await adapter.DiscoverDevicesAsync()).Count);
    }

    /// <summary>管理器损坏时补连所监听的多开，不按端口赋予品牌，不启动游戏或自动选中账号。</summary>
    [Fact]
    public async Task Discovery_ManagerFailureRecoversListenersWithoutChoosingTarget()
    {
        var adb = new FakeAdbService([], ["existing"]);
        adb.DeviceProperties["127.0.0.1:16416"] = "[ro.product.manufacturer]: [NetEase]";
        var log = new TestLog();
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.MuMu), adb, log, (_, _) => throw new Exception("不得启动"))
        {
            ReadMuMuSerials = _ => throw new AdbException("unknown private response"),
            ReadMuMuListeningPorts = () => [16416, 16672]
        };
        var devices = await adapter.DiscoverDevicesAsync(maxInstances: 1);
        Assert.Equal(3, devices.Count);
        Assert.Equal("MuMu", devices.Single(device => device.Serial == "127.0.0.1:16416").Brand);
        Assert.Null(devices.Single(device => device.Serial == "127.0.0.1:16672").Brand);
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh(devices, null));
        Assert.Empty(adb.LaunchedApps);
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("private response"));
        adb.ConnectOverride = (_, _, _) => throw new Exception("在线端点不得重复连接");
        Assert.Equal(3, (await adapter.DiscoverDevicesAsync()).Count);
    }

    /// <summary>候选连接失败保留原列表；用户取消不被当作自动回退成功。</summary>
    [Fact]
    public async Task Discovery_ListenerFailureAndCancellationPreserveBoundaries()
    {
        var adb = new FakeAdbService([], ["existing"]);
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.MuMu), adb, new TestLog(), (_, _) => { })
        { ReadMuMuListeningPorts = () => [16416] };
        adb.ConnectOverride = (_, _, _) => throw new AdbException("refused");
        Assert.Equal("existing", Assert.Single(await adapter.DiscoverDevicesAsync()).Serial);
        using var cts = new CancellationTokenSource();
        adb.ConnectOverride = (_, _, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(""); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DiscoverDevicesAsync(ct: cts.Token));
    }

    /// <summary>保活残留任务不应使真实入口的默认截图与输入被误判多目标；旧版无顶层字段仍可解析。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleResumedTask_UsesDisplayTopAndRealAdbEntry(bool reverseOrder)
    {
        var stale = $"Display #7 (activities from top to bottom):\n  topResumedActivity=ActivityRecord{{a u0 app.launcher/.Main t2}}\n  mResumedActivity: ActivityRecord{{b u0 {Package}/.Main t1}}\n";
        var active = $"Display #0 (activities from top to bottom):\n  topResumedActivity=ActivityRecord{{c u0 {Package}/.Main t3}}\n";
        var dump = reverseOrder ? active + stale : stale + active;
        Assert.Equal(0, MuMuInputService.ParseDisplay(dump, Package));
        var png = ImageCodec.EncodePng([1, 2, 3, 255], 1, 1);
        var inputs = new List<string[]>();
        var adb = new AdbService("missing-adb.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                if (args.Contains("input")) inputs.Add(args.ToArray());
                return Task.FromResult(args.Contains("dumpsys") ? dump : "");
            },
            DeviceBytesAsync = (_, _, _) => Task.FromResult(png)
        };
        Assert.Equal(png, await adb.ScreenshotAsync("selected"));
        await adb.TapAsync("selected", 1, 2);
        Assert.Equal(new[] { "-s", "selected", "shell", "input", "tap", "1", "2" }, Assert.Single(inputs));
        Assert.Equal(7, MuMuInputService.ParseDisplay(stale.Replace("  topResumedActivity=ActivityRecord{a u0 app.launcher/.Main t2}\n", ""), Package));
    }

    /// <summary>数组响应不推算索引；错误码、两个真实游戏显示屏和顶层冲突仍安全停止。</summary>
    [Fact]
    public void AmbiguousIdentityAndActiveDisplays_StillStop()
    {
        Assert.Empty(MuMuScreenshotService.ParseInstances(Instances.Replace("\"index\":\"9\",", "")));
        Assert.Throws<AdbException>(() => MuMuScreenshotService.ParseInstances(Instances.Replace("\"error_code\":0", "\"error_code\":1")));
        var active = $"Display #7 (activities from top to bottom):\n  topResumedActivity=ActivityRecord{{a u0 {Package}/.Main t1}}\n";
        var error = Assert.Throws<AdbException>(() => MuMuInputService.ParseDisplay(active + active.Replace("#7", "#8"), Package));
        Assert.Contains("候选=7,8", error.Message);
        Assert.Throws<AdbException>(() => MuMuInputService.ParseDisplay(active + "  topResumedActivity=null\n", Package));
    }
}
