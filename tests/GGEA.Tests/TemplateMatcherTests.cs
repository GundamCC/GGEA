using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 模板匹配器单元测试：验证精确匹配、失配返回 null、ROI 限制、遮罩与非法尺寸。
/// </summary>
public class TemplateMatcherTests
{
    /// <summary>宽文字条的精确剪枝保留像素偏移、ROI 坐标及原始阈值，不能把近似目标升级为满分。</summary>
    [Theory]
    [InlineData(31, 11)]
    [InlineData(159, 73)]
    public void Match_WideText_PreservesCoordinatesAndThreshold(int x, int y)
    {
        const int width = 240, height = 90, tw = 81, th = 17;
        var screen = TestImageFactory.CreateBgra(width, height, 0);
        var template = TestImageFactory.CreateBgra(tw, th, 0);
        var random = new Random(41025);
        for (var row = 0; row < th; row++)
            for (var col = 0; col < tw; col++)
            {
                var value = (byte)random.Next(30, 210);
                for (var channel = 0; channel < 3; channel++)
                {
                    template[(row * tw + col) * 4 + channel] = value;
                    screen[((y + row) * width + x + col) * 4 + channel] = (byte)(value + 5);
                }
            }
        var matcher = new TemplateMatcher();
        var roi = new Roi(7, 3, width - 7, height - 3);
        var result = matcher.Match(screen, width, height, template, tw, th, .98, roi);
        Assert.NotNull(result);
        Assert.Equal((x, y), (result.X, result.Y));
        Assert.Equal(1 - 5.0 / 255, result.Confidence, 8);
        Assert.Null(matcher.Match(screen, width, height, template, tw, th, .981, roi));
    }

    /// <summary>粗层同分的平坦干扰块不能遮蔽原图中真实存在的棋盘目标；覆盖非对齐位置及边缘。</summary>
    [Theory]
    [InlineData(100, 8, 160, 64)]
    [InlineData(101, 9, 160, 64)]
    [InlineData(112, 16, 160, 64)]
    [InlineData(100, 8, 320, 120)]
    [InlineData(101, 9, 320, 120)]
    [InlineData(272, 72, 320, 120)]
    public void Match_CoarseDecoy_DoesNotHideExactTarget(int targetX, int targetY, int width, int height)
    {
        var template = TestImageFactory.CreateBgra(48, 48, 0);
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 48; x++)
                for (var c = 0; c < 3; c++) template[(y * 48 + x) * 4 + c] = (byte)(((x + y) % 2) * 255);
        var screen = TestImageFactory.CreateBgra(width, height, 20);
        TestImageFactory.PasteBlock(screen, width, 48, 48, 0, 0, 127);
        for (var y = 0; y < 48; y++)
            Array.Copy(template, y * 48 * 4, screen, ((y + targetY) * width + targetX) * 4, 48 * 4);

        var match = new TemplateMatcher().Match(screen, width, height, template, 48, 48, 0.99);
        Assert.NotNull(match);
        Assert.Equal(targetX, match.X);
        Assert.Equal(targetY, match.Y);
        Assert.Equal(1, match.Confidence);
    }

    /// <summary>透明像素的 RGB 不应经金字塔平均污染有效前景；奇数模板和稀疏遮罩同样支持复查。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_MaskedCoarseDecoy_PreservesExactForeground(bool sparse)
    {
        const int tw = 49, th = 51, width = 320, height = 120, tx = 203, ty = 17;
        var template = TestImageFactory.CreateBgra(tw, th, 240);
        var screen = TestImageFactory.CreateBgra(width, height, 20);
        TestImageFactory.PasteBlock(screen, width, tw, th, 0, 0, 127);
        var random = new Random(671);
        for (var y = 0; y < th; y++)
            for (var x = 0; x < tw; x++)
            {
                var index = (y * tw + x) * 4;
                var valid = sparse ? (x + y) % 3 == 0 : x > 5 && x < tw - 6 && y > 5 && y < th - 6;
                template[index + 3] = valid ? (byte)255 : (byte)0;
                if (!valid) continue;
                var value = (byte)random.Next(256);
                for (var c = 0; c < 3; c++) template[index + c] = screen[((y + ty) * width + x + tx) * 4 + c] = value;
            }
        var match = new TemplateMatcher().Match(screen, width, height, template, tw, th, 0.99);
        Assert.NotNull(match);
        Assert.Equal((tx, ty), (match.X, match.Y));
        Assert.Equal(1, match.Confidence);
    }

    /// <summary>大区域复查仍须拒绝只有粗层均值相似的干扰，不能通过降低阈值制造命中。</summary>
    [Fact]
    public void Match_CoarseOnlySimilarity_RemainsRejected()
    {
        var template = TestImageFactory.CreateBgra(48, 48, 0);
        for (var i = 0; i < 48 * 48; i++)
            for (var c = 0; c < 3; c++) template[i * 4 + c] = (byte)((i % 2) * 255);
        var screen = TestImageFactory.CreateBgra(320, 120, 127);
        Assert.Null(new TemplateMatcher().Match(screen, 320, 120, template, 48, 48, 0.85));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Match_ExactTemplate_FindsExpectedPosition()
    {
        // 200x160 深灰背景，(80,50) 处放 30x20 亮色矩形作为目标
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        TestImageFactory.PasteBlock(screen, 200, 30, 20, 80, 50, 200);
        var template = TestImageFactory.CreateBgra(30, 20, 200);

        var matcher = new TemplateMatcher();
        var result = matcher.Match(screen, 200, 160, template, 30, 20, threshold: 0.9);

        Assert.NotNull(result);
        Assert.Equal(80, result!.X);
        Assert.Equal(50, result.Y);
        Assert.True(result.Confidence > 0.99, $"置信度应为接近 1，实际 {result.Confidence}");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Match_NoSimilarTemplate_ReturnsNull()
    {
        // 屏幕纯深灰，模板是纯亮色，两者完全不同
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        var template = TestImageFactory.CreateBgra(20, 20, 200);

        var matcher = new TemplateMatcher();
        var result = matcher.Match(screen, 200, 160, template, 20, 20, threshold: 0.9);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Match_RoiExcludesTarget_ReturnsNull()
    {
        // 目标在 (120, 100)，但 ROI 只允许左上角 (0,0)-(60,60)
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        TestImageFactory.PasteBlock(screen, 200, 30, 20, 120, 100, 200);
        var template = TestImageFactory.CreateBgra(30, 20, 200);
        var roi = new Roi(0, 0, 60, 60);

        var matcher = new TemplateMatcher();
        var result = matcher.Match(screen, 200, 160, template, 30, 20, threshold: 0.9, roi);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Match_RoiIncludesTarget_ReturnsLocalPosition()
    {
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        TestImageFactory.PasteBlock(screen, 200, 30, 20, 120, 100, 200);
        var template = TestImageFactory.CreateBgra(30, 20, 200);
        var roi = new Roi(100, 80, 100, 80);

        var matcher = new TemplateMatcher();
        var result = matcher.Match(screen, 200, 160, template, 30, 20, threshold: 0.9, roi);

        // 返回坐标应换算到全图坐标
        Assert.NotNull(result);
        Assert.Equal(120, result!.X);
        Assert.Equal(100, result.Y);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Match_TemplateLargerThanScreen_ReturnsNull()
    {
        var screen = TestImageFactory.CreateBgra(10, 10, 20);
        var template = TestImageFactory.CreateBgra(20, 20, 200);

        var matcher = new TemplateMatcher();
        var result = matcher.Match(screen, 10, 10, template, 20, 20, threshold: 0.8);

        Assert.Null(result);
    }

    [Fact]
    public void Match_WithMask_IgnoresDynamicRegion()
    {
        // 模板 40x30 全部为 200；屏幕中目标位置右半被“动态区域”改成 150
        var template = TestImageFactory.CreateBgra(40, 30, 200);
        var screen = TestImageFactory.CreateBgra(300, 200, 20);
        TestImageFactory.PasteBlock(screen, 300, 40, 30, 60, 50, 200);
        TestImageFactory.PasteBlock(screen, 300, 20, 30, 80, 50, 150);

        var matcher = new TemplateMatcher();

        // 无遮罩：右半差异导致置信度不足
        var withoutMask = matcher.Match(screen, 300, 200, template, 40, 30, threshold: 0.9);
        Assert.Null(withoutMask);

        // 遮罩屏蔽右半（动态区域）后应精确匹配
        var mask = new byte[40 * 30];
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                mask[y * 40 + x] = 255; // 左半参与匹配
            }
        }

        var withMask = matcher.Match(screen, 300, 200, template, 40, 30, threshold: 0.9, mask: mask);
        Assert.NotNull(withMask);
        Assert.Equal(60, withMask!.X);
        Assert.Equal(50, withMask.Y);
        Assert.True(withMask.Confidence > 0.99);
    }

    [Fact]
    public void Match_TransparentTemplate_IgnoresBackgroundAutomatically()
    {
        // 模板外围保留与屏幕不一致的 RGB，但设为透明；只有中央图案应参与匹配。
        var template = TestImageFactory.CreateBgra(40, 30, 240);
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                template[(y * 40 + x) * 4 + 3] = 0;
            }
        }

        TestImageFactory.PasteBlock(template, 40, 12, 10, 14, 10, 200);

        var screen = TestImageFactory.CreateBgra(300, 200, 20);
        TestImageFactory.PasteBlock(screen, 300, 12, 10, 94, 60, 200);

        // 经由实际 PNG 编解码路径复查 Alpha，避免测试只覆盖内存数组。
        var decodedTemplate = ImageCodec.DecodePng(ImageCodec.EncodePng(template, 40, 30));
        var matcher = new TemplateMatcher();
        var result = matcher.Match(
            screen,
            300,
            200,
            decodedTemplate.Bgra,
            decodedTemplate.Width,
            decodedTemplate.Height,
            threshold: 0.99);

        Assert.NotNull(result);
        Assert.Equal(80, result!.X);
        Assert.Equal(50, result.Y);
        Assert.True(result.Confidence > 0.99);
    }
}
