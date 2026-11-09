using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace GGEA.App.Updates
{
    /// <summary>发布脚本与客户端共享 v1 分块规则。滚动值只取决于最近的字节，插入数据后能重新找到相同边界。</summary>
    public static class ExeChunker
    {
        public const int Minimum = 16 * 1024;
        public const int Maximum = 128 * 1024;
        private static readonly uint[] Gear = BuildGear();

        private static uint[] BuildGear()
        {
            var table = new uint[256];
            uint value = 0x91e10da5;
            for (int i = 0; i < table.Length; i++)
            {
                value ^= value << 13;
                value ^= value >> 17;
                value ^= value << 5;
                table[i] = value;
            }
            return table;
        }

        // 调用方可在处理块时读取流；每轮从下一个逻辑偏移恢复扫描。
        public static IEnumerable<Chunk> Scan(Stream stream)
        {
            var buffer = new byte[Maximum];
            using (var sha = SHA256.Create())
            {
                for (long offset = 0; offset < stream.Length;)
                {
                    stream.Position = offset;
                    int available = (int)Math.Min(Maximum, stream.Length - offset);
                    int read = 0;
                    while (read < available)
                    {
                        int n = stream.Read(buffer, read, available - read);
                        if (n == 0) throw new EndOfStreamException();
                        read += n;
                    }
                    uint rolling = 0;
                    int size = 0;
                    while (size < available)
                    {
                        rolling = unchecked((rolling << 1) + Gear[buffer[size++]]);
                        if (size >= Minimum && (rolling & 0x7fff) == 0) break;
                    }
                    var hash = BitConverter.ToString(sha.ComputeHash(buffer, 0, size)).Replace("-", "").ToLowerInvariant();
                    yield return new Chunk(offset, size, hash);
                    offset += size;
                }
            }
        }

        public sealed class Chunk
        {
            public long Offset { get; private set; }
            public int Size { get; private set; }
            public string Hash { get; private set; }
            internal Chunk(long offset, int size, string hash) { Offset = offset; Size = size; Hash = hash; }
        }
    }
}
