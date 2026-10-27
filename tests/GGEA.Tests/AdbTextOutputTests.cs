using System.Text;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>复现中文 ADB 输出编码错配，验证文本兼容且截图二进制不被转码。</summary>
public sealed class AdbTextOutputTests
{
    private const string Message = "由于目标计算机积极拒绝，无法连接。(10061)";

    /// <summary>UTF-8 在中文 ANSI 环境下仍按 UTF-8 解码；含 BOM 同样可读。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf8_IgnoresAnsiDefault(bool bom)
    {
        var bytes = Encoding.UTF8.GetBytes(Message);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        Assert.Equal(Message, AdbTextOutput.Decode(bytes, 936));
    }

    /// <summary>旧版工具输出 GBK 时回退系统代码页，不强制 UTF-8 产生替换字符。</summary>
    [Fact]
    public void LegacyGbk_FallsBack()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(Message, AdbTextOutput.Decode(Encoding.GetEncoding(936).GetBytes(Message), 936));
    }

    /// <summary>ASCII 和空输出保持兼容。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("device emulator-5558")]
    public void Ascii_RemainsUnchanged(string value)
        => Assert.Equal(value, AdbTextOutput.Decode(Encoding.ASCII.GetBytes(value), 936));

    /// <summary>真实子进程分别写 stdout/stderr，验证文本和截图错误通道都使用新解码器。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProcessOutput_DecodesUtf8(bool binary, bool error)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Message));
        var channel = error ? "OpenStandardError" : "OpenStandardOutput";
        var script = $"$b=[Convert]::FromBase64String('{payload}'); $s=[Console]::{channel}(); $s.Write($b,0,$b.Length); $s.Flush(); exit {(error ? 1 : 0)}";
        string[] args = ["-NoProfile", "-NonInteractive", "-Command", script];
        var adb = new AdbService("powershell.exe", new TestLog());
        if (error)
        {
            var ex = await Assert.ThrowsAsync<AdbException>(async () =>
            {
                if (binary) await adb.RunBytesAsync(args, 10000, CancellationToken.None);
                else await adb.RunTextAsync(args, 10000, CancellationToken.None);
            });
            Assert.Contains(Message, ex.Message);
        }
        else Assert.Equal(Message, await adb.RunTextAsync(args, 10000, CancellationToken.None));
    }

    /// <summary>二进制截图标准输出保持逐字节原样，即使包含无效 UTF-8。</summary>
    [Fact]
    public async Task BinaryOutput_IsUnchanged()
    {
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, 255, 0, 128];
        var script = $"$b=[Convert]::FromBase64String('{Convert.ToBase64String(bytes)}'); $s=[Console]::OpenStandardOutput(); $s.Write($b,0,$b.Length)";
        var adb = new AdbService("powershell.exe", new TestLog());
        Assert.Equal(bytes, await adb.RunBytesAsync(["-NoProfile", "-NonInteractive", "-Command", script], 10000, CancellationToken.None));
    }
}
