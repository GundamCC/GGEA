using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GGEA.App.Updates;
using GGEA.Updater;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    /// <summary>真实发布脚本生成的附件由客户端重建并安装；未变化文件不下载、不备份，用户配置和插件保留。</summary>
    [Fact]
    public async Task Delta_PublisherToClient_InstallsAndPreservesUserData()
    {
        using var temp = new TempDirectory();
        var previous = Path.Combine(temp.Path, "old");
        var next = Path.Combine(temp.Path, "next");
        CreateDeltaFixtures(previous, next);
        var script = FindDeltaScript();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-BaseDirectory", previous, "-PublishDirectory", next, "-OutputDirectory", Path.Combine(temp.Path, "dist") })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        var archive = Directory.GetFiles(Path.Combine(temp.Path, "dist"), "*.zip").Single();
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("stable.dll"));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("old.dll"));
            Assert.Contains(zip.Entries, e => e.FullName.EndsWith("new.dll"));
        }
        Assert.Equal(UpdateFiles.Hash(archive), UpdatePackageDownloader.ParseChecksum(File.ReadAllText(archive + ".sha256"), Path.GetFileName(archive)));
        File.WriteAllText(Path.Combine(previous, "config/ggea.config.json"), "user settings");
        Directory.CreateDirectory(Path.Combine(previous, "plugins/custom"));
        File.WriteAllText(Path.Combine(previous, "plugins/custom/plugin.json"), "user plugin");
        var prepared = Path.Combine(temp.Path, "prepared");
        UpdatePackage.ExtractDelta(archive, prepared, "1.1.10.0", previous, "1.1.9.1");
        Assert.False(File.Exists(Path.Combine(prepared, UpdateDeltaPackage.MetadataName)));
        using var lockedUnchanged = new FileStream(Path.Combine(previous, "stable.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var backup = UpdateInstaller.Install(previous, prepared, "1.1.10.0", _ => { });
        Assert.Equal("new", File.ReadAllText(Path.Combine(previous, "GGEA.exe")));
        Assert.False(File.Exists(Path.Combine(previous, "old.dll")));
        Assert.False(File.Exists(Path.Combine(backup, "stable.dll")));
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(previous, "config/ggea.config.json")));
        Assert.True(File.Exists(Path.Combine(previous, "plugins/custom/plugin.json")));
    }

    /// <summary>基线损坏和差异包连接失败回退完整包；篡改包与取消不能触发回退。</summary>
    [Theory]
    [InlineData("success", false)]
    [InlineData("missing", true)]
    [InlineData("modified", true)]
    [InlineData("identity", true)]
    [InlineData("locked", true)]
    [InlineData("network", true)]
    [InlineData("tampered", false)]
    [InlineData("cancel", false)]
    public async Task Delta_DownloadFallbackHonorsIntegrityAndCancellation(string scenario, bool fullExpected)
    {
        using var temp = new TempDirectory();
        var previous = Path.Combine(temp.Path, "old");
        var next = Path.Combine(temp.Path, "next");
        CreateDeltaFixtures(previous, next);
        var deltaPath = CreateDeltaArchive(temp.Path, previous, next);
        var fullPath = CreateArchive(temp.Path, next, "1.1.10.0");
        var deltaBytes = File.ReadAllBytes(deltaPath);
        var fullBytes = File.ReadAllBytes(fullPath);
        var deltaHash = UpdateFiles.Hash(deltaPath);
        if (scenario == "missing") File.Delete(Path.Combine(previous, "stable.dll"));
        if (scenario == "modified") File.WriteAllText(Path.Combine(previous, "stable.dll"), "changed");
        if (scenario == "identity") File.AppendAllText(Path.Combine(previous, "build-info.json"), " ");
        if (scenario == "tampered") deltaBytes[0] ^= 1;
        using var locked = scenario == "locked"
            ? new FileStream(Path.Combine(previous, "stable.dll"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var fullName = "GGEA-v1.1.10.0-win-x64.zip";
        var deltaName = "GGEA-v1.1.10.0-from-v1.1.9.1-win-x64.delta.zip";
        var prefix = $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v1.1.10.0/";
        var update = new UpdateRelease(new(1, 1, 10, 0), "", fullName, new(prefix + fullName), fullBytes.Length, null, UpdateFiles.Hash(fullPath))
        { Delta = new("1.1.9.1", deltaName, new(prefix + deltaName), deltaBytes.Length, new(prefix + deltaName + ".sha256"), deltaHash) };
        var fullRequests = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new GitHubUpdateClient(new ReplyHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".sha256")) return JsonReply($"{deltaHash}  {deltaName}");
            if (path.EndsWith(".delta.zip"))
            {
                if (scenario == "network") return new(HttpStatusCode.NotFound);
                if (scenario == "cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(deltaBytes) };
            }
            fullRequests++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fullBytes) };
        }));
        if (scenario == "tampered")
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update, previous, new Progress<double>(), cancellation.Token));
        else if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(update, previous, new Progress<double>(), cancellation.Token));
        else
        {
            var prepared = await client.DownloadAsync(update, previous, new Progress<double>(), cancellation.Token);
            UpdateManifest.Read(prepared).Verify(prepared);
            UpdatePackageDownloader.DeleteDownload(Path.GetDirectoryName(prepared)!);
        }
        Assert.Equal(fullExpected ? 1 : 0, fullRequests);
        Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "GGEA.exe")));
        var state = Path.Combine(previous, UpdateFiles.StateDirectory);
        if (Directory.Exists(state)) Assert.Empty(Directory.GetDirectories(state, "download-*"));
    }

    /// <summary>缺失变化文件或额外文件是坏包，不得误报本机不适用而绕过校验。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("corrupt")]
    public void Delta_RejectsInvalidPayload(string mode)
    {
        using var temp = new TempDirectory();
        var previous = Path.Combine(temp.Path, "old");
        var next = Path.Combine(temp.Path, "next");
        CreateDeltaFixtures(previous, next);
        var archive = CreateDeltaArchive(temp.Path, previous, next);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var path = "GGEA-v1.1.10.0-win-x64/GGEA.exe";
            if (mode != "extra") zip.GetEntry(path)!.Delete();
            if (mode != "missing")
            {
                using var writer = new StreamWriter(zip.CreateEntry(mode == "extra" ? "GGEA-v1.1.10.0-win-x64/extra.txt" : path).Open());
                writer.Write("bad");
            }
        }
        Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractDelta(archive, Path.Combine(temp.Path, "out"), "1.1.10.0", previous, "1.1.9.1"));
    }

    /// <summary>附件选择仅接受当前版本对应且完整的小差异包；旧发布与不完整可选附件继续全量更新。</summary>
    [Theory]
    [InlineData(true, 50, true)]
    [InlineData(false, 50, false)]
    [InlineData(true, 100, false)]
    public async Task Delta_CheckSelectsOnlyCompleteSmallerMatchingAsset(bool checksum, int size, bool expected)
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ReleaseJson("1.1.10.0"))!;
        var assets = json["assets"].EnumerateArray().Select(a => (object)a.Clone()).ToList();
        var name = "GGEA-v1.1.10.0-from-v1.1.9.1-win-x64.delta.zip";
        foreach (var asset in checksum ? new[] { name, name + ".sha256" } : new[] { name })
            assets.Add(new { name = asset, state = "uploaded", size, browser_download_url = $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v1.1.10.0/{asset}" });
        json["assets"] = JsonSerializer.SerializeToElement(assets);
        using var client = new GitHubUpdateClient(new ReplyHandler(_ => JsonReply(JsonSerializer.Serialize(json))));
        Assert.Equal(expected, (await client.CheckAsync(new(1, 1, 9, 1), default))!.Delta is not null);
        Assert.Null((await client.CheckAsync(new(1, 1, 8, 0), default))!.Delta);
    }

    /// <summary>重建包安装失败时恢复删除项和旧配置；新补入的默认配置必须撤销，恢复被占用阻断后仍可重试。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delta_RollbackPreservesOriginalState_AndRetriesAfterLock(bool interruptRecovery)
    {
        using var temp = new TempDirectory();
        var previous = Path.Combine(temp.Path, "old");
        var next = Path.Combine(temp.Path, "next");
        CreateDeltaFixtures(previous, next);
        var archive = CreateDeltaArchive(temp.Path, previous, next);
        var config = Path.Combine(previous, "config/ggea.config.json");
        File.WriteAllText(config, "user settings");
        var missing = Path.Combine(previous, "config/ap_consumption_profiles.json");
        File.Delete(missing);
        File.WriteAllText(Path.Combine(previous, "config/private.json"), "private");
        var before = Directory.GetFiles(previous, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(previous, path), File.ReadAllBytes);
        var prepared = Path.Combine(temp.Path, "prepared");
        UpdatePackage.ExtractDelta(archive, prepared, "1.1.10.0", previous, "1.1.9.1");
        FileStream? locked = null;
        try
        {
            Assert.Throws<IOException>(() => UpdateInstaller.Install(previous, prepared, "1.1.10.0", directory =>
            {
                if (directory != previous) return;
                Assert.Equal("user settings", File.ReadAllText(config));
                Assert.True(File.Exists(missing));
                Assert.False(File.Exists(Path.Combine(previous, "old.dll")));
                if (interruptRecovery) locked = new FileStream(Path.Combine(previous, "GGEA.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("simulated startup failure");
            }));
            Assert.Equal(interruptRecovery, File.Exists(Path.Combine(previous, UpdateFiles.JournalName)));
        }
        finally { locked?.Dispose(); }
        UpdateInstaller.Recover(previous);
        UpdateInstaller.Recover(previous);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(previous, path)));
        Assert.False(File.Exists(missing));
        Assert.False(File.Exists(Path.Combine(previous, "new.dll")));
        Assert.False(File.Exists(Path.Combine(previous, UpdateFiles.JournalName)));
    }

    /// <summary>恢复记录可能包含旧、新清单并集，不能以单份清单的条数上限拒绝；仍先验证备份，不执行损坏恢复。</summary>
    [Fact]
    public void Recovery_AcceptsCombinedInventoryLimit_ButRejectsCorruptBackup()
    {
        using var temp = new TempDirectory();
        var backupRelative = ".ggea-update/backup-" + new string('a', 32);
        var backup = UpdateFiles.SafePath(temp.Path, backupRelative);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "first.dll"), "bad");
        var files = new List<UpdateBackupFile> { new("first.dll", true, new string('a', 64)) };
        files.AddRange(Enumerable.Range(0, 20001).Select(i => new UpdateBackupFile($"file-{i}.dll", false, null)));
        UpdateFiles.WriteJson(UpdateFiles.SafePath(temp.Path, UpdateFiles.JournalName),
            new UpdateJournal(backupRelative, "1.1.9.1", "1.1.10.0", files));
        var error = Assert.Throws<InvalidDataException>(() => UpdateInstaller.Recover(temp.Path));
        Assert.Contains("备份摘要", error.Message);
        Assert.True(File.Exists(UpdateFiles.SafePath(temp.Path, UpdateFiles.JournalName)));
    }

    private static void CreateDeltaFixtures(string previous, string next)
    {
        CreatePackage(previous, "1.1.9.1", "old", "old.dll");
        CreatePackage(next, "1.1.10.0", "new", "new.dll");
        foreach (var root in new[] { previous, next })
        {
            File.WriteAllText(Path.Combine(root, "stable.dll"), new string('s', 1024));
            var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(root, "build-info.json")))!;
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest["files"].GetRawText())!;
            files["stable.dll"] = UpdateFiles.Hash(Path.Combine(root, "stable.dll"));
            manifest["files"] = JsonSerializer.SerializeToElement(files);
            File.WriteAllText(Path.Combine(root, "build-info.json"), JsonSerializer.Serialize(manifest));
        }
    }

    private static string CreateDeltaArchive(string root, string previous, string next)
    {
        var path = Path.Combine(root, "delta.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(next, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "stable.dll"))
            zip.CreateEntryFromFile(file, "GGEA-v1.1.10.0-win-x64/" + Path.GetRelativePath(next, file).Replace('\\', '/'));
        using var writer = new StreamWriter(zip.CreateEntry("GGEA-v1.1.10.0-win-x64/delta-info.json").Open());
        writer.Write(JsonSerializer.Serialize(new { schemaVersion = 1, baseVersion = "1.1.9.1", baseManifestSha256 = UpdateFiles.Hash(Path.Combine(previous, "build-info.json")) }));
        return path;
    }

    private static string FindDeltaScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tools/package-delta.ps1");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("找不到差异包发布脚本。");
    }
}
