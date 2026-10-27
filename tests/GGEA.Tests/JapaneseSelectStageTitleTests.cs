using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日文选关标题按完整字形确认，背景与标题所在区域分别验证。</summary>
public sealed class JapaneseSelectStageTitleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-ja-title-" + Guid.NewGuid().ToString("N"));

    /// <summary>隔离日文物理素材，禁止通用中文回退掩盖日文识别失败。</summary>
    private SelectStageTitleRecognizer Recognizer()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "title.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/stage/sortie/title_select_stage.png"), path, true);
        return new(path);
    }

    /// <summary>新采集蓝色页首与原有红色背景均需通过相同 90% 字形检查。</summary>
    [Theory]
    [InlineData("select-stage-ja-seed-header.png")]
    [InlineData("select-stage-ja-red-title.png")]
    public void ActualBackgrounds_KeepCompleteJapaneseTitle(string file)
    {
        var source = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie", file)));
        // 旧素材是标题裁剪，放回足够大的页首；不能把裁剪高度当成完整屏幕高度。
        var pixels = TestImageFactory.CreateBgra(1000, 120, 0);
        var left = source.Width < 1000 ? 200 : 0;
        var top = source.Width < 1000 ? 20 : 0;
        for (var y = 0; y < source.Height; y++)
            Buffer.BlockCopy(source.Bgra, y * source.Width * 4, pixels, ((top + y) * 1000 + left) * 4, source.Width * 4);
        Assert.True(Recognizer().Recognize(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1000, 120)), default));
    }

    /// <summary>缺少末尾文字或移出页首均不能作为返回选关的证据。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTextOrOutsideHeader_IsRejected(bool outside)
    {
        var header = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Sortie/select-stage-ja-seed-header.png")));
        var pixels = TestImageFactory.CreateBgra(1000, 360, 0);
        for (var y = 0; y < header.Height; y++)
            Buffer.BlockCopy(header.Bgra, y * header.Width * 4, pixels,
                ((outside ? 200 : 0) + y) * 1000 * 4, (outside ? 1000 : 570) * 4);
        Assert.False(Recognizer().Recognize(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1000, 360)), default));
    }

    /// <summary>仅清理测试独占素材目录。</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
