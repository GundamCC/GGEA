using System.Globalization;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>一次夺取委托的消费上限和奖励读数。每张绿色「夺取」卡只计一台。</summary>
internal sealed class CaptureRequestBudget(long refresh)
{
    private const string Root = "resources/templates/base/character_requests/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _stageLabel = new(Root + "stage_capture_label.png");
    private readonly TemplateImages _rewardLabel = new(Root + "reward_capture_label.png");
    internal int RemainingAp { get; private set; } = 90;

    internal bool OffersCapture(ScreenImage frame, CancellationToken ct) =>
        _stageLabel.Match(_matcher, frame, .95, new(1040, 738, 550, 58), ct) is not null;

    internal void Reserve(int cost)
    {
        if (RequestProductionAttempt.RefreshPeriod != refresh || cost <= 0 || cost > RemainingAp)
            throw new InvalidOperationException(L.Text("夺取委托已刷新或 AP 预算不足。"));
        RemainingAp -= cost;
    }

    // 调用方已匹配 SKIP RESULT；底部实付次数排除动画空帧，再按卡槽读取标签。
    internal int? ReadRewards(ScreenImage frame, int expected, OnnxTextRecognizer ocr, CancellationToken ct)
    {
        var times = UpgradeNumbers.Read(ocr, frame, new(1408, 966, 41, 44), false, ct, out _);
        if (times != expected.ToString(CultureInfo.InvariantCulture)) return null;
        int units = 0;
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 10; col++)
        {
            int x = 94 + col * 176, y = 384 + row * 236;
            int i = (y * frame.Width + x) * 4;
            int blue = frame.Bgra[i], green = frame.Bgra[i + 1], red = frame.Bgra[i + 2];
            if (green - red > 45 && green - blue > 42)
            {
                if (_rewardLabel.Match(_matcher, frame, .95, new(x + 27, y - 21, 88, 42), ct) is null)
                    return null;
                units++;
            }
            else if (row == 0 && col == 0 &&
                !(red - green > 80 || (green >= 95 && Math.Abs(red - green) < 25 && Math.Abs(green - blue) < 25)))
                return null;
        }
        return units;
    }
}
