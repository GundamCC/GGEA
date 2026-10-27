using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GGEA.App.Updates;
using GGEA.Updater;

namespace GGEA.Tests;

/// <summary>在线更新核心回归：真实 ZIP/文件事务和可控 HTTP，不访问模拟器或修改实际安装。</summary>
public sealed partial class OnlineUpdateTests
{
    /// <summary>正式附件精确匹配平台和版本，四段版本按数字比较而非字符串。</summary>
    [Fact]
    public async Task Check_SelectsStableAssets_AndDoesNotDowngrade()
    {
        using var client = new GitHubUpdateClient(new ReplyHandler(request =>
        {
            Assert.Equal("https://api.github.com/repos/GundamCC/GGEA/releases/latest", request.RequestUri!.AbsoluteUri);
            return JsonReply(ReleaseJson("1.1.10.0"));
        }));
        var update = await client.CheckAsync(new Version(1, 1, 9, 1), default);
        Assert.Equal(new Version(1, 1, 10, 0), update!.Version);
        Assert.Equal("GGEA-v1.1.10.0-win-x64.zip", update.PackageName);
        Assert.Null(await client.CheckAsync(new Version(1, 1, 10, 0), default));
        Assert.Null(await client.CheckAsync(new Version(1, 2, 0, 0), default));
    }

    /// <summary>拒绝不完整发布、预发布和外部源，不能把 API 错误当作没有更新。</summary>
    [Fact]
    public async Task Check_RejectsIncompleteReleaseAndForeignSource()
    {
        foreach (var json in new[]
        {
            ReleaseJson("1.1.10.0").Replace(".zip.sha256", ".zip.missing"),
            ReleaseJson("1.1.10.0").Replace("\"prerelease\":false", "\"prerelease\":true"),
            ReleaseJson("1.1.10.0").Replace("https://github.com/", "https://example.org/"),
            ReleaseJson("1.1.10.0").Replace("GundamCC/GGEA/", "GundamCC/GGEA-Release/")
        })
        {
            using var client = new GitHubUpdateClient(new ReplyHandler(_ => JsonReply(json)));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(new Version(1, 0, 0, 0), default));
        }
        using var limited = new GitHubUpdateClient(new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => limited.CheckAsync(new Version(1, 0, 0, 0), default));
        Assert.Contains("限制", error.Message);
    }

    /// <summary>下载链包含 checksum、完整 ZIP 和清单验证；损坏包不能进入安装阶段。</summary>
    [Fact]
    public async Task Download_VerifiesArchive_AndRejectsTampering()
    {
        using var temp = new TempDirectory();
        var package = Path.Combine(temp.Path, "source");
        CreatePackage(package, "1.1.10.0", "new");
        var archive = CreateArchive(temp.Path, package, "1.1.10.0");
        var bytes = File.ReadAllBytes(archive);
        var name = "GGEA-v1.1.10.0-win-x64.zip";
        var url = new Uri($"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v1.1.10.0/{name}");
        var hash = UpdateFiles.Hash(archive);
        var update = new UpdateRelease(new Version(1, 1, 10, 0), "说明", name, url, bytes.Length, new Uri(url + ".sha256"), hash);
        using var client = new GitHubUpdateClient(new ReplyHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sha256")
                ? JsonReply($"{hash}  {name}\n")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var installation = Path.Combine(temp.Path, "installed");
        var prepared = await client.DownloadAsync(update, installation, new Progress<double>(), default);
        Assert.Equal("1.1.10.0", UpdateManifest.Read(prepared).Version);
        Assert.False(File.Exists(Path.Combine(installation, "GGEA.exe")));
        UpdatePackageDownloader.DeleteDownload(Path.GetDirectoryName(prepared)!);
        bytes[bytes.Length / 2] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update, installation, new Progress<double>(), default));
        Assert.Empty(Directory.GetDirectories(Path.Combine(installation, UpdateFiles.StateDirectory), "download-*"));
    }

    /// <summary>GitHub 之外的重定向在第二个请求发出之前拒绝。</summary>
    [Fact]
    public async Task Check_RejectsForeignRedirect_AndHonorsCancellation()
    {
        var calls = 0;
        using var client = new GitHubUpdateClient(new ReplyHandler(_ =>
        {
            calls++;
            var reply = new HttpResponseMessage(HttpStatusCode.Redirect);
            reply.Headers.Location = new Uri("https://example.org/release");
            return reply;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(new Version(1, 0, 0, 0), default));
        Assert.Equal(1, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(new Version(1, 0, 0, 0), cancellation.Token));
    }

    /// <summary>摘要必须绑定附件名，不能取任意 64 位字符串当作更新授权。</summary>
    [Fact]
    public void Checksum_RequiresExactName()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdatePackageDownloader.ParseChecksum(hash + "  correct.zip\n", "correct.zip"));
        Assert.Throws<InvalidDataException>(() => UpdatePackageDownloader.ParseChecksum(hash + "  other.zip", "correct.zip"));
    }

    /// <summary>目录穿越和大小写重复文件不能逃出暂存目录或覆盖已有解压文件。</summary>
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("folder/file.txt:stream")]
    [InlineData("CON.txt")]
    public void Package_RejectsUnsafePaths(string relative)
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("GGEA-v1.1.10.0-win-x64/" + relative).Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(archive, Path.Combine(temp.Path, "out"), "1.1.10.0"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.txt")));
    }

    /// <summary>正式发布上传只重命名附件，包内保留提交后缀；兼容 PowerShell 的 Windows 分隔符。</summary>
    [Theory]
    [InlineData('/', true)]
    [InlineData('\\', true)]
    [InlineData('\\', false)]
    public void Package_AcceptsPublishedLayout_AndChecksRootCommit(char separator, bool matchingCommit)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        CreatePackage(source, "1.1.10.0", "new");
        var archive = Path.Combine(temp.Path, "published.zip");
        var root = "GGEA-v1.1.10.0-win-x64-" + new string(matchingCommit ? 'a' : 'b', 12);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, root + separator + Path.GetRelativePath(source, file).Replace('\\', separator));
        var destination = Path.Combine(temp.Path, "unpacked");
        if (!matchingCommit) Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(archive, destination, "1.1.10.0"));
        else Assert.Equal("1.1.10.0", UpdatePackage.Extract(archive, destination, "1.1.10.0").Version);
    }

    /// <summary>用户配置、AP 方案和坐标保留；旧程序文件按清单删除，额外私人文件不触碰。</summary>
    [Fact]
    public void Install_PreservesUserData_RemovesOnlyObsoleteManagedFiles()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "installed");
        var package = Path.Combine(temp.Path, "new");
        CreatePackage(target, "1.1.9.1", "old", extra: "old.dll");
        CreatePackage(package, "1.1.10.0", "new", extra: "new.dll");
        File.WriteAllText(Path.Combine(target, "config/ggea.config.json"), "user settings");
        File.WriteAllText(Path.Combine(target, "config/ap_consumption_profiles.json"), "user AP");
        File.WriteAllText(Path.Combine(target, "resources/stage_work_positions.json"), "user coordinates");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "private");
        Directory.CreateDirectory(Path.Combine(target, "plugins/ggea.example"));
        File.WriteAllText(Path.Combine(target, "plugins/ggea.example/plugin.json"), "private plugin");
        var verified = new List<string>();
        using var lease = UpdateFiles.AcquireLock(target);
        var backup = UpdateInstaller.Install(target, package, "1.1.10.0", verified.Add);
        Assert.Equal(new[] { package, target }, verified);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "GGEA.exe")));
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(target, "config/ggea.config.json")));
        Assert.Equal("user AP", File.ReadAllText(Path.Combine(target, "config/ap_consumption_profiles.json")));
        Assert.Equal("user coordinates", File.ReadAllText(Path.Combine(target, "resources/stage_work_positions.json")));
        Assert.Equal("private", File.ReadAllText(Path.Combine(target, "personal.txt")));
        Assert.Equal("private plugin", File.ReadAllText(Path.Combine(target, "plugins/ggea.example/plugin.json")));
        Assert.False(File.Exists(Path.Combine(target, "old.dll")));
        Assert.True(File.Exists(Path.Combine(target, "new.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "GGEA.exe")));
        Assert.False(File.Exists(Path.Combine(target, UpdateFiles.JournalName)));
    }

    /// <summary>安装后的真实自检失败必须恢复全部旧文件，并移除新增加的文件。</summary>
    [Fact]
    public void Install_RollsBackWhenPostInstallVerificationFails()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "installed");
        var package = Path.Combine(temp.Path, "new");
        CreatePackage(target, "1.1.9.1", "old", extra: "old.dll");
        CreatePackage(package, "1.1.10.0", "new", extra: "new.dll");
        var before = File.ReadAllText(Path.Combine(target, "build-info.json"));
        Assert.Throws<IOException>(() => UpdateInstaller.Install(target, package, "1.1.10.0", path =>
        { if (path == target) throw new IOException("self-test failed"); }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "GGEA.exe")));
        Assert.Equal(before, File.ReadAllText(Path.Combine(target, "build-info.json")));
        Assert.True(File.Exists(Path.Combine(target, "old.dll")));
        Assert.False(File.Exists(Path.Combine(target, "new.dll")));
        Assert.False(File.Exists(Path.Combine(target, UpdateFiles.JournalName)));
    }

    /// <summary>文件占用或下载后的文件被修改时，在第一处目标修改之前退出。</summary>
    [Fact]
    public void Install_StopsBeforeMutationOnBusyOrCorruptFiles()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "installed");
        var package = Path.Combine(temp.Path, "new");
        CreatePackage(target, "1.1.9.1", "old");
        CreatePackage(package, "1.1.10.0", "new");
        using (var occupied = new FileStream(Path.Combine(target, "GGEA.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => UpdateInstaller.Install(target, package, "1.1.10.0", _ => { }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "GGEA.exe")));
        File.WriteAllText(Path.Combine(package, "GGEA.exe"), "corrupt");
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Install(target, package, "1.1.10.0", _ => { }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "GGEA.exe")));
    }

    /// <summary>模拟写入中断；损坏备份阻止恢复，修复备份后同一记录可重试且幂等。</summary>
    [Fact]
    public void Recovery_ValidatesBackupBeforeWriting_AndCanRetry()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "installed");
        CreatePackage(target, "1.1.9.1", "old");
        var backupRelative = ".ggea-update/backup-" + new string('a', 32);
        var backup = UpdateFiles.SafePath(target, backupRelative);
        Directory.CreateDirectory(backup);
        File.Copy(Path.Combine(target, "GGEA.exe"), Path.Combine(backup, "GGEA.exe"));
        var hash = UpdateFiles.Hash(Path.Combine(backup, "GGEA.exe"));
        UpdateFiles.WriteJson(UpdateFiles.SafePath(target, UpdateFiles.JournalName),
            new UpdateJournal(backupRelative, "1.1.9.1", "1.1.10.0", [new("GGEA.exe", true, hash), new("extra.dll", false, null)]));
        File.WriteAllText(Path.Combine(target, "GGEA.exe"), "partial");
        File.WriteAllText(Path.Combine(target, "extra.dll"), "partial");
        File.WriteAllText(Path.Combine(backup, "GGEA.exe"), "corrupt backup");
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Recover(target));
        Assert.Equal("partial", File.ReadAllText(Path.Combine(target, "GGEA.exe")));
        File.WriteAllText(Path.Combine(backup, "GGEA.exe"), "old");
        UpdateInstaller.Recover(target);
        UpdateInstaller.Recover(target);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "GGEA.exe")));
        Assert.False(File.Exists(Path.Combine(target, "extra.dll")));
    }

    /// <summary>安装锁覆盖整个应用生命周期，不能出现两个更新器同时写入。</summary>
    [Fact]
    public void InstallationLock_IsExclusive()
    {
        using var temp = new TempDirectory();
        using (UpdateFiles.AcquireLock(temp.Path)) Assert.Throws<IOException>(() => UpdateFiles.AcquireLock(temp.Path));
        using var next = UpdateFiles.AcquireLock(temp.Path);
        Assert.True(next.CanWrite);
    }

    /// <summary>普通包无正式清单也能升级，保留未知文件，并可继续安装下一正式版本。</summary>
    [Fact]
    public void Install_LocalBuild_CanUpgradeTwice()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "installed");
        var package = Path.Combine(temp.Path, "package");
        CreatePackage(target, "1.1.9.1", "old", extra: "unknown.dll");
        File.Delete(Path.Combine(target, "build-info.json"));
        File.WriteAllText(Path.Combine(target, "local-build-info.json"), "{\"version\":\"1.1.9.1\"}");
        CreatePackage(package, "1.1.10.0", "new");
        UpdateInstaller.Install(target, package, "1.1.10.0", _ => { });
        Assert.True(File.Exists(Path.Combine(target, "unknown.dll")));
        Assert.Equal("1.1.10.0", UpdateManifest.ReadInstallation(target).Version);
        var second = Path.Combine(temp.Path, "second");
        CreatePackage(second, "1.1.11.0", "next");
        UpdateInstaller.Install(target, second, "1.1.11.0", _ => { });
        Assert.Equal("1.1.11.0", UpdateManifest.ReadInstallation(target).Version);
    }

    /// <summary>构造真实清单和小文件，测试事务不依赖大模型或实际 EXE 执行。</summary>
    private static void CreatePackage(string root, string version, string content, string? extra = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        var paths = new List<string> { "GGEA.exe", "GGEA.Updater.exe", "config/ggea.config.json", "config/ap_consumption_profiles.json", "resources/stage_work_positions.json" };
        if (extra is not null) paths.Add(extra);
        foreach (var path in paths) File.WriteAllText(Path.Combine(root, path), content);
        var files = paths.ToDictionary(path => path, path => UpdateFiles.Hash(Path.Combine(root, path)));
        UpdateFiles.WriteJson(Path.Combine(root, "build-info.json"), new
        {
            schemaVersion = 1, version, commit = new string('a', 40), channel = "Release", configuration = "Release",
            runtime = "win-x64", selfContained = true, singleFile = true, files
        });
    }

    /// <summary>使用与发布脚本一致的一层包目录。</summary>
    private static string CreateArchive(string root, string package, string version)
    {
        var archive = Path.Combine(root, "package.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(package, "*", SearchOption.AllDirectories))
            zip.CreateEntryFromFile(file, $"GGEA-v{version}-win-x64/" + Path.GetRelativePath(package, file).Replace('\\', '/'));
        return archive;
    }

    /// <summary>最小 GitHub Release 响应，与固定发布仓库的附件命名保持一致。</summary>
    private static string ReleaseJson(string version) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, draft = false, prerelease = false, body = "更新说明",
        assets = new[] { $"GGEA-v{version}-win-x64.zip", $"GGEA-v{version}-win-x64.zip.sha256" }.Select(name => new
        {
            name, state = "uploaded", size = 100,
            browser_download_url = $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v{version}/{name}"
        })
    });

    /// <summary>返回独立响应，避免并发请求共用已释放的 HTTP 内容。</summary>
    private static HttpResponseMessage JsonReply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    /// <summary>替换 HTTP 传输边界，不替换实际版本选择、校验和下载逻辑。</summary>
    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(reply(request));
        }
    }

    /// <summary>每例独占随机临时目录；不删除用户安装或其它测试的工作区。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-update-test-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
