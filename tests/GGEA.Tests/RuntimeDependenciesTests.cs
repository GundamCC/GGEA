using System.IO;
using System.Runtime.InteropServices;
using GGEA.App;

namespace GGEA.Tests;

/// <summary>验证首次启动的原生环境门禁，不安装或卸载本机运行库。</summary>
public sealed class RuntimeDependenciesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GGEA-runtime-" + Guid.NewGuid().ToString("N"));

    public RuntimeDependenciesTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>丢失文件时不调用加载器，也不能误导用户安装运行库来修复包缺失。</summary>
    [Fact]
    public void MissingPackage_DoesNotProbeOrSuggestRuntimeFailure()
    {
        var result = RuntimeDependencies.Check(_root, _ => throw new Exception("must not probe"));
        Assert.Equal(RuntimeDependencies.FailureKind.MissingFile, result!.Kind);
        Assert.Equal("OpenCvSharpExtern.dll", result.Library);
    }

    /// <summary>发布和开发目录都必须探测两个库，不能只证明其中一个可用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletePackage_ProbesBothLibraries(bool nested)
    {
        CreateLibraries(nested);
        var paths = new List<string>();
        Assert.Null(RuntimeDependencies.Check(_root, paths.Add));
        Assert.Equal(new[] { "OpenCvSharpExtern.dll", "onnxruntime.dll" }, paths.Select(Path.GetFileName));
        Assert.All(paths, path => Assert.True(Path.IsPathFullyQualified(path)));
    }

    /// <summary>第二个库的传递依赖失败同样阻止启动；错误架构与依赖缺失分开报告。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFailure_PreservesReasonAndBlocksStartup(bool badImage)
    {
        CreateLibraries(false);
        var failure = RuntimeDependencies.Check(_root, path =>
        {
            if (Path.GetFileName(path) == "onnxruntime.dll")
            {
                if (badImage) throw new BadImageFormatException("wrong architecture");
                throw new DllNotFoundException("dependency missing");
            }
        });
        Assert.Equal("onnxruntime.dll", failure!.Library);
        Assert.Equal(badImage ? RuntimeDependencies.FailureKind.InvalidImage : RuntimeDependencies.FailureKind.LoadFailed, failure.Kind);
        Assert.Contains(badImage ? "wrong architecture" : "dependency missing", failure.Detail);
    }

    /// <summary>真实本机加载覆盖 P/Invoke 搜索选项及释放句柄；随构建复制独立脚本。</summary>
    [Fact]
    public void ActualBuild_ContainsInstallerAndLoadsNativeDependencies()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, RuntimeDependencies.SetupFileName)));
        Assert.Null(RuntimeDependencies.Check(AppContext.BaseDirectory));
    }

    /// <summary>只生成文件存在性夹具，加载成功与失败由探针控制。</summary>
    private void CreateLibraries(bool nested)
    {
        var directory = nested ? Path.Combine(_root, "runtimes", $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "native") : _root;
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "OpenCvSharpExtern.dll", "onnxruntime.dll" })
            File.WriteAllBytes(Path.Combine(directory, name), []);
    }
}
