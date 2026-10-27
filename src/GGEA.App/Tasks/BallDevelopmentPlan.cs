namespace GGEA.App.Tasks;

/// <summary>数量调整命令；命令只描述输入，不代表游戏操作已经成功。</summary>
public enum QuantityAction
{
    /// <summary>降至最低（1）。</summary>
    Minimum,
    /// <summary>减小 1。</summary>
    Decrement,
    /// <summary>增加 1。</summary>
    Increment,
    /// <summary>增加到最大（32）。</summary>
    Maximum,
}

/// <summary>铁球 GP 与开发批次规划；不假设制造已完成或已经获得 GP。</summary>
public static class BallDevelopmentPlan
{
    /// <summary>每台 20 GP，每批最多 32 台；余数批次保持准确，不超量制造。</summary>
    public static IReadOnlyList<int> Batches(int targetGp)
    {
        if (targetGp is < 20 or > 100000 || targetGp % 20 != 0)
            throw new ArgumentOutOfRangeException(nameof(targetGp));
        var units = targetGp / 20;
        return Enumerable.Range(0, (units + 31) / 32).Select(i => Math.Min(32, units - i * 32)).ToArray();
    }

    /// <summary>比较直接调整、最小值及最大值三条路径；相同成本优先直接调整。</summary>
    public static IReadOnlyList<QuantityAction> Adjust(int? current, int target)
    {
        if (current is < 1 or > 32 || target is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(target));
        var direct = current is { } value ? Enumerable.Repeat(target > value ? QuantityAction.Increment : QuantityAction.Decrement,
            Math.Abs(target - value)).ToArray() : null;
        var minimum = new[] { QuantityAction.Minimum }.Concat(Enumerable.Repeat(QuantityAction.Increment, target - 1)).ToArray();
        var maximum = new[] { QuantityAction.Maximum }.Concat(Enumerable.Repeat(QuantityAction.Decrement, 32 - target)).ToArray();
        return new[] { direct, minimum, maximum }.Where(x => x is not null).OrderBy(x => x!.Length).First()!;
    }

    /// <summary>执行单步后的计划值，用于算法验证与维护计数，不做视觉读回。</summary>
    public static int Apply(int value, QuantityAction action) => action switch
    {
        QuantityAction.Minimum => 1,
        QuantityAction.Maximum => 32,
        QuantityAction.Decrement => Math.Max(1, value - 1),
        QuantityAction.Increment => Math.Min(32, value + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
