using System.Reflection;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Core.Tests;

/// <summary>只引用核心的测试进程，防止日后将桌面框架或宿主依赖重新引入公共任务链。</summary>
public sealed class CoreBoundaryTests
{
    [Fact]
    public void Core_LoadsWithoutDesktopHost()
    {
        var assembly = typeof(AutomationService).Assembly;
        Assert.Equal("GGEA.Core", assembly.GetName().Name);
        Assert.Equal(".NETCoreApp,Version=v8.0", assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        var forbidden = new[] { "GGEA", "GGEA.Updater", "GGEA.Plugin.Abstractions", "PresentationCore", "PresentationFramework", "WindowsBase", "System.Windows.Forms", "System.Drawing.Common", "Microsoft.Win32.Registry" };
        var visited = new HashSet<string>();
        var pending = new Queue<Assembly>();
        pending.Enqueue(assembly);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.FullName!)) continue;
            // 递归检查产品及第三方包；共享 .NET 运行时自身的平台实现由对应运行时提供。
            if (Path.GetDirectoryName(current.Location) == Path.GetDirectoryName(typeof(object).Assembly.Location)) continue;
            foreach (var reference in current.GetReferencedAssemblies())
            {
                Assert.DoesNotContain(reference.Name, forbidden);
                pending.Enqueue(Assembly.Load(reference));
            }
        }
        // 核心只调用托管跨平台包；系统原生入口应放在宿主适配层。
        Assert.DoesNotContain(assembly.GetTypes().SelectMany(type => type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)),
            method => method.GetCustomAttribute<DllImportAttribute>() is not null);
        Assert.Same(assembly, typeof(DailyQueueRunner).Assembly);
        Assert.Same(assembly, typeof(ConfigService).Assembly);
        Assert.All(typeof(ScreenImage).GetProperties(), property =>
            Assert.Contains(property.PropertyType, new[] { typeof(int), typeof(byte[]) }));
    }

    [Fact]
    public void ConfigurationAndJapaneseResources_WorkWithoutApplication()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ggea-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var log = new GgeaLogger(directory);
            var service = new ConfigService(log);
            var path = Path.Combine(directory, "配置.json");
            var config = service.Load(path);
            config.AdbPath = "平台自定义/adb";
            service.Save(config, path);
            Assert.Equal(config.AdbPath, service.Load(path).AdbPath);
            using var resource = typeof(L).Assembly.GetManifestResourceStream("GGEA.App.Localization.ja.json");
            Assert.NotNull(resource);
            Assert.Equal("未翻译的文案", L.Translate("未翻译的文案", "ja"));
            Assert.NotEqual("设置", L.Translate("设置", "ja"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ImageBackend_IsRequiredStableAndPreservesFailures()
    {
        Assert.Throws<InvalidOperationException>(() => ImageCodec.DecodePng([]));
        Assert.Throws<ArgumentNullException>(() => ImageCodec.Initialize(null!));
        var backend = new ProbeCodec();
        ImageCodec.Initialize(backend);
        ImageCodec.Initialize(backend);
        Assert.Throws<InvalidOperationException>(() => ImageCodec.Initialize(new ProbeCodec()));
        Assert.Same(backend.Frame, ImageCodec.DecodePng(backend.Encoded));
        Assert.Same(backend.Encoded, ImageCodec.EncodePng(backend.Frame.Bgra, 1, 1));
        Assert.Same(backend.Failure, Assert.Throws<InvalidDataException>(() => ImageCodec.DecodePng([])));
    }

    // 只测试宿主边界的传递和失败语义；真实 PNG 像素兼容性由 Windows 编解码回归覆盖。
    private sealed class ProbeCodec : IImageCodec
    {
        internal readonly ScreenImage Frame = new() { Bgra = [10, 20, 30, 255], Width = 1, Height = 1 };
        internal readonly byte[] Encoded = [1, 2, 3];
        internal readonly InvalidDataException Failure = new("损坏的图像");
        public ScreenImage DecodePng(byte[] pngBytes) => ReferenceEquals(pngBytes, Encoded) ? Frame : throw Failure;
        public byte[] EncodePng(byte[] bgra, int width, int height)
        {
            Assert.Same(Frame.Bgra, bgra);
            Assert.Equal(1, width);
            Assert.Equal(1, height);
            return Encoded;
        }
    }
}
