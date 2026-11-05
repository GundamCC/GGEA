namespace GGEA.App.Tasks;

/// <summary>
/// 关卡列表视口规划器。
/// 进入作品关卡页后，视口固定显示 5 个槽位，但槽位对应哪一关取决于当前选中关卡：
/// 靠左时从第 1 关开始，靠右时显示最后 5 关，中间时当前关居中。
/// </summary>
public static class StageSlotPlanner
{
    /// <summary>视口槽位数量。</summary>
    public const int SlotCount = 5;

    /// <summary>根据当前 HARD 编号计算视口起始关卡序号。</summary>
    public static int GetViewportStart(
        int normalCount,
        int hardCount,
        int currentHardIndex,
        int sideStories = 0)
    {
        var total = GetTotalStageCount(normalCount, hardCount, sideStories);
        var currentIndex = GetOverallIndex(normalCount, currentHardIndex, sideStories);

        // 关卡不足 5 个时直接显示全部；否则按“当前关居中”并做左右边界钳制。
        if (total <= SlotCount)
        {
            return 1;
        }

        return Math.Clamp(currentIndex - (SlotCount / 2), 1, total - SlotCount + 1);
    }

    /// <summary>当前 HARD 关在整个作品关卡序列中的序号。</summary>
    public static int GetOverallIndex(int normalCount, int hardIndex, int sideStories = 0)
        => normalCount + Math.Max(0, sideStories) + hardIndex;

    /// <summary>整个作品的关卡总数。</summary>
    public static int GetTotalStageCount(int normalCount, int hardCount, int sideStories = 0)
        => normalCount + Math.Max(0, sideStories) + hardCount;

    /// <summary>
    /// 目标 HARD 关是否在当前 5 个槽位内；在则输出 1~5 的槽位号。
    /// </summary>
    public static bool TryGetSlotIndex(
        int normalCount,
        int hardCount,
        int currentHardIndex,
        int targetHardIndex,
        out int slotIndex,
        int sideStories = 0)
    {
        var start = GetViewportStart(normalCount, hardCount, currentHardIndex, sideStories);
        var targetIndex = GetOverallIndex(normalCount, targetHardIndex, sideStories);
        var relative = targetIndex - start;
        if (relative < 0 || relative >= SlotCount)
        {
            slotIndex = 0;
            return false;
        }

        slotIndex = relative + 1;
        return true;
    }

    /// <summary>
    /// 是否允许执行目标 HARD 关：只允许执行当前关及之前已解锁的关卡，
    /// 当前关之后的关卡视为尚未解锁，不执行。
    /// </summary>
    public static bool ShouldExecute(
        int normalCount,
        int currentHardIndex,
        int targetHardIndex,
        int sideStories = 0)
    {
        return GetOverallIndex(normalCount, targetHardIndex, sideStories) <=
               GetOverallIndex(normalCount, currentHardIndex, sideStories);
    }
}
