using System.IO;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>真实速度按钮与用户原图回放，验证颜色状态和同位置 MENU 的隔离。</summary>
public sealed class SpeedButtonRecognizerTests
{
    /// <summary>同批实机呼吸相位验证二倍速保持开启；不能因明暗波动授权重复切换。</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void BreathingPhases_KeepOnState(int phase, bool visible)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", $"mumu-buttons-phase-{phase}.png")));
        var result = Recognizer().Recognize(screen, new(200, 0, 200, 115));
        if (!visible) { Assert.Null(result); return; }
        Assert.True(result?.State == SpeedButtonState.On, $"phase={phase}; result={result}");
    }

    /// <summary>灰色用户截图与两种原始素材都必须按颜色判断，不受周边青色影响。</summary>
    [Theory]
    [InlineData("double_speed_on", SpeedButtonState.On)]
    [InlineData("double_speed_close", SpeedButtonState.Off)]
    [InlineData("user", SpeedButtonState.Off)]
    public void RealButton_ColorDeterminesState(string name, SpeedButtonState expected)
    {
        var image = Read(name);
        var bgra = TestImageFactory.CreateBgra(300, 115, 0);
        TestImageFactory.PasteColoredBlock(bgra, 300, 300, 115, 0, 0, 50, 180, 230);
        for (var y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Bgra, y * image.Width * 4, bgra, ((y + 10) * 300 + 10) * 4, image.Width * 4);
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(bgra, 300, 115));
        Assert.Equal(expected, Recognizer().Recognize(screen, new Roi(0, 0, 300, 115))?.State);
    }

    /// <summary>MENU 与速度交替占位：没有双箭头时不允许把灰色或青色背景当成速度按钮。</summary>
    [Theory]
    [InlineData("menu")]
    [InlineData("blank")]
    public void NonSpeedButton_IsRejected(string name)
    {
        var screen = name == "blank"
            ? ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(300, 115, 220), 300, 115))
            : Read(name);
        Assert.Null(Recognizer().Recognize(screen, new Roi(0, 0, screen.Width, screen.Height)));
    }

    /// <summary>保留真实箭头，仅注入异常红、蓝或绿底；有字形也不能把未知颜色当作关闭。</summary>
    [Theory]
    [InlineData(60, 60, 180)]
    [InlineData(180, 60, 30)]
    [InlineData(60, 180, 60)]
    public void UnexpectedColor_WithRealGlyph_RemainsUnknown(byte blue, byte green, byte red)
    {
        var screen = Read("double_speed_close");
        for (var i = 0; i < screen.Bgra.Length; i += 4)
        {
            if (screen.Bgra[i] >= 200 && screen.Bgra[i + 1] >= 200 && screen.Bgra[i + 2] >= 90) continue;
            screen.Bgra[i] = blue;
            screen.Bgra[i + 1] = green;
            screen.Bgra[i + 2] = red;
        }
        Assert.Equal(SpeedButtonState.Unknown,
            Recognizer().Recognize(screen, new Roi(0, 0, screen.Width, screen.Height))?.State);
    }

    /// <summary>原始模板路径与测试原像素均不依赖模拟器。</summary>
    private static ScreenImage Read(string name) => ImageCodec.DecodePng(File.ReadAllBytes(name == "user"
        ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", "speed-off-user.png")
        : ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png")));

    /// <summary>与生产使用相同的两种素材。</summary>
    private static SpeedButtonRecognizer Recognizer() => new(
        ConfigService.ResolvePath("resources/templates/stage/sortie/double_speed_on.png"),
        ConfigService.ResolvePath("resources/templates/stage/sortie/double_speed_close.png"));
}
