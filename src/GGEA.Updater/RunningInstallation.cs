namespace GGEA.Updater;

/// <summary>每个窗口共享安装文件。只有最后一个窗口可以升级为独占，向更新器交接。</summary>
public sealed class RunningInstallation(string root) : IDisposable
{
    private FileStream _lease = UpdateFiles.OpenLock(root, "installation.lock", exclusive: false);

    public async Task StartUpdateAsync(Func<Task> startUpdater)
    {
        // 串行化从共享到独占的切换；两个窗口同时请求更新时，失败方仍持有运行保护。
        using var transition = UpdateFiles.OpenLock(root, "handoff.lock", exclusive: true);
        _lease.Dispose();
        try
        {
            _lease = UpdateFiles.OpenLock(root, "installation.lock", exclusive: true);
            await startUpdater().ConfigureAwait(false);
            // 更新器确认就绪之后，本进程持有独占锁直到退出。
        }
        catch
        {
            _lease.Dispose();
            _lease = UpdateFiles.OpenLock(root, "installation.lock", exclusive: false);
            throw;
        }
    }

    public void Dispose() => _lease.Dispose();
}
