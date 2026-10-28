using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GGEA.App.Localization;

namespace GGEA.App;

/// <summary>在创建识别服务前探测原生依赖；只加载本包文件，不初始化 OCR 的托管静态状态。</summary>
internal static class RuntimeDependencies
{
    internal const string SetupFileName = "DependencySetup_依赖库安装.bat";
    internal const string DownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";
    internal enum FailureKind { MissingFile, InvalidImage, LoadFailed }
    internal sealed record Failure(string Library, FailureKind Kind, string Detail);

    /// <summary>支持单文件发布与 SDK 的 runtimes 布局；成功的句柄必须释放，错误保留诊断原因。</summary>
    internal static Failure? Check(string directory, Action<string>? probe = null)
    {
        probe ??= Probe;
        foreach (var library in new[] { "OpenCvSharpExtern.dll", "onnxruntime.dll" })
        {
            var path = Path.Combine(directory, library);
            if (!File.Exists(path))
                path = Path.Combine(directory, "runtimes", $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "native", library);
            if (!File.Exists(path)) return new(library, FailureKind.MissingFile, "");
            try { probe(path); }
            catch (BadImageFormatException ex) { return new(library, FailureKind.InvalidImage, ex.Message); }
            catch (Exception ex) when (ex is DllNotFoundException or FileLoadException)
            { return new(library, FailureKind.LoadFailed, $"0x{ex.HResult:X8}: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>将依赖搜索限制为 DLL 所在目录和安全系统目录，避免工作目录中的同名 DLL 干扰检查。</summary>
    private static void Probe(string path)
    {
        var handle = NativeLibrary.Load(Path.GetFullPath(path), typeof(RuntimeDependencies).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        NativeLibrary.Free(handle);
    }

    /// <summary>失败时不进入自动化；安装器另行运行，当前进程退出后由用户重新启动。</summary>
    internal static bool EnsureAvailable(string directory)
    {
        var failure = Check(directory);
        if (failure is null) return true;
        var title = L.Text("GGEA 运行环境检查");
        if (failure.Kind != FailureKind.LoadFailed)
        {
            MessageBox.Show(L.Format($"原生文件缺失、架构不匹配或损坏：{failure.Library}。请重新完整解压 win-x64 发布包，不要只复制 EXE。") + "\n" + failure.Detail,
                title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var choice = MessageBox.Show(L.Format($"无法加载 {failure.Library} 或其依赖。可能需要安装或修复 Microsoft Visual C++ v14 x64 运行库。") + "\n\n" +
            L.Text("是：运行安装或修复脚本；否：打开微软官方下载；取消：退出。安装后请重新启动 GGEA，仍失败时重新完整解压发布包。") +
            "\n\n" + DownloadUrl + "\n" + failure.Detail, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return false;
        try
        {
            var script = Path.Combine(directory, SetupFileName);
            // 脚本缺失时直接使用官方入口，不让损坏的发布包阻断用户修复环境。
            var start = choice == MessageBoxResult.Yes && File.Exists(script)
                ? new ProcessStartInfo(script) { WorkingDirectory = directory, UseShellExecute = true, Verb = "runas" }
                : new ProcessStartInfo(DownloadUrl) { UseShellExecute = true };
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(L.Text("安装引导未启动或已取消。可手动运行程序目录内的依赖库安装脚本，或复制微软下载地址到浏览器：") +
                "\n" + DownloadUrl + "\n" + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return false;
    }
}
