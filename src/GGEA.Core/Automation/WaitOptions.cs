using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>
/// 自动化等待参数，所有等待都有超时上限，禁止无限等待。
/// </summary>
public sealed record WaitOptions
{
    /// <summary>总超时时间（毫秒），默认 20 秒。</summary>
    public int TimeoutMs { get; init; } = GameTiming.RecognitionTimeoutMs;

    /// <summary>轮询间隔（毫秒），默认 5 秒。</summary>
    public int PollIntervalMs { get; init; } = GameTiming.RecognitionIntervalMs;

    /// <summary>匹配置信度阈值。</summary>
    public double Threshold { get; init; } = 0.85;

    /// <summary>仅路线图原生素材使用：严格按 1.3 和 0.832 两档匹配，不进行中间尺度搜索。</summary>
    public bool RoadmapFixedScales { get; init; }

    /// <summary>路线图首次方向探测只识别 1.3 倍放大素材，不让小比例单位触发快捷拖动。</summary>
    public bool RoadmapLargeOnly { get; init; }

    /// <summary>共享语义区域；设置后优先于固定 Roi，按当前截图尺寸解析。</summary>
    public RecognitionRegion? Region { get; init; }

    /// <summary>默认允许全局继续恢复；消费性确认阶段关闭，避免全局策略先吃掉专属 TAP TO NEXT。</summary>
    public bool AllowGlobalContinuation { get; init; } = true;

    /// <summary>可选搜索区域。</summary>
    public Roi? Roi { get; init; }
}
