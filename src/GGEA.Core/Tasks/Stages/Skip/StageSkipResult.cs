namespace GGEA.App.Tasks;

/// <summary>一次略过操作的执行结果。</summary>
public sealed record StageSkipResult(
    bool Success,
    string Message,
    bool IsUnavailable = false,
    bool IsApInsufficient = false)
{
    public int ExecutedCount { get; init; }
    public int? RemainingAp { get; init; }

    public static StageSkipResult Ok(string message) => new(true, message);
    public static StageSkipResult Fail(string message, bool isUnavailable = false)
        => new(false, message, isUnavailable);
    public static StageSkipResult ApInsufficient(string message)
        => new(false, message, IsUnavailable: true, IsApInsufficient: true);
}
