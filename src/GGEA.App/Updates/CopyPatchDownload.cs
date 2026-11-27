using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using GGEA.Updater;

namespace GGEA.App.Updates;

internal static class CopyPatchDownload
{
    /// <summary>用实际运行文件的摘要选择补丁。旧版本号相同但构建内容不同也不会套用错误差异。</summary>
    internal static async Task<bool> TryPrepareAsync(UpdateHttpTransport http, UpdateRelease update, string installation,
        string fileName, string targetHash, long targetSize, long compressedSize, FileStream destination, CancellationToken token)
    {
        if (targetSize is < FileCopyPatch.MinimumFileBytes or > FileCopyPatch.MaximumFileBytes || !update.CopyPatches.Any(p => p.TargetHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase) && p.Size < compressedSize))
            return false;
        var path = UpdateFiles.SafePath(installation, fileName);
        FileStream source;
        try { source = File.OpenRead(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        using (source)
        {
            if (source.Length > FileCopyPatch.MaximumFileBytes) return false;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token)).ToLowerInvariant();
            var patch = update.CopyPatches.SingleOrDefault(p => p.TargetHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase) && p.SourceHash == hash);
            if (patch is null || patch.Size >= compressedSize) return false;
            byte[] bytes;
            try { bytes = await VerifiedUpdateAsset.ReadAsync(http, update, patch.Url, patch.Size, patch.Sha256, token); }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
            {
                http.Trace($"{fileName} 可选补丁暂不可用，继续下载此文件。");
                return false;
            }
            using var input = new MemoryStream(bytes, writable: false);
            FileCopyPatch.Apply(source, input, destination, targetSize, token);
            http.Trace($"{fileName} 字节差异完成：下载 {bytes.Length} B");
            return true;
        }
    }
}
