namespace GGEA.App.Device;

/// <summary>设备执行层的只读观察边界；不参与 ADB 协议，也不能自行发送输入。</summary>
internal interface IAdbExecutionObserver
{
    /// <summary>所有截图通道完成后统一观察；异常须传播给任务，不能伪装成成功截图。</summary>
    Task OnScreenshotAsync(string serial, byte[] png, CancellationToken ct);

    /// <summary>输入命令完成只记作一次尝试，不能直接宣告业务已经推进。</summary>
    void OnInputCompleted(string serial);
}
