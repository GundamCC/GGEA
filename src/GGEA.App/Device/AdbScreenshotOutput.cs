using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>兼容截图标准输出中的有限前缀与单次换行转换；恢复后严格验证 PNG。</summary>
internal static class AdbScreenshotOutput
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static ReadOnlySpan<byte> ConvertedSignature => [137, 80, 78, 71, 13, 13, 10, 26, 13, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>正常截图保持原数组与原有解码流程；兼容恢复路径增加严格验证。</summary>
    internal static byte[] Normalize(byte[] output, out int prefixLength)
        => Normalize(output, out prefixLength, out _);

    /// <summary>返回实际恢复类型供调用方限频提示；未知转换不猜测、不重复剥离。</summary>
    internal static byte[] Normalize(byte[] output, out int prefixLength, out bool newlineRecovered)
    {
        prefixLength = 0;
        newlineRecovered = false;
        if (output.AsSpan().StartsWith(Signature)) return output;
        var scan = output.AsSpan(0, Math.Min(4096, output.Length));
        var offset = scan.IndexOf(Signature);
        var convertedOffset = scan.IndexOf(ConvertedSignature);
        // 只处理首个候选，避免跳过损坏截图中的伪签名后误用其它图像。
        var converted = convertedOffset >= 0 && (offset < 0 || convertedOffset < offset);
        if (converted) offset = convertedOffset;
        if (offset < 0)
        {
            AdbService.ValidateScreenshot(output);
            throw new AdbException("截图没有可恢复的 PNG 签名");
        }

        try
        {
            var recovered = converted ? RestoreNewlines(output.AsSpan(offset)) : output.AsSpan(offset).ToArray();
            ValidateChunks(recovered);
            // WPF 的 OnLoad 与 CopyPixels 会真正解压像素，不能仅凭文件头或 CRC 放行。
            _ = ImageCodec.DecodePng(recovered);
            prefixLength = offset;
            newlineRecovered = converted;
            return recovered;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException or IOException or InvalidDataException)
        {
            throw new AdbException($"截图兼容恢复失败（前缀 {offset} 字节，换行恢复={converted}）：PNG 完整性或解码验证失败（{ex.GetType().Name}）；拒绝用于识图", ex);
        }
    }

    /// <summary>仅撤销一次 LF→CRLF：原生 CRLF 会变成 CRCRLF，因此必须保留前一个 CR。</summary>
    private static byte[] RestoreNewlines(ReadOnlySpan<byte> output)
    {
        var restored = new byte[output.Length];
        var count = 0;
        for (var index = 0; index < output.Length; index++)
        {
            if (output[index] == 13 && index + 1 < output.Length && output[index + 1] == 10)
                continue;
            restored[count++] = output[index];
        }
        Array.Resize(ref restored, count);
        return restored;
    }

    /// <summary>备用通道必须通过完整验证，不能只凭文件头把损坏图片交给自动化。</summary>
    internal static void ValidateRecoveredImage(byte[] png)
    {
        try
        {
            AdbService.ValidateScreenshot(png);
            ValidateChunks(png);
            _ = ImageCodec.DecodePng(png);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException or IOException or InvalidDataException)
        { throw new AdbException($"ADB 备用截图完整性或像素解码验证失败（{ex.GetType().Name}）", ex); }
    }

    /// <summary>验证块边界、首尾、CRC 和尺寸上限，防止伪签名、截断或异常尺寸被当作有效截图。</summary>
    private static void ValidateChunks(ReadOnlySpan<byte> png)
    {
        var position = 8;
        var first = true;
        var hasData = false;
        using var compressed = new MemoryStream();
        uint width = 0, height = 0;
        var bitsPerPixel = 0;
        var interlaced = false;
        while (position <= png.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > (uint)(png.Length - position - 12)) throw new InvalidOperationException("PNG 块被截断");
            var count = (int)length;
            var typeAndData = png.Slice(position + 4, count + 4);
            var type = typeAndData[..4];
            if (ComputeCrc(typeAndData) != BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position + 8 + count, 4)))
                throw new InvalidOperationException("PNG CRC 不匹配");
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || count != 13) throw new InvalidOperationException("缺少 PNG IHDR");
                width = BinaryPrimitives.ReadUInt32BigEndian(typeAndData[4..]);
                height = BinaryPrimitives.ReadUInt32BigEndian(typeAndData[8..]);
                // 仅约束恢复路径的内存开销，覆盖常见模拟器分辨率，不修改业务分辨率要求。
                if (width == 0 || height == 0 || (ulong)width * height > 32_000_000)
                    throw new InvalidOperationException("PNG 尺寸超出恢复上限");
                var depth = typeAndData[12];
                var color = typeAndData[13];
                var channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                var validDepth = color is 0 or 3 ? depth is 1 or 2 or 4 or 8 || (color == 0 && depth == 16) : depth is 8 or 16;
                if (channels == 0 || !validDepth || typeAndData[14] != 0 || typeAndData[15] != 0 || typeAndData[16] > 1)
                    throw new InvalidOperationException("PNG 编码参数无效");
                bitsPerPixel = depth * channels;
                interlaced = typeAndData[16] == 1;
                first = false;
            }
            else if (type.SequenceEqual("IHDR"u8)) throw new InvalidOperationException("重复 PNG IHDR");
            if (type.SequenceEqual("IDAT"u8))
            {
                hasData = true;
                compressed.Write(typeAndData[4..]);
            }
            position += count + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (count != 0 || !hasData || position != png.Length) throw new InvalidOperationException("PNG 结束块或尾部异常");
                ValidatePixelStream(compressed, width, height, bitsPerPixel, interlaced);
                return;
            }
        }
        throw new InvalidOperationException("缺少完整 PNG 结束块");
    }

    /// <summary>WPF 可能容忍损坏压缩流，因此单独验证 zlib 解压、扫描行长度与滤波类型。</summary>
    private static void ValidatePixelStream(MemoryStream compressed, uint width, uint height, int bitsPerPixel, bool interlaced)
    {
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        // Adam7 七轮扫描；普通 PNG 仅使用完整的一轮。
        int[] startsX = interlaced ? [0, 4, 0, 2, 0, 1, 0] : [0];
        int[] startsY = interlaced ? [0, 0, 4, 0, 2, 0, 1] : [0];
        int[] stepsX = interlaced ? [8, 8, 4, 4, 2, 2, 1] : [1];
        int[] stepsY = interlaced ? [8, 8, 8, 4, 4, 2, 2] : [1];
        Span<byte> buffer = stackalloc byte[8192];
        for (var pass = 0; pass < startsX.Length; pass++)
        {
            var columns = Math.Max(0, ((long)width - startsX[pass] + stepsX[pass] - 1) / stepsX[pass]);
            var rows = Math.Max(0, ((long)height - startsY[pass] + stepsY[pass] - 1) / stepsY[pass]);
            if (columns == 0 || rows == 0) continue;
            var rowBytes = (columns * bitsPerPixel + 7) / 8;
            for (long row = 0; row < rows; row++)
            {
                var filter = zlib.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidOperationException("PNG 扫描行滤波或长度无效");
                var remaining = rowBytes;
                while (remaining > 0)
                {
                    var count = zlib.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
                    if (count == 0) throw new InvalidOperationException("PNG 像素流截断");
                    remaining -= count;
                }
            }
        }
        if (zlib.ReadByte() != -1) throw new InvalidOperationException("PNG 像素流长度异常");
    }

    /// <summary>PNG 使用标准 CRC-32，覆盖块类型和内容，不覆盖长度字段。</summary>
    private static uint ComputeCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>只创建一次查找表，避免高分辨率截图逐位计算 CRC。</summary>
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
