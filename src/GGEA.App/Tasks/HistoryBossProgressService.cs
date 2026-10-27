using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>同难度两卡按上下位置绑定关卡序号，不维护六关扫描缓存。</summary>
internal static class HistoryBossPosition
{
    /// <summary>序号 1/2 对应 NORMAL 下/上卡，3/4 对应 HARD，5/6 对应 EXPERT；只返回当前帧位置。</summary>
    public static HistoryBossCard? Find(IReadOnlyList<HistoryBossCard> cards, int order)
    {
        var difficulty = order switch
        {
            1 or 2 => FierceEnemyDifficulty.NORMAL,
            3 or 4 => FierceEnemyDifficulty.HARD,
            5 or 6 => FierceEnemyDifficulty.EXPERT,
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        return Pair(cards, difficulty)?[order % 2 == 1 ? 1 : 0];
    }

    /// <summary>同难度只允许两张可读卡片；重复标签或异常数量不构成有效位置依据。</summary>
    private static HistoryBossCard[]? Pair(IReadOnlyList<HistoryBossCard> cards, FierceEnemyDifficulty difficulty)
    {
        var pair = cards.Where(card => card.Difficulty == difficulty).OrderBy(card => card.Label.Y).ToArray();
        if (pair.Length > 2 || pair.Length == 2 && pair[0].Label.Y == pair[1].Label.Y)
            throw new InvalidOperationException(L.Text("BOSS 同难度卡片位置存在歧义，未选择关卡。"));
        return pair.Length == 2 ? pair : null;
    }
}

/// <summary>保持当前位置的有界查找器；仅在目标不在当前视口时滚动，战后不主动回顶或回底。</summary>
internal sealed class HistoryBossLocator(
    Func<CancellationToken, Task<IReadOnlyList<HistoryBossCard>>> read,
    Func<int, CancellationToken, Task> scroll, Action<string>? report = null)
{
    /// <summary>首关从底部 NORMAL 开始，后续向高难度移动；游戏重置视口时根据实际难度纠正方向。</summary>
    public async Task<HistoryBossCard> FindAsync(int order, CancellationToken ct)
    {
        var target = (order - 1) / 2;
        if (order is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(order));
        var movement = order <= 2 ? -120 : 120;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var cards = await read(ct);
            ct.ThrowIfCancellationRequested();
            var found = HistoryBossPosition.Find(cards, order);
            if (found is { IsFullyVisible: true }) return found;
            if (cards.Count == 0)
                throw new InvalidOperationException(L.Text("未确认 BOSS 卡片位置，停止滚动。"));
            var targetLabels = cards.Where(card => Rank(card.Difficulty) == target).ToArray();
            if (found is not null)
            {
                // 两标签已确定关卡身份，但目标按钮或完成标记越界；以实际高度移回安全区，再从新帧识别。
                var destination = Math.Clamp(found.Label.Y, HistoryBossCard.MinimumLabelY + 32,
                    HistoryBossCard.MaximumLabelY - 32);
                movement = destination - found.Label.Y;
                report?.Invoke(L.Format($"BOSS 第 {order} 关 {found.Difficulty} 标识 Y={found.Label.Y}，卡片未完整显示，调整至 Y≈{destination}。"));
            }
            else if (targetLabels.Length == 1)
            {
                // 单标签不猜同难度上下关。放到列表中部，使上下相邻标签都能进入 OCR 区域，再确认难度对。
                movement = 540 - targetLabels[0].Label.Y;
                if (Math.Abs(movement) < 24)
                    throw new InvalidOperationException(L.Text("目标 BOSS 难度标识已居中，但未识别到同难度另一关，停止定位，不猜测关卡。"));
                report?.Invoke(L.Format($"BOSS {targetLabels[0].Difficulty} 仅一个标识，Y={targetLabels[0].Label.Y}，移至列表中部复核同难度两关。"));
            }
            else
            {
                // 目标难度完全不在视口时，才按高低难度决定搜索方向；边缘目标优先于其他难度。
                var ranks = cards.Select(card => Rank(card.Difficulty)).ToArray();
                if (ranks.Any(rank => rank > target)) movement = -120;
                else if (ranks.Any(rank => rank < target)) movement = 120;
            }
            if (attempt < 23) await scroll(Math.Clamp(movement, -420, 420), ct);
        }
        throw new InvalidOperationException(L.Text("有限浏览后未找到目标 BOSS 的完整难度对，停止推进。"));
    }

    /// <summary>显式排序避免依赖难度枚举的底层数值。</summary>
    private static int Rank(FierceEnemyDifficulty difficulty) => difficulty switch
    {
        FierceEnemyDifficulty.NORMAL => 0,
        FierceEnemyDifficulty.HARD => 1,
        FierceEnemyDifficulty.EXPERT => 2,
        _ => throw new InvalidOperationException(L.Text("未知 BOSS 难度，停止定位。"))
    };
}

/// <summary>在当前视口读取单关完成标记并直接挑战；仅保留本次读取到点击之间的短期位置授权。</summary>
public sealed class HistoryBossProgressService(AutomationService automation, HistoryProgressNavigator navigation,
    HistoryBossProgressRecognizer recognizer, string serial, IGgeaLog log)
{
    /// <summary>每个作品建立独立定位会话；取消、战斗或下一次读取都会使旧点击位置失效。</summary>
    public async Task<AutoProgressResult> RunAsync(string work, Func<int, CancellationToken, Task<AutoProgressBattleResult>> battle,
        Action<string> report, CancellationToken ct)
    {
        recognizer.Validate();
        await navigation.WaitForPageAsync(HistoryPageKind.Boss, ct);
        var locator = new HistoryBossLocator(ReadCurrentAsync, ScrollAsync, log.Info);
        HistoryBossCard? current = null;
        var currentOrder = 0;
        return await new HistoryBossProgressRunner(async (order, token) =>
        {
            current = null;
            var card = await locator.FindAsync(order, token);
            current = card;
            currentOrder = order;
            return new(order, card.State);
        }, async (order, token) =>
        {
            var card = current;
            current = null;
            if (order != currentOrder || card?.State != FierceEnemyProgressState.Available || card.Challenge is null)
                return new(false, L.Text("BOSS 当前关卡未确认可挑战，未点击。"));
            // 使用紧接本次读取的按钮位置；中间没有滚动或其他设备操作，不再回顶重复查找。
            token.ThrowIfCancellationRequested();
            await automation.TapAsync(serial, card.Challenge.CenterX, card.Challenge.CenterY, token);
            return await battle(order, token);
        }).RunAsync(work, report, ct);
    }

    /// <summary>一次截图确认作品页面并读取完成标记，返回只属于该帧的位置与状态。</summary>
    private async Task<IReadOnlyList<HistoryBossCard>> ReadCurrentAsync(CancellationToken ct)
    {
        var screen = await automation.CaptureScreenAsync(serial, ct);
        return await Task.Run(() =>
        {
            navigation.Require(screen, HistoryPageKind.Boss, ct);
            return recognizer.Read(screen, ct);
        }, ct);
    }

    /// <summary>只在目标不在当前视口时短距离移动；每次动作前确认仍在本作品 BOSS 页面。</summary>
    private async Task ScrollAsync(int movement, CancellationToken ct)
    {
        var screen = await automation.CaptureScreenAsync(serial, ct);
        await Task.Run(() => navigation.Require(screen, HistoryPageKind.Boss, ct), ct);
        // 手指方向与列表浏览方向相反；限制手势在卡片视口内，避免触及底部导航。
        movement = Math.Clamp(movement, -420, 420);
        var startY = movement < 0 ? 800 : 350;
        log.Info($"按 BOSS 标识位置调整列表：手指垂直移动 {movement}px，滚动后重新识别目标与挑战按钮。");
        await automation.SwipeAsync(serial, 1500, startY, 1500, startY + movement, 650, ct);
        await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
    }
}
