using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

// 发布端每隔 64 字节为旧文件建一个锚点。找到锚点后向前后扩展，插入/删除导致的偏移不影响复用。
internal static class FileCopyPatchBuilder
{
    public static void Write(byte[] source, byte[] target, Stream compressed)
    {
        var anchors = new Dictionary<ulong, int>();
        for (int i = 0; i <= source.Length - 8; i += 64)
            anchors.TryAdd(BitConverter.ToUInt64(source, i), i);

        using var codec = new BrotliStream(compressed, CompressionLevel.Optimal, true);
        using var writer = new BinaryWriter(codec);
        int literal = 0, pos = 0;
        while (pos <= target.Length - 8)
        {
            if (!anchors.TryGetValue(BitConverter.ToUInt64(target, pos), out int offset))
            {
                pos++;
                continue;
            }
            int length = 8;
            while (length < source.Length - offset && length < target.Length - pos &&
                   source[offset + length] == target[pos + length]) length++;
            if (length < 16) { pos++; continue; }
            while (pos > literal && offset > 0 && target[pos - 1] == source[offset - 1])
            {
                pos--; offset--; length++;
            }
            WriteLiteral(literal, pos);
            writer.Write(-length);
            writer.Write(offset);
            pos += length;
            literal = pos;
        }
        WriteLiteral(literal, target.Length);

        void WriteLiteral(int start, int end)
        {
            if (end == start) return;
            writer.Write(end - start);
            writer.Write(target, start, end - start);
        }
    }
}
