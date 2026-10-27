using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>实机编号字形、两帧稳定确认及真实标签对照，避免数字局部笔画和动画误授权出击。</summary>
public sealed class StageNodeRecognizerTests : IDisposable
{
    private static readonly string Templates = Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage/node_states");
    private readonly OnnxStageNumberRecognizer _ocr = new();

    /// <summary>将实机 CLEAR 字形放到已知选中节点的状态区；此为明确合成的状态回归，不冒充实机选关截图。</summary>
    [Theory]
    [InlineData(false, WorkStageNodeState.Cleared)]
    [InlineData(true, WorkStageNodeState.Incomplete)]
    public void SelectedHard_ClearBadge_PreservesPassedStateAndColorGate(bool gray, WorkStageNodeState expected)
    {
        var frame = HardTwoFrame();
        var before = Recognize(frame)!;
        var badge = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(Templates, "clear_badge.png")));
        if (gray)
            for (var i = 0; i < badge.Bgra.Length; i += 4)
            {
                var value = (byte)((badge.Bgra[i] + badge.Bgra[i + 1] + badge.Bgra[i + 2]) / 3);
                badge.Bgra[i] = badge.Bgra[i + 1] = badge.Bgra[i + 2] = value;
            }
        Paste(frame, badge, before.CenterX - badge.Width / 2, before.CenterY - 70);
        var node = Recognize(frame);
        Assert.Equal(before.Position, node?.Position);
        Assert.Equal(expected, node?.State);
        Assert.Equal(!gray, node?.HasPassed);
    }

    /// <summary>反馈图的 HARD 2 标签高于 y=600；必须识别为第 22 关，不能当作 NORMAL 2。</summary>
    [Fact]
    public void SelectedHardTwo_RealFeedback_PreservesKindAndNumber()
    {
        var node = Recognize(HardTwoFrame());
        Assert.NotNull(node);
        Assert.Equal(WorkStageKind.Hard, node.Kind);
        Assert.Equal(2, node.Number);
        Assert.Equal(22, node.Position?.OverallIndex);
        Assert.Equal(WorkStageNodeState.Incomplete, node.State);
    }

    /// <summary>合成橙色光柱连接真实光圈，模拟呼吸动画连通域变高；不能因此丢失台座。</summary>
    [Fact]
    public void SelectedHardTwo_ConnectedOrangeGlow_PreservesSelection()
    {
        var frame = HardTwoFrame();
        // 只从环底向下连接光晕，保留上半环的连续证据；不能拿抹掉环形的画面要求识别成功。
        TestImageFactory.PasteColoredBlock(frame.Bgra, frame.Width, 24, 100, 580, 860, 210, 120, 60);
        var node = Recognize(frame);
        Assert.Equal(WorkStageKind.Hard, node?.Kind);
        Assert.Equal(2, node?.Number);
        Assert.Equal(22, node?.Position?.OverallIndex);
    }

    /// <summary>两个台座同时出现选中效果时不能选面积更大的一个；合成第二光圈只用于安全反例。</summary>
    [Fact]
    public void MultipleSelectedHalos_DoNotChooseLargest()
    {
        var frame = HardTwoFrame();
        for (var y = 800; y < 870; y++)
            Buffer.BlockCopy(frame.Bgra, (y * frame.Width + 465) * 4,
                frame.Bgra, (y * frame.Width + 100) * 4, 260 * 4);
        Assert.Null(Recognize(frame));
    }

    /// <summary>原尺寸回放附件轨道；原附件为 1915×1075，测试仅补黑边，不缩放像素或放宽生产规格。</summary>
    private static ScreenImage HardTwoFrame()
    {
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/StageNodes/hard2-selected-feedback.png")));
        Paste(screen, crop, 0, 400);
        return screen;
    }

    /// <summary>COMPLETE 遮挡光圈中部时，真实两帧仍应定位 HARD 3，队列直接完成作品而不再次出击。</summary>
    [Fact]
    public async Task SelectedAsync_CompletedHardThreeSplitHalo_CompletesWithoutBattle()
    {
        var first = CompletedHardThreeFrame(1); var second = CompletedHardThreeFrame(2);
        var adb = new FakeAdbService(ImageCodec.EncodePng(first.Bgra, first.Width, first.Height));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(second.Bgra, second.Width, second.Height));
        using var recognizer = new StageNodeRecognizer(adb, new TemplateMatcher(), new TestLog(), Templates,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ocr);
        var node = await recognizer.RecognizeSelectedAsync("test", Work());
        Assert.NotNull(node);
        Assert.Equal(WorkStageKind.Hard, node.Kind);
        Assert.Equal(3, node.Number);
        Assert.Equal(WorkStageNodeState.Completed, node.State);
        Assert.Equal(23, node.Position?.OverallIndex);
        Assert.InRange(node.CenterX, 780, 800);
        Assert.Equal(2, adb.ScreenshotCount);
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(node),
            (_, _) => throw new InvalidOperationException("作品已完成，不得继续读取下一关"),
            (_, _) => throw new InvalidOperationException("已通关 HARD 3 不得再次进入战斗"));
        var result = await runner.RunAsync(new[] { Work() }, _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedWorks);
        Assert.Equal(0, result.CompletedStages);
        Assert.Empty(adb.Taps);
    }

    /// <summary>只剩星级行或单侧黄弧时不得选中邻近的 HARD 1，更不能授权重复出击。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedHardThree_StarsOrSingleArc_DoNotAuthorize(bool starsOnly)
    {
        var screen = CompletedHardThreeFrame(1);
        TestImageFactory.PasteColoredBlock(screen.Bgra, screen.Width, starsOnly ? 1015 : 100, 180,
            starsOnly ? 0 : 650, 800, 0, 0, 0);
        Assert.Null(Recognize(screen));
    }

    /// <summary>只回放原始关卡轨道裁剪，不保存账号与右侧无关内容。</summary>
    private static ScreenImage CompletedHardThreeFrame(int frame)
    {
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            $"Fixtures/StageNodes/hard3-complete-frame{frame}-mumu5556.png")));
        Paste(screen, crop, 0, 600);
        return screen;
    }

    /// <summary>保留反馈截图的轨道原像素，确认编号、类型和作品序列同时正确。</summary>
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(18)]
    public void NormalNode_RealScreenshots_PreservesNumber(int number)
    {
        var result = Recognize(CreateScreen(number));
        Assert.NotNull(result);
        Assert.Equal(WorkStageKind.Normal, result.Kind);
        Assert.Equal(number, result.Number);
        Assert.Equal(number, result.Position?.OverallIndex);
        Assert.Equal(WorkStageNodeState.Incomplete, result.State);
    }

    /// <summary>覆盖节点移动后的反馈画面，原算法此时 0/8 仅差 3.3 个百分点，仍会拒绝编号 10。</summary>
    [Fact]
    public void NormalTen_ShiftedScreenshot_PreservesNumber()
    {
        var result = Recognize(CreateScreen(10, shifted: true));
        Assert.NotNull(result);
        Assert.Equal(10, result.Number);
        Assert.Equal(10, result.Position?.OverallIndex);
    }

    /// <summary>同一第 10 关截图里的真实 8 仍须识别为 8，不能靠偏向数字 0 来修复本次反馈。</summary>
    [Theory]
    [InlineData(false, 175)]
    [InlineData(true, 130)]
    public void Ocr_RealEight_PreservesNumber(bool shifted, int centerX)
    {
        var result = _ocr
            .Recognize(CreateScreen(10, shifted), new Roi(centerX - 85, 600, 170, 150), centerX);
        Assert.Equal(8, result.Number);
    }

    /// <summary>移除编号后保留真实 NEXT/橙色箭头，再添加白色闪光块，均不能伪造可信数字。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumberMissingOrWhiteFlash_DoesNotGuess(bool whiteFlash)
    {
        var screen = CreateScreen(9);
        TestImageFactory.PasteColoredBlock(screen.Bgra, screen.Width, 110, 80, 455, 685, 0, 0, 0);
        if (whiteFlash) TestImageFactory.PasteColoredBlock(screen.Bgra, screen.Width, 26, 50, 496, 703, 255, 255, 255);
        var result = Recognize(screen);
        Assert.NotNull(result);
        Assert.Null(result.Number);
        Assert.Null(result.Position);
    }

    /// <summary>三位数字不能被截取为两位并授权出击；重复 1 必须由独立连通域保留。</summary>
    [Fact]
    public void ThreeDigitLine_DoesNotAcceptPartialNumber()
    {
        var screen = CreateScreen(9);
        TestImageFactory.PasteColoredBlock(screen.Bgra, screen.Width, 110, 80, 455, 685, 0, 0, 0);
        // 从已有第 11 关反馈截图提取完整的首个 1，避免测试继续依赖淘汰的数字模板。
        var oneRegion = new Roi(420, 695, 28, 65);
        var binary = OnnxTextRecognizer.CreateWhiteMask(CreateScreen(11), oneRegion, default);
        var pixels = TestImageFactory.CreateBgra(oneRegion.Width, oneRegion.Height, 0);
        for (var index = 0; index < binary.Length; index++)
            pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = binary[index];
        var mask = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, oneRegion.Width, oneRegion.Height));
        foreach (var x in new[] { 475, 500, 525 }) Paste(screen, mask, x, 703);
        var number = _ocr.Recognize(screen, new Roi(424, 600, 170, 200), 509);
        Assert.Contains("不符合一至两位完整数字", number.Message);
        var result = Recognize(screen);
        Assert.NotNull(result);
        Assert.Null(result.Number);
        Assert.Null(result.Position);
    }

    /// <summary>编号被 ROI 左边界切断时不把 9 的残笔当作别的数字，也不把 14 截成 4。</summary>
    [Theory]
    [InlineData(9, 505)]
    [InlineData(14, 490)]
    public void Ocr_ClippedNumber_ReturnUnknown(int number, int left)
    {
        var result = _ocr
            .Recognize(CreateScreen(number), new Roi(left, 600, 594 - left, 230), 509);
        Assert.Null(result.Number);
    }

    /// <summary>两帧编号/选中节点一致才返回结果；关卡滚动中的不同编号由现有导航流程继续重试。</summary>
    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 9)]
    [InlineData(false, 10)]
    public async Task SelectedAsync_RequiresTwoStableFrames(bool changing, int number)
    {
        var first = CreateScreen(number);
        var second = CreateScreen(changing ? 11 : number);
        var adb = new FakeAdbService(ImageCodec.EncodePng(first.Bgra, first.Width, first.Height));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(second.Bgra, second.Width, second.Height));
        var log = new TestLog();
        var delays = new List<int>();
        var recognizer = new StageNodeRecognizer(adb, new TemplateMatcher(), log, Templates,
            (ms, ct) => { ct.ThrowIfCancellationRequested(); delays.Add(ms); return Task.CompletedTask; }, _ocr);
        var result = await recognizer.RecognizeSelectedAsync("test", Work());
        if (changing)
        {
            Assert.Null(result);
            Assert.Contains(log.Entries, entry => entry.Level == "INFO" && entry.Message.Contains("尚未稳定"));
        }
        else Assert.Equal(number, result?.Number);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Equal(new[] { 500 }, delays);
        Assert.Empty(adb.Taps);
    }

    /// <summary>两帧中节点与字形像素微移，仍需在既有位置容差内确认同一个 10，且不执行点击。</summary>
    [Fact]
    public async Task SelectedAsync_ShiftedTen_ConfirmsStableNumber()
    {
        var first = CreateScreen(10, shifted: true);
        var second = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
        Paste(second, first, 1, 0, first.Width - 1);
        var adb = new FakeAdbService(ImageCodec.EncodePng(first.Bgra, first.Width, first.Height));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(second.Bgra, second.Width, second.Height));
        var recognizer = new StageNodeRecognizer(adb, new TemplateMatcher(), new TestLog(), Templates,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ocr);
        Assert.Equal(10, (await recognizer.RecognizeSelectedAsync("test", Work()))?.Number);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>18 的光圈中心随呼吸动画移动，编号固定时两帧应确认；编号真的移动时仍拒绝。</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(-14, false)]
    public async Task SelectedAsync_EighteenHaloAnimation_TracksNumberPosition(int verticalShift, bool expected)
    {
        var first = CreateScreen(18);
        var second = CreateScreen(18, shifted: true);
        if (verticalShift != 0)
        {
            var moved = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
            var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures/StageNodes/normal18-frame2-mumu5556.png")));
            Paste(moved, crop, 0, 600 + verticalShift);
            second = moved;
        }
        var adb = new FakeAdbService(ImageCodec.EncodePng(first.Bgra, first.Width, first.Height));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(second.Bgra, second.Width, second.Height));
        var recognizer = new StageNodeRecognizer(adb, new TemplateMatcher(), new TestLog(), Templates,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ocr);
        if (!expected)
        {
            var before = recognizer.RecognizeSelected(first, Work())!;
            var after = recognizer.RecognizeSelected(second, Work())!;
            // 动画偏移抵消实际移动时，仅看光圈会误认为稳定；编号的位置必须阻止这次确认。
            Assert.InRange(Math.Abs(before.CenterY - after.CenterY), 0, 12);
            Assert.True(Math.Abs(before.NumberRegion!.Value.CenterY - after.NumberRegion!.Value.CenterY) > 12);
        }
        var result = await recognizer.RecognizeSelectedAsync("test", Work());
        if (expected) Assert.Equal(18, result?.Number); else Assert.Null(result);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>取消发生在两帧间时不再截图，也不返回第一帧的编号作为确认成功。</summary>
    [Fact]
    public async Task SelectedAsync_CancelledBetweenFrames_DoesNotConfirm()
    {
        var screen = CreateScreen(9);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height));
        using var cancellation = new CancellationTokenSource();
        var recognizer = new StageNodeRecognizer(adb, new TemplateMatcher(), new TestLog(), Templates,
            (_, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ocr);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.RecognizeSelectedAsync("test", Work(), cancellation.Token));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>三种随包标签在选中节点上方仍须有效；少量横向偏移保留容差。</summary>
    [Theory]
    [InlineData("label_hard", WorkStageKind.Hard, 0)]
    [InlineData("label_hard", WorkStageKind.Hard, 24)]
    [InlineData("label_boss", WorkStageKind.Boss, 0)]
    [InlineData("label_side_story", WorkStageKind.SideStory, 0)]
    public void RealLabelGlyphs_AreRecognized(string name, WorkStageKind kind, int offset)
    {
        var screen = CreateScreen();
        var label = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(Templates, name + ".png")));
        Paste(screen, label, 450 - label.Width / 2 + offset, 620);
        Assert.Equal(kind, Recognize(screen)!.Kind);
    }

    /// <summary>大块同色背景不是字形，即使在节点中心且有大量红色，也不能识别为 HARD。</summary>
    [Fact]
    public void RedBlockAboveSelectedNode_IsNotHard()
    {
        var screen = CreateScreen();
        TestImageFactory.PasteColoredBlock(screen.Bgra, screen.Width, 132, 62, 384, 620, 255, 30, 65);
        Assert.Equal(WorkStageKind.Normal, Recognize(screen)!.Kind);
    }

    /// <summary>邻近节点的真实 HARD 字形不能改变当前普通节点类型。</summary>
    [Fact]
    public void NeighborHardLabel_DoesNotChangeSelectedNode()
    {
        var screen = CreateScreen();
        var label = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(Templates, "label_hard.png")));
        Paste(screen, label, 450 - label.Width / 2 - 74, 620);
        Assert.Equal(WorkStageKind.Normal, Recognize(screen)!.Kind);
    }

    /// <summary>作品有 20 个普通关卡和 3 个 HARD，与反馈账号当前作品一致。</summary>
    private StageNodeRecognition? Recognize(ScreenImage screen)
        => new StageNodeRecognizer(new FakeAdbService([]), new TemplateMatcher(), new TestLog(), Templates, numberRecognizer: _ocr)
            .RecognizeSelected(screen, Work());

    /// <summary>所有编号验证使用同一作品范围，不借用配置猜测当前关卡。</summary>
    private static HardStageEntry Work() => new() { Id = 20, Work = "Mobile Suit Victory Gundam", NormalCount = 20, HardCount = 3 };

    /// <summary>每个测试释放自己的模型会话，不将原生缓存跨测试共享。</summary>
    public void Dispose() => _ocr.Dispose();

    /// <summary>只存储左下轨道，重建其原坐标，避免把账号、AP 等无关截图内容纳入仓库。</summary>
    private static ScreenImage CreateScreen(int number = 11, bool shifted = false)
    {
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
        var device = number is 9 or 10 or 18 ? "mumu5556" : "mumu5558";
        var variant = shifted ? number == 18 ? "-frame2" : "-shifted" : "";
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, $"Fixtures/StageNodes/normal{number}{variant}-{device}.png")));
        Paste(screen, crop, 0, 600);
        return screen;
    }

    /// <summary>原尺寸复制像素，不缩放或重新绘制实机字形。</summary>
    private static void Paste(ScreenImage screen, ScreenImage patch, int x, int y, int? width = null)
    {
        for (var row = 0; row < patch.Height; row++)
            Buffer.BlockCopy(patch.Bgra, row * patch.Width * 4, screen.Bgra, ((y + row) * screen.Width + x) * 4, (width ?? patch.Width) * 4);
    }
}
