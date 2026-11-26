using System.IO;

namespace GGEA.App.Updates;

/// <summary>只统计下载操作从 HTTP 响应实际读入的字节，包括未完成的尝试；不含本地解压和复用。</summary>
public sealed class UpdateDownloadTraffic
{
    private long _bytes;
    public long Bytes => Interlocked.Read(ref _bytes);
    internal int Count(int bytes) { Interlocked.Add(ref _bytes, bytes); return bytes; }
}

internal sealed class DownloadBody(Stream input, UpdateDownloadTraffic traffic) : Stream
{
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => traffic.Count(input.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        => traffic.Count(await input.ReadAsync(buffer, token).ConfigureAwait(false));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
