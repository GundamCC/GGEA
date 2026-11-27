using GGEA.App.Config;

namespace GGEA.App.Tasks;

/// <summary>自动推图新增素材；独立于自动刷本配置，避免普通刷本意外进入故事流程。</summary>
public sealed record AutoProgressFlowTemplates(
    string Menu, string Skip, string Story, string SelectStageTitle,
    string AutoClose, string AutoAll, string AutoEnemy, string SeriesCompleteClose, string MainStageTitle,
    string SeriesCompleteTitle)
{
    public string? EventAttemptHint { get; init; }

    /// <summary>统一解析随包资源路径，测试可注入隔离素材。</summary>
    public static AutoProgressFlowTemplates FromDefaults()
    {
        string Resolve(string name) => ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png");
        return new(Resolve("menu"), Resolve("skip"), Resolve("story"), Resolve("title_select_stage"),
            Resolve("auto_close"), Resolve("auto_all"), Resolve("auto_enemy"),
            ConfigService.ResolvePath("resources/templates/common/close.png"),
            ConfigService.ResolvePath("resources/templates/stage/main_stage.png"),
            ConfigService.ResolvePath("resources/templates/stage/series_complete_title.png"));
    }
}
