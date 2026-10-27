using System.IO;
using System.Runtime.InteropServices;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>回归用户仅看到 NativeMethods 外层异常的问题，覆盖包缺失、依赖失败及真实 CPU 加载。</summary>
public sealed class OnnxRuntimeDiagnosticsTests
{
    /// <summary>模拟真实首次初始化异常链；错误继续抛出，最外层消息必须含处理方式和底层诊断。</summary>
    [Fact]
    public void Initialize_WrappedNativeFailure_PreservesCauseAndExplainsRepair()
    {
        var cause = new DllNotFoundException("Unable to load DLL 'onnxruntime': 0x8007007E");
        var error = new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", cause);
        var result = Assert.Throws<InvalidOperationException>(() =>
            OnnxRuntimeDiagnostics.Initialize<int>(() => throw error));
        Assert.Same(error, result.InnerException);
        Assert.Same(cause, result.GetBaseException());
        Assert.Contains("Visual C++ v14 x64", result.Message);
        Assert.Contains(OnnxRuntimeDiagnostics.RuntimeDownloadUrl, result.Message);
        Assert.Contains("重新启动", result.Message);
        Assert.Contains("DllNotFoundException", result.Message);
        Assert.Contains("0x8007007E", result.Message);
    }

    /// <summary>文件存在仅能排除包缺失，不能把缺少传递依赖判成成功；兼容开发输出的 runtimes 布局。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescribeFailure_PackageAndDependencyFailures_HaveDifferentRecovery(bool developmentLayout)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GGEA-onnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var error = new DllNotFoundException("Native dependency unavailable");
            var missing = OnnxRuntimeDiagnostics.DescribeFailure(error, directory);
            Assert.Contains("缺少 onnxruntime.dll", missing.Message);
            Assert.Contains("完整解压", missing.Message);
            Assert.DoesNotContain("Visual C++", missing.Message);
            var nativeDirectory = developmentLayout
                ? Path.Combine(directory, "runtimes", $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "native")
                : directory;
            Directory.CreateDirectory(nativeDirectory);
            File.WriteAllBytes(Path.Combine(nativeDirectory, "onnxruntime.dll"), []);
            var dependency = OnnxRuntimeDiagnostics.DescribeFailure(error, directory);
            Assert.Contains("已找到", dependency.Message);
            Assert.Contains("Visual C++ v14 x64", dependency.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>架构与入口不匹配不能一概归因于缺少运行库，仍须提示重新解压匹配版本。</summary>
    [Fact]
    public void Initialize_IncompatibleNativeLibrary_ExplainsArchitectureOrVersion()
    {
        var architecture = Assert.Throws<InvalidOperationException>(() =>
            OnnxRuntimeDiagnostics.Initialize<int>(() => throw new BadImageFormatException("wrong machine")));
        Assert.Contains("架构不符", architecture.Message);
        Assert.DoesNotContain("Visual C++", architecture.Message);
        var version = Assert.Throws<InvalidOperationException>(() =>
            OnnxRuntimeDiagnostics.Initialize<int>(() => throw new EntryPointNotFoundException("OrtGetApiBase")));
        Assert.Contains("版本不一致", version.Message);
        Assert.Contains("OrtGetApiBase", version.Message);
    }

    /// <summary>模型、业务和取消错误不能被包装成环境故障或吞掉，正常返回值也保持一致。</summary>
    [Fact]
    public void Initialize_UnrelatedErrorsAndSuccess_PreserveBehavior()
    {
        foreach (var error in new Exception[] { new InvalidDataException("bad model"), new OperationCanceledException() })
            Assert.Same(error, Assert.Throws(error.GetType(), () =>
                OnnxRuntimeDiagnostics.Initialize<int>(() => throw error)));
        Assert.Equal(42, OnnxRuntimeDiagnostics.Initialize(() => 42));
    }

    /// <summary>加载随包真实模型和 CPU 原生库，检查诊断接入没有破坏正常初始化与幂等验证。</summary>
    [Fact]
    public void Validate_PackagedCpuModel_LoadsSuccessfully()
    {
        using var recognizer = new OnnxTextRecognizer();
        recognizer.Validate();
        recognizer.Validate();
    }
}
