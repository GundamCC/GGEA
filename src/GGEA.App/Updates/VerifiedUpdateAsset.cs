using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace GGEA.App.Updates;

/// <summary>索引、可执行文件补丁都由正式附件元数据认证。先核对正文长度和摘要，再解释内容。</summary>
internal static class VerifiedUpdateAsset
{
    internal static async Task<byte[]> ReadAsync(UpdateHttpTransport http, UpdateRelease update,
        Uri url, int size, string hash, CancellationToken token)
    {
        var bytes = new byte[size];
        using var response = await http.GetAsync(url, update.AllowedHosts, token);
        using var stream = await http.OpenBodyAsync(response, token);
        int received = 0;
        while (received < bytes.Length)
        {
            int count = await RangePackageDownload.ReadNetworkAsync(stream, bytes.AsMemory(received), token);
            if (count == 0) throw new HttpRequestException("更新附件下载中断。");
            received += count;
        }
        if (await RangePackageDownload.ReadNetworkAsync(stream, new byte[1], token) != 0 ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新附件长度或摘要不匹配。");
        return bytes;
    }
}
