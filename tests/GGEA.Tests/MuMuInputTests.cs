using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>复现游戏独立显示、桌面默认显示的路由差异，验证失败时不发送任何输入。</summary>
public sealed class MuMuInputTests
{
    private const string Package = "com.bandainamcoent.gget_WW";

    /// <summary>构造来自真实 MuMu dumpsys 的关键字段，不依赖安装模拟器。</summary>
    private static string Activities(int display) => $$"""
        ACTIVITY MANAGER ACTIVITIES (dumpsys activity activities)
        Display #0 (activities from top to bottom):
          topResumedActivity=ActivityRecord{a u0 app.lawnchair/.LawnchairLauncher t2}
        Display #{{display}} (activities from top to bottom):
          topResumedActivity=ActivityRecord{b u0 {{Package}}/com.unity3d.player.UnityPlayerActivity t1184}
        """;

    /// <summary>显示编号不能固定为 0、1 或实例编号，也支持游戏就在主显示屏的情况。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(37)]
    public void Parse_UsesGameDisplay(int display)
        => Assert.Equal(display, MuMuInputService.ParseDisplay(Activities(display), Package));

    /// <summary>兼容已恢复 Activity 的另一种字段格式；重复声明同一显示不算多目标。</summary>
    [Fact]
    public void Parse_ResumedFieldAndDuplicate_AreSupported()
    {
        var dump = Activities(10) + $"\n  mResumedActivity: ActivityRecord{{b u0 {Package}/.Main t1}}\n";
        Assert.Equal(10, MuMuInputService.ParseDisplay(dump, Package));
        Assert.Equal(10, MuMuInputService.ParseDisplay(dump.Replace("topResumedActivity=", "mResumedActivity: "), Package));
    }

    /// <summary>历史游戏任务、同名前缀包、无显示编号、全局摘要及多显示同包均不得猜测目标。</summary>
    [Theory]
    [InlineData("history")]
    [InlineData("prefix")]
    [InlineData("missing")]
    [InlineData("summary")]
    [InlineData("ambiguous")]
    [InlineData("overflow")]
    public void Parse_UnsafeTargets_AreRejected(string kind)
    {
        var dump = kind switch
        {
            "history" => Activities(10).Replace("topResumedActivity=", "* Hist #0: "),
            "prefix" => Activities(10).Replace(Package, Package + ".clone"),
            "missing" => $"  topResumedActivity=ActivityRecord{{b u0 {Package}/.Main t1}}",
            "summary" => $"Display #0 (activities from top to bottom):\nRootWindowContainer:\n  mResumedActivity: ActivityRecord{{b u0 {Package}/.Main t1}}",
            "ambiguous" => Activities(10) + "\n" + Activities(11),
            _ => Activities(10).Replace("#10", "#99999999999999"),
        };
        Assert.Throws<AdbException>(() => MuMuInputService.ParseDisplay(dump, Package));
    }

    /// <summary>每次输入重新解析设备和显示屏；切换设备或重启游戏后不复用前一次路由。</summary>
    [Fact]
    public async Task Execute_RoutesAllInputsAndRefreshesDisplay()
    {
        var calls = new List<string[]>();
        var display = 10;
        var service = new MuMuInputService(Package, (args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args.Contains("dumpsys") ? Activities(display) : "");
        });
        string[][] inputs = [["tap", "165", "1032"], ["swipe", "900", "800", "900", "400", "500"], ["keyevent", "4"]];
        foreach (var input in inputs)
        {
            var serial = $"device-{display}";
            await service.ExecuteAsync(serial, input, default);
            Assert.Equal(new[] { "-s", serial, "shell", "dumpsys", "activity", "activities" }, calls[^2]);
            string[] expected = ["-s", serial, "shell", "input", "-d", display.ToString(), .. input];
            Assert.Equal(expected, calls[^1]);
            display++;
        }
        Assert.Equal(6, calls.Count);
    }

    /// <summary>查询失败、游戏关闭或查询后取消时不得发送后续点击。</summary>
    [Theory]
    [InlineData("error")]
    [InlineData("closed")]
    [InlineData("cancel")]
    public async Task Execute_ResolutionFailure_DoesNotSendInput(string kind)
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var service = new MuMuInputService(Package, (args, _) =>
        {
            calls++;
            Assert.Contains("dumpsys", args);
            if (kind == "error") throw new AdbException("offline");
            if (kind == "cancel") cts.Cancel();
            return Task.FromResult(kind == "closed" ? "" : Activities(10));
        });
        await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync("device", ["tap", "165", "1032"], cts.Token));
        Assert.Equal(1, calls);
    }

    /// <summary>不支持显示屏参数的 Android 即使返回零退出码也必须失败，不能无参数重试。</summary>
    [Theory]
    [InlineData("Error: Invalid arguments for command: tap")]
    [InlineData("Usage: input <command>")]
    public async Task Execute_UnsupportedDisplay_DoesNotFallback(string error)
    {
        var calls = 0;
        var service = new MuMuInputService(Package, (args, _) =>
        {
            calls++;
            return Task.FromResult(args.Contains("dumpsys") ? Activities(10) : error);
        });
        await Assert.ThrowsAsync<AdbException>(() => service.ExecuteAsync("device", ["tap", "165", "1032"], default));
        Assert.Equal(2, calls);
    }

    /// <summary>MuMu 原生截图关闭后仍会先确认游戏显示，取消令牌不得启动查询或输入。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adb_CanceledInput_DoesNotStart(bool native)
    {
        var adb = new AdbService("missing-adb.exe", new TestLog(), emulatorPath: "MuMuManager.exe", preferMuMuNativeScreenshot: native);
        var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.TapAsync("device", 165, 1032, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.SwipeAsync("device", 0, 0, 1, 1, 100, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.KeyEventAsync("device", "4", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adb.PinchInAsync("device", ct: ct));
    }
}
