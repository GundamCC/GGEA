using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>真实原生素材合成画布，验证快捷方向、首次小图优先、失败回退和取消安全边界。</summary>
public sealed class RoadmapShortcutTests
{
    private const string Prototype = "ogk_g0070u00100";
    private const string Ball = "ogk_g0010u00700";

    /// <summary>只使用随包素材，避免依赖开发者机器的图片路径。</summary>
    private static string Template(string name) => ConfigService.ResolvePath($"resources/templates/develop/dev_unit/{name}.png");

    /// <summary>按生产模板的宽度舍入规则合成立绘；合成帧用于算法回归，不冒充游戏截图。</summary>
    private static byte[] Frame(params (string Name, double Scale, int X)[] units)
    {
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(20, 20, 20));
        foreach (var unit in units)
        {
            using var source = Cv2.ImRead(Template(unit.Name), ImreadModes.Unchanged);
            using var scaled = new Mat();
            var width = (int)Math.Round(source.Width * unit.Scale);
            Cv2.Resize(source, scaled, new Size(width, (int)Math.Round(source.Height * (double)width / source.Width)));
            using var color = new Mat();
            using var alpha = new Mat();
            Cv2.CvtColor(scaled, color, ColorConversionCodes.BGRA2BGR);
            Cv2.ExtractChannel(scaled, alpha, 3);
            using var region = new Mat(screen, new Rect(unit.X, 400, scaled.Width, scaled.Height));
            color.CopyTo(region, alpha);
        }
        return screen.ToBytes(".png");
    }

    /// <summary>两项任务方向相反；快捷路径只拖一次，找到目标立即结束搜索。</summary>
    [Theory]
    [InlineData(Prototype, Ball, -1)]
    [InlineData(Ball, Prototype, 1)]
    public async Task OtherLargeFirst_MovesInRequestedDirection(string target, string other, int direction)
    {
        var initial = Frame((other, 1.3, 800));
        var after = Frame((target, 1.3, 800));
        var adb = new FakeAdbService(initial);
        adb.ScreenshotProvider = () => adb.Swipes.Count == 0 ? initial : after;
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
        var match = await service.FindOnRoadmapAsync("device", Template(target), "", maxDragsPerDirection: 3);
        Assert.NotNull(match);
        var swipe = Assert.Single(adb.Swipes);
        Assert.Equal(direction * 850, swipe.X2 - swipe.X1);
        Assert.Equal(swipe.Y1, swipe.Y2);
        Assert.True(adb.ScreenshotCount >= 4); // 首帧、辅助确认、拖动后帧和目标确认。
        Assert.Empty(adb.Taps); // 共享搜索只返回结果，由上层执行唯一目标点击。
    }

    /// <summary>首屏目标的大、小两档均优先确认；即使另一单位的大图在场也绝不先拖动。</summary>
    [Theory]
    [InlineData(Prototype, Ball, .832)]
    [InlineData(Ball, Prototype, .832)]
    [InlineData(Prototype, Ball, 1.3)]
    [InlineData(Ball, Prototype, 1.3)]
    public async Task InitialTargetAtEitherScale_WinsOverOtherLarge(string target, string other, double scale)
    {
        var adb = new FakeAdbService(Frame((target, scale, 500), (other, 1.3, 1200)));
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
        var match = await service.FindOnRoadmapAsync("device", Template(target), "");
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 500, 800);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>快捷拖动未命中后，从当前位置重新走原有上、右归位与逐行扫描，不反复触发快捷路径。</summary>
    [Theory]
    [InlineData(Prototype, Ball, -1)]
    [InlineData(Ball, Prototype, 1)]
    public async Task ShortcutMiss_ResumesOriginalSearch(string target, string other, int direction)
    {
        var initial = Frame((other, 1.3, 800));
        var blank = Frame();
        var targetFrame = Frame((target, .832, 800));
        var adb = new FakeAdbService(initial);
        // 快捷失败后，只有原逐行扫描的左拖才显示目标，不依赖边界复核增加的截图/手势次数。
        adb.ScreenshotProvider = () => adb.Swipes.Count == 0 ? initial
            : adb.Swipes.Skip(2).Any(s => s.X2 < s.X1) ? targetFrame : blank;
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
        Assert.NotNull(await service.FindOnRoadmapAsync("device", Template(target), "", maxDragsPerDirection: 3));
        Assert.Equal(new[] { (direction * 850, 0), (direction * 850, 0), (0, 250), (0, 250), (850, 0), (850, 0), (-850, 0) },
            adb.Swipes.Select(s => (s.X2 - s.X1, s.Y2 - s.Y1)));
    }

    /// <summary>短拖动或首个手势未生效时仍允许第二段；第二段命中后不进入普通归位。</summary>
    [Theory]
    [InlineData(Prototype, Ball, -1, false)]
    [InlineData(Ball, Prototype, 1, false)]
    [InlineData(Prototype, Ball, -1, true)]
    [InlineData(Ball, Prototype, 1, true)]
    public async Task ShortcutSecondDrag_FindsTarget(string target, string other, int direction, bool unchanged)
    {
        var initial = Frame((other, 1.3, 800));
        var intermediate = unchanged ? initial : Frame((other, 1.3, 1000));
        var final = Frame((target, .832, 800));
        var adb = new FakeAdbService(initial);
        adb.ScreenshotProvider = () => adb.Swipes.Count switch { 0 => initial, 1 => intermediate, _ => final };
        var log = new TestLog();
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), log), log, true);
        Assert.NotNull(await service.FindOnRoadmapAsync("device", Template(target), "", maxDragsPerDirection: 3));
        Assert.Equal(2, adb.Swipes.Count);
        Assert.All(adb.Swipes, s => { Assert.Equal(direction * 850, s.X2 - s.X1); Assert.Equal(s.Y1, s.Y2); });
        Assert.Empty(adb.Taps);
    }

    /// <summary>小比例辅助单位和重复大图不触发快捷方向，继续原有首次归位。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmallOrAmbiguousOther_DoesNotTriggerShortcut(bool ambiguous)
    {
        var initial = ambiguous ? Frame((Ball, 1.3, 500), (Ball, 1.3, 1200)) : Frame((Ball, .832, 800));
        var target = Frame((Prototype, .832, 800));
        var adb = new FakeAdbService(initial);
        adb.ScreenshotProvider = () => adb.Swipes.Count == 0 ? initial : target;
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
        Assert.NotNull(await service.FindOnRoadmapAsync("device", Template(Prototype), "", maxDragsPerDirection: 3));
        Assert.Equal(0, adb.Swipes[0].X2 - adb.Swipes[0].X1);
        Assert.Equal(250, adb.Swipes[0].Y2 - adb.Swipes[0].Y1);
    }

    /// <summary>辅助确认时出现目标小图，立即识别新帧，不按照过期方向继续拖动。</summary>
    [Fact]
    public async Task TargetAppearsDuringOtherConfirmation_DoesNotDrag()
    {
        var first = Frame((Ball, 1.3, 800));
        var next = Frame((Prototype, .832, 800));
        var adb = new FakeAdbService(first);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? first : next;
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
        Assert.NotNull(await service.FindOnRoadmapAsync("device", Template(Prototype), ""));
        Assert.Empty(adb.Swipes);
        Assert.Equal(3, adb.ScreenshotCount);
    }

    /// <summary>辅助确认阶段被取消时仍向上抛出，不发出快捷手势或点击。</summary>
    [Fact]
    public async Task CancellationDuringOtherConfirmation_StopsWithoutInput()
    {
        using var cancel = new CancellationTokenSource();
        var adb = new FakeAdbService(Frame((Ball, 1.3, 800)));
        adb.BeforeScreenshotAsync = (count, ct) =>
        {
            if (count == 2) cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindOnRoadmapAsync("device", Template(Prototype), "", ct: cancel.Token));
        Assert.Empty(adb.Swipes);
        Assert.Empty(adb.Taps);
    }

    /// <summary>同一画面序列对比原搜索：官方路径一次拖动完成，自定义同名素材仍走原归位路径。</summary>
    [Theory]
    [InlineData(Prototype, Ball)]
    [InlineData(Ball, Prototype)]
    public async Task Shortcut_ReducesGestures_WithoutChangingCustomTemplateSearch(string target, string other)
    {
        var directory = Directory.CreateTempSubdirectory("ggea-shortcut-");
        try
        {
            var custom = Path.Combine(directory.FullName, target + ".png");
            File.Copy(Template(target), custom);
            var first = Frame((other, 1.3, 800));
            var next = Frame((target, .832, 800));
            async Task<int> Search(string path)
            {
                var adb = new FakeAdbService(first);
                adb.ScreenshotProvider = () => adb.Swipes.Count == 0 ? first : next;
                var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), true);
                Assert.NotNull(await service.FindOnRoadmapAsync("device", path, "", maxDragsPerDirection: 3));
                return adb.Swipes.Count;
            }
            // 自定义模板归位到疑似顶部时即检索目标，不再把它拖到下一方向才识别。
            Assert.Equal(2, await Search(custom));
            Assert.Equal(1, await Search(Template(target)));
        }
        finally { directory.Delete(true); } // 仅清理本测试创建的唯一目录。
    }
}
