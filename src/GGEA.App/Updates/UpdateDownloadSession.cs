namespace GGEA.App.Updates;

/// <summary>一次下载到安装的交接：只有完整包验证成功且未取消才允许自动安装，失败保留可重试的已验证包。</summary>
public sealed class UpdateDownloadSession
{
    public string? PreparedPackage { get; private set; }

    /// <summary>下载委托必须包含完整校验；安装委托由界面持有的执行租约保护，不启动设备任务。</summary>
    public async Task RunAsync(Func<CancellationToken, Task<string>> downloadAndVerify,
        Func<string, CancellationToken, Task> install, bool autoInstall, CancellationToken cancellationToken)
    {
        PreparedPackage = await downloadAndVerify(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (autoInstall) await install(PreparedPackage, cancellationToken);
    }
}
