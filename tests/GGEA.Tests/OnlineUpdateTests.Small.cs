using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using GGEA.App.Updates;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    [Theory]
    [InlineData("resources/common/templates/logo/new.png", true, true)]
    [InlineData("resources/common/templates/logo/new.png", false, false)]
    [InlineData("resources/common/ocr/model.onnx", true, false)]
    [InlineData("GGEA.Core.dll", true, false)]
    public void OnlyCompatibleTemplatesCanBeAppliedToRunningWindow(string file, bool sameEngine, bool expected)
    {
        var engine = new string('a', 64);
        var baseline = new GGEA.Updater.UpdateManifest("1.4.23.0", "", new Dictionary<string, string>())
            { TemplateEngine = engine };
        var target = baseline with { Files = new Dictionary<string, string> { [file] = "changed" },
            TemplateEngine = sameEngine ? engine : new string('b', 64) };
        Assert.Equal(expected, GGEA.Updater.UpdateInstaller.CanApplyTemplates(baseline, target));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(-3, 2)]
    [InlineData(4, -1)]
    public void FilePatch_RejectsOutOfBoundsCommands(int command, int offset)
    {
        using var recipe = new MemoryStream();
        using (var encoder = new BrotliStream(recipe, CompressionLevel.Optimal, true))
        using (var writer = new BinaryWriter(encoder))
        {
            writer.Write(command);
            if (command < 0) writer.Write(offset);
        }
        recipe.Position = 0;
        using var source = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => FileCopyPatch.Apply(source, recipe, output, 3, default));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task SmallRelease_MissingAttachmentsNeverDownloadsFullZip()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        using var client = new GitHubUpdateClient(new ReplyHandler(_ =>
        {
            requests++;
            return new(HttpStatusCode.NotFound);
        }));
        var release = new UpdateRelease(new(1, 4, 23, 1), "", "package.zip", new("https://github.com/package.zip"),
            100_000_000, null, new string('0', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(release, temp.Path, new Progress<double>(), default));
        Assert.Equal(0, requests);
    }
}
