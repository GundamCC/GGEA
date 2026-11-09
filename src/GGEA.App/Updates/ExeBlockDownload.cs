using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>按目标顺序从本机和块附件重建 EXE。索引已认证，每个输出块及整个文件仍须通过摘要校验。</summary>
internal static class ExeBlockDownload
{
    private sealed record Block(long Offset, int Compressed, int Size, string Hash, long? LocalOffset);

    public static async Task<bool> TryPrepareAsync(UpdateHttpTransport http, UpdateRelease update, UpdateBlockAsset asset,
        JsonElement map, string installation, FileStream output, long targetSize, long wholeFileCost, CancellationToken token)
    {
        if (map.GetProperty("algorithm").GetString() != "gear-v1") return false;
        FileStream source;
        Dictionary<string, ExeChunker.Chunk> local = new(StringComparer.Ordinal);
        try
        {
            source = File.OpenRead(UpdateFiles.SafePath(installation, "GGEA.exe"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        using (source)
        {
            if (source.Length > UpdatePackage.MaximumPackageBytes) return false;
            try
            {
                foreach (var chunk in ExeChunker.Scan(source))
                {
                    token.ThrowIfCancellationRequested();
                    local.TryAdd(chunk.Hash, chunk);
                }
            }
            catch (IOException) { return false; }
            var plan = new List<Block>();
            long packed = 0, expanded = 0, remoteCost = 0;
            foreach (var item in map.GetProperty("chunks").EnumerateArray())
            {
                var size = item.GetProperty("size").GetInt32();
                var compressed = item.GetProperty("compressed").GetInt32();
                var hash = item.GetProperty("hash").GetString();
                if (size is <= 0 or > ExeChunker.Maximum || compressed is <= 0 or > ExeChunker.Maximum + 1024 ||
                    hash is null || !UpdateSourceCatalog.IsHash(hash) || packed + compressed > asset.Size ||
                    expanded + size > targetSize || plan.Count > 65536)
                    throw new InvalidDataException("主程序块索引无效。");
                long? offset = local.TryGetValue(hash!, out var found) && found.Size == size ? found.Offset : null;
                if (!offset.HasValue) remoteCost += compressed;
                plan.Add(new(packed, compressed, size, hash!, offset));
                packed += compressed;
                expanded += size;
            }
            if (expanded != targetSize || packed != asset.Size) throw new InvalidDataException("主程序块索引不完整。");
            if (remoteCost >= wholeFileCost) return false;

            var buffer = new byte[ExeChunker.Maximum];
            for (var i = 0; i < plan.Count;)
            {
                token.ThrowIfCancellationRequested();
                var block = plan[i];
                if (block.LocalOffset is { } localOffset)
                {
                    source.Position = localOffset;
                    try { await source.ReadExactlyAsync(buffer.AsMemory(0, block.Size), token); }
                    catch (IOException) { return Reset(output); }
                    await WriteVerifiedAsync(output, buffer, block, token);
                    i++;
                    continue;
                }
                // 连续缺失块共用一个 Range；每组最多 4 MiB，限制内存并减少 HTTP 往返。
                var end = i + 1;
                while (end < plan.Count && plan[end].LocalOffset == null &&
                    plan[end].Offset + plan[end].Compressed - block.Offset <= 4 * 1024 * 1024) end++;
                var last = plan[end - 1];
                var count = checked((int)(last.Offset + last.Compressed - block.Offset));
                byte[] data;
                try
                {
                    data = await ReadGroupAsync(http, update, asset, block.Offset, count, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                {
                    return Reset(output);
                }
                for (; i < end; i++)
                {
                    var next = plan[i];
                    using var slice = new MemoryStream(data, (int)(next.Offset - block.Offset), next.Compressed, false);
                    using var decoder = new DeflateStream(slice, CompressionMode.Decompress);
                    decoder.ReadExactly(buffer, 0, next.Size);
                    if (decoder.ReadByte() >= 0) throw new InvalidDataException("主程序块超过声明大小。");
                    await WriteVerifiedAsync(output, buffer, next, token);
                }
            }
            return true;
        }
    }

    private static bool Reset(FileStream output)
    {
        output.SetLength(0);
        output.Position = 0;
        return false;
    }

    private static async Task<byte[]> ReadGroupAsync(UpdateHttpTransport http, UpdateRelease update,
        UpdateBlockAsset asset, long offset, int count, CancellationToken token)
    {
        var last = offset + count - 1;
        using var response = await http.GetAsync(asset.Url, update.AllowedHosts, token, new(offset, last));
        if (response.StatusCode == HttpStatusCode.OK) throw new HttpRequestException("更新源不支持块下载。");
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
            range.From != offset || range.To != last || range.Length != asset.Size)
            throw new InvalidDataException("主程序块响应范围不匹配。");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[count];
        int received = 0;
        while (received < count)
        {
            var read = await RangePackageDownload.ReadNetworkAsync(stream, bytes.AsMemory(received), token);
            if (read == 0) throw new HttpRequestException("主程序块下载中断。");
            received += read;
        }
        if (await RangePackageDownload.ReadNetworkAsync(stream, new byte[1], token) != 0)
            throw new InvalidDataException("主程序块响应过长。");
        return bytes;
    }

    private static async Task WriteVerifiedAsync(FileStream output, byte[] buffer, Block block, CancellationToken token)
    {
        if (!Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, block.Size))).Equals(block.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("主程序块校验失败。");
        await output.WriteAsync(buffer.AsMemory(0, block.Size), token);
    }
}
