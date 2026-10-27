using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>从真实 AdbService 入口回归所有品牌共用的命令链；仅替换进程 I/O。</summary>
public sealed class UnifiedDeviceCommandsTests
{
    private const string Package = "com.bandainamcoent.gget_WW";

    /// <summary>截图总预算也约束显示探测，超时不能变成无限等待或成功回退。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenshotBudget_IncludesOptionalProbe()
    {
        var adb = new AdbService("missing-adb.exe", new TestLog(), screenshotTimeoutMs: 30)
        {
            DeviceTextAsync = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; },
            DeviceBytesAsync = (_, _, _) => throw new Exception("预算耗尽后不得截图")
        };
        var error = await Assert.ThrowsAsync<AdbException>(() => adb.ScreenshotAsync("selected"));
        Assert.Contains("超过总时限", error.Message);
    }

    /// <summary>品牌、端口与管理器路径都不能影响默认显示屏截图和输入。</summary>
    [Theory]
    [InlineData("127.0.0.1:16384", "Z:/missing/MuMuManager.exe")]
    [InlineData("emulator-5554", "Z:/missing/HD-Player.exe")]
    [InlineData("127.0.0.1:5555", "Z:/missing/dnplayer.exe")]
    [InlineData("unknown-device", "")]
    public async Task DefaultDisplay_DoesNotRequireBrandOrManager(string serial, string manager)
    {
        var calls = new List<string[]>();
        var png = ImageCodec.EncodePng([1, 2, 3, 255], 1, 1);
        var adb = new AdbService("missing-adb.exe", new TestLog(), emulatorPath: manager)
        {
            DeviceTextAsync = (args, _, _) =>
            {
                calls.Add(args.ToArray());
                Assert.Equal(serial, args[1]);
                Assert.True(args.Contains("dumpsys") || args.Contains("input"));
                return Task.FromResult(args.Contains("dumpsys") ? Activities(0) : "");
            },
            DeviceBytesAsync = (args, _, _) =>
            {
                calls.Add(args.ToArray());
                Assert.Equal(new[] { "-s", serial, "exec-out", "screencap", "-p" }, args);
                return Task.FromResult(png);
            }
        };
        Assert.Equal(png, await adb.ScreenshotAsync(serial));
        await adb.TapAsync(serial, 10, 20);
        await adb.SwipeAsync(serial, 1, 2, 3, 4, 100);
        await adb.KeyEventAsync(serial, "4");
        Assert.Equal(3, calls.Count(args => args.Contains("input")));
        Assert.All(calls.Where(args => args.Contains("input")), args => Assert.DoesNotContain("-d", args));
    }

    /// <summary>旧安卓 dumpsys 缺字段或权限不足不阻止标准 ADB；截图仍须是真正有效 PNG。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalDisplayProbeUnavailable_UsesStandardAdb(bool throws)
    {
        var png = ImageCodec.EncodePng([1, 2, 3, 255], 1, 1);
        var inputs = 0;
        var adb = new AdbService("missing-adb.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                if (args.Contains("dumpsys") && throws) throw new AdbException("unsupported");
                if (args.Contains("input")) inputs++;
                return Task.FromResult("legacy Android output");
            },
            DeviceBytesAsync = (_, _, _) => Task.FromResult(png)
        };
        Assert.Equal(png, await adb.ScreenshotAsync("selected"));
        await adb.TapAsync("selected", 1, 2);
        Assert.Equal(1, inputs);
        adb.DeviceBytesAsync = (_, _, _) => Task.FromResult(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<AdbException>(() => adb.ScreenshotAsync("selected"));
    }

    /// <summary>同一串口切换游戏显示屏后重新路由输入；无需品牌缓存或重启身份。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ChangedDisplay_RoutesInputWithoutBrandProbe()
    {
        var display = 7;
        var inputs = new List<string[]>();
        var adb = new AdbService("missing-adb.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                if (args.Contains("input")) inputs.Add(args.ToArray());
                return Task.FromResult(args.Contains("dumpsys") ? Activities(display) : "");
            }
        };
        await adb.TapAsync("selected", 1, 2);
        Assert.Equal(new[] { "-s", "selected", "shell", "input", "-d", "7", "tap", "1", "2" }, inputs[0]);
        display = 0;
        await adb.TapAsync("selected", 3, 4);
        Assert.Equal(new[] { "-s", "selected", "shell", "input", "tap", "3", "4" }, inputs[1]);
    }

    /// <summary>独立显示屏关闭原生能力、存在多目标或截图中途切屏均不得返回桌面帧。</summary>
    [Theory]
    [InlineData("independent")]
    [InlineData("ambiguous")]
    [InlineData("changed")]
    [Trait("Category", "Smoke")]
    public async Task UnsafeDisplay_DoesNotReturnFrame(string kind)
    {
        var reads = 0;
        var captures = 0;
        var adb = new AdbService("missing-adb.exe", new TestLog(), preferMuMuNativeScreenshot: false)
        {
            DeviceTextAsync = (_, _, _) => Task.FromResult(kind switch
            {
                "independent" => Activities(7),
                "ambiguous" => Activities(7) + "\n" + Activities(8),
                _ => Activities(reads++ == 0 ? 0 : 7)
            }),
            DeviceBytesAsync = (_, _, _) =>
            {
                captures++;
                return Task.FromResult(ImageCodec.EncodePng([1, 2, 3, 255], 1, 1));
            }
        };
        await Assert.ThrowsAsync<AdbException>(() => adb.ScreenshotAsync("selected"));
        Assert.Equal(kind == "changed" ? 1 : 0, captures);
    }

    /// <summary>取消显示探测时不继续截图或输入，不能当作可选探测失败。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task CanceledProbe_DoesNotExecuteCommand()
    {
        using var cts = new CancellationTokenSource();
        var adb = new AdbService("missing-adb.exe", new TestLog())
        {
            DeviceTextAsync = (args, _, _) =>
            {
                Assert.Contains("dumpsys", args);
                cts.Cancel();
                throw new AdbException("probe canceled");
            },
            DeviceBytesAsync = (_, _, _) => throw new Exception("取消后不得截图")
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.ScreenshotAsync("selected", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.TapAsync("selected", 1, 2, cts.Token));
    }

    /// <summary>只保留实际显示路由字段，不提供任何品牌信息。</summary>
    private static string Activities(int display)
        => $"Display #{display} (activities from top to bottom):\n  topResumedActivity=ActivityRecord{{a u0 {Package}/.Main t1}}";
}
