using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>不同流程并发保存时共享目录轮转，不能重置槽位覆盖刚保存的证据。</summary>
public class RecognitionEvidenceTests
{
    [Fact]
    public async Task MultipleInstances_ShareSlotsAndRateLimit()
    {
        using var temp = new TempDirectory();
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(16, 16, 20), 16, 16);
        var screen = ImageCodec.DecodePng(png);
        var first = new RecognitionEvidence(temp.Path, new TestLog());
        var second = new RecognitionEvidence(Path.Combine(temp.Path, "."), new TestLog());
        await Task.WhenAll(first.SaveAsync("home", "serial", png, screen),
            second.SaveAsync("logo", "serial", png, screen));
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*.zip").Length);
        await second.SaveAsync("home", "serial", png, screen);
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*.zip").Length);
        for (var i = 0; i < 4; i++)
            await new RecognitionEvidence(temp.Path, new TestLog()).SaveAsync("next" + i, "serial", png, screen);
        var paths = Directory.GetFiles(temp.Path, "*.zip");
        Assert.Equal(5, paths.Length);
        var kinds = paths.Select(path =>
        {
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("diagnostic.json")!.Open();
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.GetProperty("kind").GetString();
        }).ToArray();
        foreach (var i in Enumerable.Range(0, 4)) Assert.Contains("next" + i, kinds);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
    /// <summary>只清理本用例创建的唯一临时目录。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-evidence-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
