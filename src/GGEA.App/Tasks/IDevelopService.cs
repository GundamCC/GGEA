using GGEA.App.Localization;
namespace GGEA.App.Tasks;

/// <summary>可复用的单次开发流程：从主界面进入开发、处理确认并返回主界面；取消和异常向调用方传播。</summary>
public interface IDevelopService
{
    /// <summary>从已确认的主界面进入首个开发作品，搜索并点击目标后停止，不执行开发确认或返回主界面。</summary>
    Task<GGEA.App.Vision.TemplateMatchResult?> FindAndTapAsync(string serial, DevelopRequest request, CancellationToken ct = default);

    /// <summary>已选定数量后检测资源不足或执行两层确认并跳过立绘；不重新选目标、不返回主界面。</summary>
    Task<DevelopResult> ConfirmAsync(string serial, DevelopRequest request, CancellationToken ct = default);

    /// <summary>执行指定目标的开发，区分实际完成、无可用开发与识别失败；调用方决定如何编排后续步骤。</summary>
    Task<DevelopResult> ExecuteAsync(string serial, DevelopRequest request, CancellationToken ct = default);
}

/// <summary>单次开发结果；跳过不能被循环调用方计为一次产出。</summary>
public enum DevelopResult
{
    /// <summary>未确认必要页面或目标，流程已停止。</summary>
    Failed,
    /// <summary>当前开发阶段完成；仅 ExecuteAsync 还包含返回主界面步骤。</summary>
    Completed,
    /// <summary>连续确认按钮禁用，无可用开发，已返回主界面。</summary>
    Unavailable,
    /// <summary>执行开发暗态且金币行无红字，持有单位达到上限；不执行消费性点击。</summary>
    UnitLimitReached,
    /// <summary>同帧暗态执行按钮与金币行红字共同确认金币不足。</summary>
    InsufficientCoins,
}

/// <summary>两种任务统一解释资源不足原因，未知失败不生成金币不足提示。</summary>
public static class DevelopResultExtensions
{
    /// <summary>仅明确的资源不足结果返回提示，供退出流程与最终汇总复用。</summary>
    public static string? GetBlockedReason(this DevelopResult result) => result switch
    {
        DevelopResult.InsufficientCoins => L.Text("持有金币不足"),
        DevelopResult.UnitLimitReached => L.Text("持有单位已达上限"),
        _ => null,
    };
}
