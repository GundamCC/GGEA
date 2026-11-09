using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using GGEA.App.Updates;
using GGEA.Updater;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    [Theory]
    [InlineData("range")]
    [InlineData("unsupported")]
    [InlineData("corrupt")]
    [InlineData("cancel")]
    public async Task RangeIndex_PublisherToClient_VerifiesAndReusesFiles(string scenario)
    {
        using var temp = new TempDirectory();
        var old = Path.Combine(temp.Path, "old");
        var next = Path.Combine(temp.Path, "next");
        CreateDeltaFixtures(old, next);
        var archive = CreateArchive(temp.Path, next, "1.1.10.0");
        var bytes = File.ReadAllBytes(archive);
        var index = archive + ".index.json";
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(Path.GetDirectoryName(FindDeltaScript())!,
            "package-range-index.ps1"), archive, next, UpdateFiles.Hash(archive), index }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var indexBytes = File.ReadAllBytes(index);
        var update = new UpdateRelease(new(1,1,10,0), "", "package.zip", new("https://github.com/package.zip"),
            bytes.Length, null, UpdateFiles.Hash(archive))
        { Index = new(new("https://github.com/index"), UpdateFiles.Hash(index), indexBytes.Length) };
        int fullRequests = 0, rangeBytes = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new GitHubUpdateClient(new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/index") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(indexBytes) };
            if (request.Headers.Range?.Ranges.Single() is not { } range)
            {
                fullRequests++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            if (scenario == "cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (scenario == "unsupported") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            int from = (int)range.From!.Value, to = (int)range.To!.Value;
            var part = bytes[from..(to + 1)];
            rangeBytes += part.Length;
            if (scenario == "corrupt") Array.Fill(part, (byte)255);
            var content = new ByteArrayContent(part);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(update, old, new Progress<double>(), cancellation.Token));
        else if (scenario == "corrupt")
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update, old, new Progress<double>(), default));
        else
        {
            var prepared = await client.DownloadAsync(update, old, new Progress<double>(), default);
            UpdateManifest.Read(prepared).Verify(prepared);
            if (scenario == "range") Assert.InRange(rangeBytes, 1, bytes.Length - 1);
        }
        Assert.Equal(scenario == "unsupported" ? 1 : 0, fullRequests);
    }
}
