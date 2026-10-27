using GGEA.App.Automation;
using GGEA.App.UI;

namespace GGEA.App.Tasks;

/// <summary>单次开发的布局、目标素材及有界等待参数；调用方可替换目标机体，不依赖每日任务或窗口。</summary>
public sealed record DevelopRequest
{
    /// <summary>主界面布局。</summary>
    public required UiLayout MainLayout { get; init; }

    /// <summary>开发页布局（第一个路线图、全部开发）。</summary>
    public required UiLayout DevelopLayout { get; init; }

    /// <summary>第一个路线图模板。</summary>
    public required string RoadmapFirstTemplatePath { get; init; }

    /// <summary>目标机体模板。</summary>
    public required string MsTemplatePath { get; init; }

    /// <summary>目标机体大图模板（与小图共同覆盖不同缩放比例）；为空表示不启用。</summary>
    public required string MsLargeTemplatePath { get; init; }

    /// <summary>“全部开发”按钮模板。</summary>
    public required string DevelopAllTemplatePath { get; init; }

    /// <summary>“全部开发”按钮灰色禁用状态模板；为空表示不检测禁用状态。</summary>
    public required string DevelopAllDisabledTemplatePath { get; init; }

    /// <summary>确认弹窗“执行开发”按钮模板。</summary>
    public required string ExecuteDevelopTemplatePath { get; init; }

    /// <summary>第二层确认弹窗“执行”按钮模板。</summary>
    public required string ExecuteConfirmTemplatePath { get; init; }

    /// <summary>机体立绘底部 TAP TO NEXT 模板。</summary>
    public required string ShowcaseTapToNextTemplatePath { get; init; }

    /// <summary>开发路线图左上角返回按钮模板。</summary>
    public required string BackButtonTemplatePath { get; init; }

    /// <summary>开发页左上角页面标题模板；为空表示不识别标题。</summary>
    public required string DevelopTitleTemplatePath { get; init; }

    /// <summary>每个方向的最大拖动次数。</summary>
    public int MaxDragsPerDirection { get; init; } = 10;

    /// <summary>单次拖动时长（毫秒）。</summary>
    public int DragDurationMs { get; init; } = 700;

    /// <summary>目标机体匹配阈值。</summary>
    public double Threshold { get; init; } = 0.86;

    /// <summary>页面切换等待时长（毫秒）。</summary>
    public int StepDelayMs { get; init; } = 2500;

    /// <summary>每个开发确认阶段的超时（毫秒）。</summary>
    public int DevelopWaitTimeoutMs { get; init; } = 30000;

    /// <summary>每次点击后等待网络加载完成的稳定时长。</summary>
    public int NetworkSettleDelayMs { get; init; } = 3000;

    /// <summary>开发页标题匹配置信度阈值。</summary>
    public double TitleThreshold { get; init; } = 0.85;

    /// <summary>进入开发页后等待标题出现的总超时。</summary>
    public int TitleWaitMs { get; init; } = GameTiming.ShortRecognitionTimeoutMs;

    /// <summary>首次进入路线图时“开发路线图显示设置”标题模板；为空表示不处理。</summary>
    public string RoadmapDisplaySettingsTitleTemplatePath { get; init; } = "";
}
