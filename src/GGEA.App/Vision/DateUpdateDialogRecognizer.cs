using System.IO;
using GGEA.App.Config;

namespace GGEA.App.Vision;

/// <summary>同帧确认日期更新标题和返回主页文字，提供中断检查证据，不授权点击或推断关卡完成。</summary>
public sealed class DateUpdateDialogRecognizer
{
    private readonly TemplateMatcher _matcher = new();
    private readonly string _titlePath;
    private readonly string _homePath;

    /// <summary>默认使用当前语言素材；测试可注入独立路径，不修改全局语言。</summary>
    public DateUpdateDialogRecognizer(string? titlePath = null, string? homePath = null)
    {
        _titlePath = titlePath ?? ConfigService.ResolvePath("resources/templates/common/date_update_title.png");
        _homePath = homePath ?? ConfigService.ResolvePath("resources/templates/common/date_update_home.png");
    }

    /// <summary>标题与按钮必须同时位于居中弹窗区域；缺少该语言素材时不确认弹窗。</summary>
    public TemplateMatchResult? Recognize(ScreenImage screen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var title = Match(_titlePath, new Roi(screen.Width / 4, 0, screen.Width / 2, screen.Height / 3));
        if (title is null) return null;
        return Match(_homePath, new Roi(screen.Width / 4, screen.Height * 2 / 3,
            screen.Width / 2, screen.Height - screen.Height * 2 / 3));

        // 只忽略不存在的语言素材；损坏文件与取消仍传播，不伪造成功。
        TemplateMatchResult? Match(string path, Roi region)
            => TemplateFallback.Match(path, candidate =>
            {
                if (!File.Exists(candidate)) return null;
                var template = ImageCodec.DecodePng(File.ReadAllBytes(candidate));
                return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                    template.Width, template.Height, .90, region, ct: ct);
            }, ct);
    }
}
