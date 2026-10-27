using System.IO;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>MuMu 原生截图的实例归属、像素约定、配置和失败保护。</summary>
public sealed class MuMuScreenshotTests
{
    /// <summary>独立颜色标记同时检测上下翻转、红蓝交换和 alpha 归一化。</summary>
    [Fact]
    public void Pixels_AreTopDownBgra()
    {
        byte[] input = [255, 0, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 255, 255, 255, 0];
        byte[] expected = [255, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255];
        var bgra = MuMuNativeCapture.ConvertRgbaBottomUp(input, 2, 2);
        Assert.Equal(expected, bgra);
        Assert.Equal(expected, ImageCodec.DecodePng(ImageCodec.EncodePng(bgra, 2, 2)).Bgra);
    }

    /// <summary>损坏或超限尺寸在原生内存分配前拒绝。</summary>
    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidDimensions_AreRejected(int width, int height)
        => Assert.Throws<InvalidOperationException>(() => MuMuNativeCapture.ValidateSize(width, height));

    /// <summary>无效版本不能污染 SDK 路径，也不能丢弃具备有效身份的在线实例。</summary>
    [Fact]
    public void ManagerInfo_UsesExplicitIdentity()
    {
        var instances = MuMuScreenshotService.ParseInstances("""
            {"0":{"is_android_started":false},
             "1":{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16416,"android_version":"12.0"},
             "2":{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16448,"android_version":"../15"}}
            """);
        Assert.Equal(new MuMuScreenshotService.Instance(1, "127.0.0.1:16416", "12.0"), instances[0]);
        Assert.Equal(new MuMuScreenshotService.Instance(2, "127.0.0.1:16448", null), instances[1]);
        Assert.Equal(2, instances.Count);
    }

    /// <summary>回归：旧管理器缺少版本字段时，直接地址与 emulator 别名均须保留真实实例。</summary>
    [Theory]
    [InlineData("127.0.0.1:16384")]
    [InlineData("emulator-5554")]
    public async Task MissingVersion_DoesNotDiscardRunningInstance(string selected)
    {
        var instances = MuMuScreenshotService.ParseInstances("""
            {"0":{"is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16384}}
            """);
        var boot = Guid.NewGuid().ToString();
        var instance = await MuMuScreenshotService.ResolveAsync(instances, selected, (_, _) => Task.FromResult(boot), default);
        Assert.Equal(0, instance.Index);
        Assert.Null(instance.AndroidVersion);
    }

    /// <summary>版本只是可选元数据；仅合法数字参与后续目录选择。</summary>
    [Theory]
    [InlineData("12", "12.0")]
    [InlineData("\"12.0\"", "12.0")]
    [InlineData("null", null)]
    [InlineData("true", null)]
    [InlineData("\"../15\"", null)]
    public void OptionalVersion_IsNormalized(string value, string? expected)
    {
        var json = "{\"0\":{\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384,\"android_version\":" + value + "}}";
        Assert.Equal(expected, Assert.Single(MuMuScreenshotService.ParseInstances(json)).AndroidVersion);
    }

    /// <summary>端点尚未注册到 ADB 时自动连接，但仍需启动身份一致，不能因连接成功直接选择。</summary>
    [Fact]
    public async Task Alias_ConnectsCandidatesAndStillChecksIdentity()
    {
        var boot = Guid.NewGuid().ToString();
        var connected = new HashSet<string>();
        MuMuScreenshotService.Instance[] instances = [new(0, "one", null), new(1, "two", null)];
        var result = await MuMuScreenshotService.ResolveAsync(instances, "emulator-5554", (serial, _) =>
        {
            if (serial != "emulator-5554" && !connected.Contains(serial)) throw new AdbException("not connected");
            return Task.FromResult(serial == "one" ? Guid.NewGuid().ToString() : boot);
        }, default, (serial, _) => { connected.Add(serial); return Task.CompletedTask; });
        Assert.Equal(1, result.Index);
        Assert.Equal(2, connected.Count);
    }

    /// <summary>售后日志区分没有候选、候选不可读和身份冲突，不再统一要求用户刷新。</summary>
    [Fact]
    public async Task MappingFailure_ReportsCountsWithoutRawIdentity()
    {
        var boot = Guid.NewGuid().ToString();
        var empty = await Assert.ThrowsAsync<AdbException>(() => MuMuScreenshotService.ResolveAsync([], "selected", (_, _) => Task.FromResult(boot), default));
        Assert.Contains("未返回可用在线实例", empty.Message);
        var unreadable = await Assert.ThrowsAsync<AdbException>(() => MuMuScreenshotService.ResolveAsync(
            [new(0, "one", null)], "selected", (serial, _) => serial == "selected" ? Task.FromResult(boot) : throw new AdbException("not connected"), default,
            (_, _) => throw new AdbException("private raw output")));
        Assert.Contains("候选=1，匹配=0，不可读=1", unreadable.Message);
        Assert.DoesNotContain("private raw output", unreadable.Message);
        Assert.DoesNotContain(boot, unreadable.Message);
    }

    /// <summary>取消立即停止，不连接候选，也不因直接地址命中而忽略取消。</summary>
    [Fact]
    public async Task MappingCancellation_DoesNotConnect()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MuMuScreenshotService.ResolveAsync(
            [new(0, "selected", null)], "selected", (_, _) => throw new InvalidOperationException(), new CancellationToken(true),
            (_, _) => throw new InvalidOperationException()));
    }

    /// <summary>emulator 别名按 boot_id 映射，不能根据编号猜测实例。</summary>
    [Fact]
    public async Task Alias_ResolvesByBootId()
    {
        var id = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        MuMuScreenshotService.Instance[] instances = [new(9, "127.0.0.1:16672", "12.0"), new(1, "127.0.0.1:16416", "12.0")];
        var result = await MuMuScreenshotService.ResolveAsync(instances, "emulator-5556", (serial, _) => Task.FromResult(serial == "127.0.0.1:16416" ? other : id), default);
        Assert.Equal(9, result.Index);
    }

    /// <summary>无法唯一确定实例时必须停止，不能退回默认实例 0。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousOrInvalidIdentity_IsRejected(bool ambiguous)
    {
        var id = ambiguous ? Guid.NewGuid().ToString() : "invalid";
        MuMuScreenshotService.Instance[] instances = [new(1, "one", "12.0"), new(2, "two", "12.0")];
        await Assert.ThrowsAsync<AdbException>(() => MuMuScreenshotService.ResolveAsync(instances, "emulator-5556", (_, _) => Task.FromResult(id), default));
    }

    /// <summary>独立游戏显示屏仍不能截取默认桌面；标准显示屏是否可用不由品牌决定。</summary>
    [Fact]
    public async Task NativeFailure_RequiresVerifiedAdbFallback()
    {
        var adb = new AdbService("nonexistent-adb.exe", new TestLog(), emulatorPath: "Z:/missing/MuMuManager.exe");
        adb.DeviceTextAsync = (_, _, _) => Task.FromResult("Display #10 (activities from top to bottom):\n  topResumedActivity=ActivityRecord{a u0 com.bandainamcoent.gget_WW/.Main t1}");
        var error = await Assert.ThrowsAsync<AdbException>(() => adb.ScreenshotAsync("emulator-5556"));
        Assert.Contains("command=MuMuNative", error.Message);
        Assert.Contains("ADB 回退=", error.Message);
        var fallback = new AdbService("nonexistent-adb.exe", new TestLog(), emulatorPath: "Z:/missing/MuMuManager.exe", preferMuMuNativeScreenshot: false);
        fallback.DeviceTextAsync = adb.DeviceTextAsync;
        var adbError = await Assert.ThrowsAsync<AdbException>(() => fallback.ScreenshotAsync("emulator-5556"));
        Assert.Contains("command=exec-out screencap -p", adbError.Message);
        Assert.Contains("独立显示屏", adbError.Message);
    }

    /// <summary>旧配置自动采用原生截图，显式关闭后保存与读取不会丢失。</summary>
    [Fact]
    public void Config_PreservesExplicitChoice()
    {
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{}")!.PreferMuMuNativeScreenshot);
        var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"preferMuMuNativeScreenshot\":false}")!;
        Assert.False(config.PreferMuMuNativeScreenshot);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(config))!.PreferMuMuNativeScreenshot);
    }

    /// <summary>取消不能启动原生助手或管理器。</summary>
    [Fact]
    public async Task CanceledNativeCapture_DoesNotStart()
    {
        var adb = new AdbService("missing-adb.exe", new TestLog(), emulatorPath: "Z:/missing/MuMuManager.exe");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.ScreenshotAsync("serial", new CancellationToken(true)));
    }

    /// <summary>只选择匹配的 Android SDK 目录，不使用另一个实例的版本。</summary>
    [Fact]
    public void LibrarySelection_UsesInstanceVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggea-mumu-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var correct = Path.Combine(root, "nx_device", "12.0", "shell", "sdk", "external_renderer_ipc.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(correct)!);
            File.WriteAllBytes(correct, []);
            Assert.Equal(correct, MuMuScreenshotService.FindLibrary(root, "12.0"));
            Assert.Throws<AdbException>(() => MuMuScreenshotService.FindLibrary(root, "15.0"));
            Assert.Throws<AdbException>(() => MuMuScreenshotService.FindLibrary(root, "../12.0"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>缺失管理器版本时自动兼容旧版共用 SDK 和新版按 Android 版本布局；不要求用户提供命令输出。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingVersion_ResolvesSdkAutomatically(bool legacy)
    {
        var root = Path.Combine(Path.GetTempPath(), "ggea-mumu-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var correct = legacy ? Path.Combine(root, "shell", "sdk", "external_renderer_ipc.dll")
                : Path.Combine(root, "nx_device", "12.0", "shell", "sdk", "external_renderer_ipc.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(correct)!);
            File.WriteAllBytes(correct, []);
            var queries = 0;
            var result = await MuMuScreenshotService.ResolveLibraryAsync(root, null,
                _ => { queries++; return Task.FromResult("12\r\n"); }, default);
            Assert.Equal(correct, result);
            Assert.Equal(legacy ? 0 : 1, queries);
            if (!legacy)
            {
                await Assert.ThrowsAsync<AdbException>(() => MuMuScreenshotService.ResolveLibraryAsync(root, null, _ => Task.FromResult("15"), default));
                await Assert.ThrowsAsync<AdbException>(() => MuMuScreenshotService.ResolveLibraryAsync(root, null, _ => Task.FromResult("../12.0"), default));
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MuMuScreenshotService.ResolveLibraryAsync(root, null,
                _ => throw new InvalidOperationException(), new CancellationToken(true)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
