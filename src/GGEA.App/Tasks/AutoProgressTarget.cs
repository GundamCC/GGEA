using GGEA.App.Localization;
using GGEA.App.Config;

namespace GGEA.App.Tasks;

/// <summary>队列项冻结类别与目标；主线和强敌编号分别去重，不污染共享作品配置。</summary>
public sealed record AutoProgressTarget(HardStageEntry? MainWork, FierceEnemyUnit? Enemy, HardStageEntry? HistoryWork = null, bool AttackHistoryBoss = true)
{
    public string Key => HistoryWork is { } history ? $"history:{history.Id}" : MainWork is { } work ? $"main:{work.Id}" : $"enemy:{Enemy!.Id}";
    public string Work => HistoryWork?.Work ?? MainWork?.Work ?? Enemy!.Name;
    public string Summary => HistoryWork is { } history
        ? AttackHistoryBoss ? L.Format($"历史 · 关卡至 HARD {history.HardCount} → BOSS 六关 → 双 100% 复核")
            : L.Format($"历史 · 仅关卡至 HARD {history.HardCount} → 关卡 100% 复核")
        : MainWork is { } work
        ? L.Format($"主线 · 普通（含 BOSS）{work.NormalCount} 关 · 支线 {work.SideStories} 关 · HARD {work.HardCount} 关")
        : L.Text("强敌来袭 · NORMAL → HARD → EXPERT · 跳过 CLEAR / COMPLETE");
}
