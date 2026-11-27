using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>库存、勾选数量和可用状态属于当前弹窗的一帧，不能跨单位复用。</summary>
internal readonly record struct MaterialStack(int Selected, int Stock, bool Available);

/// <summary>按铜、银、金分配实际要消耗的材料份数，核实数量之后只提交一次。</summary>
internal sealed class UpgradeMaterialService(AutomationService automation, OnnxTextRecognizer ocr)
{
    private const string Root = "resources/templates/upgrade/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(Root + "title_lv_materials.png");
    private readonly TemplateImages _confirm = new(Root + "button_apply_lv.png");
    private readonly TemplateImages _minus = new(Root + "material_minus.png");
    private readonly TemplateImages _cancel = new("resources/templates/dialog/cancel.png");
    private readonly TemplateImages _unit = new(Root + "title_unit_info.png");
    private readonly TemplateImages _result = new(Root + "title_lv_up_complete.png");
    private readonly ScreenImage[] _icons = Enumerable.Range(1, 3)
        .Select(i => MaterialPatch(new TemplateImages(Root + $"uexp_000{i}.png").Primary)).ToArray();
    private static readonly Roi[] IconAreas = [new(932, 324, 49, 42), new(737, 323, 49, 42), new(532, 319, 49, 42)];
    private static int Center(int tier) => 960 - tier * 190;

    // 只比较原图中不透明的内部纹理，置灰的铜材料也保留同样的轮廓。
    private static ScreenImage MaterialPatch(ScreenImage sprite)
    {
        const int w = 49, h = 42;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(sprite.Bgra, ((sprite.Height / 3 + y) * sprite.Width + sprite.Width / 5) * 4,
                pixels, y * w * 4, w * 4);
        return new() { Bgra = pixels, Width = w, Height = h };
    }

    internal static int[]? Allocate(IReadOnlyList<MaterialStack> stacks, int count)
    {
        if (count is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(count));
        var plan = new int[3];
        for (var i = 0; i < plan.Length; i++)
        {
            plan[i] = stacks[i].Available ? Math.Min(stacks[i].Stock, count) : 0;
            count -= plan[i];
        }
        return count == 0 ? plan : null;
    }

    /// <summary>完成或缺料都返回单位资料页；未知画面和未确认的提交结果中止，不重发。</summary>
    internal async Task<bool> ApplyAsync(string serial, int required, CancellationToken ct)
    {
        var stacks = await WaitStacksAsync(serial, null, ct);
        var plan = Allocate(stacks, required);
        if (plan is null)
        {
            await TapControlAsync(_cancel, new(560, 920, 400, 150), .93);
            await RequireUnitAsync();
            return false;
        }

        // 先移除游戏额外预选的材料，再逐份补足。每次操作后核对三栏的库存和数量。
        foreach (var remove in new[] { true, false })
        for (var tier = 0; tier < plan.Length; tier++)
        {
            while (remove ? stacks[tier].Selected > plan[tier] : stacks[tier].Selected < plan[tier])
            {
                var x = Center(tier);
                if (remove)
                    await TapControlAsync(_minus, new(x + 20, 260, 74, 60), .93);
                else
                {
                    if (!stacks[tier].Available) throw Unknown();
                    await automation.TapAsync(serial, x, 345, ct);
                }
                var expected = stacks.ToArray();
                expected[tier] = expected[tier] with { Selected = expected[tier].Selected + (remove ? -1 : 1) };
                stacks = await WaitStacksAsync(serial, expected, ct);
            }
        }

        // 提交授权必须来自同时包含正确三栏数量和蓝色提升按钮的新帧。
        var submit = await StableObservation.WaitAsync(async () =>
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            if (ReadStacks(frame, ct) is not { } seen || !seen.SequenceEqual(stacks)) return null;
            return _confirm.Match(_matcher, frame, .92, new Roi(1030, 930, 240, 120), ct);
        }, (a,b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();
        await automation.TapAsync(serial, submit.CenterX, submit.CenterY, ct);
        var next = await StableObservation.WaitAsync(async () =>
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            if (_result.Match(_matcher, frame, .93, new Roi(720, 215, 475, 158), ct) is null) return null;
            return automation.TapToNext.Recognize(frame, ct);
        }, (a,b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();
        await automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
        await RequireUnitAsync();
        return true;

        async Task TapControlAsync(TemplateImages template, Roi roi, double threshold)
        {
            var match = await StableObservation.WaitAsync(async () =>
            {
                var frame = await automation.CaptureScreenAsync(serial, ct);
                if (ReadStacks(frame, ct) is not { } seen || !seen.SequenceEqual(stacks)) return null;
                return template.Match(_matcher, frame, threshold, roi, ct);
            }, (a,b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();
            await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
        }

        async Task RequireUnitAsync()
        {
            _ = await StableObservation.WaitAsync(async () => _unit.Match(_matcher,
                await automation.CaptureScreenAsync(serial, ct), .93, new Roi(175, 0, 390, 108), ct),
                (a,b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();
        }
    }

    private async Task<MaterialStack[]> WaitStacksAsync(string serial, MaterialStack[]? expected, CancellationToken ct) =>
        await StableObservation.WaitAsync(async () =>
        {
            var seen = ReadStacks(await automation.CaptureScreenAsync(serial, ct), ct);
            if (expected is null || seen is null) return seen;
            // 预览达到等级上限时按钮状态可能变化，库存与本次加减仍须完全一致。
            return seen.Select(s => (s.Stock, s.Selected)).SequenceEqual(expected.Select(s => (s.Stock, s.Selected))) ? seen : null;
        }, (a,b) => a.SequenceEqual(b), GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();

    internal MaterialStack[]? ReadStacks(ScreenImage frame, CancellationToken ct)
    {
        if (_title.Match(_matcher, frame, .93, new Roi(850, 30, 220, 113), ct) is null) return null;
        var stacks = new MaterialStack[3];
        for (var i = 0; i < 3; i++)
        {
            if (!TemplateMatcher.MatchesFixedTexture(frame, _icons[i], IconAreas[i], .95)) return null;
            var text = UpgradeNumbers.Read(ocr, frame, new Roi(Center(i) - 70, 384, 140, 29), false, ct, out var contrast);
            if (!UpgradeNumbers.TryReadPair(text, out var selected, out var stock)) return null;
            if (contrast is > 60 and < 105) return null;
            stacks[i] = new(selected, stock, contrast >= 105 && stock > 0);
        }
        return stacks;
    }

    private static InvalidOperationException Unknown() => new(L.Text("未能确认强化材料或操作结果，已停止本轮。"));
}
