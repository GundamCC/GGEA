namespace GGEA.App.Tasks;

/// <summary>已挑战者允许积分未知；胜负标记仍决定已挑战，未知值绝不等同零分。</summary>
public sealed record MasterLeagueOpponent(int? Points, bool Victory, bool Defeat)
{
    public bool Unchallenged => Points == 0 && !Victory && !Defeat;
}

/// <summary>同一帧的对手状态与变更次数；总分、最高分和晋级门槛不参与自动化决策。</summary>
public sealed record MasterLeagueOpponentPage(int? RefreshRemaining,
    IReadOnlyList<MasterLeagueOpponent> Opponents)
{
    /// <summary>复核选敌所需状态；已挑战者的分数不影响下一名选择，不因该字段 OCR 波动重置稳定等待。</summary>
    public bool SameAs(MasterLeagueOpponentPage other) => RefreshRemaining == other.RefreshRemaining && Opponents.Count == other.Opponents.Count && Opponents.Zip(other.Opponents).All(pair =>
            pair.First.Victory == pair.Second.Victory && pair.First.Defeat == pair.Second.Defeat &&
            (pair.First.Victory || pair.First.Defeat || pair.First.Points == pair.Second.Points));
}

/// <summary>只根据对手是否已挑战和变更次数选择下一步，晋级完全交由游戏。</summary>
internal static class MasterLeagueProgress
{
    /// <summary>界面从左到右编号 1～5；普通挑战、补选和败北刷新共用 3、2、4、1、5 优先级。</summary>
    public static IReadOnlyList<int> ChallengeOrder { get; } = Array.AsReadOnly(new[] { 2, 1, 3, 0, 4 });

    /// <summary>保留五名对手的状态边界，未知单列交给选敌时跳过。</summary>
    public static bool IsConsistent(MasterLeagueOpponentPage page) => (page.RefreshRemaining is null or >= 0 and <= 5) && page.Opponents.Count == 5 &&
        page.Opponents.All(o => (o.Points is null or >= 0) && !(o.Victory && o.Defeat));

    /// <summary>先处理未挑战者；全部挑战后仅在选项开启且仍有次数时刷新明确败北者。</summary>
    public static (int Index, bool Refresh, string? StopReason) Choose(MasterLeagueOpponentPage page, bool retryDefeated)
    {
        if (!IsConsistent(page)) return (-1, false, "对手状态未可靠确认，已停止挑战。");
        // 五个胜利标记已足以确认本轮完成，无须再读取积分或刷新次数。
        if (page.Opponents.All(o => o.Victory)) return (-1, false, "五名对手均已胜利，大师联盟本轮挑战完成。");
        foreach (var i in ChallengeOrder)
            if (page.Opponents[i].Unchallenged) return (i, false, null);
        if (page.Opponents.Any(o => o.Points is null && !o.Victory && !o.Defeat))
            return (-1, false, "仍有对手状态未知，等待重新识别。");
        if (!retryDefeated) return (-1, false, "五名对手均已挑战，未启用刷新失败对手，已停止。");
        if (page.RefreshRemaining is null) return (-1, false, "变更次数暂未识别，等待重新读取。");
        if (page.RefreshRemaining == 0) return (-1, false, "对手变更次数已耗尽，已停止。");
        foreach (var i in ChallengeOrder)
            if (page.Opponents[i].Defeat) return (i, true, null);
        return (-1, false, "五名对手均已挑战，但未确认可刷新的败北对手，已停止。");
    }
}
