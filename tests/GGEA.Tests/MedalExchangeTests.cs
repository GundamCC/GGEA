using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public sealed class MedalExchangeTests
{
    private static ScreenImage LoadStrip(string name, int x, int y)
    {
        var frame = TestImageFactory.CreateBgra(1920, 1080, 12);
        var im = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath($"Fixtures/Navigation/{name}.png")));
        for (var i = 0; i < im.Height; i++)
            Buffer.BlockCopy(im.Bgra, i * im.Width * 4, frame, ((y + i) * 1920 + x) * 4, im.Width * 4);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(frame, 1920, 1080));
    }

    [Theory]
    [InlineData("pool-next-key", 0, 3)]
    [InlineData("pool-full-key", 7, 7)]
    [InlineData("reward-status-0", 5, 7)]
    [InlineData("pool-next-general", 0, 997)]
    [InlineData("pool-full-general", 793, 793)]
    [InlineData("exchange100-after-general", 685, 793)]
    [InlineData("reward-status-1", 591, 793)]
    public void ReadCategoryFromCapturedRewardRow(string source, int obtained, int total)
    {
        using var ocr = new OnnxTextRecognizer();
        var frame = LoadStrip("medal-count-" + source, 1185, 390);
        Assert.Equal(new RewardCount(obtained, total), new MedalPoolReader(ocr).Count(frame, 409, default));
    }

    [Fact]
    public void ReadMedalBalanceWithThousandsSeparator()
    {
        using var ocr = new OnnxTextRecognizer();
        var frame = LoadStrip("medal-balance", 1762, 33);
        Assert.Equal(3327, new MedalPoolReader(ocr).Balance(frame, default));
    }

    [Fact]
    public void PartialBatchKeepsCurrentPoolUntilBothCategoriesAreFull()
    {
        var before = new MedalPool(new(5, 7), new(591, 793));
        var after = new MedalPool(new(7, 7), new(685, 793));
        Assert.Equal(96, after.GainSince(before, 100));
        Assert.Equal(108, after.Remaining);
        Assert.Throws<InvalidOperationException>(() => before.GainSince(after, 100));
        Assert.Throws<InvalidOperationException>(() => after.GainSince(after, 100));
        Assert.Equal(0, new MedalPool(new(7, 7), new(793, 793)).Remaining);
        Assert.True(new MedalPool(new(0, 3), new(0, 997)).IsNew);
    }
}
