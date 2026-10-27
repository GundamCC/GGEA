using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>
/// 模拟器一键启动服务测试：覆盖新设备上线、复用已运行设备、超时与未配置路径。
/// </summary>
public class EmulatorServiceTests
{
    /// <summary>管理器只接受明确的布尔状态；未知响应不能视为实例已关闭。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid-json")]
    [InlineData("{\"is_android_started\":\"false\"}")]
    [InlineData("{\"error_code\":1,\"is_android_started\":false}")]
    public void MuMuRunningState_UnknownIsRejected(string json)
        => Assert.Throws<AdbException>(() => MuMuEmulatorAdapter.ParseRunningState(json));

    /// <summary>有效的已启动与未启动状态保持原有启动语义。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MuMuRunningState_AcceptsExplicitState(bool running)
        => Assert.Equal(running, MuMuEmulatorAdapter.ParseRunningState(
            $"{{\"error_code\":0,\"is_android_started\":{running.ToString().ToLowerInvariant()}}}"));

    /// <summary>取消启动时不能再启动模拟器或发送打开游戏命令。</summary>
    [Theory]
    [InlineData("custom")]
    [InlineData("mumu")]
    public async Task CanceledStart_DoesNotLaunch(string type)
    {
        var adb = new FakeAdbService(Array.Empty<byte>());
        var launched = false;
        var service = new EmulatorService(adb, new TestLog(), (_, _) => launched = true);
        var options = new EmulatorLaunchOptions
        {
            EmulatorType = type,
            EmulatorPath = "emulator.exe",
            GamePackage = "example.game",
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.StartAndOpenGameAsync(options, new CancellationToken(true)));
        Assert.False(launched);
        Assert.Empty(adb.LaunchedApps);
    }

    /// <summary>模拟器启动期间取消后，不能继续发送打开游戏命令。</summary>
    [Fact]
    public async Task CancelDuringEmulatorLaunch_DoesNotOpenGame()
    {
        using var cts = new CancellationTokenSource();
        var adb = new FakeAdbService(Array.Empty<byte>());
        var service = new EmulatorService(adb, new TestLog(), (_, _) =>
        {
            adb.AddDevice("emulator-test");
            cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAndOpenGameAsync(
            new EmulatorLaunchOptions { EmulatorPath = "emulator.exe", GamePackage = "example.game" }, cts.Token));
        Assert.Empty(adb.LaunchedApps);
    }

    [Fact]
    public async Task StartAndOpenGameAsync_NewDeviceAppears_LaunchesGame()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        var launcherCalled = false;
        var service = new EmulatorService(fakeAdb, new TestLog(), (path, args) =>
        {
            launcherCalled = true;
            // 模拟模拟器启动后新设备上线
            fakeAdb.AddDevice("emulator-1234");
        });

        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "C:/Emulator/emu.exe",
            EmulatorArgs = "launch --index 0",
            GamePackage = "com.bandainamcoent.gget_WW",
            DeviceWaitTimeoutMs = 3000,
            BootCompletedTimeoutMs = 3000,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.True(result.Success);
        Assert.Equal("emulator-1234", result.Serial);
        Assert.True(launcherCalled);
        Assert.Contains(fakeAdb.LaunchedApps, x => x.Serial == "emulator-1234" && x.Package == "com.bandainamcoent.gget_WW");
    }

    /// <summary>设备已确定后先执行准备回调，再打开游戏，确保插件在游戏进程启动前完成准备。</summary>
    [Fact]
    public async Task StartAndOpenGameAsync_PreparesBeforeGameLaunch()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, _) => fakeAdb.AddDevice("emulator-5554"));
        var callbackSerial = string.Empty;
        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "C:/Emulator/emu.exe",
            GamePackage = "com.example.game",
            BeforeGameLaunchAsync = (serial, _) =>
            {
                callbackSerial = serial;
                Assert.Empty(fakeAdb.LaunchedApps);
                return Task.CompletedTask;
            },
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.True(result.Success);
        Assert.Equal("emulator-5554", callbackSerial);
        Assert.Single(fakeAdb.LaunchedApps);
    }

    [Fact]
    public async Task StartAndOpenGameAsync_DeviceAlreadyRunning_RequiresExplicitSelection()
    {
        // 模拟器已运行：启动前就存在可用设备
        var fakeAdb = new FakeAdbService(Array.Empty<byte>(), new[] { "emulator-5554" });
        var launched = false;
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, _) => launched = true);

        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "C:/Emulator/emu.exe",
            EmulatorArgs = "",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 200,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.False(result.Success);
        Assert.False(launched);
        Assert.Empty(fakeAdb.LaunchedApps);
    }

    [Fact]
    public async Task StartAndOpenGameAsync_NoDeviceAppears_TimesOut()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, _) => { });

        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "C:/Emulator/emu.exe",
            EmulatorArgs = "",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 200,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.False(result.Success);
        Assert.Contains("超时", result.Message);
        Assert.Empty(fakeAdb.LaunchedApps);
    }

    [Fact]
    public async Task StartAndOpenGameAsync_EmptyEmulatorPath_Throws()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        // 使用默认启动器（会校验路径为空）
        var service = new EmulatorService(fakeAdb, new TestLog());

        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "",
            EmulatorArgs = "",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 200,
            PollIntervalMs = 10,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAndOpenGameAsync(options));
    }

    [Fact]
    public async Task MuMuType_LaunchesFirstInstanceAndConnectsByPort()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        string? launchedPath = null;
        string? launchedArgs = null;
        var service = new EmulatorService(new MuMuEmulatorAdapter(fakeAdb, new TestLog(),
            (path, args) => { launchedPath = path; launchedArgs = args; },
            () => "C:/MuMu/MuMuManager.exe", (_, _, _) => Task.FromResult(false))
        {
            ReadInstanceInfoAsync = (_, _, _) => Task.FromResult(
                """{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16384}""")
        });

        var options = new EmulatorLaunchOptions
        {
            EmulatorType = "mumu",
            EmulatorPath = "",
            EmulatorArgs = "control launch --vmindex 0",
            GamePackage = "com.bandainamcoent.gget_WW",
            DeviceWaitTimeoutMs = 3000,
            BootCompletedTimeoutMs = 3000,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.True(result.Success);
        Assert.Equal("127.0.0.1:16384", result.Serial);
        Assert.Equal("C:/MuMu/MuMuManager.exe", launchedPath);
        Assert.Equal("control launch --vmindex 0", launchedArgs);
        Assert.Contains(fakeAdb.LaunchedApps, x => x.Serial == "127.0.0.1:16384" && x.Package == "com.bandainamcoent.gget_WW");
    }

    [Fact]
    public async Task MuMuType_AlreadyRunning_DoesNotLaunchAgain()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>(), new[] { "127.0.0.1:16384" });
        var launched = false;
        var service = new EmulatorService(new MuMuEmulatorAdapter(fakeAdb, new TestLog(),
            (_, _) => launched = true, () => "C:/MuMu/MuMuManager.exe", (_, _, _) => Task.FromResult(true))
        {
            ReadInstanceInfoAsync = (_, _, _) => Task.FromResult(
                """{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16384}""")
        });

        var options = new EmulatorLaunchOptions
        {
            EmulatorType = "mumu",
            EmulatorPath = "C:/MuMu/MuMuManager.exe",
            EmulatorArgs = "control launch --vmindex 0",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 3000,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.True(result.Success);
        Assert.False(launched);
        Assert.Equal("127.0.0.1:16384", result.Serial);
    }

    [Fact]
    public async Task MuMuType_NotDetected_Throws()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        var service = new EmulatorService(
            fakeAdb,
            new TestLog(),
            mumuPathResolver: () => null);

        var options = new EmulatorLaunchOptions
        {
            EmulatorType = "mumu",
            EmulatorPath = "",
            EmulatorArgs = "control launch --vmindex 0",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 200,
            PollIntervalMs = 10,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAndOpenGameAsync(options));
    }

    [Theory]
    [InlineData("control launch --vmindex 0", 0)]
    [InlineData("control launch --vmindex 2", 2)]
    [InlineData("launch -v 5", 5)]
    [InlineData("launch --index 0", 0)]
    public void ParseVmIndex_ReturnsExpectedIndex(string args, int expected)
    {
        Assert.Equal(expected, MuMuEmulatorAdapter.ParseVmIndex(args));
    }

    /// <summary>伪装手机品牌的 MuMu 通过管理器地址识别，同内核别名合并，其他模拟器不受影响。</summary>
    [Fact]
    public async Task Discovery_MaskedMuMu_UsesManagerAndBootIdentity()
    {
        var adb = new FakeAdbService(Array.Empty<byte>(), new[] { "127.0.0.1:16448", "emulator-5558", "emulator-5554" });
        adb.BootIds["127.0.0.1:16448"] = adb.BootIds["emulator-5558"] = "00000000-0000-0000-0000-000000000001";
        adb.BootIds["emulator-5554"] = "00000000-0000-0000-0000-000000000002";
        foreach (var serial in adb.BootIds.Keys) adb.DeviceProperties[serial] = "[ro.hardware]: [vivo]";
        var adapter = (GenericEmulatorAdapter)EmulatorAdapterFactory.Create("custom", adb, new TestLog());
        adapter.ReadShadowedAdbPorts = _ => []; // 隔离本机监听状态，保持设备身份用例确定性。
        adapter.ReadMuMuSerials = _ => Task.FromResult(MuMuDeviceIdentity.ParseRunningSerials("""
            {"0":{"is_android_started":false,"adb_host_ip":"127.0.0.1","adb_port":16384},
             "2":{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16448}}
            """));
        var devices = await adapter.DiscoverDevicesAsync();
        Assert.Equal(2, devices.Count);
        Assert.Equal("MuMu", devices.Single(d => d.Serial == "emulator-5558").Brand);
        Assert.Null(devices.Single(d => d.Serial == "emulator-5554").Brand);
    }

    /// <summary>只有通用别名时仍验证管理器端点；不匹配或无效身份不得解锁 MuMu 能力。</summary>
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", "MuMu")]
    [InlineData("00000000-0000-0000-0000-000000000002", null)]
    [InlineData("", null)]
    public async Task Discovery_MuMuEndpointNotListed_VerifiesBootIdentity(string endpointBootId, string? expectedBrand)
    {
        var adb = new FakeAdbService(Array.Empty<byte>(), new[] { "emulator-5554" });
        adb.DeviceProperties["emulator-5554"] = "[ro.product.model]: [2206122SC]";
        adb.BootIds["emulator-5554"] = "00000000-0000-0000-0000-000000000001";
        adb.BootIds["127.0.0.1:16448"] = endpointBootId;
        var adapter = (GenericEmulatorAdapter)EmulatorAdapterFactory.Create("custom", adb, new TestLog());
        adapter.ReadShadowedAdbPorts = _ => []; // 隔离本机监听状态，保持设备身份用例确定性。
        adapter.ReadMuMuSerials = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "127.0.0.1:16448" });

        var devices = await adapter.DiscoverDevicesAsync();
        var device = devices.Single(item => item.Serial == "emulator-5554");
        // 不同身份的管理器端点现在必须在本轮返回；相同身份仍只保留别名。
        Assert.Equal(expectedBrand == "MuMu" ? 1 : 2, devices.Count);
        Assert.Equal("emulator-5554", device.Serial);
        Assert.Equal(expectedBrand, device.Brand);
        Assert.Contains(await adb.ListDevicesAsync(), item => item.Serial == "127.0.0.1:16448");
    }

    /// <summary>雷电类型未填启动参数时，应自动补全 launch --index 0。</summary>
    [Fact]
    public async Task LeidianType_EmptyArgs_FillsDefaultLaunchIndex()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        string? launchedArgs = null;
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, args) =>
        {
            launchedArgs = args;
            // 模拟模拟器启动后新设备上线
            fakeAdb.AddDevice("emulator-5555");
        });

        var options = new EmulatorLaunchOptions
        {
            EmulatorType = "ld",
            EmulatorPath = "C:/LDPlayer/LDPlayer9/ldconsole.exe",
            EmulatorArgs = "",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 2000,
            BootCompletedTimeoutMs = 2000,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.True(result.Success);
        Assert.Equal("launch --index 0", launchedArgs);
    }

    [Fact]
    public async Task MultipleExistingDevices_DoesNotGuessTarget()
    {
        var fakeAdb = new FakeAdbService(
            Array.Empty<byte>(),
            new[] { "emulator-5554", "127.0.0.1:5555" });
        var launcherCalled = false;
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, _) => launcherCalled = true);
        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "C:/Emulator/emu.exe",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 200,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.False(result.Success);
        Assert.Contains("多个", result.Message);
        Assert.Empty(fakeAdb.LaunchedApps);
        Assert.False(launcherCalled);
    }

    /// <summary>刷新只保留明确目标；首次发现或目标离线时不得替用户选择账号。</summary>
    [Fact]
    public void EmulatorDeviceSelector_PreservesOnlyExplicitOnlineSelection()
    {
        var devices = new[]
        {
            new AdbDevice("emulator-5554", "device", string.Empty),
            new AdbDevice("127.0.0.1:5555", "device", string.Empty),
        };

        Assert.Null(EmulatorDeviceSelector.SelectForRefresh(devices, null));
        Assert.Equal("127.0.0.1:5555",
            EmulatorDeviceSelector.SelectForRefresh(devices, "127.0.0.1:5555")?.Serial);
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh(devices, "offline-device"));
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh(new[] { new AdbDevice("offline", "offline", "") }, null));
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh(devices[..1], null));
    }

    /// <summary>同一启动的 TCP/别名可恢复选择；串口被新实例复用或身份含糊时必须停下。</summary>
    [Fact]
    public void EmulatorDeviceSelector_RequiresIdentityForAliasAndRejectsSerialReuse()
    {
        var id = Guid.NewGuid();
        var device = new AdbDevice("emulator-5554", "device", "") { BootId = id };
        Assert.Equal(device, EmulatorDeviceSelector.SelectForRefresh([device], "127.0.0.1:16384", id));
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh([device], device.Serial, Guid.NewGuid()));
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh([device], "127.0.0.1:16384"));
        Assert.Null(EmulatorDeviceSelector.SelectForRefresh([device, device with { Serial = "other" }], "missing", id));
    }

    /// <summary>管理器查询异步等待时允许取消；失败和取消都不能被当作“未运行”而启动进程。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MuMuStateQuery_CancelOrFailure_DoesNotLaunch(bool cancel)
    {
        var adb = new FakeAdbService([]);
        var launched = false;
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new EmulatorService(adb, new TestLog(), (_, _) => launched = true,
            mumuRunningCheck: async (_, _, ct) =>
            {
                entered.SetResult();
                if (!cancel) throw new AdbException("管理器查询失败");
                await Task.Delay(Timeout.Infinite, ct);
                return false;
            });
        var task = service.StartAndOpenGameAsync(new EmulatorLaunchOptions
        { EmulatorType = "mumu", EmulatorPath = "MuMuManager.exe", GamePackage = "example.game" }, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            Assert.False(task.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else await Assert.ThrowsAsync<AdbException>(() => task);
        Assert.False(launched);
        Assert.Empty(adb.LaunchedApps);
    }

    /// <summary>冷启动后若同时出现多个设备，通用适配器也不得按列表顺序猜测目标。</summary>
    [Fact]
    public async Task MultipleDevicesAppearAfterLaunch_DoesNotGuessTarget()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, _) =>
        {
            fakeAdb.AddDevice("emulator-5554");
            fakeAdb.AddDevice("127.0.0.1:5555");
        });
        var options = new EmulatorLaunchOptions
        {
            EmulatorPath = "C:/Emulator/emu.exe",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 200,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.False(result.Success);
        Assert.Contains("多个", result.Message);
        Assert.Empty(fakeAdb.LaunchedApps);
    }

    [Fact]
    public async Task BlueStacksType_NoDevice_ShowsAdbEnableDiagnostic()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>());
        var service = new EmulatorService(fakeAdb, new TestLog(), (_, _) => { });
        var options = new EmulatorLaunchOptions
        {
            EmulatorType = "bs",
            EmulatorPath = "C:/BlueStacks_nxt/HD-Player.exe",
            GamePackage = "com.example.game",
            DeviceWaitTimeoutMs = 50,
            PollIntervalMs = 10,
        };

        var result = await service.StartAndOpenGameAsync(options);

        Assert.False(result.Success);
        Assert.Contains("设置 → 高级", result.Message);
        Assert.Contains("Android Debug Bridge", result.Message);
    }

    [Fact]
    public async Task BlueStacksAdapter_DiscoverDevices_AddsBrandWithoutPortGuessing()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>(), new[] { "emulator-5554" });
        fakeAdb.DeviceProperties["emulator-5554"] = "[ro.product.brand]: [BlueStacks]";
        var adapter = EmulatorAdapterFactory.Create("bs", fakeAdb, new TestLog(), (_, _) => { }, () => null);
        ((GenericEmulatorAdapter)adapter).ReadShadowedAdbPorts = _ => [];

        var devices = await adapter.DiscoverDevicesAsync();

        var device = Assert.Single(devices);
        Assert.Equal("BlueStacks", device.Brand);
        Assert.Equal("emulator-5554", device.Serial);
    }

    /// <summary>MuMu 适配器读取管理器真实端点，并只移除同一启动身份的重复连接。</summary>
    [Fact]
    public async Task MuMuAdapter_DiscoverDevices_UsesManagerAndDeduplicatesAliases()
    {
        var fakeAdb = new FakeAdbService(Array.Empty<byte>(), new[] { "emulator-5554" });
        var adapter = EmulatorAdapterFactory.Create(
            "mumu",
            fakeAdb,
            new TestLog(),
            (_, _) => { },
            () => "C:/MuMu/MuMuManager.exe",
            (_, _, _) => Task.FromResult(true));
        ((GenericEmulatorAdapter)adapter).ReadShadowedAdbPorts = _ => [];

        ((GenericEmulatorAdapter)adapter).ReadMuMuSerials = _ => Task.FromResult<IReadOnlySet<string>>(
            new HashSet<string> { "127.0.0.1:16384", "127.0.0.1:16416" });
        foreach (var serial in new[] { "emulator-5554", "127.0.0.1:16384", "127.0.0.1:16416" })
            fakeAdb.DeviceProperties[serial] = "[ro.product.manufacturer]: [Netease MuMu]";

        fakeAdb.BootIds["emulator-5554"] = fakeAdb.BootIds["127.0.0.1:16384"] = "00000000-0000-0000-0000-000000000001";
        var devices = await adapter.DiscoverDevicesAsync(maxInstances: 2);

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, device => device.Serial == "emulator-5554");
        Assert.Contains(devices, device => device.Serial == "127.0.0.1:16416");
        Assert.DoesNotContain(devices, device => device.Serial == "127.0.0.1:16384");
        Assert.All(devices, device => Assert.Equal("MuMu", device.Brand));
    }

    /// <summary>同一个 ADB 返回不同品牌时，不能把客户端品牌传播给所有设备。</summary>
    [Theory]
    [InlineData("mumu")]
    [InlineData("ld")]
    [InlineData("bs")]
    public async Task Discovery_IdentifiesEachDeviceIndependently(string clientType)
    {
        var adb = new FakeAdbService(Array.Empty<byte>(), new[] { "emulator-5554", "127.0.0.1:16384", "third", "unknown" });
        adb.DeviceProperties["emulator-5554"] = "[ro.product.manufacturer]: [leidian]";
        adb.DeviceProperties["127.0.0.1:16384"] = "[ro.hardware]: [nemu]";
        adb.DeviceProperties["third"] = "[ro.product.brand]: [BlueStacks]";
        var adapter = EmulatorAdapterFactory.Create(clientType, adb, new TestLog(), (_, _) => { }, () => null);
        // 本例只验证已有设备的品牌，隔离新增监听探测，避免本机 MuMu 影响固定列表。
        ((GenericEmulatorAdapter)adapter).ReadMuMuListeningPorts = () => [];
        var devices = await adapter.DiscoverDevicesAsync(0);
        Assert.Equal(new string?[] { "雷电", "MuMu", "BlueStacks", null }, devices.Select(device => device.Brand));
    }

}
