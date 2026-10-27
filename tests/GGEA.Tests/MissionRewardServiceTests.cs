using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>领取使命奖励服务测试。</summary>
public class MissionRewardServiceTests
{
    [Fact]
    public async Task ClaimAsync_Success_ClaimsAndReturnsMain()
    {
        using var temp = new TempDirectory();
        var claimAllPath = Path.Combine(temp.Path, "claim_all.png");
        var claimAllDisabledPath = Path.Combine(temp.Path, "claim_all_disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(
            claimAllPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(180, 85, 200), 180, 85));
        File.WriteAllBytes(
            claimAllDisabledPath,
            CreateDisabledPng());
        File.WriteAllBytes(
            okPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));

        var missionPage = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(missionPage, 1920, 180, 85, 1600, 870, 200);
        var resultPage = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(resultPage, 1920, 160, 70, 870, 945, 180);

        var adb = new FakeAdbService(ImageCodec.EncodePng(missionPage, 1920, 1080));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(resultPage, 1920, 1080));
        var log = new TestLog();
        var service = new MissionRewardService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log);

        var ok = await service.ClaimAsync(
            "serial",
            CreateMainLayout(),
            CreateMissionLayout(),
            claimAllPath,
            claimAllDisabledPath,
            okPath,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 2000,
            threshold: 0.9);

        Assert.True(ok);
        Assert.Contains((114, 700), adb.Taps);
        Assert.Contains((1690, 912), adb.Taps);
        Assert.Contains((950, 980), adb.Taps);
        Assert.Contains((184, 977), adb.Taps);
    }

    [Fact]
    public async Task ClaimAsync_NoClaimAll_FailsWithoutRecoveryClick()
    {
        using var temp = new TempDirectory();
        var claimAllPath = Path.Combine(temp.Path, "claim_all.png");
        var claimAllDisabledPath = Path.Combine(temp.Path, "claim_all_disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(
            claimAllPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(180, 85, 200), 180, 85));
        File.WriteAllBytes(
            claimAllDisabledPath,
            CreateDisabledPng());
        File.WriteAllBytes(
            okPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));

        var blank = TestImageFactory.CreateBgra(1920, 1080, 20);
        var adb = new FakeAdbService(ImageCodec.EncodePng(blank, 1920, 1080));
        var log = new TestLog();
        var service = new MissionRewardService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log);

        var ok = await service.ClaimAsync(
            "serial",
            CreateMainLayout(),
            CreateMissionLayout(),
            claimAllPath,
            claimAllDisabledPath,
            okPath,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 200,
            threshold: 0.9);

        Assert.False(ok);
        Assert.Equal((114, 700), Assert.Single(adb.Taps));
    }

    [Fact]
    public async Task ClaimAsync_ClaimAllDisabled_ReturnsMainWithoutClaim()
    {
        using var temp = new TempDirectory();
        var claimAllPath = Path.Combine(temp.Path, "claim_all.png");
        var claimAllDisabledPath = Path.Combine(temp.Path, "claim_all_disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(
            claimAllPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(180, 85, 200), 180, 85));
        File.WriteAllBytes(
            claimAllDisabledPath,
            CreateDisabledPng());
        File.WriteAllBytes(
            okPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 110, 100, 1600, 870, 90);
        TestImageFactory.PasteBlock(screen, 1920, 110, 100, 1710, 870, 10);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var service = new MissionRewardService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log);

        var ok = await service.ClaimAsync(
            "serial",
            CreateMainLayout(),
            CreateMissionLayout(),
            claimAllPath,
            claimAllDisabledPath,
            okPath,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 2000,
            threshold: 0.9);

        Assert.True(ok);
        Assert.Contains((114, 700), adb.Taps);
        Assert.Contains((184, 977), adb.Taps);
        Assert.DoesNotContain((1710, 920), adb.Taps);
    }

    /// <summary>使用真实使命画面和随包亮暗态素材；扩大区域后仍须点击按钮中心，未知状态不授权点击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimAsync_ShippedTemplatesAndLayout_RecognizesFullButton(bool disabled)
    {
        var resources = new ResourceResolver(AppContext.BaseDirectory, "zh-CN");
        var enabledPath = resources.Resolve("resources/templates/mission/button_claim_all.png");
        var disabledPath = resources.Resolve("resources/templates/mission/button_claim_all_disabled.png");
        var okPath = resources.Resolve("resources/templates/dialog/button_ok.png");
        var mission = new UiLayoutService(new TestLog()).Load(resources.Resolve("resources/ui_layout/mission.json"));
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Mission", "claim-all-ldplayer.png")));
        if (disabled)
        {
            // 仅替换按钮状态，其余保留真实页面；验证暗态不会被误点为可领取。
            var dark = ImageCodec.DecodePng(File.ReadAllBytes(disabledPath));
            for (var row = 0; row < dark.Height; row++)
                Buffer.BlockCopy(dark.Bgra, row * dark.Width * 4, screen.Bgra,
                    ((883 + row) * screen.Width + 1600) * 4, dark.Width * 4);
        }
        var page = ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        var okImage = ImageCodec.DecodePng(File.ReadAllBytes(okPath));
        var resultPixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < okImage.Height; row++)
            Buffer.BlockCopy(okImage.Bgra, row * okImage.Width * 4, resultPixels,
                ((930 + row) * 1920 + 780) * 4, okImage.Width * 4);
        var resultFrame = ImageCodec.EncodePng(resultPixels, 1920, 1080);
        var adb = new FakeAdbService(page);
        adb.ScreenshotProvider = () => adb.Taps.Count < 2 ? page : resultFrame;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, resources: resources);
        var service = new MissionRewardService(automation, log);
        var success = await service.ClaimAsync("serial", CreateMainLayout(), mission,
            enabledPath, disabledPath, okPath, networkSettleDelayMs: 0, waitTimeoutMs: 2000);
        Assert.True(success);
        if (disabled) Assert.Equal(new[] { (114, 700), (184, 977) }, adb.Taps);
        else
        {
            Assert.Equal(4, adb.Taps.Count);
            Assert.InRange(adb.Taps[1].Item1, 1715, 1735);
            Assert.InRange(adb.Taps[1].Item2, 910, 930);
            Assert.NotEqual((mission.Find("claimAll")!.CenterX, mission.Find("claimAll")!.CenterY), adb.Taps[1]);
        }
    }

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navMission", X = 34, Y = 640, Width = 160, Height = 120 },
            new() { Name = "navHome", X = 74, Y = 900, Width = 220, Height = 155 },
        },
    };

    private static byte[] CreateDisabledPng()
    {
        var pixels = TestImageFactory.CreateBgra(220, 100, 90);
        TestImageFactory.PasteBlock(pixels, 220, 110, 100, 110, 0, 10);
        return ImageCodec.EncodePng(pixels, 220, 100);
    }

    private static UiLayout CreateMissionLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "claimAll", X = 1600, Y = 870, Width = 220, Height = 100 },
            new() { Name = "claimResultOk", X = 870, Y = 945, Width = 160, Height = 70 },
        },
    };

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-mission-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }
}
