using System.IO;
using System.Runtime.InteropServices;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>MuMu 原生渲染接口；仅在一次性截图子进程中加载，退出时释放连接与 DLL。</summary>
internal static class MuMuNativeCapture
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int Connect([MarshalAs(UnmanagedType.LPWStr)] string root, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Disconnect(int handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Display(int handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string package, int appIndex);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Capture(int handle, uint display, int size, ref int width, ref int height, IntPtr buffer);

    /// <summary>独立入口只允许原生截图，不创建窗口、连接 ADB 或执行任何点击。</summary>
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length != 6 || !int.TryParse(args[3], out var index) || index < 0)
                throw new ArgumentException("MuMu 截图参数无效");
            var result = CapturePng(args[1], args[2], index, args[4]);
            File.WriteAllBytes(args[5], result.Png);
            Console.Out.WriteLine($"MuMuNative instance={index}, display={result.DisplayId}, size={result.Width}x{result.Height}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MuMu 原生截图失败: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    /// <summary>按目标游戏包名解析显示编号；不回退默认显示，避免截到其他标签页。</summary>
    internal static (byte[] Png, int DisplayId, int Width, int Height) CapturePng(string libraryPath, string root, int index, string package)
    {
        if (!Path.IsPathFullyQualified(libraryPath) || !Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(package))
            throw new ArgumentException("MuMu 路径或游戏包名无效");
        var library = NativeLibrary.Load(libraryPath);
        var handle = 0;
        Disconnect? disconnect = null;
        try
        {
            var connect = Marshal.GetDelegateForFunctionPointer<Connect>(NativeLibrary.GetExport(library, "nemu_connect"));
            disconnect = Marshal.GetDelegateForFunctionPointer<Disconnect>(NativeLibrary.GetExport(library, "nemu_disconnect"));
            var display = Marshal.GetDelegateForFunctionPointer<Display>(NativeLibrary.GetExport(library, "nemu_get_display_id"));
            var capture = Marshal.GetDelegateForFunctionPointer<Capture>(NativeLibrary.GetExport(library, "nemu_capture_display"));
            handle = connect(root, index);
            if (handle == 0) throw new InvalidOperationException("无法连接指定 MuMu 实例");
            var displayId = display(handle, package, 0);
            if (displayId < 0) throw new InvalidOperationException("未找到目标游戏显示，请在选定实例打开游戏；未回退默认显示");
            var width = 0;
            var height = 0;
            var code = capture(handle, (uint)displayId, 0, ref width, ref height, IntPtr.Zero);
            if (code != 0) throw new InvalidOperationException($"读取原生显示尺寸失败，code={code}");
            var length = ValidateSize(width, height);
            var pixels = new byte[length];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var originalWidth = width;
            var originalHeight = height;
            try { code = capture(handle, (uint)displayId, length, ref width, ref height, pin.AddrOfPinnedObject()); }
            finally { pin.Free(); }
            if (code != 0 || width != originalWidth || height != originalHeight)
                throw new InvalidOperationException($"原生截图失败或尺寸发生变化，code={code}，请重试");
            var bgra = ConvertRgbaBottomUp(pixels, width, height);
            return (ImageCodec.EncodePng(bgra, width, height), displayId, width, height);
        }
        finally
        {
            try { if (handle != 0) disconnect?.Invoke(handle); }
            finally { NativeLibrary.Free(library); }
        }
    }

    /// <summary>提前限制原生缓冲区容量，防止异常尺寸导致整数溢出或大额分配。</summary>
    internal static int ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000)
            throw new InvalidOperationException("MuMu 截图尺寸无效或超过 3200 万像素");
        return checked(width * height * 4);
    }

    /// <summary>MuMu SDK 返回倒序 RGBA；翻转行并转换为项目约定的正序 BGRA，不缩放图像。</summary>
    internal static byte[] ConvertRgbaBottomUp(byte[] rgba, int width, int height)
    {
        var length = ValidateSize(width, height);
        if (rgba.Length != length) throw new InvalidOperationException("MuMu 像素缓冲区长度不匹配");
        var result = new byte[length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = ((height - 1 - y) * width + x) * 4;
            var target = (y * width + x) * 4;
            result[target] = rgba[source + 2];
            result[target + 1] = rgba[source + 1];
            result[target + 2] = rgba[source];
            result[target + 3] = 255; // 屏幕输出按不透明像素处理，避免 SDK 未定义 alpha 影响识图。
        }
        return result;
    }
}
