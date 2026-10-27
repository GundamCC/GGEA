namespace GGEA.App.Vision;

/// <summary>连续三张新截图中的入口类型、位置和尺寸一致才就绪，不以固定等待时间代替画面证据。</summary>
public sealed class StableStageEntry
{
    private TemplateMatchResult? _anchor;
    private string? _kind;
    private int _confirmations;

    /// <summary>发现候选后快速复核；尚未发现入口时仍使用普通加载轮询，避免持续高频截图。</summary>
    public bool HasCandidate => _anchor is not null;

    /// <summary>只返回本帧重新识别的按钮，不缓存旧坐标授权点击；未知帧立即撤销此前稳定证据。</summary>
    public TemplateMatchResult? Observe(TemplateMatchResult? match, string kind)
    {
        if (match is null) { _anchor = null; _kind = null; _confirmations = 0; return null; }
        if (_anchor is null || _kind != kind || Math.Abs(match.CenterX - _anchor.CenterX) > 3 ||
            Math.Abs(match.CenterY - _anchor.CenterY) > 3 ||
            match.Region.Width != _anchor.Region.Width || match.Region.Height != _anchor.Region.Height)
        {
            _anchor = match; _kind = kind; _confirmations = 1;
            return null;
        }
        // 始终与本轮首帧比较，防止每帧微移累计成明显位移；点击只使用最新帧坐标。
        return ++_confirmations >= 3 ? match : null;
    }
}
