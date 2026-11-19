namespace GGEA.App.Tasks;

/// <summary>
/// 单设备关卡导航上下文。普通作品关卡页退出后，游戏会把该作品置于屏幕中心；
/// 但未解锁作品的详情页退出会保留原镜头，因此必须保存两种情况下的实际视口偏移。
/// </summary>
public sealed class StageNavigationContext
{
    public string? Serial { get; private set; }
    public StageMapMode Mode { get; private set; } = StageMapMode.Main;
    public StagePageState PageState { get; private set; } = StagePageState.Unknown;
    public int? CurrentWorkId { get; private set; }
    public int? CurrentHardIndex { get; private set; }
    public double? ViewOffsetX { get; private set; }
    public bool IsReliable { get; private set; }

    /// <summary>记录首次从左端绝对定位得到的视口偏移。</summary>
    public void SetLocatedCanvas(string serial, StageMapMode mode, double viewOffsetX)
    {
        Serial = serial;
        Mode = mode;
        PageState = StagePageState.MainStageCanvas;
        CurrentWorkId = null;
        CurrentHardIndex = null;
        ViewOffsetX = viewOffsetX;
        IsReliable = true;
    }

    /// <summary>记录已经进入指定作品的具体关卡页。</summary>
    public void SetWorkStageList(string serial, StageMapMode mode, int workId, int? hardIndex = null)
    {
        Serial = serial;
        Mode = mode;
        PageState = StagePageState.WorkStageList;
        CurrentWorkId = workId;
        CurrentHardIndex = hardIndex;
        ViewOffsetX = null;
        IsReliable = true;
    }

    /// <summary>记录当前作品对应的可靠画布视口；该作品可为居中状态或未解锁详情页返回后的保留状态。</summary>
    public void SetWorkCanvas(string serial, StageMapMode mode, int workId, double viewOffsetX)
    {
        Serial = serial;
        Mode = mode;
        PageState = StagePageState.MainStageCanvas;
        CurrentWorkId = workId;
        CurrentHardIndex = null;
        ViewOffsetX = viewOffsetX;
        IsReliable = true;
    }

    /// <summary>设备或页面状态发生矛盾时清空上下文。</summary>
    public void Invalidate()
    {
        PageState = StagePageState.Unknown;
        CurrentWorkId = null;
        CurrentHardIndex = null;
        ViewOffsetX = null;
        IsReliable = false;
    }
}
