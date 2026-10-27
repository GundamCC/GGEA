using System.Windows.Threading;

namespace GGEA.App.Vision;

/// <summary>进程级图像线程：复用 Dispatcher，避免每一帧重复创建线程和隐藏窗口。</summary>
internal static class ImageCodecThread
{
    private static readonly Lazy<Dispatcher> Worker = new(StartWorker);
    private static Thread? _thread;
    private static int _stopping;

    /// <summary>串行执行图像操作；异常返回当前调用方，不终止后续编解码。</summary>
    internal static T Invoke<T>(Func<T> action)
    {
        if (Volatile.Read(ref _stopping) != 0)
            throw new ObjectDisposedException(nameof(ImageCodecThread));
        return Worker.Value.Invoke(action);
    }

    /// <summary>在应用退出、CLR 仍允许线程正常清理时关闭；不在 ProcessExit 中等待线程。</summary>
    internal static void Shutdown()
    {
        // 由应用生命周期调用，退出前不再提交图像任务；未使用图像功能时不创建线程。
        if (Interlocked.Exchange(ref _stopping, 1) != 0 || !Worker.IsValueCreated) return;
        Worker.Value.BeginInvokeShutdown(DispatcherPriority.Send);
        // 避免线程内调用时等待自身；正常入口由应用主线程负责等待。
        if (Thread.CurrentThread != _thread && !_thread!.Join(TimeSpan.FromSeconds(2)))
            System.Diagnostics.Trace.TraceWarning("图像线程未在退出期限内停止");
    }

    /// <summary>首次使用时启动 STA 消息循环；进程退出时在所属线程释放窗口。</summary>
    private static Dispatcher StartWorker()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher? dispatcher = null;
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                // 初始化失败也必须唤醒调用方，不能让首帧永久等待。
                if (!ready.TrySetException(error)) throw;
            }
            finally
            {
                dispatcher?.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "GGEA PNG codec" };
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        thread.Start();
        var worker = ready.Task.GetAwaiter().GetResult();
        return worker;
    }
}
