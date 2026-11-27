using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

public class EventStagesDailyTests
{
    // 原始 1080p 采集只保留识别区域；每一步均验证点击落点，错误输入不能推进回放。
    [Theory]

    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    public async Task DailyReplayHonorsLimitsAndClaimsBothTabs(bool alreadyDone, bool claimRewards, bool rewardsOnly, bool startsOnStage)
    {
        var steps = new List<(string Page, int X, int Y)>();
        if (startsOnStage) steps.Add(("hard1done",122,48));
        steps.Add(("story_return",1246,880));
        if (claimRewards)
        {
            if (!alreadyDone)
            {
                steps.Add(("mission_ready",1728,917));
                steps.Add(("claim",960,995));
            }
            steps.Add(("claimed",1568,128));
            if (!alreadyDone)
            {
                steps.Add(("cumulative",1728,917));
                steps.Add(("claim",960,995));
            }
            steps.Add(("cumulative_done",122,48));
        }
        else steps.Add(("claimed",122,48));
        var pages = steps.Select(s=>s.Page).Append("story_return").Distinct().ToDictionary(n=>n, n=>
            File.ReadAllBytes(ConfigService.ResolvePath($"Fixtures/Navigation/limited-daily-{n}.png")));
        int index=0;
        var device=new FakeAdbService(pages["story_return"]);
        device.ScreenshotProvider=()=>pages[index < steps.Count ? steps[index].Page : "story_return"];
        device.TapFailure=(x,y)=>
        {
            var expected=steps[index++];
            Assert.InRange(x,expected.X-13,expected.X+13);
            Assert.InRange(y,expected.Y-13,expected.Y+13);
            return null;
        };
        var log=new TestLog();
        using var automation=new AutomationService(device,new TemplateMatcher(),log);
        var title = new SelectStageTitleRecognizer(EventStagesService.PathFor("stage_title"));
        var settlement=new StageSkipService(automation,log,new StageSkipOptions
        {
            SkipResultTemplatePath=ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            PostSkipConfirmation=title.Recognize, SettleAfterOkMs=0
        });
        var navigation = new StageService(automation, new FakeStageWorkRecognizer(), log);
        var service = new EventStagesService(automation,"test");
        if (rewardsOnly) await service.ClaimFromStoryAsync(default);
        else await service.DailyAsync(settlement,
            new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json")),navigation,default,claimRewards);
        Assert.Equal(steps.Count,index);
    }

    [Fact]
    public async Task EventListUsesSharedEntryAndVerifiesSelectedPoster()
    {
        var list = File.ReadAllBytes(ConfigService.ResolvePath("Fixtures/Navigation/limited-event-list.png"));
        var story = File.ReadAllBytes(ConfigService.ResolvePath("Fixtures/Navigation/limited-daily-story_return.png"));
        var device = new FakeAdbService(list);
        device.ScreenshotProvider = () => device.Taps.Count < 3 ? list : story;
        using var automation = new AutomationService(device, new TemplateMatcher(), new TestLog());
        await new EventStagesService(automation, "test").OpenFromMenuAsync(default);
        Assert.Equal(3, device.Taps.Count);
        Assert.Equal((1165,400), device.Taps[0]);
        Assert.InRange(device.Taps[1].X, 286, 300);
        Assert.InRange(device.Taps[2].X, 1660, 1690);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task FullDailyReplayChecksMissionAfterSingleNormalSkip(int finalCount)
    {
        var steps = new List<(string Frame, int X, int Y)>();
        void Mission(string count) { steps.Add(("story_return",1246,880)); steps.Add((count,122,48)); }
        void Enter() => steps.Add(("story_return",1240,407));
        void Execute(string detail, string dialog)
        {
            steps.Add((detail,1674,710)); steps.Add((dialog,1148,994)); steps.Add(("result1",1676,988));
        }
        Mission("progress0");
        for (int i = 1; i <= 3; i++)
        {
            Enter();
            steps.Add(("hard3done",30 + i*190, i == 2 ? 458 : 394));
            Execute(i == 1 ? "nativehard1" : $"hard{i}", "skip1");
            if (i == 3) break;
            steps.Add(($"hard{i}done",122,48));
            Mission($"progress{i*3}");
        }
        steps.Add(("hard3done",220,394));
        steps.Add(("hard1done",30,458));
        Execute("normal_done","normal_skip");
        // 结算后仅能确认关卡页，不要求仍选中普通末关。
        steps.Add(("hard3done",122,48));
        Mission($"progress{finalCount}");

        byte[] Frame(string key)
        {
            bool native = key.StartsWith("progress") || key == "nativehard1";
            var name = native ? $"limited-native720-{(key == "nativehard1" ? "hard1" : key)}" : $"limited-daily-{key}";
            var raw = File.ReadAllBytes(ConfigService.ResolvePath($"Fixtures/Navigation/{name}.png"));
            return GameResolution.ReadPng(raw).Normalize(raw);
        }
        var frames = steps.Select(s => s.Frame).Append("story_return").Distinct().ToDictionary(n => n, Frame);
        int index = 0;
        var device = new FakeAdbService(frames["story_return"]);
        device.ScreenshotProvider = () => frames[index == steps.Count ? "story_return" : steps[index].Frame];
        device.TapFailure = (x,y) =>
        {
            var step = steps[index++];
            Assert.InRange(x,step.X-13,step.X+13);
            Assert.InRange(y,step.Y-13,step.Y+13);
            return null;
        };
        var log = new TestLog();
        var matcher = new TemplateMatcher();
        using var automation = new AutomationService(device, matcher, log);
        using var ocr = new OnnxTextRecognizer();
        using var nodes = new StageNodeRecognizer(device, matcher, log, ConfigService.ResolvePath(new AppConfig().StageNodeTemplateDirectory),
            numberRecognizer: new OnnxStageNumberRecognizer(ocr), layout: StageNodeLayout.History);
        var titles = new OnnxStageTitleRecognizer(device, log, ocr, eventSuffix: true);
        var navigation = new StageService(automation, new FakeStageWorkRecognizer(), log,
            new StageNavigationOptions { PageSettleMs = 0 }).ForEndAlignedTrack(nodes, titles);
        var settlement = new StageSkipService(automation,log,new StageSkipOptions
        {
            SkipResultTemplatePath=ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            PostSkipConfirmation=new SelectStageTitleRecognizer(EventStagesService.PathFor("stage_title")).Recognize,
            EventQuantityLayout=true, RecoverUnchangedExecutionDialog=true, SkipDialogWaitMs=0, SettleAfterOkMs=0
        });
        var message = await new EventStagesService(automation,"test").DailyAsync(settlement,
            new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json")), navigation, default, false);
        if (finalCount < 10) Assert.Contains("9/10", message);
        Assert.Equal(steps.Count,index);
    }

    [Theory]
    [InlineData("hard1")]
    [InlineData("normal7")]
    public async Task Native720TrackConfirmsExpectedSelection(string stage)
    {
        var path = ConfigService.ResolvePath($"Fixtures/Navigation/limited-native720-{stage}.png");
        var raw = File.ReadAllBytes(path);
        var size = GameResolution.ReadPng(raw);
        Assert.Equal(new GameResolution(1280,720), size);
        var device = new FakeAdbService(size.Normalize(raw));
        var matcher = new TemplateMatcher();
        var log = new TestLog();
        using var automation = new AutomationService(device, matcher, log);
        using var ocr = new OnnxTextRecognizer();
        using var nodes = new StageNodeRecognizer(device, matcher, log,
            ConfigService.ResolvePath(new AppConfig().StageNodeTemplateDirectory),
            numberRecognizer: new OnnxStageNumberRecognizer(ocr), layout: StageNodeLayout.History);
        var titles = new OnnxStageTitleRecognizer(device, log, ocr, eventSuffix: true);
        var nav = new StageService(automation, new FakeStageWorkRecognizer(), log,
            new StageNavigationOptions { PageSettleMs = 0 }).ForEndAlignedTrack(nodes, titles);
        var layout = EventStagesService.CreateTrackLayout(new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json")));
        var work = EventStagesService.CreateWork();
        if (stage == "normal7")
        {
            var next = File.ReadAllBytes(ConfigService.ResolvePath("Fixtures/Navigation/limited-native720-normal7-next.png"));
            device.EnqueueScreenshots(size.Normalize(next));
            await nav.ConfirmBeforeHardAsync("test", work, default);
        }
        else
        {
            Assert.True((await nav.SelectHardStageAsync("test", layout, [work], work.Id, 1)).Success);
            Assert.True((await nav.ConfirmCurrentHardStageAsync("test", layout, work, 1)).Success);
            Assert.False((await nav.ConfirmCurrentHardStageAsync("test", layout, work, 2)).Success);
        }
    }

    [Fact]
    public async Task FullyClearedOverviewEndsProgressWithoutEnteringABattle()
    {
        var frame = File.ReadAllBytes(ConfigService.ResolvePath("Fixtures/Navigation/limited-daily-story_return.png"));
        var device = new FakeAdbService(frame);
        using var automation = new AutomationService(device, new TemplateMatcher(), new TestLog());
        Assert.False(await new EventStagesService(automation, "test").EnterAsync(true, default));
        Assert.Empty(device.Taps);
    }

    [Fact]
    public void SelectedPartsOccupyOneQueueEntry()
    {
        var saved = new LimitedEventDailySettings(true,false,true,FierceEnemyDifficulty.EXPERT);
        var item = saved.CreateItem();
        Assert.Same(saved, Assert.Single(DailyTaskPlanner.BuildExecutionPlan([item])).LimitedEventSelection);
        item.IsChecked = false;
        Assert.Empty(DailyTaskPlanner.BuildExecutionPlan([item]));
        Assert.Throws<InvalidOperationException>(() => (saved with { Stages=false, Medals=false }).CreateItem());
    }
}
