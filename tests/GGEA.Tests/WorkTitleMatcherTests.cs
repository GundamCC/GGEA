using GGEA.App.Config;

namespace GGEA.Tests;

/// <summary>
/// 作品标题匹配测试：OCR 归一化与歧义处理。
/// </summary>
public class WorkTitleMatcherTests
{
    private static readonly string[] Works =
    {
        "Mobile Suit Gundam",
        "Mobile Suit Zeta Gundam",
        "Mobile Suit Gundam ZZ",
        "Mobile Suit Gundam Char's Counter Attack",
        "Mobile Suit Gundam SEED",
        "After War Gundam X",
    };

    [Theory]
    [InlineData("M0biIe Suit Gundam", "Mobile Suit Gundam")]
    [InlineData("MOBILE SUIT ZETA GUNDAM", "Mobile Suit Zeta Gundam")]
    [InlineData("M0biIe Suit Gundam ZZ", "Mobile Suit Gundam ZZ")]
    [InlineData("MOBILE SUIT GUNDAM SEED", "Mobile Suit Gundam SEED")]
    [InlineData("AFTER WAR GUNDAM X", "After War Gundam X")]
    public void Match_ValidOcr_ReturnsWork(string ocr, string expected)
    {
        Assert.Equal(expected, WorkTitleMatcher.Match(ocr, Works));
    }

    [Fact]
    public void Match_ShortAmbiguousOcr_ReturnsNull()
    {
        // “MOBILE SUIT” 过短且是多部作品共同前缀，必须判定为歧义
        Assert.Null(WorkTitleMatcher.Match("MOBILE SUIT", Works));
    }

    [Fact]
    public void Normalize_ReplacesOcrConfusions()
    {
        Assert.Equal("MOBIIESUITGUNDAM", WorkTitleMatcher.Normalize("M0biIe Suit Gundam"));
    }
}
