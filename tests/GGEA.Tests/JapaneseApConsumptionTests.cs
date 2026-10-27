using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服 Z 高达实机回归：次数上限必须阻止重复略过，长标题页面仍能确认左侧选中节点。</summary>
public sealed class JapaneseApConsumptionTests : IDisposable
{
    private readonly string _templates = Path.Combine(Path.GetTempPath(), "ggea-ja-ap-" + Guid.NewGuid().ToString("N"));

    /// <summary>每个用例独占临时素材目录，不写入生产资源。</summary>
    public JapaneseApConsumptionTests() => Directory.CreateDirectory(_templates);

    /// <summary>使用独立外部模板路径，避免切换全局语言影响并行测试；截图来自采集模板之外的作品。</summary>
    [Theory]
    [InlineData("ja-zeta-hard2-ready.png", true)]
    [InlineData("ja-zeta-hard2-done.png", false)]
    public async Task IsSkipEnabledAsync_ActualJapaneseFrames_DistinguishesDailyLimit(string file, bool expected)
    {
        var path = Path.Combine(_templates, "count_limit.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/stage/skip/count_limit.png"), path);
        var adb = new FakeAdbService(Fixture(file));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new StageSkipService(automation, log, new StageSkipOptions { SkipCountLimitTemplatePath = path });
        var layout = new UiLayoutService(log).Load(Path.Combine(AppContext.BaseDirectory, "resources/common/ui_layout/stage.json"));

        Assert.Equal(expected, await service.IsSkipEnabledAsync("replay", layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>右上角长标题未达到 OCR 阈值时，生产回退须以两帧选中光圈、HARD、编号和已通过状态完整确认。</summary>
    [Fact]
    public async Task SelectedNode_LongJapaneseTitle_ConfirmsPassedHard2()
    {
        var adb = new FakeAdbService(Fixture("ja-zeta-hard2-ready.png"));
        using var nodes = new StageNodeRecognizer(adb, new TemplateMatcher(), new TestLog(),
            Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage/node_states"),
            delay: (_, _) => Task.CompletedTask);
        var work = new HardStageEntry { Id = 2, NormalCount = 15, HardCount = 3 };

        var node = await nodes.RecognizeSelectedAsync("replay", work);

        Assert.NotNull(node);
        Assert.Equal(WorkStageKind.Hard, node.Kind);
        Assert.Equal(2, node.Number);
        Assert.Equal(WorkStageNodeState.Completed, node.State);
        Assert.NotNull(node.Position);
        Assert.Equal(17, node.Position.OverallIndex);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>夹具保留原始 ADB 像素；不以合成结果冒充实机画面。</summary>
    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/ApConsumption", name));

    /// <summary>只清理本用例创建的目录，测试失败也不残留副本。</summary>
    public void Dispose() => Directory.Delete(_templates, recursive: true);
}
