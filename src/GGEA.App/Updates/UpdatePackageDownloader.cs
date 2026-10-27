using GGEA.App.Localization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>不同来源共用完整包下载与校验；校验失败不作为切换来源的理由。</summary>
public sealed class UpdatePackageDownloader
{
    private readonly UpdateHttpTransport _http;
    internal UpdatePackageDownloader(UpdateHttpTransport http) => _http = http;

    /// <summary>先解析独立校验文件，并与来源元数据中的摘要交叉核对。</summary>
    internal async Task<string> ResolveHashAsync(UpdateRelease update, CancellationToken cancellationToken)
    {
        var hash = update.Sha256;
        if (update.ChecksumUrl is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var checksum = await _http.ReadSmallAsync(update.ChecksumUrl, 4096, update.AllowedHosts, timeout.Token);
            var declared = ParseChecksum(checksum, update.PackageName);
            if (hash is not null && !hash.Equals(declared, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.Text("更新附件摘要与校验文件不一致，已停止更新。"));
            hash = declared;
        }
        return hash ?? throw new InvalidDataException(L.Text("更新源没有提供 SHA-256 摘要。"));
    }

    /// <summary>下载完整包并验证服务端摘要及逐文件清单；失败时不留下可误用的半成品。</summary>
    public async Task<string> DownloadAsync(UpdateRelease update, string installation, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var workspace = UpdateFiles.SafePath(installation, ".ggea-update/download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            var expected = update.Sha256 ?? throw new InvalidDataException(L.Text("缺少可信的更新包摘要。"));
            var archive = Path.Combine(workspace, "package.zip");
            using (var response = await _http.GetAsync(update.PackageUrl, update.AllowedHosts, timeout.Token))
            {
                if (response.Content.Headers.ContentLength is long length && length != update.PackageSize)
                    throw new InvalidDataException(L.Text("下载长度与 GitHub 附件信息不一致。"));
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    stall.CancelAfter(TimeSpan.FromSeconds(45));
                    int count;
                    try { count = await input.ReadAsync(buffer, stall.Token); }
                    catch (IOException ex) { throw new HttpRequestException(L.Text("更新下载连接中断。"), ex); }
                    if (count == 0) break;
                    received += count;
                    if (received > update.PackageSize) throw new InvalidDataException(L.Text("下载大小超过发布记录。"));
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress.Report(100.0 * received / update.PackageSize);
                }
                if (received != update.PackageSize) throw new InvalidDataException(L.Text("更新包下载不完整，请重试。"));
            }
            var package = Path.Combine(workspace, "package");
            await Task.Run(() =>
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!UpdateFiles.Hash(archive).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.Text("更新包 SHA-256 校验失败，未修改当前安装。"));
                UpdatePackage.Extract(archive, package, update.Version.ToString(4), timeout.Token);
            }, timeout.Token);
            File.Delete(archive);
            return package;
        }
        catch
        {
            DeleteDownload(workspace);
            throw;
        }
    }

    /// <summary>只清理本功能创建的下载工作区；不递归跟随任何链接。</summary>
    public static void DeleteDownload(string workspace)
    {
        try
        {
            var directory = new DirectoryInfo(workspace);
            if (!Regex.IsMatch(directory.Name, "^download-[a-f0-9]{32}$") || directory.Parent?.Name != UpdateFiles.StateDirectory)
                throw new InvalidDataException(L.Text("无效的下载清理目录。"));
            DeleteTree(directory);
        }
        catch (IOException) { /* 文件占用时保留缓存，不影响原安装和取消结果。 */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>逐层检查链接后删除文件，避免递归枚举进入目录联接。</summary>
    private static void DeleteTree(DirectoryInfo directory)
    {
        if (!directory.Exists) return;
        UpdateFiles.RejectLinks(directory.FullName);
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            UpdateFiles.RejectLinks(item.FullName);
            if (item is DirectoryInfo child) DeleteTree(child);
            else item.Delete();
        }
        directory.Delete();
    }

    /// <summary>校验文件必须明确绑定精确附件名，不能接受任意摘要文本。</summary>
    public static string ParseChecksum(string text, string name)
    {
        var match = Regex.Match(text.Trim().TrimStart('\uFEFF'), @"^([a-fA-F0-9]{64})\s+\*?" + Regex.Escape(name) + "$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException(L.Text("SHA-256 校验文件格式或文件名不一致。"));
        return match.Groups[1].Value.ToLowerInvariant();
    }

}
