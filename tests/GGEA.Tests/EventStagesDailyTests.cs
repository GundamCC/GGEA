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
    [InlineData(false)]
    [InlineData(true)]
    public async Task DailyReplayHonorsLimitsAndClaimsBothTabs(bool alreadyDone)
    {
        var steps = new List<(string Page, int X, int Y)> { ("story_return",1240,407) };
        int[] xs = [220,409,601];
        for (int n = 1; n <= 3; n++)
        {
            string selected = $"hard{n}";
            steps.Add((n == 1 ? "hard3done" : $"hard{n-1}done",xs[n-1], n==2 ? 458 : 394));
            if (alreadyDone) continue;
            steps.Add((selected,1674,710));
            steps.Add(("skip1",1148,994));
            steps.Add(("result1",1676,988));
        }
        if (!alreadyDone)
        {
            steps.Add(("hard3done",31,458));
            steps.Add(("normal_done",1674,710));
            steps.Add(("normal_skip",1148,994));
            steps.Add(("result1",1676,988));
        }
        steps.Add((alreadyDone ? "hard3done" : "normal_done",122,48));
        steps.Add(("story_return",1246,880));
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
        using var ocr = new OnnxTextRecognizer();
        await new EventStagesService(automation,"test").DailyAsync(settlement,
            new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json")),ocr,default);
        Assert.Equal(steps.Count,index);
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
