using System.Reflection;
using System.Runtime.Loader;

namespace GGEA.Tests;

/// <summary>隔离静态状态验证退出清理，不影响其他并行测试的共享图像线程。</summary>
public sealed class ImageCodecLifetimeTests
{
    /// <summary>未初始化和编解码后的关闭均可重复调用；已启动的线程必须真正退出。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shutdown_StopsWorkerAndRejectsFurtherCalls(bool initialize)
    {
        var context = new AssemblyLoadContext("codec-lifetime-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            // 加载专用夹具内链接编译的原始实现；每个用例仍拥有独立静态状态，
            // 但不让同名 GGEA/BAML 程序集进入另一个上下文干扰真实窗口的资源加载。
            var assembly = context.LoadFromAssemblyPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "GGEA.CodecLifetimeFixture.dll"));
            var type = assembly.GetType("GGEA.App.Vision.ImageCodecThread", throwOnError: true)!;
            var invoke = type.GetMethod("Invoke", BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(Thread));
            var shutdown = type.GetMethod("Shutdown", BindingFlags.Static | BindingFlags.NonPublic)!;
            Func<Thread> current = () => Thread.CurrentThread;
            Thread? worker = null;
            try
            {
                if (initialize)
                {
                    worker = (Thread)invoke.Invoke(null, [current])!;
                    Assert.True(worker.IsAlive);
                    Assert.Equal(ApartmentState.STA, worker.GetApartmentState());
                }
            }
            finally { shutdown.Invoke(null, null); }

            shutdown.Invoke(null, null);
            if (worker is not null) Assert.False(worker.IsAlive);
            var error = Assert.Throws<TargetInvocationException>(() => invoke.Invoke(null, [current]));
            Assert.IsType<ObjectDisposedException>(error.InnerException);
        }
        finally { context.Unload(); }
    }
}
