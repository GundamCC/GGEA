using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public class DeliveryPageTests
{
    // 使用实机的无突破、金星和彩星样本，并独立复核 LV60 边界的两个设置。
    [Theory]
    [InlineData(true, true, 580, 20)]
    [InlineData(true, false, 1150, 60)]
    [InlineData(false, true, 770, 20)]
    [InlineData(false, false, 770, 20)]
    public void ChoosesOnlyPermittedCards(bool stars, bool level, int expectedX, int expectedLevel)
    {
        var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = new byte[1920 * 1080 * 4] };
        void Paste(string path, int x, int y)
        {
            var patch = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var i = 0; i < patch.Height; i++)
                Buffer.BlockCopy(patch.Bgra, i * patch.Width * 4, frame.Bgra, ((y + i) * 1920 + x) * 4, patch.Width * 4);
        }
        void Fixture(string name, int x, int y) => Paste(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Base", name + ".png"), x, y);
        void Label(string name, int x, int y) => Paste(ConfigService.ResolvePath("resources/templates/base/character_requests/" + name + ".png"), x, y);
        Fixture("delivery_bottom_row", 490, 600);
        Fixture("delivery_target", 503, 286);
        Fixture("delivery_empty_count", 1337, 799);
        Fixture("delivery_amount_one", 1000, 343);
        Label("title_confirm_delivery", 874, 71);
        Label("button_cancel_delivery", 729, 967);
        for (var y = 654; y <= 848; y++) Array.Fill(frame.Bgra, (byte)245, (y * 1920 + 1440) * 4, 3);
        Assert.True(DeliveryPage.HasEmptyStarRow(frame, 580, 642));
        Assert.False(DeliveryPage.HasEmptyStarRow(frame, 770, 642));
        Fixture("delivery_one_star", 720, 663);
        Assert.False(DeliveryPage.HasEmptyStarRow(frame, 770, 546));
        Fixture("delivery_three_stars", 910, 579);
        Assert.False(DeliveryPage.HasEmptyStarRow(frame, 960, 462));
        Fixture("delivery_bottom_row", 490, 600);
        // 保留二星 LV20 和无突破 LV60；两项全开时只额外提供第一台无突破 LV20。
        for (var y = 600; y < 779; y++)
        {
            if (!stars || !level) Array.Clear(frame.Bgra, (y * 1920 + 490) * 4, 180 * 4);
            Array.Clear(frame.Bgra, (y * 1920 + 870) * 4, 180 * 4);
        }
        using var ocr = new OnnxTextRecognizer();
        var view = new DeliveryPage(ocr, new(stars, level), 1).Read(frame, default);
        Assert.NotNull(view);
        Assert.True(view.AtBottom);
        Assert.Equal(expectedX, view.Candidate?.X);
        Assert.Equal(expectedLevel, view.Candidate?.Level);
        if (!stars && !level) Assert.Null(new DeliveryPage(ocr, new(true, true), 1).Read(frame, default)?.Candidate);
    }
}
