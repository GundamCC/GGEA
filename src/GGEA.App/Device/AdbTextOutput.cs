using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GGEA.App.Device;

/// <summary>ADB 文本按原始字节解码：优先严格 UTF-8，旧版工具回退本机 ANSI 代码页。</summary>
internal static class AdbTextOutput
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>读取完成后再判断编码，避免多字节字符跨缓冲区或默认 StreamReader 编码造成乱码。</summary>
    internal static async Task<string> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return Decode(buffer.ToArray(), (int)GetACP());
    }

    /// <summary>有效 UTF-8 不作二次修复；仅解码失败时使用指定系统代码页，不猜测已损坏文本。</summary>
    internal static string Decode(byte[] bytes, int ansiCodePage)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        try { return StrictUtf8.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(ansiCodePage).GetString(bytes);
        }
    }

    /// <summary>Windows ANSI 代码页由系统决定，不用界面语言或控制台设置替代。</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
