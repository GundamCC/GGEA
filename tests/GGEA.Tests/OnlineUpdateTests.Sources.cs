using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GGEA.App.Updates;
using GGEA.Updater;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    /// <summary>新客户端只访问主仓库的固定入口；尚无正式版必须报告失败，不能伪报已经最新。</summary>
    [Fact]
    public async Task Sources_MigrationUsesMainRepository_AndMissingReleaseIsNotSuccess()
    {
        using var temp = new TempDirectory();
        var requests = new List<string>();
        using var client = new OnlineUpdateClient(temp.Path, handler: new ReplyHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            return url == "https://raw.githubusercontent.com/GundamCC/GGEA/main/update-sources.json"
                ? JsonReply(Catalog()) : new(HttpStatusCode.NotFound);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckAsync(new Version(1, 3, 4, 0), default));
        Assert.Equal(2, requests.Count);
        Assert.Contains("https://raw.githubusercontent.com/GundamCC/GGEA/main/update-sources.json", requests);
        Assert.Contains("https://api.github.com/repos/GundamCC/GGEA/releases/latest", requests);
        Assert.Empty(new UpdateSourceStore(temp.Path).Load());
    }

    /// <summary>即使没有新版本也学习来源；下一次 GitHub 完全离线时仍可从磁盘缓存发现新版。</summary>
    [Fact]
    public async Task Sources_LearnWhileUpToDate_ThenUseCacheWhenGitHubIsOffline()
    {
        using var temp = new TempDirectory();
        var mirror = Source("mirror");
        using (var online = new OnlineUpdateClient(temp.Path, handler: new ReplyHandler(request =>
                   request.RequestUri!.Host == "raw.githubusercontent.com" ? JsonReply(Catalog(mirror)) : JsonReply(ReleaseJson("1.1.10.0")))))
            Assert.Null(await online.CheckAsync(new Version(1, 1, 10, 0), default));
        Assert.Equal("mirror", Assert.Single(new UpdateSourceStore(temp.Path).Load()).Id);

        using var offline = new OnlineUpdateClient(temp.Path, handler: new ReplyHandler(request =>
            request.RequestUri!.Host == "mirror.example.com"
                ? JsonReply(MirrorRelease("1.1.11.0", "mirror", 100, new string('a', 64)))
                : new(HttpStatusCode.ServiceUnavailable)));
        var update = await offline.CheckAsync(new Version(1, 1, 10, 0), default);
        Assert.Equal("mirror", update!.SourceId);
        Assert.Equal(new Version(1, 1, 11, 0), update.Version);
        Assert.Equal("备用源说明", update.Notes);
        Assert.Equal("mirror", Assert.Single(new UpdateSourceStore(temp.Path).Load()).Id);
    }

    /// <summary>损坏远程配置不能抹掉缓存；GitHub 可用时不会因为已有镜像而改变首选源。</summary>
    [Fact]
    public async Task Sources_GitHubStaysPreferred_AndBadDiscoveryKeepsCache()
    {
        using var temp = new TempDirectory();
        var store = new UpdateSourceStore(temp.Path);
        store.SaveCache(Catalog(Source("mirror")));
        using var client = new OnlineUpdateClient(temp.Path, handler: new ReplyHandler(request =>
        {
            Assert.NotEqual("mirror.example.com", request.RequestUri!.Host);
            return JsonReply(request.RequestUri.Host == "raw.githubusercontent.com" ? "{broken" : ReleaseJson("1.1.11.0"));
        }));
        Assert.Equal("github", (await client.CheckAsync(new Version(1, 1, 10, 0), default))!.SourceId);
        Assert.Equal("mirror", Assert.Single(store.Load()).Id);
    }

    /// <summary>导入配置可直接覆盖同 ID 缓存；非法地址拒绝且原文件不变，空文件可明确清除手动列表。</summary>
    [Fact]
    public void Sources_ImportIsValidatedAndAtomic()
    {
        using var temp = new TempDirectory();
        var store = new UpdateSourceStore(temp.Path);
        store.SaveCache(Catalog(Source("mirror")));
        store.Import(Catalog(Source("mirror") with { Name = "手动来源" }));
        Assert.Equal("手动来源", Assert.Single(store.Load()).Name);
        var before = File.ReadAllText(Path.Combine(temp.Path, UpdateSourceStore.ImportFile));
        foreach (var url in new[] { "http://mirror.example.com/latest.json", "https://localhost/latest.json",
                     "https://127.0.0.1/latest.json", "https://outside.example.com/latest.json", "https://user@mirror.example.com/latest.json" })
            Assert.Throws<InvalidDataException>(() => store.Import(Catalog(Source("mirror") with { ManifestUrl = url })));
        Assert.Equal(before, File.ReadAllText(Path.Combine(temp.Path, UpdateSourceStore.ImportFile)));
        Assert.Throws<InvalidDataException>(() => store.SaveCache(Catalog(Source("mirror")).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")));
        store.Import(Catalog());
        Assert.Equal("mirror", Assert.Single(store.Load()).Id);
        store.SaveCache(Catalog());
        Assert.Empty(store.Load());
    }

    /// <summary>第一个备用源未同步新版时继续检查后续来源，不误把旧镜像当作最新版本。</summary>
    [Fact]
    public async Task Sources_SkipStaleMirror_AndTryNext()
    {
        using var temp = new TempDirectory();
        new UpdateSourceStore(temp.Path).Import(Catalog(Source("first"), Source("second")));
        using var client = new OnlineUpdateClient(temp.Path, handler: new ReplyHandler(request => request.RequestUri!.Host switch
        {
            "first.example.com" => JsonReply(MirrorRelease("1.1.9.0", "first", 100, new string('a', 64))),
            "second.example.com" => JsonReply(MirrorRelease("1.1.11.0", "second", 100, new string('b', 64))),
            _ => new(HttpStatusCode.ServiceUnavailable)
        }));
        Assert.Equal("second", (await client.CheckAsync(new Version(1, 1, 10, 0), default))!.SourceId);
    }

    /// <summary>已取得原站摘要后下载断网，必须用同版本、同摘要镜像完成真实 ZIP 校验。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Sources_DownloadFailsOver_WithoutChangingIdentity(bool mismatchedHash, bool interruptedStream)
    {
        using var temp = new TempDirectory();
        var package = Path.Combine(temp.Path, "source");
        CreatePackage(package, "1.1.11.0", "new");
        var archive = CreateArchive(temp.Path, package, "1.1.11.0");
        var bytes = File.ReadAllBytes(archive);
        var hash = UpdateFiles.Hash(archive);
        var name = "GGEA-v1.1.11.0-win-x64.zip";
        var installation = Path.Combine(temp.Path, "installed");
        new UpdateSourceStore(installation).Import(Catalog(Source("mirror")));
        var packageRequests = 0;
        using var client = new OnlineUpdateClient(installation, handler: new ReplyHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "github.com")
                return uri.AbsolutePath.EndsWith(".sha256") ? JsonReply($"{hash}  {name}")
                    : interruptedStream ? new(HttpStatusCode.OK) { Content = new InterruptedContent(bytes) }
                    : new(HttpStatusCode.ServiceUnavailable);
            if (uri.AbsolutePath.EndsWith("latest.json"))
                return JsonReply(MirrorRelease("1.1.11.0", "mirror", bytes.Length, mismatchedHash ? new string('f', 64) : hash));
            packageRequests++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var selected = new UpdateRelease(new Version(1, 1, 11, 0), "", name,
            new Uri("https://github.com/package.zip"), bytes.Length, new Uri("https://github.com/package.zip.sha256"), null);
        if (mismatchedHash)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(selected, new Progress<double>(), default));
            Assert.Equal(0, packageRequests);
        }
        else
        {
            var result = await client.DownloadAsync(selected, new Progress<double>(), default);
            Assert.Equal("1.1.11.0", UpdateManifest.Read(result).Version);
            Assert.Equal(1, packageRequests);
        }
        Assert.False(File.Exists(Path.Combine(installation, "GGEA.exe")));
    }

    /// <summary>用户取消或来源校验失败不会偷偷尝试其他镜像；备用源不允许跳到未授权域名。</summary>
    [Fact]
    public async Task Sources_CancellationAndBadRedirectNeverFallThrough()
    {
        using var temp = new TempDirectory();
        new UpdateSourceStore(temp.Path).Import(Catalog(Source("first"), Source("second")));
        var requests = new List<string>();
        using var client = new OnlineUpdateClient(temp.Path, handler: new ReplyHandler(request =>
        {
            var host = request.RequestUri!.Host;
            requests.Add(host);
            if (host != "first.example.com") return new(HttpStatusCode.ServiceUnavailable);
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://outside.example.com/latest.json");
            return redirect;
        }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(new Version(1, 0, 0, 0), cancelled.Token));
        Assert.Empty(requests);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(new Version(1, 0, 0, 0), default));
        Assert.DoesNotContain("outside.example.com", requests);
        Assert.DoesNotContain("second.example.com", requests);
    }

    /// <summary>模拟下载已收到部分正文后连接断开，覆盖流读取异常而不仅是 HTTP 状态码。</summary>
    private sealed class InterruptedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new InterruptedStream(bytes));
    }

    /// <summary>第一次读取只有半包，随后模拟底层网络流 IOException。</summary>
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes[..(bytes.Length / 2)])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position > 0 ? ValueTask.FromException<int>(new IOException("test connection lost")) : base.ReadAsync(buffer, cancellationToken);
    }

    /// <summary>配置模板只使用测试域名，真实下载地址由用户维护者明确提供。</summary>
    private static UpdateSource Source(string id) => new(id, id, $"https://{id}.example.com/latest.json", [$"{id}.example.com"]);

    /// <summary>模拟远程与手动导入共用的 v1 来源协议。</summary>
    private static string Catalog(params UpdateSource[] sources) =>
        JsonSerializer.Serialize(new UpdateSourceCatalog(1, sources), UpdateFiles.JsonOptions);

    /// <summary>备用源是普通静态 JSON，不要求部署 GitHub API 兼容服务。</summary>
    private static string MirrorRelease(string version, string source, long bytes, string hash) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, runtime = "win-x64", version, notes = "备用源说明",
        packageName = $"GGEA-v{version}-win-x64.zip", packageSize = bytes, sha256 = hash,
        packageUrl = $"https://{source}.example.com/GGEA-v{version}-win-x64.zip"
    });
}
