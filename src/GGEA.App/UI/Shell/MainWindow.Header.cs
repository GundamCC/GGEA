using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using GGEA.App.Config;
using GGEA.App.Localization;

namespace GGEA.App.UI;

/// <summary>页眉主题与社区入口，以及底部版本信息。</summary>
public partial class MainWindow
{
    // 从程序集元数据读取完整四段显示版本，避免修订号被截断而与项目配置不一致。
    private static string AppVersion =>
        Updates.UpdateLauncher.CurrentVersion.ToString(4);

    // 底部版权声明固定归属作者，年份随运行时间自动更新，无需每年手动修改界面资源。
    private static readonly string AppCopyright = $"© {DateTime.Now.Year} GundamC";

    /// <summary>切换明暗主题并更新按钮含义；运行中的任务与当前界面状态保持不变。</summary>
    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        if (!_executionGate.IsHeld) _config.IsDarkMode = app.IsDarkMode;
        UpdateThemeButton();
        // 切换即保存，退出或异常结束后也能恢复；未初始化的设计/测试窗口不写磁盘。
        if (_logger is not null)
        {
            try { new ConfigService(_logger).Update(ConfigService.ResolvePath(DefaultConfigFile), latest => latest.IsDarkMode = app.IsDarkMode); }
            catch (Exception ex) { _logger.Warn(L.Format($"主题设置保存失败: {ex.GetType().Name}")); }
        }
    }

    /// <summary>应用主题恢复后同步灯泡外观和提示。</summary>
    private void UpdateThemeButton()
    {
        var app = (App)Application.Current;
        BulbGlass.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            app.IsDarkMode ? "Brush.CardAlt" : "Brush.TextDim");
        BulbRays.Visibility = app.IsDarkMode ? Visibility.Collapsed : Visibility.Visible;
        var label = app.IsDarkMode ? L.Text("切换到日间模式") : L.Text("切换到夜间模式");
        ThemeToggleButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(ThemeToggleButton, label);
    }

    private void QqGroupButton_Click(object sender, RoutedEventArgs e)
        => OpenCommunityPage("https://qm.qq.com/q/zkxGhEiVQQ");

    private void WebsiteButton_Click(object sender, RoutedEventArgs e)
        => OpenCommunityPage(L.Language == "ja" ? "https://www.gundamc.com/ggea/ja/" : "https://www.gundamc.com/ggea/");

    private void DocumentationButton_Click(object sender, RoutedEventArgs e)
        => OpenCommunityPage(L.Language == "ja" ? "https://www.gundamc.com/ggea/docs/ja/guide/install/" : "https://www.gundamc.com/ggea/docs/guide/install/");

    /// <summary>仅打开代码内固定的官方入口；浏览器启动失败时给出可手动访问的地址。</summary>
    private void OpenCommunityPage(string url)
    {
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"打开网页失败: {ex.GetType().Name}"));
            MessageBox.Show(this, L.Format($"无法打开浏览器，请手动访问：\n{url}"), L.Text("打开网页失败"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>启动及保存设置后统一刷新底部信息，区服和语言沿用设置中的显示名称。</summary>
    private void UpdateFooterText()
    {
        ApplyServerFeatureVisibility();
        var server = GameProfiles.Servers.FirstOrDefault(item => item.Id == _config.GameServer)?.Label ?? _config.GameServer;
        var language = GameProfiles.Languages.FirstOrDefault(item => item.Id == _config.GameLanguage)?.Label ?? _config.GameLanguage;
        FooterText.Text = $"v{AppVersion} · {L.Text(server)} · {language}";
    }
}
