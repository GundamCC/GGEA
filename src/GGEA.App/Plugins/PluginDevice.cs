using GGEA.Plugin.Abstractions;

namespace GGEA.App.Plugins;

/// <summary>设备会话快照，目标和执行通道在创建时固定；旧实例撤销后不能向新设备发命令。</summary>
internal sealed class PluginDevice(string serial, string package, string language, Guid bootId,
    Func<IReadOnlyList<string>, CancellationToken, Task<string>> execute, string connectionKey = "") : IPluginDevice, IDisposable
{
    private readonly CancellationTokenSource _revoked = new();
    private readonly object _gate = new();
    private bool _disposed;
    public string Serial => serial;
    public string GamePackage => package;
    public string GameLanguage => language;
    public Guid BootId => bootId;
    internal string ConnectionKey => connectionKey;

    public async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // 复制调用方列表，防止等待期间被改成另一个命令；不允许 -s、connect 或 server 全局操作。
        var args = arguments.ToArray();
        if (args.Length < 2 || args[0] is not ("shell" or "push" or "pull") || args.Any(arg => arg is null))
            throw new ArgumentException("插件设备入口只支持 shell、push、pull。");
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _revoked.Token);
        }
        using (linked) return await execute(args, linked.Token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _revoked.Cancel();
            _revoked.Dispose();
        }
    }
}
