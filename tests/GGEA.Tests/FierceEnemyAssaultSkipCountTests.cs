using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>次数 OCR 的真实像素回放、零次终止和不可信文字拒绝测试。</summary>
public sealed class FierceEnemyAssaultSkipCountTests
{
    /// <summary>使用随包模型读取 ADB 原始数值裁剪，不能用图片文件名或预期值替代识别。</summary>
    [Fact]
    public void Recognize_RealSixOfSix_ReturnsAvailable()
    {
        using var ocr = new OnnxTextRecognizer();
        var result = new FierceEnemyAssaultSkipCountRecognizer(ocr).Recognize(Frame());
        Assert.Equal(6, result.Remaining);
        Assert.True(result.CanSkip, result.Message);
    }

    /// <summary>零次为正常结束，非零才允许后续略过。</summary>
    [Theory]
    [InlineData("0/6", 0, false)]
    [InlineData("6/6", 6, true)]
    [InlineData("1 / 6", 1, true)]
    public void Decode_ValidCount_DecidesWhetherTaskMayContinue(string text, int count, bool canSkip)
    {
        var result = FierceEnemyAssaultSkipCountRecognizer.Decode(new(text, .99, .98));
        Assert.Equal(count, result.Remaining);
        Assert.Equal(canSkip, result.CanSkip);
        if (count == 0) Assert.Equal("强敌来袭略过次数为0", result.Message);
    }

    /// <summary>无斜杠、误读字母、异常范围和低置信度不能被当成还有次数。</summary>
    [Theory]
    [InlineData("O/6", .99, .99)]
    [InlineData("66", .99, .99)]
    [InlineData("7/6", .99, .99)]
    [InlineData("6/8", .99, .99)]
    [InlineData("0/6", .89, .88)]
    [InlineData("6/6", .99, .84)]
    public void Decode_UncertainCount_Stops(string text, double mean, double minimum)
    {
        var result = FierceEnemyAssaultSkipCountRecognizer.Decode(new(text, mean, minimum));
        Assert.Null(result.Remaining);
        Assert.False(result.CanSkip);
    }

    /// <summary>两帧真实次数一致才放行；识别过程不产生任何点击。</summary>
    [Fact]
    public async Task ReadCount_RealFrames_ConfirmsWithoutTap()
    {
        using var ocr = new OnnxTextRecognizer();
        var frame = Frame();
        var adb = new FakeAdbService(ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height));
        var log = new TestLog();
        var service = new FierceEnemyAssaultEntryService(new AutomationService(adb, new TemplateMatcher(), log), log,
            new(ocr), pollIntervalMs: 10);
        var result = await service.ReadCountAsync("serial");
        Assert.True(result.CanSkip, result.Message);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>事件页按钮命中后只点击一次，再读取两帧次数；标题缺失则不得点击。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnterAndRead_TitleGateAndButton_ProtectNavigation(bool hasTitle)
    {
        using var ocr = new OnnxTextRecognizer();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        void Paste(string path, int x, int y)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, path)));
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels,
                    ((y + row) * 1920 + x) * 4, image.Width * 4);
        }
        Paste(FierceEnemyAssaultEntryService.ButtonTemplate, 1496, 832);
        if (hasTitle) Paste(FierceEnemyAssaultService.TitleTemplate, 220, 20);
        var eventFrame = ImageCodec.EncodePng(pixels, 1920, 1080);
        var count = Frame();
        var countFrame = ImageCodec.EncodePng(count.Bgra, count.Width, count.Height);
        var adb = new FakeAdbService(eventFrame);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? eventFrame : countFrame;
        var log = new TestLog();
        var result = await new FierceEnemyAssaultEntryService(new AutomationService(adb, new TemplateMatcher(), log),
            log, new(ocr), timeoutMs: hasTitle ? 15000 : 150, pollIntervalMs: 10).EnterAndReadAsync("serial");
        Assert.Equal(hasTitle, result.CanSkip);
        if (hasTitle) Assert.Equal(new[] { (1675, 891) }, adb.Taps);
        else Assert.Empty(adb.Taps);
    }

    /// <summary>空白数值超时与用户取消均停止，不能把无文字视为非零次数。</summary>
    [Fact]
    public async Task ReadCount_UnknownOrCanceled_StopsWithoutTap()
    {
        using var ocr = new OnnxTextRecognizer();
        ocr.Validate();
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        var service = new FierceEnemyAssaultEntryService(new AutomationService(adb, new TemplateMatcher(), log), log,
            new(ocr), timeoutMs: 150, pollIntervalMs: 10);
        Assert.False((await service.ReadCountAsync("serial")).CanSkip);
        Assert.Single(log.Entries, e => e.Message == "等待强敌来袭略过次数连续两帧确认。");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadCountAsync("serial", cts.Token));
        Assert.Empty(adb.Taps);
    }
    /// <summary>原像素放回同一坐标，其余画面清空，不伪造真实游戏截图。</summary>
    private static ScreenImage Frame()
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-skip-count.png")));
        var roi = FierceEnemyAssaultSkipCountRecognizer.CountRegion;
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels,
                ((roi.Y + row) * 1920 + roi.X) * 4, crop.Width * 4);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
    }
}
