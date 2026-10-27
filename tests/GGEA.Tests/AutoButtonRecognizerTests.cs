using System.IO;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证 AUTO 状态必须由同一位置、同一状态的字形与颜色共同证明。</summary>
public sealed class AutoButtonRecognizerTests : IDisposable
{
    /// <summary>本次雷电卡住时的红色 AUTO 必须识别为敌军自动，不能当成关闭或全部自动。</summary>
    [Fact]
    public void LdPlayerActualEnemy_IsRecognized()
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Sortie/auto-enemy-ldplayer-20261026.png")));
        Assert.Equal(AutoButtonState.Enemy, Recognizer().Recognize(screen, new(28, 15, 120, 70))?.State);
    }

    private readonly string _commonDirectory = Path.Combine(Path.GetTempPath(), "ggea-auto-common-" + Guid.NewGuid().ToString("N"));

    /// <summary>释放本测试隔离复制的素材目录。</summary>
    public void Dispose() { if (Directory.Exists(_commonDirectory)) Directory.Delete(_commonDirectory, true); }

    /// <summary>当前 MuMu 关卡介绍页关闭按钮与通用素材兼容，不能误认成全部自动或敌方自动。</summary>
    [Theory]
    [InlineData("close", AutoButtonState.Close)]
    [InlineData("all", AutoButtonState.All)]
    public void MuMuCurrentButton_UsesCommonTemplates(string state, AutoButtonState expected)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", $"mumu-auto-{state}-current.png")));
        var result = CommonRecognizer().Recognize(screen, new(0, 0, screen.Width, screen.Height));
        Assert.NotNull(result);
        Assert.Equal(expected, result.State);
        Assert.InRange(result.Match.CenterX, 100, 120);
        Assert.InRange(result.Match.CenterY, 45, 65);
    }

    /// <summary>独立青色实帧即使处于较暗呼吸相位也保持全部自动；不依赖测试输出里残留的语言素材。</summary>
    [Theory]
    [InlineData("auto-all")]
    [InlineData("auto-uncertain")]
    public void CommonAuto_RecognizesRealBlueFrames(string name)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", $"ja-progress-{name}.png")));
        Assert.Equal(AutoButtonState.All, CommonRecognizer().Recognize(screen, new(1400, 0, 200, 115))?.State);
    }

    /// <summary>使用隔离目录中的通用素材，避免全局语言映射遮蔽本次验证目标。</summary>
    private AutoButtonRecognizer CommonRecognizer()
    {
        // 通用路径可能被资源解析器改写到语言目录，先放入测试生命周期管理的外部目录。
        Directory.CreateDirectory(_commonDirectory);
        string Copy(string name)
        {
            var path = Path.Combine(_commonDirectory, $"auto_{name}.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "resources", "common", "templates", "stage", "sortie", $"auto_{name}.png"), path, true);
            return path;
        }
        return new(new TemplateMatcher(), Copy("all"), Copy("enemy"), Copy("close"));
    }

    /// <summary>连续实帧覆盖青色呼吸亮暗变化；全部自动不可因动画被判为关闭或敌方自动。</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void BreathingPhases_KeepAllState(int phase, bool visible)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", $"mumu-buttons-phase-{phase}.png")));
        var result = CommonRecognizer().Recognize(screen, new(0, 0, 200, 115));
        if (!visible) { Assert.Null(result); return; }
        Assert.True(result?.State == AutoButtonState.All, $"phase={phase}; result={result}");
    }

    /// <summary>实际三态素材均能识别，避免通过一律返回未知来阻止误点。</summary>
    [Theory]
    [InlineData("all", AutoButtonState.All)]
    [InlineData("enemy", AutoButtonState.Enemy)]
    [InlineData("close", AutoButtonState.Close)]
    public void ActualTemplates_KeepTheirState(string name, AutoButtonState expected)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(PathFor(name)));
        Assert.Equal(expected, Recognizer().Recognize(screen, new(0, 0, screen.Width, screen.Height))?.State);
    }

    /// <summary>合成暖色混入的关闭字形曾被旧实现判为 Enemy；颜色占比不能借用关闭素材的高分授权切换。</summary>
    [Fact]
    public void WarmCloseGlyph_DoesNotBecomeEnemy()
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(PathFor("close")));
        for (var i = 0; i < screen.Bgra.Length; i += 4)
        {
            screen.Bgra[i] = (byte)Math.Max(0, screen.Bgra[i] - 22);
            screen.Bgra[i + 1] = (byte)Math.Max(0, screen.Bgra[i + 1] - 22);
            screen.Bgra[i + 2] = (byte)Math.Min(255, screen.Bgra[i + 2] + 45);
        }
        var result = Recognizer().Recognize(screen, new(0, 0, screen.Width, screen.Height));
        Assert.NotNull(result);
        Assert.True(result.Match.Confidence > .95);
        Assert.Equal(AutoButtonState.Unknown, result.State);
    }

    /// <summary>所有用例使用产品实际素材。</summary>
    private static string PathFor(string state)
        => ConfigService.ResolvePath($"resources/templates/stage/sortie/auto_{state}.png");

    /// <summary>每次创建隔离识别器，不共享可变画面。</summary>
    private static AutoButtonRecognizer Recognizer()
        => new(new TemplateMatcher(), PathFor("all"), PathFor("enemy"), PathFor("close"));
}
