using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>更换完整按钮素材时，同时约束语言及通用回退图片与生产搜索区域，防止尺寸迁移遗漏。</summary>
public class TemplateRegionCompatibilityTests
{
    /// <summary>覆盖本次素材迁移涉及的按钮；区域从实际布局或生产区域定义读取。</summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ja")]
    public void ResizedTemplates_FitProductionRegions_IncludingCommonFallback(string language)
    {
        var resources = new ResourceResolver(AppContext.BaseDirectory, language);
        Roi Layout(string name, string anchor) => new UiLayoutService(new TestLog())
            .Load(resources.Resolve($"resources/ui_layout/{name}.json")).Find(anchor)!.Roi;
        var bottom = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, 1920, 1080);
        var cases = new (string Path, Roi Region)[] {
            ("base/button_recycle_all.png", Layout("personal_base", "claimAll")),
            ("base/button_recycle_all_disabled.png", Layout("personal_base", "claimAll")),
            ("gift/button_collect_all.png", bottom),
            ("gift/button_collect_all_disabled.png", bottom),
            ("mission/button_claim_all.png", Layout("mission", "claimAll")),
            ("mission/button_claim_all_disabled.png", Layout("mission", "claimAll")),
            ("shop/button_premium_free_buy.png", Layout("shop", "freeItemBuy")),
            ("shop/button_confirm_buy.png", Layout("shop", "confirmBuy")),
            ("safety/diamond_icon.png", Layout("shop", "diamondRiskRegion")),
            ("common/close.png", bottom),
            ("dialog/button_ok.png", bottom),
            ("home/home_screen_button.png", MainScreenDetector.DefaultOptions.ButtonRoi),
            ("home/home_screen_button_activated.png", MainScreenDetector.DefaultOptions.ButtonRoi),
            ("develop/title_develop.png", Layout("develop", "developTitle")),
            ("develop/title_roadmap_display_settings.png", Layout("develop", "roadmapDisplaySettingsTitle")),
            ("stage/title_stage.png", RecognitionRegions.Resolve(RecognitionRegion.PageTitle, 1920, 1080)),
            ("stage/upgrade_stage.png", new Roi(0, 0, 737, 104)),
            ("stage/main_stage.png", RecognitionRegions.Resolve(RecognitionRegion.PageTitle, 1920, 1080)),
            ("stage/sortie/title_select_stage.png", new Roi(0, 0, 1000, 120)),
            ("stage/select.png", new Roi(1484, 816, 436, 144)),
            ("stage/skip/count_limit.png", Layout("stage", "skipCountLimit")),
            (DevelopExecutionButton.DisabledTemplate.Replace("resources/templates/", ""), bottom),
            (ScoutService.ClosedButtonTemplate.Replace("resources/templates/", ""), ScoutService.ButtonRegion),
            (FierceEnemySkipService.DarkSkipTemplate.Replace("resources/templates/", ""),
                FierceEnemyDifficultyRecognizer.GetSkipRegion(new Roi(1100, 200, 180, 40))!.Value),
        };
        foreach (var (path, region) in cases)
        foreach (var candidate in resources.TemplateCandidates("resources/templates/" + path))
        {
            var template = ImageCodec.DecodePng(File.ReadAllBytes(candidate));
            Assert.True(template.Width <= region.Width && template.Height <= region.Height,
                $"{candidate}: 素材 {template.Width}×{template.Height} 超过生产区域 {region.Width}×{region.Height}");
        }
    }
}
