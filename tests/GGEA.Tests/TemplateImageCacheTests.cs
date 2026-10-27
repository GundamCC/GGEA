using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>缓存只减少重复解码，不能遮蔽用户更新、损坏或删除的模板。</summary>
public sealed class TemplateImageCacheTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ggea-template-cache-");

    /// <summary>只清理由本用例创建的唯一临时目录。</summary>
    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>稳定素材复用同一像素；同大小、不同更新时间的替换必须立即重新解码。</summary>
    [Fact]
    public void Load_UnchangedReusesPixels_ModifiedReloads()
    {
        var path = Path.Combine(_directory.FullName, "template.png");
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(20, 20, 80), 20, 20);
        File.WriteAllBytes(path, png);
        var cache = new TemplateImageCache();
        var first = cache.Load(path);
        for (var i = 0; i < 100; i++) Assert.Same(first, cache.Load(path));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        var refreshed = cache.Load(path);
        Assert.NotSame(first, refreshed);
        Assert.Equal(first.Bgra, refreshed.Bgra);
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 170), 30, 20));
        var changed = cache.Load(path);
        Assert.Equal(30, changed.Width);
        Assert.Equal(170, changed.Bgra[0]);
    }

    /// <summary>文件损坏或删除必须抛出错误，不能返回上一次成功的识别素材。</summary>
    [Fact]
    public void Load_CorruptOrDeleted_DoesNotReuseOldEvidence()
    {
        var path = Path.Combine(_directory.FullName, "template.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(20, 20, 80), 20, 20));
        var cache = new TemplateImageCache();
        cache.Load(path);
        File.WriteAllText(path, "invalid PNG");
        Assert.ThrowsAny<Exception>(() => cache.Load(path));
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => cache.Load(path));
    }
}
