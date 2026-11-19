namespace GGEA.App.Tasks;

/// <summary>
/// 作品关卡序列规划器。
/// NORMAL 的最后一关固定视为 BOSS；SIDE STORY（如存在）位于 BOSS 后、HARD 前。
/// 统一序号用于把识别到的当前节点与像素距离关联，禁止再假设固定五槽位。
/// </summary>
public static class WorkStageSequencePlanner
{
    /// <summary>返回作品内全部节点数。</summary>
    public static int GetTotalCount(int normalCount, int sideStories, int hardCount)
        => Math.Max(0, normalCount) + Math.Max(0, sideStories) + Math.Max(0, hardCount);

    /// <summary>根据类型和显示编号换算从左到右的一基序号。</summary>
    public static bool TryGetPosition(
        int normalCount,
        int sideStories,
        int hardCount,
        WorkStageKind kind,
        int number,
        out WorkStagePosition? position)
    {
        position = null;
        if (normalCount < 1 || sideStories < 0 || hardCount < 1 || number < 1)
        {
            return false;
        }

        switch (kind)
        {
            case WorkStageKind.Normal when number < normalCount:
                position = new WorkStagePosition(kind, number, number);
                return true;
            case WorkStageKind.Boss when number == normalCount:
                position = new WorkStagePosition(kind, number, normalCount);
                return true;
            case WorkStageKind.SideStory when number <= sideStories:
                position = new WorkStagePosition(kind, number, normalCount + number);
                return true;
            case WorkStageKind.Hard when number <= hardCount:
                position = new WorkStagePosition(kind, number, normalCount + sideStories + number);
                return true;
            default:
                return false;
        }
    }
}
