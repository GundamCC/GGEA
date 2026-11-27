using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>同一锁标也出现在挑战战斗等卡片上，按故事事件页的目标入口分别限定区域。</summary>
internal static class LimitedEntryLock
{
    internal const string Template = "stage/history_boss_lock";
    internal static readonly Roi Boss = new(1805, 185, 78, 80);
    internal static readonly Roi Supply = new(1805, 580, 78, 80);
}
