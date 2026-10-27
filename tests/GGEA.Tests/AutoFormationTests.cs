using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>用点击驱动的假页面验证部队权限、标题授权和出击重入，不操作真实游戏。</summary>
public sealed class AutoFormationTests
{
    /// <summary>首关两队满编也要初始化，且忽略日常修复开关；准备页和选关页恢复共用确认记录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_HistoryInitialFormation_FormsBothOnlyOnce(bool alreadyOpened)
    {
        using var scene = new Scene(0);
        var calls = 0;
        var settings = new AutoProgressSettings { AdjustSeriesTeam = false, AdjustFreeTeam = false };
        var initial = new AutoFormationSession();
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, _, _, _) => Task.FromResult(++calls == 1
                ? SortieFlowResult.ProgressResumePreparation : SortieFlowResult.ProgressStageCompleted));
        for (var navigation = 0; navigation < 2; navigation++)
        {
            var result = await battle.RunAsync("test", settings, WorkStageKind.Normal, _ => { }, default,
                preparationAlreadyOpened: alreadyOpened, initialFormation: initial);
            Assert.True(result.Completed);
        }
        Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.AutoButton));
        Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.ConfirmButton));
        Assert.Equal(new[] { (145, 445), Scene.AutoButton, Scene.ConfirmButton,
            (145, 680), Scene.AutoButton, Scene.ConfirmButton }, scene.Adb.Taps.SkipWhile(tap => tap != (145, 445)).Take(6));
        Assert.False(settings.CanAutoForm);
    }

    /// <summary>首关初始化仍受同帧标题授权与最低单位约束，失败不能进入出击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_HistoryInitialFormation_StopsOnUnsafePreparation(bool missingTitle)
    {
        using var scene = new Scene(1, showTitle: !missingTitle, bothTeamsEmpty: true, persistentIssue: true);
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, _, _, _) => throw new InvalidOperationException("不得进入战斗"));
        var run = () => battle.RunAsync("test", new(), WorkStageKind.Normal, _ => { }, default,
            preparationAlreadyOpened: true, initialFormation: new());
        if (missingTitle) await Assert.ThrowsAsync<InvalidOperationException>(run);
        else Assert.False((await run()).Completed);
        Assert.DoesNotContain(Scene.SortieButton, scene.Adb.Taps);
        Assert.Equal(missingTitle ? 0 : 2, scene.Adb.Taps.Count(tap => tap == Scene.ConfirmButton));
    }

    /// <summary>强敌挑战已打开准备页时直接复用编制与结算，不再寻找或重复点击选关页入口。</summary>
    [Fact]
    public async Task Battle_AlreadyOpenedPreparation_ReusesFormationAndSortie()
    {
        using var scene = new Scene(1);
        var calls = 0;
        // 故意不给有效入口素材，证明该路径仍在准备页复核成功后才出击。
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, "not-an-entry.png",
            (_, allowEmpty, _, _) =>
            {
                Assert.True(allowEmpty);
                calls++;
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            });
        var result = await battle.RunAsync("test", new() { EmptySlotAction = FormationIssueAction.ContinueSortie },
            WorkStageKind.Unknown, _ => { }, default, preparationAlreadyOpened: true);
        Assert.True(result.Completed);
        Assert.Equal(1, calls);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
        Assert.DoesNotContain(Scene.AutoButton, scene.Adb.Taps);
    }

    /// <summary>准备页恢复必须重查部队，保留原继续出击策略，并限制连续出击未生效的重试次数。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_RecoveryPreparation_RechecksWithoutResettingPolicy(bool persistent)
    {
        using var scene = new Scene(1);
        var calls = 0;
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, allowEmpty, _, _) =>
            {
                Assert.True(allowEmpty);
                calls++;
                return Task.FromResult(persistent || calls == 1 ? SortieFlowResult.ProgressResumePreparation : SortieFlowResult.ProgressStageCompleted);
            });
        var result = await battle.RunAsync("test", new() { EmptySlotAction = FormationIssueAction.ContinueSortie },
            WorkStageKind.Normal, _ => { }, default);
        Assert.Equal(!persistent, result.Completed);
        Assert.Equal(persistent ? 4 : 2, calls);
        Assert.Equal(calls, scene.Adb.Taps.Count(tap => tap == Scene.SortieButton));
        Assert.DoesNotContain(Scene.AutoButton, scene.Adb.Taps);
    }

    /// <summary>恢复需要准备页两个独立控件，单独的出击按钮不能授权重入编制。</summary>
    [Fact]
    public async Task PreparationRecovery_RequiresBothButtons()
    {
        using var scene = new Scene(1);
        var screen = ImageCodec.DecodePng(await scene.Adb.ScreenshotAsync("test"));
        Assert.True(await scene.Service.IsPreparationPageAsync(screen, default));
        for (var y = 900; y < 940; y++)
            Array.Clear(screen.Bgra, (y * 1920 + 400) * 4, 40 * 4);
        Assert.False(await scene.Service.IsPreparationPageAsync(screen, default));
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>每个用例持有独立素材目录，结束后仅清理自己创建的路径。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-formation-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>缺省配置启用自动编制；显式保存的选择和撤销部队权限仍受持久化约束。</summary>
    [Fact]
    public void Settings_DefaultsAndPermissionResetSurvivePersistence()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var service = new ConfigService(new TestLog());
        File.WriteAllText(path, "{}");
        var config = service.Load(path);
        Assert.True(config.AutoProgress.AdjustSeriesTeam);
        Assert.True(config.AutoProgress.AdjustFreeTeam);
        Assert.Equal(FormationIssueAction.AutoForm, config.AutoProgress.EmptySlotAction);
        Assert.Equal(FormationIssueAction.AutoForm, config.AutoProgress.TerrainAction);
        config.AutoProgress.EmptySlotAction = FormationIssueAction.ContinueSortie;
        config.AutoProgress.TerrainAction = FormationIssueAction.ContinueSortie;
        service.Save(config, path);
        var saved = service.Load(path).AutoProgress;
        Assert.True(saved.CanAutoForm);
        Assert.Equal(FormationIssueAction.ContinueSortie, saved.EmptySlotAction);
        Assert.Equal(FormationIssueAction.ContinueSortie, saved.TerrainAction);
        config.AutoProgress = new() { AdjustSeriesTeam = false, AdjustFreeTeam = false,
            EmptySlotAction = FormationIssueAction.AutoForm, TerrainAction = FormationIssueAction.AutoForm };
        service.Save(config, path);
        var loaded = service.Load(path).AutoProgress;
        Assert.False(loaded.CanAutoForm);
        Assert.Equal(FormationIssueAction.ContinueSortie, loaded.EmptySlotAction);
        Assert.Equal(FormationIssueAction.ContinueSortie, loaded.TerrainAction);
    }

    /// <summary>单位空槽、驾驶员空槽和地形标记都只能点击底部自动编制；标题及确认后才算修复。</summary>
    [Theory]
    [InlineData(1, FormationIssue.EmptySlot)]
    [InlineData(2, FormationIssue.EmptySlot)]
    [InlineData(3, FormationIssue.Terrain)]
    public async Task Adjust_SeriesFirst_ClicksBottomButtonThenConfirm(int marker, FormationIssue issue)
    {
        using var scene = new Scene(marker);
        Assert.Equal(AutoFormationResult.Adjusted, await scene.Service.AdjustAsync("test", issue, new(), new(), _ => { }, default));
        Assert.Equal(new[] { (145, 445), Scene.AutoButton, Scene.ConfirmButton }, scene.Adb.Taps);
    }

    /// <summary>只有自由部队被允许或系列部队无目标时，才切换自由部队并等待整整一秒。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adjust_FreeTarget_RespectsPermissionAndSwitchDelay(bool allowSeries)
    {
        using var scene = new Scene(1, freeOnly: true);
        var settings = new AutoProgressSettings { AdjustSeriesTeam = allowSeries };
        Assert.Equal(AutoFormationResult.Adjusted, await scene.Service.AdjustAsync("test", FormationIssue.EmptySlot, settings, new(), _ => { }, default));
        Assert.Equal(allowSeries
            ? new[] { (145, 445), (145, 680), Scene.AutoButton, Scene.ConfirmButton }
            : new[] { (145, 680), Scene.AutoButton, Scene.ConfirmButton }, scene.Adb.Taps);
        Assert.Equal(1000, scene.Delays[0]);
    }

    /// <summary>未允许自由部队时不能通过切换来寻找目标；全部撤权时连截图都不需要。</summary>
    [Fact]
    public async Task Adjust_UnauthorizedTeams_NeverClicked()
    {
        using var scene = new Scene(1, freeOnly: true);
        var settings = new AutoProgressSettings { AdjustFreeTeam = false };
        Assert.Equal(AutoFormationResult.NoTarget, await scene.Service.AdjustAsync("test", FormationIssue.EmptySlot, settings, new(), _ => { }, default));
        Assert.Equal(new[] { (145, 445) }, scene.Adb.Taps);
        settings.AdjustSeriesTeam = false;
        var captures = scene.Adb.ScreenshotCount;
        Assert.Equal(AutoFormationResult.NoTarget, await scene.Service.AdjustAsync("test", FormationIssue.EmptySlot, settings, new(), _ => { }, default));
        Assert.Equal(captures, scene.Adb.ScreenshotCount);
    }

    /// <summary>有确定按钮而无正确标题时必须停止，不点击其他页面的确定。</summary>
    [Fact]
    public async Task Adjust_MissingTitle_DoesNotConfirm()
    {
        using var scene = new Scene(3, showTitle: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scene.Service.AdjustAsync(
            "test", FormationIssue.Terrain, new(), new(), _ => { }, default));
        Assert.Equal(new[] { (145, 445), Scene.AutoButton }, scene.Adb.Taps);
    }

    /// <summary>亮度变化在 85% 到 90% 间的确定按钮可确认；更低分按钮和未达 90% 的标题仍停止。</summary>
    [Theory]
    [InlineData(32, 0, true)]
    [InlineData(48, 0, false)]
    [InlineData(32, 32, false)]
    public async Task Adjust_ConfirmBrightness_UsesIndependentThreshold(int confirmDelta, int titleDelta, bool expected)
    {
        using var scene = new Scene(1, confirmDelta: confirmDelta, titleDelta: titleDelta);
        if (expected)
        {
            Assert.Equal(AutoFormationResult.Adjusted, await scene.Service.AdjustAsync("test", FormationIssue.EmptySlot, new(), new(), _ => { }, default));
            Assert.Equal(new[] { (145, 445), Scene.AutoButton, Scene.ConfirmButton }, scene.Adb.Taps);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scene.Service.AdjustAsync(
                "test", FormationIssue.EmptySlot, new(), new(), _ => { }, default));
            Assert.Equal(new[] { (145, 445), Scene.AutoButton }, scene.Adb.Taps);
        }
    }

    /// <summary>地形提示仍由出击后取消触发编制；不修改继续出击策略，重复提示不能无限重编。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_CancelRepairRetry_IsBounded(bool repeatedWarning)
    {
        using var scene = new Scene(3);
        var calls = 0;
        var settings = new AutoProgressSettings { AdjustFreeTeam = false,
            EmptySlotAction = FormationIssueAction.ContinueSortie,
            TerrainAction = FormationIssueAction.AutoForm };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (kind, empty, terrain, ct) =>
            {
                Assert.True(empty);
                Assert.False(terrain);
                calls++;
                return Task.FromResult(calls == 1 || repeatedWarning
                    ? SortieFlowResult.TerrainCancelled
                    : SortieFlowResult.ProgressStageCompleted);
            });
        battle.Validate(settings);
        var result = await battle.RunAsync("test", settings, WorkStageKind.Normal, _ => { }, default);
        Assert.Equal(!repeatedWarning, result.Completed);
        Assert.Equal(2, calls);
        Assert.Equal(1, scene.Adb.Taps.Count(tap => tap == Scene.AutoButton));
        Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.SortieButton));
    }

    /// <summary>单位/驾驶员仍有部分空位时，在普通和 HARD 出击流程均回退为依旧出击，不重编或改写设置。</summary>
    [Theory]
    [InlineData(1, false, WorkStageKind.Normal)]
    [InlineData(2, false, WorkStageKind.Normal)]
    [InlineData(2, true, WorkStageKind.Normal)]
    [InlineData(1, false, WorkStageKind.Hard)]
    public async Task Battle_AutoFormationStillHasSlots_ContinuesSortie(int marker, bool freeOnly, WorkStageKind kind)
    {
        using var scene = new Scene(marker, freeOnly: freeOnly, persistentIssue: true);
        var calls = 0;
        var reports = new List<string>();
        var settings = new AutoProgressSettings { AdjustSeriesTeam = !freeOnly, AdjustFreeTeam = freeOnly };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (actualKind, empty, terrain, _) =>
            {
                calls++;
                Assert.Equal(kind, actualKind);
                Assert.True(empty);
                Assert.False(terrain);
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            });
        var result = await battle.RunAsync("test", settings, kind, reports.Add, default);
        Assert.True(result.Completed);
        Assert.Equal(1, calls);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.AutoButton);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.ConfirmButton);
        Assert.Equal(freeOnly ? 1 : 0, scene.Adb.Taps.Count(tap => tap == (145, 680)));
        Assert.Contains(reports, message => message.Contains("仍存在单位或驾驶员空位，将依旧出击"));
        Assert.DoesNotContain(scene.Log.Entries, entry => entry.Level is "WARN" or "ERROR");
        Assert.Equal(FormationIssueAction.AutoForm, settings.EmptySlotAction);
    }

    /// <summary>已允许空位出击却返回取消时不能伪报完成，保留有限失败出口且不反复编制。</summary>
    [Fact]
    public async Task Battle_UnexpectedCancellationAfterAllowedSlots_StopsWithoutReforming()
    {
        using var scene = new Scene(1);
        var calls = 0;
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, empty, _, _) => { Assert.True(empty); calls++; return Task.FromResult(SortieFlowResult.EmptySlotCancelled); });
        var result = await battle.RunAsync("test", new() { AdjustFreeTeam = false }, WorkStageKind.Normal, _ => { }, default);
        Assert.False(result.Completed);
        Assert.Contains("仍返回空位取消", result.Message);
        Assert.Equal(1, calls);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.AutoButton);
        Assert.DoesNotContain((145, 680), scene.Adb.Taps);
    }

    /// <summary>地形编制后的剩余空位不再报编制失败；依旧出击及自动编制设置均保留。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adjust_TerrainThenEmptySlots_RespectsContinuePolicy(bool allowEmptySlots)
    {
        using var scene = new Scene(3, persistentIssue: true, afterConfirmMarker: 2);
        var settings = new AutoProgressSettings { EmptySlotAction = allowEmptySlots
            ? FormationIssueAction.ContinueSortie : FormationIssueAction.AutoForm };
        scene.Service.Validate(settings);
        Assert.Equal(AutoFormationResult.Adjusted,
            await scene.Service.AdjustAsync(
            "test", FormationIssue.Terrain, settings, new(), _ => { }, default));
    }

    /// <summary>默认策略在出击前编制空槽；显式依旧出击不自动调整，两者仍正确传入战斗流程。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_PassesFormationPolicy_WithoutUnrequestedAdjustment(bool continueSortie)
    {
        using var scene = new Scene(1);
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (kind, empty, terrain, ct) =>
            {
                Assert.True(empty);
                Assert.Equal(continueSortie, terrain);
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            });
        var settings = continueSortie
            ? new AutoProgressSettings { EmptySlotAction = FormationIssueAction.ContinueSortie, TerrainAction = FormationIssueAction.ContinueSortie }
            : new AutoProgressSettings();
        Assert.True((await battle.RunAsync("test", settings, WorkStageKind.Normal, _ => { }, default)).Completed);
        Assert.Equal(continueSortie ? 0 : 1, scene.Adb.Taps.Count(tap => tap == Scene.AutoButton));
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
    }

    /// <summary>两队完全为空且游戏不返回空位弹窗时，也必须先完成两次编制和最终复查才能第一次出击。</summary>
    [Theory]
    [InlineData(WorkStageKind.Normal)]
    [InlineData(WorkStageKind.Hard)]
    public async Task Battle_BothTeamsEmpty_FormsBothBeforeFirstSortie(WorkStageKind kind)
    {
        using var scene = new Scene(1, bothTeamsEmpty: true);
        var calls = 0;
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (actualKind, _, _, _) =>
            {
                calls++;
                Assert.Equal(kind, actualKind);
                Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.AutoButton));
                Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.ConfirmButton));
                Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
                Assert.True(scene.Adb.Taps.IndexOf(Scene.SortieButton) > scene.Adb.Taps.LastIndexOf(Scene.ConfirmButton));
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            });
        Assert.True((await battle.RunAsync("test", new(), kind, _ => { }, default)).Completed);
        Assert.Equal(1, calls);
    }

    /// <summary>两队各尝试编制后仍完全无单位时停止，不能用允许部分空槽的回退跳过最低条件。</summary>
    [Fact]
    public async Task Battle_BothTeamsEmptyAndInsufficient_NeverSorties()
    {
        using var scene = new Scene(1, bothTeamsEmpty: true, persistentIssue: true);
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, _, _, _) => throw new InvalidOperationException("阵容不足时不得进入出击后流程"));
        var result = await battle.RunAsync("test", new(), WorkStageKind.Normal, _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(AutoFormationService.NoUnitsMessage, result.Message);
        Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.AutoButton));
        Assert.DoesNotContain(Scene.SortieButton, scene.Adb.Taps);
    }

    /// <summary>自由部队编制后系列部队再有空位，只要另一队有单位就依旧出击，不重复编制系列部队。</summary>
    [Fact]
    public async Task Battle_SecondTeamFormationReopensFirst_ContinuesWithoutReforming()
    {
        using var scene = new Scene(1, bothTeamsEmpty: true, seriesLostAfterFree: true);
        var reports = new List<string>();
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, empty, _, _) => { Assert.True(empty); return Task.FromResult(SortieFlowResult.ProgressStageCompleted); });
        var result = await battle.RunAsync("test", new(), WorkStageKind.Normal, reports.Add, default);
        Assert.True(result.Completed);
        Assert.Contains(reports, message => message.StartsWith("系列部队自动编制后仍存在"));
        Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.AutoButton));
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
    }

    /// <summary>复现用户日志：系列编制成功、自由队无目标，返回系列复查才发现空位时也应继续出击。</summary>
    [Fact]
    public async Task Battle_SeriesSlotsReappearAfterFreeInspection_ContinuesSortie()
    {
        using var scene = new Scene(1, seriesLostAfterFreeInspection: true);
        var reports = new List<string>();
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, empty, terrain, _) =>
            {
                Assert.True(empty);
                Assert.False(terrain);
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            });
        Assert.True((await battle.RunAsync("test", new(), WorkStageKind.Normal, reports.Add, default)).Completed);
        Assert.Contains(reports, message => message.StartsWith("系列部队自动编制后仍存在"));
        Assert.Contains(scene.Log.Entries, entry => entry.Message.Contains("自由部队未命中"));
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.AutoButton);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
    }

    /// <summary>地形编制后产生空位，下一次出击应允许该空位，不能再为了同队空位重复编制。</summary>
    [Fact]
    public async Task Battle_TerrainFormationLeavesSlots_ContinuesWithoutReforming()
    {
        using var scene = new Scene(3, persistentIssue: true, afterConfirmMarker: 2);
        var calls = 0;
        var settings = new AutoProgressSettings { AdjustFreeTeam = false };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, empty, terrain, _) =>
            {
                calls++;
                Assert.Equal(calls > 1, empty);
                Assert.False(terrain);
                return Task.FromResult(calls == 1 ? SortieFlowResult.TerrainCancelled : SortieFlowResult.ProgressStageCompleted);
            });
        Assert.True((await battle.RunAsync("test", settings, WorkStageKind.Normal, _ => { }, default)).Completed);
        Assert.Equal(2, calls);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.AutoButton);
        Assert.Equal(2, scene.Adb.Taps.Count(tap => tap == Scene.SortieButton));
        Assert.Equal(FormationIssueAction.AutoForm, settings.EmptySlotAction);
    }

    /// <summary>只允许调整一支部队时，不得通过出击前检查扩大编制权限。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preflight_OnlyAuthorizedTeamMayBeAdjusted(bool allowSeries)
    {
        using var scene = new Scene(1, bothTeamsEmpty: true);
        var settings = new AutoProgressSettings { AdjustSeriesTeam = allowSeries, AdjustFreeTeam = !allowSeries };
        Assert.Equal(FormationPreflightResult.Ready, await scene.Service.PrepareForSortieAsync(
            "test", settings, new(), _ => { }, default));
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.AutoButton);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.ConfirmButton);
        if (allowSeries) Assert.DoesNotContain((145, 680), scene.Adb.Taps);
        Assert.DoesNotContain(Scene.SortieButton, scene.Adb.Taps);
    }

    /// <summary>依旧出击和全部撤权都不能绕过两队无单位的硬条件；只读切换并恢复原部队，不编制。</summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 8)]
    public async Task Battle_ContinueWithNoUnits_StopsWithoutFormation(bool withdrawPermissions, int marker)
    {
        using var scene = new Scene(marker, bothTeamsEmpty: true);
        var settings = new AutoProgressSettings { EmptySlotAction = FormationIssueAction.ContinueSortie,
            AdjustSeriesTeam = !withdrawPermissions, AdjustFreeTeam = !withdrawPermissions };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, _, _, _) => throw new InvalidOperationException("两队无单位时不得出击"));
        var result = await battle.RunAsync("test", settings, WorkStageKind.Normal, _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(AutoFormationService.NoUnitsMessage, result.Message);
        Assert.Equal(new[] { (1520, 800), (145, 445), (145, 680), (145, 445) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains("至少需要一个单位"));
    }

    /// <summary>系列部队全空但自由部队有单位时，依旧出击仍有效；驾驶员全空不能被误判为单位全空。</summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Battle_ContinueWithUnits_DoesNotAutoForm(bool freeHasUnit, int marker)
    {
        using var scene = new Scene(marker, bothTeamsEmpty: true, freeHasUnit: freeHasUnit);
        var settings = new AutoProgressSettings { EmptySlotAction = FormationIssueAction.ContinueSortie };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, empty, _, _) =>
            {
                Assert.True(empty);
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            });
        Assert.True((await battle.RunAsync("test", settings, WorkStageKind.Normal, _ => { }, default)).Completed);
        Assert.DoesNotContain(Scene.AutoButton, scene.Adb.Taps);
        Assert.Single(scene.Adb.Taps, tap => tap == Scene.SortieButton);
    }

    /// <summary>进入准备页时若仍选中自由部队，必须先明确选中系列部队，不能拿同一支空部队的截图检查两次。</summary>
    [Fact]
    public async Task Battle_PreparationKeepsFreeSelected_ReadsSeriesBeforeRejectingRoster()
    {
        using var scene = new Scene(1, bothTeamsEmpty: true, initialTeam: FormationTeam.Free, seriesHasUnit: true);
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, _, _, _) => Task.FromResult(SortieFlowResult.ProgressStageCompleted));
        var result = await battle.RunAsync("test", new() { EmptySlotAction = FormationIssueAction.ContinueSortie },
            WorkStageKind.Normal, _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(new[] { (1520, 800), (145, 445), Scene.SortieButton }, scene.Adb.Taps);
    }

    /// <summary>实机系列部队的两个空单位及三个禁用单位都算无单位；驾驶员和支援加号不参与计数。</summary>
    [Fact]
    public void MinimumRoster_RealEmptyAndLockedSlots_AreRecognized()
    {
        using var scene = new Scene(0);
        var service = new AutoFormationService(scene.Automation, new TemplateMatcher(), scene.Log,
            AutoFormationTemplates.FromConfig(new()));
        service.Validate(new() { EmptySlotAction = FormationIssueAction.ContinueSortie,
            TerrainAction = FormationIssueAction.ContinueSortie });
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Formation", "series-empty-two-unlocked-mumu5556.png")));
        var frame = TestImageFactory.CreateBgra(1920, 1080, 0);
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, frame,
                ((175 + row) * 1920 + 300) * 4, crop.Width * 4);
        Assert.True(service.HasNoUnits(ImageCodec.DecodePng(ImageCodec.EncodePng(frame, 1920, 1080)), default));
        // 仅一列失去空/禁用证据，便不能把剩余四列空槽解释成整队无单位。
        for (var row = 210; row < 582; row++)
            Array.Clear(frame, (row * 1920 + 315) * 4, 326 * 4);
        Assert.False(service.HasNoUnits(ImageCodec.DecodePng(ImageCodec.EncodePng(frame, 1920, 1080)), default));
        // 禁用斜线必须包含暗边；白色机体的大块亮区不能仅凭白色像素冒充“无法编制”。
        for (var index = 0; index < frame.Length; index += 4)
        {
            frame[index] = frame[index + 1] = frame[index + 2] = 255;
            frame[index + 3] = 255;
        }
        Assert.False(service.HasNoUnits(ImageCodec.DecodePng(ImageCodec.EncodePng(frame, 1920, 1080)), default));
    }

    /// <summary>出击前取消不发送点击；进入编制后标题缺失时不确认，也不出击。</summary>
    [Fact]
    public async Task Preflight_CancellationAndMissingTitle_DoNotSortie()
    {
        using var scene = new Scene(1, showTitle: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Service.PrepareForSortieAsync(
            "test", new(), new(), _ => { }, cancellation.Token));
        Assert.Empty(scene.Adb.Taps);
        Assert.Equal(0, scene.Adb.ScreenshotCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scene.Service.PrepareForSortieAsync(
            "test", new(), new(), _ => { }, default));
        Assert.Equal(new[] { (145, 445), Scene.AutoButton }, scene.Adb.Taps);
    }

    /// <summary>类型来自本轮已识别节点；各类关卡均须确认返回页面，HARD 的退出点击不能代表完成。</summary>
    [Theory]
    [InlineData(WorkStageKind.Hard, SortieFlowResult.AllRoundsCompleted, false)]
    [InlineData(WorkStageKind.Hard, SortieFlowResult.ProgressStageCompleted, true)]
    [InlineData(WorkStageKind.Hard, SortieFlowResult.ProgressWorkNavigationRequired, true)]
    [InlineData(WorkStageKind.Normal, SortieFlowResult.AllRoundsCompleted, false)]
    [InlineData(WorkStageKind.Normal, SortieFlowResult.ProgressStageCompleted, true)]
    [InlineData(WorkStageKind.Boss, SortieFlowResult.ProgressWorkNavigationRequired, true)]
    [InlineData(WorkStageKind.Unknown, SortieFlowResult.ProgressStageCompleted, true)]
    [InlineData(WorkStageKind.Unknown, SortieFlowResult.ProgressWorkNavigationRequired, true)]
    [InlineData(WorkStageKind.Unknown, SortieFlowResult.FailureDetected, false)]
    public async Task Battle_CompletionMatchesStageKind(WorkStageKind kind, SortieFlowResult flowResult, bool expected)
    {
        using var scene = new Scene(0);
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (actualKind, _, _, _) =>
            {
                Assert.Equal(kind, actualKind);
                return Task.FromResult(flowResult);
            });
        Assert.Equal(expected, (await battle.RunAsync("test", new(), kind, _ => { }, default)).Completed);
    }

    /// <summary>真实入口素材必须正确区分；直接出击须处理实机 GUEST 执行确认，准备入口仍执行玩家部队检查。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Battle_Entry_ProductionButtonsChooseCorrectBranch(bool direct)
    {
        using var scene = new Scene(0);
        var preparationPath = ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie_preparations.png");
        var directPath = ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png");
        var template = ImageCodec.DecodePng(File.ReadAllBytes(direct ? directPath : preparationPath));
        var watchPath = ConfigService.ResolvePath("resources/templates/stage/sortie/watch.png");
        var executePath = ConfigService.ResolvePath("resources/templates/dialog/execute.png");
        var watch = ImageCodec.DecodePng(File.ReadAllBytes(watchPath));
        // 常规入口与观看同时可见时，仍优先常规入口，不能进入观看确认页。
        var entryFrame = EntryFrame((template, 1500, 780), (watch, 1500, 940));
        var guestFrame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Sortie/guest-execute-wing-mumu5558.png"));
        var preparationProvider = scene.Adb.ScreenshotProvider!;
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count == 0 ? entryFrame : direct ? guestFrame : preparationProvider();
        var executed = 0;
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, preparationPath,
            (kind, _, _, _) =>
            {
                Assert.Equal(WorkStageKind.Normal, kind);
                // 下载与战斗接管前必须已确认执行，不能仅因点击出击就进入长时间等待。
                if (direct)
                {
                    Assert.Equal(2, scene.Adb.Taps.Count);
                    Assert.InRange(scene.Adb.Taps[1].X, 1135, 1160);
                    Assert.InRange(scene.Adb.Taps[1].Y, 840, 860);
                }
                executed++;
                return Task.FromResult(SortieFlowResult.ProgressStageCompleted);
            }, directPath, watchPath, executePath);
        var result = await battle.RunAsync("test", new(), WorkStageKind.Normal, _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(1, executed);
        Assert.Equal((1500 + template.Width / 2, 780 + template.Height / 2), scene.Adb.Taps[0]);
        if (direct) Assert.Equal(2, scene.Adb.Taps.Count);
        else Assert.Contains((145, 445), scene.Adb.Taps);
    }

    /// <summary>直接出击继续沿用结算与提示策略；遇到取消不重入不存在的玩家编制页，也不误报通关。</summary>
    [Theory]
    [InlineData(WorkStageKind.Normal, SortieFlowResult.ProgressStageCompleted, true, false)]
    [InlineData(WorkStageKind.Normal, SortieFlowResult.AllRoundsCompleted, false, false)]
    [InlineData(WorkStageKind.Boss, SortieFlowResult.ProgressStageCompleted, true, true)]
    [InlineData(WorkStageKind.Boss, SortieFlowResult.ProgressWorkNavigationRequired, true, true)]
    [InlineData(WorkStageKind.Hard, SortieFlowResult.AllRoundsCompleted, false, false)]
    [InlineData(WorkStageKind.Hard, SortieFlowResult.ProgressStageCompleted, true, false)]
    [InlineData(WorkStageKind.Normal, SortieFlowResult.EmptySlotCancelled, false, false)]
    [InlineData(WorkStageKind.Normal, SortieFlowResult.TerrainCancelled, false, false)]
    public async Task Battle_DirectEntry_RequiresSettlementWithoutFormationRecovery(
        WorkStageKind kind, SortieFlowResult flowResult, bool completed, bool continueSortie)
    {
        using var scene = new Scene(1, bothTeamsEmpty: true, persistentIssue: true);
        var directPath = ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png");
        var template = ImageCodec.DecodePng(File.ReadAllBytes(directPath));
        var frame = EntryFrame(template, 1500, 780);
        scene.Adb.ScreenshotProvider = () => frame;
        var settings = new AutoProgressSettings
        {
            EmptySlotAction = continueSortie ? FormationIssueAction.ContinueSortie : FormationIssueAction.AutoForm,
            TerrainAction = continueSortie ? FormationIssueAction.ContinueSortie : FormationIssueAction.AutoForm,
        };
        var executed = 0;
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (actualKind, allowEmpty, allowTerrain, _) =>
            {
                Assert.Equal(kind, actualKind);
                Assert.Equal(continueSortie, allowEmpty);
                Assert.Equal(continueSortie, allowTerrain);
                executed++;
                return Task.FromResult(flowResult);
            }, directPath);
        var result = await battle.RunAsync("test", settings, kind, _ => { }, default);
        Assert.Equal(completed, result.Completed);
        Assert.Equal(completed && flowResult == SortieFlowResult.ProgressWorkNavigationRequired, result.NeedsWorkNavigation);
        Assert.Equal(1, executed);
        Assert.Single(scene.Adb.Taps);
    }

    /// <summary>区域外的直接出击素材不能授权；取消轮询后不点击，也不进入战斗。</summary>
    [Fact]
    public async Task Battle_DirectEntry_OutsideRegionThenCancelled_NeverClicks()
    {
        using var scene = new Scene(0);
        using var cancellation = new CancellationTokenSource();
        var directPath = ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png");
        var template = ImageCodec.DecodePng(File.ReadAllBytes(directPath));
        var frame = EntryFrame(template, 400, 400);
        var frames = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            if (++frames == 2) cancellation.Cancel();
            return frame;
        };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service, scene.Preparation,
            (_, _, _, _) => throw new InvalidOperationException("区域外入口不得进入战斗"), directPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => battle.RunAsync(
            "test", new(), WorkStageKind.Normal, _ => { }, cancellation.Token));
        Assert.Equal(2, frames);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>将真实素材放入隔离的标准规格画面；只用于流程回归，不冒充模拟器运行截图。</summary>
    private static byte[] EntryFrame(ScreenImage template, int x, int y)
        => EntryFrame((template, x, y));

    /// <summary>同一帧可同时包含多个入口，用于验证优先级和相似文字的误识别。</summary>
    private static byte[] EntryFrame(params (ScreenImage Template, int X, int Y)[] items)
    {
        var frame = TestImageFactory.CreateBgra(1920, 1080, 0);
        foreach (var (template, x, y) in items)
            for (var row = 0; row < template.Height; row++)
                Buffer.BlockCopy(template.Bgra, row * template.Width * 4, frame,
                    ((y + row) * 1920 + x) * 4, template.Width * 4);
        return ImageCodec.EncodePng(frame, 1920, 1080);
    }

    /// <summary>复现橙色共框误判，文字复核必须拒绝把观看当成出击，并允许真实观看按钮。</summary>
    [Fact]
    public void WatchEntry_SimilarOrangeBorderCannotAuthorizeSortie()
    {
        var directPath = ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png");
        var watchPath = ConfigService.ResolvePath("resources/templates/stage/sortie/watch.png");
        var direct = ImageCodec.DecodePng(File.ReadAllBytes(directPath));
        var watch = ImageCodec.DecodePng(File.ReadAllBytes(watchPath));
        var screen = ImageCodec.DecodePng(EntryFrame(watch, 1500, 780));
        var area = new Roi(1470, 754, 450, 326);
        Assert.NotNull(new TemplateMatcher().Match(screen.Bgra, screen.Width, screen.Height,
            direct.Bgra, direct.Width, direct.Height, .90, area));
        var buttons = new StageEntryButtonRecognizer();
        Assert.Null(buttons.Match(screen, directPath, area, .90, default));
        Assert.NotNull(buttons.Match(screen, watchPath, area, .90, default));
    }

    /// <summary>观看后必须在底部 320 像素找到执行；区域上半部的真实执行按钮同样有效，下载取消不得计为完成。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_WatchEntry_ExecutesBeforeStartingDownloadFlow(bool downloadCancelled)
    {
        using var scene = new Scene(0);
        var watchPath = ConfigService.ResolvePath("resources/templates/stage/sortie/watch.png");
        var executePath = ConfigService.ResolvePath("resources/templates/dialog/execute.png");
        var watch = ImageCodec.DecodePng(File.ReadAllBytes(watchPath));
        var execute = ImageCodec.DecodePng(File.ReadAllBytes(executePath));
        var watchFrame = EntryFrame(watch, 1500, 780);
        var executeFrame = EntryFrame(execute, 500, 770);
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count == 0 ? watchFrame : executeFrame;
        var expected = new[] { (1500 + watch.Width / 2, 780 + watch.Height / 2),
            (500 + execute.Width / 2, 770 + execute.Height / 2) };
        var executed = 0;
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service,
            ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie_preparations.png"),
            (kind, _, _, _) =>
            {
                Assert.Equal(WorkStageKind.Normal, kind);
                Assert.Equal(expected, scene.Adb.Taps);
                executed++;
                return Task.FromResult(downloadCancelled ? SortieFlowResult.DownloadCancelled : SortieFlowResult.ProgressStageCompleted);
            }, ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png"), watchPath, executePath);
        Assert.Equal(!downloadCancelled, (await battle.RunAsync("test", new(), WorkStageKind.Normal, _ => { }, default)).Completed);
        Assert.Equal(1, executed);
        Assert.Equal(expected, scene.Adb.Taps);
    }

    /// <summary>观看与直接出击都只授权进入确认页；执行按钮在区域外且任务取消时，不能沿用入口坐标或开始下载。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_ExecuteOutsideRegionCancelled_NeverStartsFlow(bool direct)
    {
        using var scene = new Scene(0);
        using var cancellation = new CancellationTokenSource();
        var watchPath = ConfigService.ResolvePath("resources/templates/stage/sortie/watch.png");
        var executePath = ConfigService.ResolvePath("resources/templates/dialog/execute.png");
        var watch = ImageCodec.DecodePng(File.ReadAllBytes(direct
            ? ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png") : watchPath));
        var execute = ImageCodec.DecodePng(File.ReadAllBytes(executePath));
        var watchFrame = EntryFrame(watch, 1500, 780);
        var outsideFrame = EntryFrame(execute, 500, 500);
        var confirmationFrames = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            if (scene.Adb.Taps.Count == 0) return watchFrame;
            if (++confirmationFrames == 2) cancellation.Cancel();
            return outsideFrame;
        };
        var battle = new AutoProgressBattleService(scene.Automation, scene.Service,
            ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie_preparations.png"),
            (_, _, _, _) => throw new InvalidOperationException("未确认执行不得开始下载"),
            ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png"), watchPath, executePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => battle.RunAsync(
            "test", new(), WorkStageKind.Normal, _ => { }, cancellation.Token));
        Assert.Single(scene.Adb.Taps);
        Assert.Equal((1500 + watch.Width / 2, 780 + watch.Height / 2), scene.Adb.Taps[0]);
    }

    /// <summary>实际随包资源必须可读取；节点素材不完整时不得把缺失 COMPLETE 模板解释成未通关。</summary>
    [Fact]
    public void ProductionResources_ValidateAndMissingNodeResourcesFail()
    {
        using var scene = new Scene(1);
        var settings = new AutoProgressSettings { EmptySlotAction = FormationIssueAction.AutoForm, TerrainAction = FormationIssueAction.AutoForm };
        var config = new AppConfig();
        var formation = new AutoFormationService(scene.Automation, new TemplateMatcher(), new TestLog(), AutoFormationTemplates.FromConfig(config));
        var battle = new AutoProgressBattleService(scene.Automation, formation,
            ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie_preparations.png"),
            (_, _, _, _) => throw new InvalidOperationException("预检不得出击"),
            ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png"),
            ConfigService.ResolvePath("resources/templates/stage/sortie/watch.png"),
            ConfigService.ResolvePath("resources/templates/dialog/execute.png"));
        battle.Validate(settings);
        var nodes = new StageNodeRecognizer(scene.Adb, new TemplateMatcher(), new TestLog(),
            ConfigService.ResolvePath("resources/templates/stage/node_states"));
        nodes.ValidateForProgress();
        using var temp = new TempDirectory();
        var missing = new StageNodeRecognizer(scene.Adb, new TemplateMatcher(), new TestLog(), temp.Path);
        Assert.Throws<InvalidOperationException>(missing.ValidateForProgress);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>取消信号在首次截图前生效，不遗留自动编制点击。</summary>
    [Fact]
    public async Task Adjust_Cancelled_NoClicks()
    {
        using var scene = new Scene(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Service.AdjustAsync(
            "test", FormationIssue.EmptySlot, new(), new(), _ => { }, cancellation.Token));
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>隔离素材使用不同随机纹理，避免单色模板互相命中；页面状态只由已发送点击决定。</summary>
    private sealed class Scene : IDisposable
    {
        public static readonly (int X, int Y) AutoButton = (420, 920);
        public static readonly (int X, int Y) ConfirmButton = (920, 920);
        public static readonly (int X, int Y) SortieButton = (1720, 920);
        private readonly TempDirectory _temp = new();
        private readonly byte[][] _pixels = new byte[9][];
        public FakeAdbService Adb { get; }
        public AutomationService Automation { get; }
        public AutoFormationService Service { get; }
        public TestLog Log { get; }
        public string Preparation { get; }
        public List<int> Delays { get; } = new();

        /// <summary>准备页包含出击和编制按钮；弹窗关闭前不展示准备页按钮。</summary>
        public Scene(int marker, bool freeOnly = false, bool showTitle = true, int confirmDelta = 0, int titleDelta = 0,
            bool persistentIssue = false, int? afterConfirmMarker = null, bool bothTeamsEmpty = false,
            bool seriesLostAfterFree = false, bool freeHasUnit = false,
            FormationTeam initialTeam = FormationTeam.Series, bool seriesHasUnit = false,
            bool seriesLostAfterFreeInspection = false)
        {
            var paths = Enumerable.Range(0, 9).Select(i => Path.Combine(_temp.Path, $"{i}.png")).ToArray();
            for (var i = 0; i < paths.Length; i++)
            {
                _pixels[i] = TestImageFactory.CreateBgra(40, 40, 0);
                var random = new Random(701 + i);
                for (var p = 0; p < _pixels[i].Length; p += 4)
                    for (var channel = 0; channel < 3; channel++) _pixels[i][p + channel] = (byte)random.Next(30, 255);
                File.WriteAllBytes(paths[i], ImageCodec.EncodePng(_pixels[i], 40, 40));
            }
            Preparation = paths[7];
            var prep = Frame((0, 1700, 900), (4, 400, 900), (7, 1500, 780));
            var withIssue = bothTeamsEmpty
                ? Frame((0, 1700, 900), (4, 400, 900), (7, 1500, 780),
                    (marker, 330, marker == 2 ? 600 : 400), (marker, 643, marker == 2 ? 600 : 400),
                    (marker, 956, marker == 2 ? 600 : 400), (marker, 1269, marker == 2 ? 600 : 400),
                    (marker, 1582, marker == 2 ? 600 : 400))
                : Frame((0, 1700, 900), (4, 400, 900), (7, 1500, 780), (marker, 500, marker == 2 ? 600 : 300));
            var afterConfirm = persistentIssue
                ? bothTeamsEmpty ? withIssue : Frame((0, 1700, 900), (4, 400, 900), (7, 1500, 780),
                    (afterConfirmMarker ?? marker, 500, (afterConfirmMarker ?? marker) == 2 ? 600 : 300)) : prep;
            // 标题置于居中区域的右半边，防止退回原来的左上区域；亮度变化只影响截图，不改模板。
            var dialogPixels = ImageCodec.DecodePng(showTitle
                ? Frame((5, 1100, 50), (6, 900, 900)) : Frame((6, 900, 900))).Bgra;
            foreach (var (x, y, delta) in new[] { (1100, 50, titleDelta), (900, 900, confirmDelta) })
                for (var row = 0; row < 40; row++)
                    for (var column = 0; column < 40; column++)
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var offset = ((y + row) * 1920 + x + column) * 4 + channel;
                            dialogPixels[offset] = (byte)Math.Min(255, dialogPixels[offset] + delta);
                        }
            var dialog = ImageCodec.EncodePng(dialogPixels, 1920, 1080);
            Adb = new FakeAdbService(prep);
            Adb.ScreenshotProvider = () =>
            {
                if (Adb.Taps.Count(t => t == AutoButton) > Adb.Taps.Count(t => t == ConfirmButton)) return dialog;
                var currentTeam = initialTeam;
                var confirmedTeams = new HashSet<FormationTeam>();
                foreach (var tap in Adb.Taps)
                {
                    if (tap == (145, 680)) currentTeam = FormationTeam.Free;
                    if (tap == (145, 445)) currentTeam = FormationTeam.Series;
                    if (tap == ConfirmButton) confirmedTeams.Add(currentTeam);
                }
                // 两队皆空回归独立记录每支部队的编制，不能用一次确认把另一个部队伪造为已填满。
                if (bothTeamsEmpty)
                {
                    if (freeHasUnit && currentTeam == FormationTeam.Free) return prep;
                    if (seriesHasUnit && currentTeam == FormationTeam.Series) return prep;
                    if (seriesLostAfterFree && currentTeam == FormationTeam.Series && confirmedTeams.Contains(FormationTeam.Free))
                        return withIssue;
                    return confirmedTeams.Contains(currentTeam) ? afterConfirm : withIssue;
                }
                // 默认编制能清除问题；回归场景显式保留空槽或换成另一类标记。
                if (seriesLostAfterFreeInspection && currentTeam == FormationTeam.Series &&
                    Adb.Taps.Contains(ConfirmButton) && Adb.Taps.Contains((145, 680))) return withIssue;
                if (Adb.Taps.Contains(ConfirmButton)) return afterConfirm;
                return !freeOnly || currentTeam == FormationTeam.Free ? withIssue : prep;
            };
            var matcher = new TemplateMatcher();
            Log = new TestLog();
            Automation = new AutomationService(Adb, matcher, Log);
            Service = new AutoFormationService(Automation, matcher, Log,
                new(paths[0], paths[1], paths[2], paths[3], paths[4], paths[5], paths[6], paths[8]),
                (ms, ct) => { ct.ThrowIfCancellationRequested(); Delays.Add(ms); return Task.CompletedTask; }, maxPolls: 2);
        }

        /// <summary>把模板精确放在约定区域，区域外不存在其他匹配目标。</summary>
        private byte[] Frame(params (int Id, int X, int Y)[] items)
        {
            var frame = TestImageFactory.CreateBgra(1920, 1080, 0);
            foreach (var item in items)
                for (var row = 0; row < 40; row++)
                    Buffer.BlockCopy(_pixels[item.Id], row * 160, frame, ((item.Y + row) * 1920 + item.X) * 4, 160);
            return ImageCodec.EncodePng(frame, 1920, 1080);
        }

        /// <summary>删除隔离测试素材。</summary>
        public void Dispose() => _temp.Dispose();
    }
}
