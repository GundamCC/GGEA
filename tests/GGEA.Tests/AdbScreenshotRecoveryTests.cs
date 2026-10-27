using System.Buffers.Binary;
using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>用实际 PNG 验证前缀兼容路径，拒绝把损坏图片或伪签名作为截图。</summary>
public sealed class AdbScreenshotRecoveryTests
{
    /// <summary>原样截图不额外复制，保留既有快速路径。</summary>
    [Fact]
    public void PlainPng_ReturnsSameArray()
    {
        var png = CreatePng();
        Assert.Same(png, AdbScreenshotOutput.Normalize(png, out var prefix));
        Assert.Equal(0, prefix);
    }

    /// <summary>复现用户的 347 字节前缀，也覆盖非固定长度与扫描边界。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(347)]
    [InlineData(1024)]
    [InlineData(4088)]
    public void PrefixedPng_RecoversExactPixels(int length)
    {
        var png = CreatePng();
        var output = Prefix(png, length);
        var recovered = AdbScreenshotOutput.Normalize(output, out var prefix);
        Assert.Equal(length, prefix);
        Assert.Equal(png, recovered);
        Assert.Equal(new byte[] { 20, 30, 40, 255 }, ImageCodec.DecodePng(recovered).Bgra);
        Assert.Equal(0x41, output[0]);
    }

    /// <summary>项目真实模板经兼容恢复后仍保持原始字节与尺寸。</summary>
    [Fact]
    public void RealTemplate_WithPrefix_RemainsDecodable()
    {
        var png = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "common", "templates", "stage", "sortie", "sortie.png"));
        var recovered = AdbScreenshotOutput.Normalize(Prefix(png, 347), out _);
        Assert.Equal(png, recovered);
        var image = ImageCodec.DecodePng(recovered);
        Assert.Equal(366, image.Width);
        Assert.Equal(133, image.Height);
    }

    /// <summary>过长前缀不做无界扫描，也不尝试猜测输出内容。</summary>
    [Fact]
    public void PrefixBeyondLimit_IsRejected()
        => Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix(CreatePng(), 4096), out _));

    /// <summary>CRC 损坏、截断和额外尾部都必须拒绝。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DamagedPng_IsRejected(int kind)
    {
        var png = CreatePng();
        if (kind == 0) png[29] ^= 1;
        if (kind == 1) png = png[..^5];
        if (kind == 2) png = [.. png, 0];
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix(png, 347), out _));
    }

    /// <summary>压缩内容损坏但 CRC 正确时，必须由实际解码拦截。</summary>
    [Fact]
    public void InvalidPixelsWithValidCrc_IsRejected()
    {
        var png = CreatePng();
        var position = 8;
        while (!png.AsSpan(position + 4, 4).SequenceEqual("IDAT"u8))
            position += (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position)) + 12;
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
        png.AsSpan(position + 8, length).Clear();
        RewriteCrc(png, position);
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix(png, 347), out _));
    }

    /// <summary>异常尺寸在像素分配前拒绝，避免恢复路径放大内存需求。</summary>
    [Fact]
    public void ExcessiveDimensions_IsRejected()
    {
        var png = CreatePng();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), uint.MaxValue);
        RewriteCrc(png, 8);
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix(png, 347), out _));
    }

    /// <summary>仅有签名不算成功，也不能跳过第一个伪候选继续猜测。</summary>
    [Fact]
    public void FalseSignature_IsRejected()
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix(signature, 347), out _));
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix([.. signature, .. CreatePng()], 347), out _));
    }

    /// <summary>生成真实可解码且像素确定的测试 PNG。</summary>
    private static byte[] CreatePng() => ImageCodec.EncodePng([20, 30, 40, 255], 1, 1);

    /// <summary>模拟传输将每个 LF 扩展为 CRLF；恢复必须逐字节等于原图。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(347)]
    [InlineData(4086)]
    public void NewlineConvertedPng_RecoversOriginalBytes(int prefixLength)
    {
        var png = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "common", "templates", "stage", "sortie", "sortie.png"));
        var converted = ExpandNewlines(png);
        var recovered = AdbScreenshotOutput.Normalize(Prefix(converted, prefixLength), out var prefix, out var newlineRecovered);
        Assert.Equal(prefixLength, prefix);
        Assert.True(newlineRecovered);
        Assert.Equal(png, recovered);
    }

    /// <summary>转换签名同样受扫描上限约束，不能越过前缀边界或跳过首个损坏候选。</summary>
    [Fact]
    public void ConvertedSignatureBeyondLimitOrAfterFalseCandidate_IsRejected()
    {
        var converted = ExpandNewlines(CreatePng());
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(Prefix(converted, 4096), out _));
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize([.. converted[..10], .. CreatePng()], out _));
    }

    /// <summary>换行恢复不应放行 CRC 损坏、截断、尾部污染或只转换文件头的输出。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidNewlineConvertedPng_IsRejected(int kind)
    {
        var png = CreatePng();
        if (kind == 0) png[29] ^= 1;
        if (kind == 1) png = png[..^5];
        if (kind == 2) png = [.. png, 0];
        // tEXt 含原生 CRLF，可检验恢复只移除新增 CR，不能直接删除所有 CR。
        if (kind == 3) png = InsertTextChunk(png);
        var converted = kind == 3 ? (byte[])[.. ExpandNewlines(png[..8]), .. png[8..]] : ExpandNewlines(png);
        Assert.Throws<AdbException>(() => AdbScreenshotOutput.Normalize(converted, out _));
    }

    /// <summary>有效 PNG 内原有 CRLF 与孤立 CR 必须完整保留。</summary>
    [Fact]
    public void NewlineRecovery_PreservesOriginalCarriageReturns()
    {
        var png = InsertTextChunk(CreatePng());
        Assert.Equal(png, AdbScreenshotOutput.Normalize(ExpandNewlines(png), out _));
    }

    /// <summary>在 IHDR 后加入合法文本块，提供明确的二进制换行回归样本。</summary>
    private static byte[] InsertTextChunk(byte[] png)
    {
        byte[] data = "note\0a\r\nb\rc\n"u8.ToArray();
        byte[] chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        RewriteCrc(chunk, 0);
        return [.. png[..33], .. chunk, .. png[33..]];
    }

    /// <summary>复现输出通道的单次 LF 到 CRLF 转换，不通过文本编码处理二进制。</summary>
    private static byte[] ExpandNewlines(byte[] bytes)
        => bytes.SelectMany(value => value == 10 ? new byte[] { 13, 10 } : new byte[] { value }).ToArray();

    /// <summary>模拟输出污染，不使用用户真实前缀内容。</summary>
    private static byte[] Prefix(byte[] png, int length) => [.. Enumerable.Repeat((byte)0x41, length), .. png];

    /// <summary>测试独立按位计算 CRC，以构造结构有效但像素或尺寸无效的输入。</summary>
    private static void RewriteCrc(byte[] png, int position)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
        var crc = uint.MaxValue;
        foreach (var value in png.AsSpan(position + 4, length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(position + 8 + length), ~crc);
    }
}
