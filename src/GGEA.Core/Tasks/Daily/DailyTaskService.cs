using GGEA.App.Logging;

namespace GGEA.App.Tasks;

/// <summary>
/// 每日任务流程占位。
/// 按 agent.md 第 14 节约定：真实流程必须等获得游戏截图与模板后再实现，
/// 禁止虚构页面、坐标与任务步骤。
/// </summary>
public sealed class DailyTaskService
{
    private readonly IGgeaLog _log;

    public DailyTaskService(IGgeaLog log)
    {
        _log = log;
    }

    public Task RunAsync(string serial, CancellationToken ct = default)
    {
        _log.Warn($"每日任务流程尚未实现（设备 {serial}）：需要真实游戏截图与识别模板");
        return Task.CompletedTask;
    }
}
