using System.IO;
using System.IO.Compression;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>MuMu 16416 逐关实测的失败帧回归；保留呼吸光柱、COMPLETE 遮挡与碎片，禁止合成成功画面。</summary>
public sealed class StageNodeLiveFeedbackTests : IDisposable
{
    private static readonly string Templates = Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage/node_states");
    private static readonly HardStageEntry Work = new() { Id = 20, Work = "Mobile Suit Victory Gundam", NormalCount = 20, HardCount = 3 };
    private readonly OnnxStageNumberRecognizer _numbers = new(TapToNextOcrSamples.SharedOcr);

    /// <summary>MuMu 5558 的 Z 高达选关页保持原识别阈值；返回稳定后两帧应确认 HARD 2，而非猜测上一关加一。</summary>
    [Fact]
    public async Task ZetaHardTwo_AfterReturn_ConfirmsActualNode()
    {
        var adb = new FakeAdbService(Encode(Frame("hard2-z", "mumu5558")));
        using var recognizer = Recognizer(adb);
        var node = await recognizer.RecognizeSelectedAsync("test",
            new HardStageEntry { Work = "Mobile Suit Zeta Gundam", NormalCount = 15, HardCount = 3 });
        Assert.Equal(new WorkStagePosition(WorkStageKind.Hard, 2, 17), node?.Position);
        Assert.Equal(WorkStageNodeState.Incomplete, node?.State);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>同一节点的真实两帧都应可用：高光柱、已通关分裂环和台座碎片不能导致等待耗尽。</summary>
    [Theory]
    [InlineData("hard3", WorkStageKind.Hard, 3, WorkStageNodeState.Incomplete)]
    [InlineData("hard2-complete", WorkStageKind.Hard, 2, WorkStageNodeState.Completed)]
    [InlineData("normal3", WorkStageKind.Normal, 3, WorkStageNodeState.Completed)]
    public async Task BreathingFrames_ConfirmActualSelectedNode(string fixture, WorkStageKind kind, int number, WorkStageNodeState state)
    {
        var adb = new FakeAdbService(Encode(Frame(fixture + "-1")));
        adb.EnqueueScreenshots(Encode(Frame(fixture + "-2")));
        using var recognizer = Recognizer(adb);
        var node = await recognizer.RecognizeSelectedAsync("test", Work);
        Assert.NotNull(node);
        Assert.Equal(kind, node.Kind);
        Assert.Equal(number, node.Number);
        Assert.Equal(state, node.State);
        Assert.Equal(kind == WorkStageKind.Hard ? 20 + number : number, node.Position?.OverallIndex);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>右侧面板遮住半个光圈时应安全等待，不能把仅剩一侧黄弧当成完整选中证据。</summary>
    [Fact]
    public void PartlyCoveredHalo_DoesNotAuthorizeSelection()
    {
        using var recognizer = Recognizer(new FakeAdbService([]));
        Assert.Null(recognizer.RecognizeSelected(Frame("normal13-edge-2"), Work));
    }

    /// <summary>HARD 3 未锁定台座曾以 81.45% 命中 BOSS 锁定模板；状态必须绑定已确认的 HARD 类型。</summary>
    [Fact]
    public void CompletedHardTwo_NextHardThreeIsNotMistakenForLockedBoss()
    {
        using var recognizer = Recognizer(new FakeAdbService([]));
        var next = recognizer.RecognizeNext(Frame("hard2-complete-2"), Work);
        Assert.Equal(new WorkStagePosition(WorkStageKind.Hard, 3, 23), next?.Position);
        Assert.InRange(next!.CenterX, 780, 800);
        Assert.InRange(next.CenterY, 715, 740);
    }

    /// <summary>从已通过 HARD 2 原页点击真实 NEXT，再在真实 HARD 3 新帧复核；本测试不执行战斗。</summary>
    [Fact]
    public async Task CurrentPage_RealHardTwoToHardThree_SelectsAndVerifiesOnce()
    {
        var before = Encode(Frame("hard2-complete-1"));
        var after = Encode(Frame("hard3-1"));
        var adb = new FakeAdbService(before);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? before : after;
        var log = new TestLog();
        using var recognizer = Recognizer(adb);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        var stage = new StageService(automation, new FakeStageWorkRecognizer(), log,
            new StageNavigationOptions { PageSettleMs = 0, TitlePollIntervalMs = 1 }, nodeRecognizer: recognizer);
        var selected = await stage.ReadCurrentWorkForProgressAsync("test", Work, default);
        Assert.Equal(new WorkStagePosition(WorkStageKind.Hard, 3, 23), selected.Position);
        Assert.Equal(WorkStageNodeState.Incomplete, selected.State);
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, 780, 800);
        Assert.InRange(tap.Y, 715, 740);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>故障注入只移除光圈黄像素，保留实机 NEXT、编号和台座；编号失败不得重选，仍保存真实观察帧。</summary>
    [Fact]
    public async Task MissingHalo_DoesNotReselectAndSavesActualFailureFrame()
    {
        var actual = Frame("hard2-next", "mumu16448");
        var missing = Frame("hard2-next", "mumu16448");
        for (var y = 800; y < 980; y++)
            for (var x = 0; x < 1015; x++)
            {
                var offset = (y * missing.Width + x) * 4;
                var b = missing.Bgra[offset]; var g = missing.Bgra[offset + 1]; var r = missing.Bgra[offset + 2];
                if (r >= 235 && g >= 200 && b <= 180 && r - b >= 55)
                    Array.Clear(missing.Bgra, offset, 3);
            }
        var before = Encode(missing); var after = Encode(actual);
        var adb = new FakeAdbService(before);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? before : after;
        var directory = Path.Combine(Path.GetTempPath(), "ggea-stage-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new TestLog();
            using var recognizer = new StageNodeRecognizer(adb, new TemplateMatcher(), log, Templates,
                (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _numbers,
                new RecognitionEvidence(directory, log));
            Assert.Null(recognizer.RecognizeSelected(missing, Work));
            Assert.Equal(new WorkStagePosition(WorkStageKind.Hard, 2, 22), recognizer.RecognizeNext(missing, Work)?.Position);
            using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
            var stage = new StageService(automation, new FakeStageWorkRecognizer(), log,
                new StageNavigationOptions { PageSettleMs = 0, TitlePollIntervalMs = 1 }, nodeRecognizer: recognizer);
            var selected = await stage.ReadCurrentWorkForProgressAsync("test", Work, default);
            Assert.Equal(StageNodeRecognition.Unidentified, selected);
            Assert.Empty(adb.Taps);
            Assert.Empty(adb.Swipes);
            using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(directory, "*.zip")));
            using var input = zip.GetEntry("frame.png")!.Open();
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            Assert.Equal(before, copy.ToArray());
            // 随后成功观察应清除失败帧；不会因另一个设备或后续成功状态重复取证。
            adb.ScreenshotProvider = () => after;
            Assert.NotNull((await recognizer.RecognizeSelectedAsync("test", Work))?.Position);
            await recognizer.SaveLastFailureAsync("other");
            await recognizer.SaveLastFailureAsync("test");
            Assert.Single(Directory.GetFiles(directory, "*.zip"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    /// <summary>真实截图只裁掉无关账号区域，恢复原坐标，不缩放、不重绘选中效果。</summary>
    private static ScreenImage Frame(string fixture, string device = "mumu16416")
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            $"Fixtures/StageNodes/{fixture}-{device}.png")));
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels, ((row + 400) * 1920) * 4, crop.Width * 4);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
    }

    /// <summary>测试仅替换 ADB 与等待，模板、像素和识别门槛均沿用生产实现。</summary>
    private StageNodeRecognizer Recognizer(FakeAdbService adb) => new(adb, new TemplateMatcher(), new TestLog(),
        Templates, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _numbers);

    /// <summary>通过实际编解码路径进入截图测试，不让内存修改与 Bitmap 内容不一致。</summary>
    private static byte[] Encode(ScreenImage frame) => ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);

    /// <summary>编号识别器借用公共 OCR，会话生命周期仍由公共测试夹具管理。</summary>
    public void Dispose() => _numbers.Dispose();
}
