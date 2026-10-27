using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Markup;
using System.Windows.Media;

namespace GGEA.App.Localization;

/// <summary>
/// 使用原文作为稳定资源键。插值按格式与参数分离，译文可以调整参数顺序，
/// 不在运行时匹配或替换已拼接的文本，也不改变业务数据、日志级别或数值文化。
/// </summary>
public static class L
{
    private static readonly IReadOnlyDictionary<string, string> Japanese = LoadJapanese();
    public static string Language { get; private set; } = "zh-CN";

    /// <summary>仅在启动创建窗口之前应用，避免正在执行的任务及已创建列表混用语言。</summary>
    public static void Initialize(string? language) => Language = UiLanguages.Resolve(language, CultureInfo.CurrentUICulture);

    /// <summary>原文同时作为中文资源及缺译回退，未知的外部错误信息保持原样。</summary>
    public static string Text(string source) => Translate(source, Language);

    /// <summary>显式语言入口便于测试，不修改进程或线程区域设置。</summary>
    public static string Translate(string source, string language)
        => language == "ja" && Japanese.TryGetValue(source, out var value) ? value : source;

    /// <summary>保留参数类型及格式说明符；系统错误、文件路径和用户输入不送往翻译服务。</summary>
    public static string Format(FormattableString message)
        => string.Format(CultureInfo.CurrentCulture, Text(message.Format), message.GetArguments());

    /// <summary>语言文件嵌入程序集，单文件发布与离线启动均不依赖外部服务。</summary>
    private static IReadOnlyDictionary<string, string> LoadJapanese()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("GGEA.App.Localization.ja.json")
            ?? throw new InvalidOperationException("Missing embedded Japanese UI resource.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Invalid Japanese UI resource.");
    }
}

/// <summary>在 XAML 构建控件时读取统一文案，保留原有控件、布局、绑定及自动化入口。</summary>
public sealed class TextExtension : MarkupExtension
{
    public string Source { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => L.Text(Source);
}

/// <summary>WPF 标记扩展返回强类型字体，中文沿用原字体，日文使用 Windows 日文字体。</summary>
public sealed class UiFontExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new FontFamily(L.Text("Microsoft YaHei UI"));
}
