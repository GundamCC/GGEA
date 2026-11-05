namespace GGEA.App.Vision;

/// <summary>平台图像边界：解码必须输出非预乘、逐行紧凑的 BGRA32；失败必须抛出异常。</summary>
public interface IImageCodec
{
    ScreenImage DecodePng(byte[] pngBytes);
    byte[] EncodePng(byte[] bgra, int width, int height);
}

/// <summary>进程级图像服务。宿主在启动时注册一次，任务不得更换后端，避免并发识别使用不同像素规则。</summary>
public static class ImageCodec
{
    private static IImageCodec? _backend;

    public static void Initialize(IImageCodec backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var previous = Interlocked.CompareExchange(ref _backend, backend, null);
        if (previous is not null && !ReferenceEquals(previous, backend))
            throw new InvalidOperationException("图像编解码器已初始化，不能在运行中替换。");
    }

    private static IImageCodec Backend => Volatile.Read(ref _backend)
        ?? throw new InvalidOperationException("宿主尚未初始化图像编解码器，不能启动识别。");

    public static ScreenImage DecodePng(byte[] pngBytes) => Backend.DecodePng(pngBytes);
    public static byte[] EncodePng(byte[] bgra, int width, int height) => Backend.EncodePng(bgra, width, height);
}
