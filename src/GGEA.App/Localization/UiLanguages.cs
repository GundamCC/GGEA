using System.Globalization;

namespace GGEA.App.Localization;

/// <summary>助手界面语言独立于游戏资源语言；下拉项始终使用语言自称。</summary>
public static class UiLanguages
{
    /// <summary>稳定的持久化标识与用户可读的本语言名称。</summary>
    public sealed record Option(string Id, string NativeName)
    {
        public override string ToString() => NativeName;
    }

    public static IReadOnlyList<Option> All { get; } = Array.AsReadOnly(new[]
    {
        new Option("zh-CN", "简体中文"), new Option("ja", "日本語")
    });

    /// <summary>首次使用取 Windows 界面语言；未支持的语言明确回退到简体中文。</summary>
    public static string Resolve(string? saved, CultureInfo systemLanguage)
    {
        if (string.Equals(saved, "zh-CN", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (string.Equals(saved, "ja", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(saved, "ja-JP", StringComparison.OrdinalIgnoreCase)) return "ja";
        return systemLanguage.TwoLetterISOLanguageName == "ja" ? "ja" : "zh-CN";
    }
}
