namespace GGEA.App.Tasks;

/// <summary>关卡导航失败原因，调用方可据此决定重试或恢复。</summary>
public enum StageNavigationFailure
{
    None,
    UnsupportedMode,
    WorkNotConfigured,
    InitialLocationFailed,
    /// <summary>作品详情页的“选择”按钮为暗态，表示该作品尚未解锁。</summary>
    WorkStageLocked,
    TitleNotRecognized,
    SkipNotConfirmed,
    InvalidContext,
    /// <summary>从主界面进入关卡菜单后未确认标题，必须停止整段导航。</summary>
    StageMenuNotRecognized,
}

/// <summary>关卡导航结果，避免用一个无条件 true 掩盖失败阶段。</summary>
public sealed record StageNavigationResult(
    bool Success,
    StageNavigationFailure Failure,
    string Message)
{
    public static StageNavigationResult Ok(string message) =>
        new(true, StageNavigationFailure.None, message);

    public static StageNavigationResult Fail(StageNavigationFailure failure, string message) =>
        new(false, failure, message);
}
