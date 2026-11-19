using GGEA.App.Localization;
using System.IO;
using System.Windows;

namespace GGEA.App;

/// <summary>
/// 应用入口。
/// 每个进程创建独立窗口，安装更新前确认同目录其他窗口已退出。
/// </summary>
public partial class App : Application
{
    /// <summary>先注册 Windows 图像后端及悬停反馈，再由 WPF 创建资源和窗口。</summary>
    public App()
    {
        Vision.WpfImageCodec.Initialize();
        UI.HoverFeedback.Initialize();
    }

    /// <summary>启动时读取一次配置，语言与主窗口使用同一份快照。</summary>
    internal Config.AppConfig StartupConfig { get; private set; } = new();
    /// <summary>主题由应用持有，未保存偏好时默认夜间模式，所有窗口共用同一配色。</summary>
    public bool IsDarkMode { get; private set; } = true;

    /// <summary>仅替换主题资源，不重建窗口，避免切换时丢失任务选择与输入内容。</summary>
    public void ToggleTheme()
        => SetTheme(!IsDarkMode);

    /// <summary>恢复已保存主题；幂等调用不会反向切换主题。</summary>
    public void SetTheme(bool nextDarkMode)
    {
        if (nextDarkMode == IsDarkMode) return;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/GGEA;component/UI/Themes/{(nextDarkMode ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        Resources.MergedDictionaries[0] = palette;
        IsDarkMode = nextDarkMode;
    }

    private Updater.RunningInstallation? _installation;

    /// <summary>在当前安装的独占保护下向更新器交接，失败时恢复当前窗口的共享运行状态。</summary>
    internal async Task StartUpdateAsync(string? package, string? version, bool recover, CancellationToken ct)
    {
        if (_installation is null) throw new InvalidOperationException(L.Text("当前目录不支持在线安装。"));
        try
        {
            await _installation.StartUpdateAsync(() =>
                Updates.UpdateLauncher.StartAsync(AppContext.BaseDirectory, package, version, recover, ct));
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 32)
        {
            throw new IOException(L.Text("请先关闭同一目录中的其他 GGEA 窗口，再重试更新。"), ex);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 原生截图在独立进程执行，先于安装锁和窗口初始化，避免影响正在运行的主程序。
        if (e.Args.Length > 0 && e.Args[0] == "--mumu-capture")
        {
            Shutdown(Device.MuMuNativeCapture.Run(e.Args));
            return;
        }

        // 发布自检先于安装锁与主窗口启动，禁止连接设备或触发任何游戏操作。
        if (e.Args.Length == 1 && e.Args[0] == "--verify-release")
        {
            Shutdown(ReleaseVerification.Run());
            return;
        }

        // 创建窗口前确定语言；日志目录不可写时仍给出本机语言提示，不进入未初始化的主窗口。
        L.Initialize(null);
        try
        {
            using var startupLog = new Logging.GgeaLogger(Path.Combine(AppContext.BaseDirectory, "logs"));
            StartupConfig = new Config.ConfigService(startupLog).Load(Path.Combine(AppContext.BaseDirectory, "config", "ggea.config.json"));
            StartupConfig.UiLanguage = UiLanguages.Resolve(StartupConfig.UiLanguage, System.Globalization.CultureInfo.CurrentUICulture);
            L.Initialize(StartupConfig.UiLanguage);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.Format($"无法创建启动日志，请检查程序目录的写入权限：{ex.Message}"), "GGEA", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 先检查恢复标记再创建主窗口，禁止在半更新的资源集合上运行自动化。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "build-info.json")) ||
            File.Exists(Path.Combine(AppContext.BaseDirectory, "local-build-info.json")) ||
            File.Exists(Path.Combine(AppContext.BaseDirectory, Updater.UpdateFiles.JournalName)))
        {
            try
            {
                _installation = new Updater.RunningInstallation(AppContext.BaseDirectory);
                if (File.Exists(Updater.UpdateFiles.SafePath(AppContext.BaseDirectory, Updater.UpdateFiles.JournalName)))
                {
                    if (MessageBox.Show(L.Text("上次更新未完成，需先恢复旧版再使用。是否现在恢复？"), L.Text("GGEA 更新恢复"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        Task.Run(() => StartUpdateAsync(null, null, true, CancellationToken.None)).GetAwaiter().GetResult();
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.Format($"安装目录正被使用，或更新恢复无法启动。请关闭其他实例后重试。\n{ex.Message}"), L.Text("GGEA 在线更新"), MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
        }

        // 更新恢复优先；原生环境不完整时不创建主窗口或启动任务。
        if (!RuntimeDependencies.EnsureAvailable(AppContext.BaseDirectory))
        {
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>退出时释放本进程的图像线程和安装目录占用。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // 原生截图助手与主窗口共用此入口；必须在 CLR ProcessExit 前释放图像线程。
        Vision.ImageCodecThread.Shutdown();
        _installation?.Dispose();
        base.OnExit(e);
    }
}
