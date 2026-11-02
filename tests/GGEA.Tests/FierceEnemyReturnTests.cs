using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public sealed partial class AutoProgressFlowTests
{
    /// <summary>强敌结算返回只需标题；没有主线节点或准备按钮时，正常轮询与恢复均应交回难度识别。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FierceEnemyReturn_TitleOnly_HandsBackToDifficultyReader(bool recovery)
    {
        using var scene = new Scene();
        var title = new SelectStageTitleRecognizer(ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png"));
        var frame = ImageCodec.DecodePng(scene.Frame(("title", 220, 20)));
        // Scene 默认补入主线入口；清除它，以覆盖强敌没有主线节点和入口的真实差异。
        for (var y = 754; y < frame.Height; y++)
            Array.Clear(frame.Bgra, (y * frame.Width + 1470) * 4, (frame.Width - 1470) * 4);
        var returned = ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);
        var clock = new ManualProgressTime();
        var reads = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            if (scene.Adb.Taps.Count == 0)
                return scene.Frame(("again", 1200, 920), ("select", 1600, 920));
            if (++reads == 1)
            {
                if (recovery) clock.Advance(120);
                else return scene.Frame(); // 加载帧不能交接，下一帧标题出现后立即继续。
            }
            return returned;
        };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(
            recoveryTime: clock, detectHome: false, confirmSelectedNode: title.Recognize));
        Assert.Single(scene.Adb.Taps);
        Assert.Equal(2, reads);
        Assert.Equal(recovery ? 1 : 0, scene.Log.Entries.Count(e => e.Message.Contains("连续 2 分钟")));
    }
}
