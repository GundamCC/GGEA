using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>真实地址和跨品牌共存回归；使用独立进程边界替身，不触碰用户账号。</summary>
public sealed class EmulatorCompatibilityRegressionTests
{
    /// <summary>发现和原生截图使用同一端点规范，旧版 localhost 与 IPv6 地址不会产生不同实例映射。</summary>
    [Theory]
    [InlineData("localhost", "127.0.0.1:16384")]
    [InlineData("LOCALHOST", "127.0.0.1:16384")]
    [InlineData("::1", "[::1]:16384")]
    public void ManagerEndpoint_IsSharedByDiscoveryAndNativeCapture(string host, string expected)
    {
        var instance = $$"""{"is_android_started":true,"adb_host_ip":"{{host}}","adb_port":16384}""";
        var all = "{\"0\":" + instance + "}";
        Assert.Equal(expected, MuMuDeviceIdentity.ParseEndpoint(instance)?.Serial);
        Assert.Contains(expected, MuMuDeviceIdentity.ParseRunningSerials(all));
        Assert.Equal(expected, Assert.Single(MuMuScreenshotService.ParseInstances(all)).Serial);
    }

    /// <summary>任意实例编号都使用管理器真实地址，不能连接公式端口或其他在线账号。</summary>
    [Theory]
    [InlineData(0, "127.0.0.1", 16389)]
    [InlineData(37, "192.0.2.10", 26001)]
    [InlineData(8, "::1", 26002)]
    public async Task Start_UsesReportedEndpoint(int index, string host, int port)
    {
        var adb = new FakeAdbService([], ["other-account"]);
        var queries = 0;
        var adapter = new MuMuEmulatorAdapter(adb, new TestLog(), (_, _) => throw new Exception("实例已运行"), () => null)
        {
            ReadInstanceInfoAsync = (_, requested, _) =>
            {
                Assert.Equal(index, requested);
                queries++;
                return Task.FromResult($$"""{"is_android_started":true,"adb_host_ip":"{{host}}","adb_port":{{port}}}""");
            }
        };
        var result = await new EmulatorService(adapter).StartAndOpenGameAsync(Options(index));
        var expected = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        Assert.True(result.Success);
        Assert.Equal(expected, result.Serial);
        Assert.Equal(expected, Assert.Single(adb.LaunchedApps).Serial);
        Assert.Equal(2, queries);
        Assert.Equal(2, (await adb.ListDevicesAsync()).Count);
    }

    /// <summary>冷启动后地址可以延迟出现；不在没有明确地址时连接默认端口。</summary>
    [Fact]
    public async Task Start_WaitsForAddressAfterLaunch()
    {
        var adb = new FakeAdbService([]);
        var launched = false;
        var queries = 0;
        var adapter = new MuMuEmulatorAdapter(adb, new TestLog(), (_, _) => launched = true, () => null)
        {
            ReadInstanceInfoAsync = (_, _, _) => Task.FromResult(++queries switch
            {
                1 => """{"is_android_started":false}""",
                2 => """{"is_android_started":true}""",
                _ => """{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":27001}"""
            })
        };
        var result = await adapter.StartAndOpenGameAsync(Options(12));
        Assert.True(launched);
        Assert.True(result.Success);
        Assert.Equal("127.0.0.1:27001", Assert.Single(adb.LaunchedApps).Serial);
        Assert.Single(await adb.ListDevicesAsync());
    }

    /// <summary>地址一直缺失时按总时限退出，已有设备不会被拿来代替目标。</summary>
    [Fact]
    public async Task Start_MissingEndpointTimesOutWithoutInput()
    {
        var adb = new FakeAdbService([], ["other-account"]);
        var adapter = new MuMuEmulatorAdapter(adb, new TestLog(), (_, _) => { }, () => null)
        {
            ReadInstanceInfoAsync = (_, _, _) => Task.FromResult("""{"is_android_started":true}""")
        };
        var result = await adapter.StartAndOpenGameAsync(Options(0) with { DeviceWaitTimeoutMs = 40 });
        Assert.False(result.Success);
        Assert.Empty(adb.LaunchedApps);
        Assert.Equal("other-account", Assert.Single(await adb.ListDevicesAsync()).Serial);
    }

    /// <summary>管理器损坏、取消或端点查询超时都不能打开游戏。</summary>
    [Theory]
    [InlineData("error")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task Start_EndpointFailureDoesNotLaunchGame(string mode)
    {
        var adb = new FakeAdbService([]);
        using var cancellation = new CancellationTokenSource();
        var adapter = new MuMuEmulatorAdapter(adb, new TestLog(), (_, _) => { }, () => null,
            (_, _, _) => Task.FromResult(true))
        {
            ReadInstanceInfoAsync = async (_, _, ct) =>
            {
                if (mode == "error") return "broken";
                if (mode == "cancel") cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return "";
            }
        };
        var task = adapter.StartAndOpenGameAsync(Options(0) with { DeviceWaitTimeoutMs = 40 }, cancellation.Token);
        if (mode == "error") await Assert.ThrowsAsync<AdbException>(() => task);
        else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.False((await task).Success);
        Assert.Empty(adb.LaunchedApps);
        Assert.Empty(await adb.ListDevicesAsync());
    }

    /// <summary>高编号实例首次刷新就出现；并保留已在线的其他品牌设备。</summary>
    [Fact]
    public async Task Discovery_ReturnsNewManagerEndpointImmediately()
    {
        var adb = new FakeAdbService([], ["existing"]);
        var adapter = new MuMuEmulatorAdapter(adb, new TestLog(), (_, _) => { }, () => null)
        {
            ReadMuMuSerials = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "127.0.0.1:28001" })
        };
        var devices = await adapter.DiscoverDevicesAsync();
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Serial == "127.0.0.1:28001" && d.Brand == "MuMu");
        Assert.Contains(devices, d => d.Serial == "existing");
    }

    /// <summary>新端点失败不丢失旧设备；用户取消必须退出刷新。</summary>
    [Fact]
    public async Task Discovery_ConnectionFailurePreservesExistingAndHonorsCancellation()
    {
        var adb = new FakeAdbService([], ["existing"]);
        var adapter = new MuMuEmulatorAdapter(adb, new TestLog(), (_, _) => { }, () => null)
        {
            ReadMuMuSerials = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "127.0.0.1:28001" })
        };
        adb.ConnectOverride = (_, _, _) => throw new AdbException("refused");
        Assert.Equal("existing", Assert.Single(await adapter.DiscoverDevicesAsync()).Serial);
        using var cancellation = new CancellationTokenSource();
        adb.ConnectOverride = (_, _, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DiscoverDevicesAsync(ct: cancellation.Token));
    }

    /// <summary>非法端点不可作为连接目标；等待期间允许暂缺地址。</summary>
    [Theory]
    [InlineData("0.0.0.0", "16384")]
    [InlineData("127.0.0.1", "0")]
    [InlineData("127.0.0.1", "65536")]
    [InlineData("127.0.0.1", "\"bad\"")]
    [InlineData("bad host", "16384")]
    public void InvalidEndpoint_IsNotUsed(string host, string port)
        => Assert.Null(MuMuDeviceIdentity.ParseEndpoint(
            $$"""{"is_android_started":true,"adb_host_ip":"{{host}}","adb_port":{{port}}}"""));

    /// <summary>型号与无关包名不改变列表展示品牌；该信息不再阻止标准 ADB 操作。</summary>
    [Theory]
    [InlineData("[ro.product.manufacturer]: [Nox]", false)]
    [InlineData("[ro.product.vendor.manufacturer]: [Microvirt]", false)]
    [InlineData("[ro.product.model]: [Nox]", true)]
    [InlineData("[debug.package]: [com.microvirt.example]", true)]
    public void GenericBrands_RequireDeviceEvidence(string properties, bool unknown)
    {
        Assert.Equal(unknown, GenericEmulatorAdapter.DetectDeviceType(properties) == EmulatorTypes.Custom);
    }

    /// <summary>测试始终显式指定目标实例，并使用很短的有界轮询。</summary>
    private static EmulatorLaunchOptions Options(int index) => new()
    {
        EmulatorType = "mumu", EmulatorPath = "MuMuManager.exe", EmulatorArgs = $"control launch --vmindex {index}",
        GamePackage = "example.game", DeviceWaitTimeoutMs = 2000, PollIntervalMs = 1
    };
}
