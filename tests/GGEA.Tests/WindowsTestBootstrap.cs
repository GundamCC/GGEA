using System.Runtime.CompilerServices;
using GGEA.App.Vision;

namespace GGEA.Tests;

internal static class WindowsTestBootstrap
{
    // 测试直接创建核心服务，不经过 App 构造；使用与真实 Windows 入口相同的后端。
    [ModuleInitializer]
    internal static void Initialize() => WpfImageCodec.Initialize();
}
