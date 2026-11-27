using System.Globalization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>资金委托与角色无关。用详情进度校对确认框的 CAPITAL 数量，再授权一次交付。</summary>
internal sealed class FundingRequest(OnnxTextRecognizer ocr)
{
    private const string Root = "resources/templates/base/character_requests/";
    private readonly TemplateImages _confirmation = new(Root + "title_confirm_delivery.png");
    private readonly TemplateImages _capital = new(Root + "capital_label.png");
    private readonly TemplateImages _submit = new(Root + "button_submit_delivery.png");
    private readonly TemplateMatcher _matcher = new();

    internal int? ReadAmount(ScreenImage detail, CancellationToken ct)
    {
        var text = UpgradeNumbers.Read(ocr, detail, new(1680, 404, 109, 44), true, ct, out _);
        return UpgradeNumbers.TryReadPair(text, out var progress, out var total) && progress == 0 && total > 0
            ? total : null;
    }

    internal sealed record Confirmation(Roi Button);

    internal Confirmation? ReadConfirmation(ScreenImage frame, int expected, CancellationToken ct)
    {
        if (_confirmation.Match(_matcher, frame, .95, new(857, 201, 207, 75), ct) is null ||
            _capital.Match(_matcher, frame, .96, new(631, 543, 193, 74), ct) is null) return null;
        var text = UpgradeNumbers.Read(ocr, frame, new(515, 620, 101, 42), false, ct, out _);
        if (text is not { Length: > 1 } || text[0] is not ('×' or 'x' or 'X') ||
            !int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            amount != expected) return null;
        var button = _submit.Match(_matcher, frame, .94, new(1086, 806, 130, 88), ct);
        return button is null ? null : new(button.Region);
    }
}
