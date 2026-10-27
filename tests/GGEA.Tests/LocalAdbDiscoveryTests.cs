using System.Net;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>覆盖多品牌共用端口、蓝叠伪装型号以及设备身份保护。</summary>
public sealed class LocalAdbDiscoveryTests
{
    /// <summary>备用连接失败或超时不清空原设备；调用方取消必须传播。</summary>
    [Fact]
    public async Task ConnectionFailure_PreservesDevicesAndPropagatesCallerCancellation()
    {
        var adb = new FakeAdbService([], ["existing"]);
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.Leidian), adb, new TestLog(), (_, _) => { })
        { ReadShadowedAdbPorts = _ => [5555] };
        adb.ConnectOverride = (_, _, _) => throw new AdbException("connection refused");
        Assert.Equal("existing", Assert.Single(await adapter.DiscoverDevicesAsync()).Serial);
        adb.ConnectOverride = (_, _, _) => throw new OperationCanceledException();
        Assert.Equal("existing", Assert.Single(await adapter.DiscoverDevicesAsync()).Serial);
        using var canceled = new CancellationTokenSource();
        adb.ConnectOverride = (_, _, ct) => { canceled.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(""); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DiscoverDevicesAsync(ct: canceled.Token));
    }

    /// <summary>备用地址与 emulator 别名若属于同一次启动，仍仅呈现一个设备。</summary>
    [Fact]
    public async Task SameBootIdentity_DeduplicatesAlternateEndpoint()
    {
        var adb = new FakeAdbService([], ["emulator-5554"]);
        var id = Guid.NewGuid().ToString();
        adb.BootIds["emulator-5554"] = adb.BootIds["127.0.0.2:5555"] = id;
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.Leidian), adb, new TestLog(), (_, _) => { })
        { ReadShadowedAdbPorts = _ => [5555] };
        Assert.Equal("emulator-5554", Assert.Single(await adapter.DiscoverDevicesAsync()).Serial);
        adb.ConnectOverride = (_, _, _) => throw new Exception("已在线端点不应重复连接");
        Assert.Single(await adapter.DiscoverDevicesAsync());
    }

    /// <summary>只有通配地址与主回环地址同时监听的标准端口才是备用连接候选。</summary>
    [Fact]
    public void Candidates_RequireOverlappingListenersAndBoundedPorts()
    {
        IPEndPoint[] listeners = [new(IPAddress.Any, 5555), new(IPAddress.Loopback, 5555),
            new(IPAddress.Any, 5557), new(IPAddress.Loopback, 5559),
            new(IPAddress.Any, 5561), new(IPAddress.Loopback, 5561),
            new(IPAddress.Any, 5037), new(IPAddress.Loopback, 5037)];
        Assert.Equal(new[] { 5555 }, LocalAdbDiscovery.FindShadowedPorts(listeners, 3));
        Assert.Empty(LocalAdbDiscovery.FindShadowedPorts(listeners, 0));
    }

    /// <summary>连接遮蔽端口后重读列表，两种品牌分别识别，未知设备不被端口赋予品牌。</summary>
    [Fact]
    public async Task Refresh_DiscoversShadowedDeviceWithoutChangingExistingTarget()
    {
        var adb = new FakeAdbService([], ["emulator-5554"]);
        adb.DeviceProperties["emulator-5554"] = "[init.svc.bstsvcmgrtest]: [running]";
        adb.DeviceProperties["127.0.0.2:5555"] = "[init.svc.ldinit]: [running]";
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.Leidian), adb, new TestLog(), (_, _) => { })
        { ReadShadowedAdbPorts = _ => [5555, 5557] };
        var devices = await adapter.DiscoverDevicesAsync();
        Assert.Equal("BlueStacks", devices.Single(d => d.Serial == "emulator-5554").Brand);
        Assert.Equal("雷电", devices.Single(d => d.Serial == "127.0.0.2:5555").Brand);
        Assert.Null(devices.Single(d => d.Serial == "127.0.0.2:5557").Brand);
        Assert.Equal("emulator-5554", EmulatorDeviceSelector.SelectForRefresh(devices, "emulator-5554")?.Serial);
        Assert.Equal(3, (await adapter.DiscoverDevicesAsync()).Count);
    }

    /// <summary>蓝叠服务标记用于列表品牌，不依赖安装了哪个管理器。</summary>
    [Theory]
    [InlineData("running")]
    [InlineData("stopped")]
    [InlineData("restarting")]
    public void BlueStacksService_ConfirmsListBrand(string state)
    {
        var properties = $"[ro.product.manufacturer]: [OnePlus]\n[init.svc.bstsvcmgrtest]: [{state}]";
        Assert.Equal(EmulatorTypes.BlueStacks, GenericEmulatorAdapter.DetectDeviceType(properties));
    }

    /// <summary>手机型号、包名、近似服务名与无效值不能作为蓝叠身份证据。</summary>
    [Theory]
    [InlineData("[ro.product.model]: [NE2211]")]
    [InlineData("[debug.package]: [bstsvcmgrtest]")]
    [InlineData("[init.svc.bstsvcmgrtest-other]: [running]")]
    [InlineData("[init.svc.bstsvcmgrtest]: [unknown]")]
    public void UnrelatedServiceText_RemainsUnknown(string properties)
        => Assert.Equal(EmulatorTypes.Custom, GenericEmulatorAdapter.DetectDeviceType(properties));
}
