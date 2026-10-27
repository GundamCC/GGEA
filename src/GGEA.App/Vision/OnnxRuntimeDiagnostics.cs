using System.IO;
using System.Runtime.InteropServices;

namespace GGEA.App.Vision;

/// <summary>将 OCR 原生加载异常转换为可操作的诊断；不改变模型错误、推理结果或失败返回。</summary>
internal static class OnnxRuntimeDiagnostics
{
    internal const string RuntimeDownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";

    /// <summary>覆盖 SessionOptions 和会话的首次初始化，使 UI 只记录 Message 时仍保留底层原因。</summary>
    internal static T Initialize<T>(Func<T> initialize)
    {
        try { return initialize(); }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw DescribeFailure(ex, AppContext.BaseDirectory);
        }
    }

    /// <summary>仅包装原生加载与 ONNX 静态初始化错误，模型损坏、取消和业务异常仍按原语义传播。</summary>
    private static bool IsNativeLoadFailure(Exception error)
    {
        if (error.GetBaseException() is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            return true;
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is TypeInitializationException initialization &&
                initialization.TypeName?.StartsWith("Microsoft.ML.OnnxRuntime.", StringComparison.Ordinal) == true)
                return true;
        return false;
    }

    /// <summary>区分包内 DLL 缺失与已存在但加载失败；文件存在不等于 VC++ 依赖完整或版本可用。</summary>
    internal static InvalidOperationException DescribeFailure(Exception error, string baseDirectory)
    {
        var cause = error.GetBaseException();
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var nativeExists = File.Exists(Path.Combine(baseDirectory, "onnxruntime.dll")) ||
            File.Exists(Path.Combine(baseDirectory, "runtimes", $"win-{architecture}", "native", "onnxruntime.dll"));
        const string unpack = "请重新完整解压最新 win-x64 发布包，保留 GGEA.exe 同目录的 DLL 和 resources，勿混用旧版本文件或只复制 EXE。";
        var install = $"请安装或修复 Microsoft Visual C++ v14 x64 运行库（微软官方下载：{RuntimeDownloadUrl}），然后关闭并重新启动 GGEA。";
        var guidance = cause switch
        {
            BadImageFormatException => $"原生 DLL 架构不符或文件损坏。{unpack}",
            EntryPointNotFoundException => $"原生 DLL 与托管 ONNX Runtime 版本不一致，或依赖版本过旧。{unpack}{install}",
            DllNotFoundException when !nativeExists => $"程序目录缺少 onnxruntime.dll。{unpack}",
            DllNotFoundException => $"onnxruntime.dll 已找到，但它或其依赖无法加载。{install}若仍失败，{unpack}",
            _ => $"ONNX Runtime 初始化失败。{unpack}{install}"
        };
        // 静态初始化失败会被 CLR 缓存；提示重启，保留异常链供发布自检和后续诊断使用。
        return new InvalidOperationException($"离线 OCR 运行环境不可用（进程 {architecture}）：{guidance} " +
            $"底层错误：{cause.GetType().Name}，0x{cause.HResult:X8}：{cause.Message}", error);
    }
}
