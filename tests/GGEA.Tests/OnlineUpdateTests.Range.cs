using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using GGEA.App.Updates;
using GGEA.Updater;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    [Theory]
    [InlineData("local-blocks")]
    [InlineData("updater-blocks")]
    [InlineData("blocks")]
    [InlineData("block-unavailable")]
    [InlineData("block-interrupted")]
    [InlineData("image-only-unavailable")]
    [InlineData("image-only-expensive")]
    [InlineData("block-corrupt")]
    [InlineData("range")]
    [InlineData("unsupported")]
    [InlineData("corrupt")]
    [InlineData("cancel")]
    public async Task RangeIndex_PublisherToClient_VerifiesAndReusesFiles(string scenario)
    {
        using var temp = new TempDirectory();
        var old = Path.Combine(temp.Path, "old");
        var next = Path.Combine(temp.Path, "next");
        var imageOnly = scenario.StartsWith("image-only-", StringComparison.Ordinal);
        var version = imageOnly ? new Version(1, 4, 25, 1) : new Version(1, 1, 10, 0);
        if (imageOnly)
        {
            CreatePackage(old, "1.4.25.0", "unchanged");
            CreatePackage(next, version.ToString(), "unchanged");
        }
        else CreateDeltaFixtures(old, next);
        var localBuild = scenario == "local-blocks";
        if (localBuild)
        {
            File.Delete(Path.Combine(old, "build-info.json"));
            File.WriteAllText(Path.Combine(old, "local-build-info.json"), """{"version":"1.1.9.1"}""");
        }
        var updater = scenario == "updater-blocks";
        var fileName = updater ? "GGEA.Updater.exe" : "GGEA.exe";
        var useBlocks = localBuild || updater || imageOnly || scenario.StartsWith("block", StringComparison.Ordinal);
        if (useBlocks)
        {
            var program = new byte[(imageOnly ? 2 : 1) * 1024 * 1024];
            new Random(17).NextBytes(program);
            File.WriteAllBytes(Path.Combine(old, fileName), program);
            if (scenario == "image-only-expensive") new Random(72).NextBytes(program);
            // 前部插入一段数据，验证后续偏移变化时仍可复用本机块。
            File.WriteAllBytes(Path.Combine(next, fileName), [.. program[..5000], 7, 3, 1, .. program[5000..]]);
            var manifestPath = Path.Combine(next, "build-info.json");
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["files"]![fileName] = UpdateFiles.Hash(Path.Combine(next, fileName));
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        if (imageOnly)
        {
            foreach (var root in new[] { old, next })
            {
                var path = Path.Combine(root, "build-info.json");
                var info = JsonNode.Parse(File.ReadAllText(path))!;
                info["templateEngine"] = new string('b', 64);
                info["files"]![fileName] = UpdateFiles.Hash(Path.Combine(root, fileName));
                if (root == next)
                {
                    var logo = Path.Combine(root, "resources/logo.png");
                    File.WriteAllBytes(logo, [1, 2, 3]);
                    info["files"]!["resources/logo.png"] = UpdateFiles.Hash(logo);
                }
                File.WriteAllText(path, info.ToJsonString());
            }
        }
        var archive = CreateArchive(temp.Path, next, version.ToString());
        var bytes = File.ReadAllBytes(archive);
        var index = archive + ".index.json";
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(Path.GetDirectoryName(FindDeltaScript())!,
            "package-range-index.ps1"), archive, next, UpdateFiles.Hash(archive), index }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var indexBytes = File.ReadAllBytes(index);
        var blocks = File.ReadAllBytes(archive + (updater ? ".updater.blocks" : ".blocks"));
        var update = new UpdateRelease(version, "", "package.zip", new("https://github.com/package.zip"),
            bytes.Length, null, UpdateFiles.Hash(archive))
        { Index = new(new("https://github.com/index"), UpdateFiles.Hash(index), indexBytes.Length),
          UpdaterBlocks = updater ? new(new("https://github.com/updater.blocks"), blocks.Length) : null,
          Blocks = useBlocks && !updater ? new(new("https://github.com/program.blocks"), blocks.Length) : null };
        int fullRequests = 0, rangeBytes = 0, blockBytes = 0;
        using var cancellation = new CancellationTokenSource();
        var traffic = new UpdateDownloadTraffic();
        var log = new List<string>();
        using var client = new OnlineUpdateClient(old, status => log.Add(status.Message), new ReplyHandler(request =>
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
            bool isBlock = request.RequestUri.AbsolutePath == (updater ? "/updater.blocks" : "/program.blocks");
            if (imageOnly)
            {
                Assert.Equal("image-only-unavailable", scenario);
                Assert.True(isBlock, "素材更新不应请求 ZIP 内的整个主程序。");
                return new(HttpStatusCode.ServiceUnavailable);
            }
            if (isBlock && scenario == "block-unavailable") return new(HttpStatusCode.NotFound);
            var source = isBlock ? blocks : bytes;
            var part = source[from..(to + 1)];
            if (isBlock && scenario == "block-interrupted") part = part[..(part.Length / 2)];
            if (isBlock) blockBytes += part.Length;
            if (isBlock && scenario == "block-corrupt") Array.Fill(part, (byte)255);
            rangeBytes += part.Length;
            if (scenario == "corrupt") Array.Fill(part, (byte)255);
            var content = new ByteArrayContent(part);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, source.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        if (imageOnly)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(update, new Progress<double>(), default, traffic));
            Assert.Equal(indexBytes.Length, traffic.Bytes);
            UpdateManifest.Read(old).Verify(old);
        }
        else if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(update, new Progress<double>(), cancellation.Token, traffic));
        else if (scenario is "corrupt" or "block-corrupt")
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update, new Progress<double>(), default, traffic));
        else
        {
            var prepared = await client.DownloadAsync(update, new Progress<double>(), default, traffic);
            UpdateManifest.Read(prepared).Verify(prepared);
            Assert.Equal(indexBytes.LongLength + rangeBytes + fullRequests * bytes.LongLength, traffic.Bytes);
            if (scenario == "block-interrupted")
            {
                Assert.True(blockBytes > 0);
                Assert.Contains(log, message => message.Contains("分块重建未完成") && message.Contains("下载中断"));
            }
            if (localBuild || scenario == "blocks" || updater) Assert.InRange(blockBytes, 1, blocks.Length / 2);
            if (scenario == "range") Assert.InRange(rangeBytes, 1, bytes.Length - 1);
        }
        Assert.Equal(scenario == "unsupported" ? 1 : 0, fullRequests);
    }
}
