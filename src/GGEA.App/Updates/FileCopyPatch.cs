using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace GGEA.App.Updates;

/// <summary>应用已认证的 Brotli 复制指令流：正长度为新字节，负长度从旧文件指定偏移复制。</summary>
internal static class FileCopyPatch
{
    // 小文件直接取压缩后的目标文件，避免为几 KB 的数据增加发布附件。
    public const int MinimumFileBytes = 64 * 1024;
    public const int MaximumPatchBytes = 8 * 1024 * 1024;
    public const int MaximumFileBytes = 256 * 1024 * 1024;

    public static void Apply(Stream source, Stream patch, Stream destination, long targetSize, CancellationToken token)
    {
        if (targetSize < 0 || targetSize > MaximumFileBytes || source.Length > MaximumFileBytes)
            throw new InvalidDataException("文件差异超出大小限制。");
        using var codec = new BrotliStream(patch, CompressionMode.Decompress, true);
        using var reader = new BinaryReader(codec);
        var buffer = new byte[81920];
        long written = 0;
        while (written < targetSize)
        {
            token.ThrowIfCancellationRequested();
            int command = reader.ReadInt32();
            long length = Math.Abs((long)command);
            if (length == 0 || length > targetSize - written)
                throw new InvalidDataException("文件差异指令长度无效。");
            Stream input = codec;
            if (command < 0)
            {
                int offset = reader.ReadInt32();
                if (offset < 0 || offset > source.Length - length)
                    throw new InvalidDataException("文件差异复制范围无效。");
                source.Position = offset;
                input = source;
            }
            written += length;
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(length, buffer.Length);
                input.ReadExactly(buffer, 0, count);
                destination.Write(buffer, 0, count);
                length -= count;
            }
        }
        if (codec.ReadByte() != -1) throw new InvalidDataException("文件差异含多余指令。");
    }
}
