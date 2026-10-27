namespace GGEA.App.Automation;

/// <summary>当前截图已确认连接错误或数据更新，要求调用方终止任务链并提示用户，禁止页面恢复点击。</summary>
public sealed class ConnectionErrorException : Exception
{
    /// <summary>保留明确的错误类型，避免上层再次截图后丢失已经确认的错误。</summary>
    public ConnectionErrorException() : base("检测到网络错误、游戏数据更新或日期更新，自动化流程已停止")
    {
    }
}
