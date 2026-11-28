using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>从已认证的目标索引组装暂存目录。只复用实际内容相同的文件，不修改运行中的安装。</summary>
internal static class RangePackageDownload
{
    private sealed record Entry(string Path, long Offset, long Compressed, long Size, int Method);

    public static async Task<string> PrepareAsync(UpdateHttpTransport http, UpdateRelease update, UpdateRangeIndex index,
        string installation, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var token = timeout.Token;
        var workspace = UpdateFiles.SafePath(installation, ".ggea-update/download-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(workspace, "package");
        Directory.CreateDirectory(package);
        try
        {
            var bytes = await VerifiedUpdateAsset.ReadAsync(http, update, index.Url, index.Size, index.Sha256, token);
            using var document = ParseIndex(bytes, index.Brotli);
            var root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 ||
                !string.Equals(root.GetProperty("archiveHash").GetString(), update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新索引与正式附件不匹配。");
            var manifestPath = UpdateFiles.SafePath(package, "build-info.json");
            await File.WriteAllBytesAsync(manifestPath, root.GetProperty("manifest").GetBytesFromBase64(), token);
            var manifest = UpdateManifest.Read(package);
            if (manifest.Version != update.Version.ToString(4) || !manifest.Files.ContainsKey("GGEA.Updater.exe"))
                throw new InvalidDataException("更新索引版本或文件集合无效。");
            var pending = new List<Entry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            long previousEnd = 0;
            foreach (var property in root.GetProperty("entries").EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                var value = property.Value;
                var file = new Entry(property.Name, value.GetProperty("offset").GetInt64(),
                    value.GetProperty("compressed").GetInt64(), value.GetProperty("size").GetInt64(), value.GetProperty("method").GetInt32());
                if (!paths.Add(file.Path) || !manifest.Files.TryGetValue(file.Path, out var hash) ||
                    file.Offset < previousEnd || file.Compressed < 0 || file.Offset > update.PackageSize - file.Compressed ||
                    file.Size < 0 || file.Size > UpdatePackage.MaximumExpandedBytes - expanded ||
                    file.Method is not (0 or 8) || (file.Method == 0 && file.Compressed != file.Size))
                    throw new InvalidDataException("更新索引条目无效。");
                previousEnd = file.Offset + file.Compressed;
                expanded += file.Size;
                var target = UpdateFiles.SafePath(package, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (TryReuse(installation, file.Path, file.Size, hash, target)) continue;
                if (UpdateFiles.IsUserFile(file.Path) && TryReuse(PublishedDefaults.CacheRoot(installation), file.Path, file.Size, hash, target)) continue;
                pending.Add(file);
            }
            if (paths.Count != manifest.Files.Count) throw new InvalidDataException("更新索引缺少文件。");
            var total = pending.Sum(file => file.Size);
            long written = 0;
            var buffer = new byte[81920];
            foreach (var file in pending)
            {
                using var output = File.Create(UpdateFiles.SafePath(package, file.Path));
                if (await CopyPatchDownload.TryPrepareAsync(http, update, installation, file.Path, manifest.Files[file.Path], file.Size, file.Compressed, output, token))
                {
                    written += file.Size;
                    progress.Report(100.0 * written / Math.Max(1, total));
                    continue;
                }
                // 旧索引只有 exeBlocks。两个附件独立选择，一项缺失时只回退对应文件。
                var (blocks, mapName) = file.Path switch
                {
                    "GGEA.exe" => (update.Blocks, "exeBlocks"),
                    "GGEA.Updater.exe" => (update.UpdaterBlocks, "updaterBlocks"),
                    _ => (null, "")
                };
                if (blocks is not null && root.TryGetProperty(mapName, out var blockMap) &&
                    await ExeBlockDownload.TryPrepareAsync(http, update, blocks, blockMap, installation, file.Path, output,
                        file.Size, file.Compressed, token))
                {
                    written += file.Size;
                    progress.Report(100.0 * written / Math.Max(1, total));
                    continue;
                }
                if (mapName.Length > 0 && (blocks is null || !root.TryGetProperty(mapName, out _)))
                    http.Trace($"{file.Path} 回退整文件：缺少分块附件或索引。");
                http.Trace($"整文件：{file.Path}（{file.Compressed} B 压缩数据）");
                if (file.Compressed == 0 && file.Size == 0) continue;
                var last = file.Offset + file.Compressed - 1;
                using var response = await http.GetAsync(update.PackageUrl, update.AllowedHosts, token, new(file.Offset, last));
                if (response.StatusCode == HttpStatusCode.OK) throw new HttpRequestException("更新源不支持分段下载。");
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                    range.From != file.Offset || range.To != last || range.Length != update.PackageSize)
                    throw new InvalidDataException("更新源返回的字节范围不匹配。");
                using var stream = await http.OpenBodyAsync(response, token);
                using var decoder = file.Method == 8 ? new DeflateStream(stream, CompressionMode.Decompress, true) : null;
                Stream input = decoder is null ? stream : decoder;
                long fileBytes = 0;
                int count;
                while ((count = await ReadNetworkAsync(input, buffer, token)) != 0)
                {
                    fileBytes += count;
                    if (fileBytes > file.Size) throw new InvalidDataException("更新文件超过声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    written += count;
                    progress.Report(100.0 * written / Math.Max(1, total));
                }
                if (fileBytes != file.Size) throw new InvalidDataException("更新文件不完整。");
            }
            manifest.Verify(package, cancellationToken: token);
            progress.Report(100);
            return package;
        }
        catch
        {
            UpdatePackageDownloader.DeleteDownload(workspace);
            throw;
        }
    }

    // 压缩附件经过摘要校验后仍限制展开大小，避免有缺陷的发布索引耗尽内存。
    private static JsonDocument ParseIndex(byte[] bytes, bool compressed)
    {
        if (!compressed) return JsonDocument.Parse(bytes);
        using var source = new MemoryStream(bytes);
        using var decoder = new BrotliStream(source, CompressionMode.Decompress);
        using var json = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = decoder.Read(buffer)) != 0)
        {
            if (json.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("更新索引展开后过大。");
            json.Write(buffer, 0, count);
        }
        json.Position = 0;
        return JsonDocument.Parse(json);
    }

    // 单次等待有界。只转换网络读取异常，内容校验和磁盘错误保留原原因。
    internal static async Task<int> ReadNetworkAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(TimeSpan.FromSeconds(45));
        try { return await stream.ReadAsync(buffer, stall.Token); }
        catch (IOException ex) { throw new HttpRequestException("更新下载连接中断。", ex); }
    }

    // 文件缺失、被修改或占用时只重取这一项；复制后统一复核，覆盖并发修改窗口。
    private static bool TryReuse(string installation, string relativePath, long size, string hash, string target)
    {
        try
        {
            var path = UpdateFiles.SafePath(installation, relativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != size ||
                !UpdateFiles.Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
            File.Copy(path, target, overwrite: true);
            return new FileInfo(target).Length == size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
