using GGEA.App.Tasks;
using GGEA.App.Localization;

namespace GGEA.App.Config;

public sealed record LimitedEventDailySettings(bool Stages, bool Boss, bool Medals, FierceEnemyDifficulty Difficulty,
    bool IsChecked = true, int Index = 0)
{
    public DailyTaskItem CreateItem()
    {
        if (!(Stages || Boss || Medals) || !Enum.IsDefined(Difficulty))
            throw new InvalidOperationException(L.Text("请选择有效的限时活动子任务。"));
        return new DailyTaskItem { Name = L.Text("限时活动"), Kind = DailyTaskKind.LimitedEvent,
            LimitedEventSelection = this, IsChecked = IsChecked };
    }

    public string Describe() => string.Join(" / ", new[] { Stages ? L.Text("每日10次关卡") : null,
        Boss ? $"BOSS {Difficulty}" : null, Medals ? L.Text("奖章机体补给") : null }.Where(s => s is not null));
}
