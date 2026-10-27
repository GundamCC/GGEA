using GGEA.App.Config;
using System.IO;
using GGEA.App.Device;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>
/// 单个作品的匹配结果。
/// </summary>
public sealed record StageWorkMatch(string Name, double Confidence, int CenterX, int CenterY, bool IsCurrent);

/// <summary>
/// 主要关卡作品识别器。
/// 直接对当前截图匹配全部作品模板，不依赖复位/滚动流程：
/// 镜头位置不确定时，也能优雅地识别当前可见的作品（取置信度最高者，相同则取更靠近屏幕中心的）。
/// </summary>
public sealed class StageWorkRecognizer : IStageWorkRecognizer
{
    private readonly IAdbService _adb;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;
    private readonly string _templateDirectory;
    private IReadOnlyList<(string Name, TemplateImages Images)>? _templates;

    public StageWorkRecognizer(
        IAdbService adb,
        TemplateMatcher matcher,
        IGgeaLog log,
        string templateDirectory)
    {
        _adb = adb;
        _matcher = matcher;
        _log = log;
        _templateDirectory = templateDirectory;
    }

    /// <summary>
    /// 识别当前画面中的作品。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="threshold">匹配置信度阈值。</param>
    /// <returns>可见作品列表；IsCurrent 表示当前主要作品。一个都没识别到时返回空列表。</returns>
    public async Task<IReadOnlyList<StageWorkMatch>> RecognizeAsync(
        string serial,
        double threshold = 0.85,
        CancellationToken ct = default)
    {
        var templates = LoadTemplates();
        var png = await _adb.ScreenshotAsync(serial, ct);
        var screen = ImageCodec.DecodePng(png);
        var centerX = screen.Width / 2;
        var centerY = screen.Height / 2;

        var matches = new List<StageWorkMatch>();
        foreach (var (name, images) in templates)
        {
            var result = images.Match(_matcher, screen, threshold, ct: ct);
            if (result is null)
            {
                continue;
            }

            matches.Add(new StageWorkMatch(
                name,
                result.Confidence,
                result.CenterX,
                result.CenterY,
                IsCurrent: false));
        }

        if (matches.Count == 0)
        {
            _log.Warn("当前画面未识别到任何主要关卡作品");
            return matches;
        }

        // 当前作品：置信度最高；相同置信度取更靠近屏幕中心的
        var current = matches
            .OrderByDescending(m => m.Confidence)
            .ThenBy(m => Math.Abs(m.CenterX - centerX) + Math.Abs(m.CenterY - centerY))
            .First();
        var index = matches.IndexOf(current);
        matches[index] = current with { IsCurrent = true };

        _log.Info($"识别到 {matches.Count} 个作品，当前作品: {current.Name} ({current.Confidence:P1})");
        return matches;
    }

    /// <summary>在预计区域内只识别一个目标作品。</summary>
    public async Task<StageWorkMatch?> FindAsync(
        string serial,
        string templateName,
        Roi roi,
        double threshold = 0.78,
        CancellationToken ct = default)
    {
        var template = LoadTemplates().FirstOrDefault(item =>
            string.Equals(item.Name, templateName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(template.Name))
        {
            _log.Warn($"作品模板不存在: {templateName}");
            return null;
        }

        var png = await _adb.ScreenshotAsync(serial, ct);
        var screen = ImageCodec.DecodePng(png);
        var result = template.Images.Match(_matcher, screen, threshold, roi, ct);
        if (result is null)
        {
            return null;
        }

        return new StageWorkMatch(
            template.Name,
            result.Confidence,
            result.CenterX,
            result.CenterY,
            IsCurrent: true);
    }

    /// <summary>加载模板目录下全部作品截图（只加载一次并缓存）。</summary>
    private IReadOnlyList<(string Name, TemplateImages Images)> LoadTemplates()
    {
        if (_templates is not null)
        {
            return _templates;
        }

        if (!ConfigService.Resources.DirectoryExists(_templateDirectory))
        {
            _log.Warn($"作品模板目录不存在: {_templateDirectory}");
            _templates = Array.Empty<(string, TemplateImages)>();
            return _templates;
        }

        var list = new List<(string, TemplateImages)>();
        foreach (var file in ConfigService.Resources.GetFiles(_templateDirectory, "*.png"))
        {
            list.Add((Path.GetFileNameWithoutExtension(file), new TemplateImages(file)));
        }

        _templates = list;
        _log.Info($"已加载 {list.Count} 个作品模板");
        return _templates;
    }
}
