namespace GGEA.App.Automation;

/// <summary>游戏画面等待的统一默认值；检测放慢时同时保留加载预算，取消令牌仍即时中断等待。</summary>
public static class GameTiming
{
    /// <summary>已发现候选后的新帧复核间隔；只控制采样频率，不代表等待到时就可点击。</summary>
    public const int ConfirmationIntervalMs = 500;
    /// <summary>尚无目标时的默认重试间隔；候选复核使用短间隔，不能用此值替代就绪判断。</summary>
    public const int RecognitionIntervalMs = 5000;
    /// <summary>部分流程的重试与动画观察预算；不是固定睡眠时长。</summary>
    public const int AnimationSettleMs = 10000;
    /// <summary>原十秒识别预算延长到十五秒，容纳慢加载与多次画面观察。</summary>
    public const int ShortRecognitionTimeoutMs = 15000;
    /// <summary>原十五秒识别预算延长到二十秒，不无限延长卡顿等待。</summary>
    public const int RecognitionTimeoutMs = 20000;
}
