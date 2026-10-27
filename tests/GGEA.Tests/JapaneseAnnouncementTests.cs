using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服公告实机标题回归，保持入口流程现有的顶部区域与阈值。</summary>
public sealed class JapaneseAnnouncementTests
{
    /// <summary>实际标题在 200×128 区域内命中；缺失文字的同色背景不得命中。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Announcement_ActualHeader_RequiresTitle(bool hasTitle)
    {
        var header = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Navigation", "announcement-ja-header.png")));
        var template = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "resources", "ja", "templates", "home", "announcement.png")));
        // 用标题旁的真实背景覆盖文字，验证不能只靠相同底色通过识别。
        if (!hasTitle)
            for (var y = 70; y < 118; y++)
                for (var x = 10; x < 190; x++)
                    Array.Copy(header.Bgra, (y * header.Width + 2) * 4,
                        header.Bgra, (y * header.Width + x) * 4, 4);
        var result = new TemplateMatcher().Match(header.Bgra, header.Width, header.Height,
            template.Bgra, template.Width, template.Height, .85, new Roi(0, 0, 200, 128));
        Assert.Equal(hasTitle, result is not null);
        if (result is not null)
        {
            Assert.InRange(result.CenterX, 98, 102);
            Assert.InRange(result.CenterY, 92, 96);
        }
    }
}
