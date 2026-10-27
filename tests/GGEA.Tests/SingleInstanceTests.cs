using System.Reflection;

namespace GGEA.Tests;

/// <summary>使用独立命名锁验证真实启动门禁，避免测试占用正在运行的程序锁。</summary>
public sealed class SingleInstanceTests
{
    /// <summary>另一线程持有锁时，Debug 仍可启动，Release 拒绝；锁释放后均可启动。</summary>
    [Fact]
    public void StartupGate_RespectsBuildConfiguration_AndReleasedLock()
    {
        var mutexName = @"Local\GGEA.Test." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(true, mutexName);
            ready.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        var acquire = typeof(App.App).GetMethod("TryAcquireSingleInstance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cleanup = typeof(App.App).GetMethod("ReleaseSingleInstance", BindingFlags.NonPublic | BindingFlags.Static)!;
        owner.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            var allowed = (bool)acquire.Invoke(null, new object[] { mutexName })!;
#if DEBUG
            Assert.True(allowed);
#else
            Assert.False(allowed);
#endif
        }
        finally
        {
            cleanup.Invoke(null, null);
            release.Set();
            owner.Join();
        }

        try { Assert.True((bool)acquire.Invoke(null, new object[] { mutexName })!); }
        finally { cleanup.Invoke(null, null); }
    }
}
