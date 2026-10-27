using GGEA.App.Localization;
using System.Configuration;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace GGEA.App;

/// <summary>
/// 应用入口。
/// 开发版允许多开；发布版通过命名互斥量保持单实例，重复启动时激活已有窗口。
/// </summary>
public partial class App : Application
{
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

    /// <summary>单实例互斥量名称（Local 命名空间按登录会话隔离）。</summary>
    private const string SingleInstanceMutexName = @"Local\GGEA.SingleInstance";

    /// <summary>标记持有互斥量的窗口，避免开发版并存时激活错误的窗口。</summary>
    private const string SingleInstanceWindowProperty = "GGEA.SingleInstanceWindow";

    /// <summary>窗口句柄创建后登记单实例窗口；窗口销毁时 Windows 自动清理属性。</summary>
    internal static void RegisterSingleInstanceWindow(IntPtr handle)
    {
        if (_singleInstanceMutex is not null)
            _ = SetProp(handle, SingleInstanceWindowProperty, new IntPtr(1));
    }

    /// <summary>当前实例持有的单实例互斥量；非首个实例时为空。</summary>
    private static Mutex? _singleInstanceMutex;
    /// <summary>正式安装生命周期锁，更新器只在主进程完全退出后获得写权限。</summary>
    private FileStream? _installationLock;

    /// <summary>Win32 恢复窗口命令：显示并还原（不强制最大化）。</summary>
    private const int SwRestore = 9;

    /// <summary>
    /// 启动入口：开发版直接启动，发布版先尝试成为唯一实例。
    /// 若已有实例在运行，则激活其窗口并退出，不创建任何窗口。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 原生截图在独立进程执行，先于单实例和窗口初始化，避免影响正在运行的主程序。
        if (e.Args.Length > 0 && e.Args[0] == "--mumu-capture")
        {
            Shutdown(Device.MuMuNativeCapture.Run(e.Args));
            return;
        }

        // 发布自检先于单实例与主窗口启动，禁止连接设备或触发任何游戏操作。
        if (e.Args.Length == 1 && e.Args[0] == "--verify-release")
        {
            Shutdown(ReleaseVerification.Run());
            return;
        }

        if (!TryAcquireSingleInstance())
        {
            ActivateExistingInstance();
            Shutdown();
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
            File.Exists(Path.Combine(AppContext.BaseDirectory, Updater.UpdateFiles.JournalName)))
        {
            try
            {
                _installationLock = Updater.UpdateFiles.AcquireLock(AppContext.BaseDirectory);
                if (File.Exists(Updater.UpdateFiles.SafePath(AppContext.BaseDirectory, Updater.UpdateFiles.JournalName)))
                {
                    if (MessageBox.Show(L.Text("上次更新未完成，需先恢复旧版再使用。是否现在恢复？"), L.Text("GGEA 更新恢复"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        Task.Run(() => Updates.UpdateLauncher.StartAsync(AppContext.BaseDirectory, null, null, true, CancellationToken.None)).GetAwaiter().GetResult();
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

        base.OnStartup(e);
    }

    /// <summary>
    /// 应用退出时释放单实例互斥量，保证下一次启动可以正常成为唯一实例。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // 原生截图助手与主窗口共用此入口；必须在 CLR ProcessExit 前释放图像线程。
        Vision.ImageCodecThread.Shutdown();
        _installationLock?.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    /// <summary>
    /// 尝试取得单实例互斥量所有权。
    /// 处理“已有实例异常退出导致互斥量被遗弃”的情况：被遗弃的互斥量可以由新实例接管。
    /// </summary>
    /// <returns>当前进程是否为唯一实例。</returns>
    private static bool TryAcquireSingleInstance(string mutexName = SingleInstanceMutexName)
    {
#if DEBUG
        // 开发版不占用正式版互斥量，支持两种构建并存。
        return true;
#else
        // 本地普通验收包带有独立清单，不占用正式版互斥量，避免启动时只唤醒旧窗口。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "local-build-info.json"))) return true;
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);

        if (!createdNew)
        {
            // 互斥量已存在：若持有者已崩溃（被遗弃），WaitOne 会抛 AbandonedMutexException，
            // 此时所有权已转移给当前进程，可以继续启动。
            try
            {
                if (mutex.WaitOne(0))
                {
                    createdNew = true;
                }
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _singleInstanceMutex = mutex;
        return true;
#endif
    }

    /// <summary>释放单实例互斥量。</summary>
    private static void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null)
        {
            return;
        }

        try
        {
            _singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 退出时互斥量可能已被释放，忽略即可
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }

    /// <summary>
    /// 找到已运行的 GGEA 主窗口，最小化时先还原，再置为前台。
    /// 通过 EnumWindows 按进程与窗口类名精确查找可见主窗口，
    /// 避免 Process.MainWindowHandle 可能指向隐藏辅助窗口导致激活失效。
    /// 激活失败不影响后续逻辑（新实例直接退出即可）。
    /// </summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var currentId = (uint)Environment.ProcessId;
            IntPtr? target = null;

            _ = EnumWindows((hWnd, lParam) =>
            {
                _ = GetWindowThreadProcessId(hWnd, out var processId);
                if (processId == currentId || !IsWindowVisible(hWnd) ||
                    GetProp(hWnd, SingleInstanceWindowProperty) == IntPtr.Zero)
                {
                    return true;
                }

                var className = new StringBuilder(256);
                _ = GetClassName(hWnd, className, className.Capacity);
                if (className.ToString().StartsWith("HwndWrapper[GGEA", StringComparison.Ordinal))
                {
                    // 找到目标窗口后停止枚举
                    target = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (target is null)
            {
                return;
            }

            _ = ShowWindowAsync(target.Value, SwRestore);
            _ = SetForegroundWindow(target.Value);
        }
        catch
        {
            // 窗口激活失败不应阻断新实例退出
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>为发布版窗口登记跨进程可识别的属性。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hWnd, string name, IntPtr value);

    /// <summary>读取窗口属性，排除没有持有单实例锁的开发版窗口。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hWnd, string name);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
