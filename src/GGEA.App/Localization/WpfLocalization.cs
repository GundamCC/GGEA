using System.Windows.Markup;
using System.Windows.Media;

namespace GGEA.App.Localization;

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
