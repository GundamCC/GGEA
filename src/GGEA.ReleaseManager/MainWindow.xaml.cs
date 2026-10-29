using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Xml.Linq;

namespace GGEA.ReleaseManager;

/// <summary>本地构建与打包窗口；固定提交校验和当前工作区构建保持独立。</summary>
public partial class MainWindow : Window
{
    private readonly ReleaseManagerSettingsStore _settingsStore = new();
    private bool _settingsWritable = true;
    private Process? _runningProcess;
    private string? _version;
    private string? _commit;
    private bool _clean;
    private bool _busy;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        string? source = FindSourceRoot(AppContext.BaseDirectory);
        var settings = new ReleaseManagerSettings();
        try { settings = _settingsStore.Load(); }
        catch
        {
            _settingsWritable = false;
            AppendLog("本机配置读取失败，原文件保持不变。请检查本机设置路径中的 settings.json 后重启。");
        }
        SourcePathBox.Text = ReleaseManagerSettingsStore.InitialSource(settings.SourceRepository, source, AppContext.BaseDirectory, settings.SchemaVersion == 1);
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; return; }
            SaveSettings();
        };
        _loaded = true;
        RefreshState();
    }

    private string SourceRoot => ReleaseManagerSettingsStore.ResolveSource(SourcePathBox.Text.Trim(), AppContext.BaseDirectory);

    /// <summary>支持把发布工具放在仓库的 artifacts 目录，也允许从窗口选择其他检出目录。</summary>
    private static string? FindSourceRoot(string start)
    {
        DirectoryInfo? directory = new(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "release.ps1")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "GGEA.App", "GGEA.App.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>只读取当前版本和提交，界面绝不猜测未提交的源码已进入发布包。</summary>
    private void RefreshState()
    {
        if (!_loaded || _busy) return;
        try
        {
            string project = Path.Combine(SourceRoot, "src", "GGEA.App", "GGEA.App.csproj");
            _version = XDocument.Load(project).Descendants("Version").Single().Value.Trim();
            _commit = RunGit(SourceRoot, "rev-parse", "HEAD").Trim();
            _clean = string.IsNullOrWhiteSpace(RunGit(SourceRoot, "status", "--porcelain", "--untracked-files=normal"));
            string stem = $"GGEA-v{_version}-win-x64-{_commit[..12]}";
            string dist = Path.Combine(SourceRoot, "artifacts", "dist");
            bool artifactExists = File.Exists(Path.Combine(dist, stem + ".zip")) &&
                              File.Exists(Path.Combine(dist, stem + ".zip.sha256")) &&
                              Directory.Exists(Path.Combine(dist, stem + "-verified"));
            VersionText.Text = $"v{_version}";
            CommitText.Text = _commit[..12];
            WorkingTreeText.Text = _clean ? (artifactExists ? "发布包已就绪" : "工作区干净 · 尚无发布包") : "源码有未提交修改";
        }
        catch (Exception ex)
        {
            _version = null;
            _commit = null;
            _clean = false;
            VersionText.Text = "—";
            CommitText.Text = "—";
            WorkingTreeText.Text = "无法读取源码仓库";
            AppendLog($"状态读取失败：{ex.Message}");
        }
        UpdateButtons();
    }

    private static string RunGit(string root, params string[] args)
    {
        ProcessStartInfo start = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Git。");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git {args[0]} 失败：{error.Trim()}");
        return output;
    }

    private void UpdateButtons()
    {
        if (!_loaded) return;
        bool ready = !_busy && _clean && _commit is not null && _version is not null;
        RefreshButton.IsEnabled = SettingsButton.IsEnabled = !_busy;
        SourcePathBox.IsEnabled = !_busy;
        BuildButton.IsEnabled = ready;
        // 本地验收包直接使用当前工作区；固定提交构建仍以干净工作区为前提。
        PublishNormalButton.IsEnabled = !_busy && _version is not null;
        PackageNormalButton.IsEnabled = PublishNormalButton.IsEnabled;
        BuildDevButton.IsEnabled = !_busy && _commit is not null && _version is not null;
        PackageDevButton.IsEnabled = BuildDevButton.IsEnabled;
        // 构建期间禁止启动产物，避免占用正在替换的文件；缺失产物由点击处理给出明确提示。
        OpenNormalButton.IsEnabled = OpenPackageButton.IsEnabled = OpenDevButton.IsEnabled = OpenDevPackageButton.IsEnabled = !_busy && _version is not null;
        CancelButton.IsEnabled = _busy;
    }

    private static string PowerShellExecutable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(folder => Path.Combine(folder, "pwsh.exe")).FirstOrDefault(File.Exists);
        return path ?? "powershell.exe";
    }

    /// <summary>异步读取脚本输出，保证长时间构建时窗口可用，失败时保留完整退出码。</summary>
    private async Task RunScriptAsync(string title, string script, params string[] arguments)
    {
        if (_busy || !SaveSettings()) return;
        _busy = true;
        StateText.Text = title + "…";
        UpdateButtons();
        AppendLog($"\n{DateTime.Now:HH:mm:ss}  {title}");
        try
        {
            string shell = PowerShellExecutable();
            ProcessStartInfo start = new(shell)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = SourceRoot,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // Windows PowerShell 回退路径只为本次子进程放开脚本执行，不修改系统策略。
            if (Path.GetFileName(shell).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
            }
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(Path.Combine(SourceRoot, "tools", script));
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            // 本地构建不需要发布令牌，避免把维护者环境中的认证传给构建子进程。
            start.Environment.Remove("GH_TOKEN");
            start.Environment.Remove("GITHUB_TOKEN");
            using Process process = new() { StartInfo = start, EnableRaisingEvents = true };
            _runningProcess = process;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Dispatcher.BeginInvoke(() => AppendLog(e.Data)); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Dispatcher.BeginInvoke(() => AppendLog(e.Data)); };
            if (!process.Start()) throw new InvalidOperationException("无法启动 PowerShell。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"{title}失败，退出码 {process.ExitCode}。");
            StateText.Text = title + "完成";
            AppendLog($"{title}完成。");
        }
        catch (Exception ex)
        {
            StateText.Text = title + "失败";
            AppendLog(ex.Message);
        }
        finally
        {
            _runningProcess = null;
            _busy = false;
            RefreshState();
        }
    }

    /// <summary>路径改动与重启持久化共用单一存储；失败可见，不在项目目录写回退文件。</summary>
    private bool SaveSettings()
    {
        if (!_loaded) return true;
        if (!_settingsWritable)
        {
            AppendLog("本机配置尚未恢复，未覆盖原文件；请修复 settings.json 后重启发布管理器。");
            return false;
        }
        try
        {
            _settingsStore.Save(new ReleaseManagerSettings { SourceRepository = ReleaseManagerSettingsStore.StoreSource(SourcePathBox.Text.Trim(), AppContext.BaseDirectory) });
            return true;
        }
        catch
        {
            AppendLog("本机配置保存失败，请检查目录路径和 LocalAppData 写入权限。");
            return false;
        }
    }

    /// <summary>只打开本机配置目录，方便维护者检查路径配置；不在仓库内生成备份。</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !SaveSettings()) return;
        try { Process.Start(new ProcessStartInfo(ReleaseManagerSettingsStore.DefaultDirectory) { UseShellExecute = true }); }
        catch { AppendLog("无法打开本机配置目录。"); }
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.Text.Length > 180_000) LogBox.Text = LogBox.Text[^150_000..];
        LogBox.ScrollToEnd();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshState();

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string initialDirectory = AppContext.BaseDirectory;
        try { if (Directory.Exists(SourceRoot)) initialDirectory = SourceRoot; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { /* 无效输入仍允许通过目录选择器修复。 */ }
        using System.Windows.Forms.FolderBrowserDialog picker = new() { Description = "选择 GGEA 源码仓库", SelectedPath = initialDirectory };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SourcePathBox.Text = ReleaseManagerSettingsStore.StoreSource(picker.SelectedPath, AppContext.BaseDirectory);
        SaveSettings();
        RefreshState();
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commit is null) return;
        await RunScriptAsync("构建发布包", "release.ps1", "-Commit", _commit);
    }

    private async void PublishNormalButton_Click(object sender, RoutedEventArgs e)
    {
        await RunScriptAsync("建立普通版本", "build-normal.ps1");
    }

    private async void PackageNormalButton_Click(object sender, RoutedEventArgs e)
    {
        await RunScriptAsync("打包普通版本", "build-normal.ps1", "-Package");
    }

    private async void BuildDevButton_Click(object sender, RoutedEventArgs e)
    {
        await RunScriptAsync("建立开发版本", "build-dev.ps1");
    }

    /// <summary>重新构建开发版并输出独立压缩包，不替换正在使用的 dev 目录。</summary>
    private async void PackageDevButton_Click(object sender, RoutedEventArgs e) =>
        await RunScriptAsync("打包开发版本", "build-dev.ps1", "-Package");

    /// <summary>查看与普通版本隔离存放的开发版压缩包。</summary>
    private void OpenDevPackageButton_Click(object sender, RoutedEventArgs e) =>
        OpenOutput("开发版打包文件路径", Path.Combine("artifacts", "dev-dist"), true);

    /// <summary>启动所选源码仓库生成的普通版本。</summary>
    private void OpenNormalButton_Click(object sender, RoutedEventArgs e) =>
        OpenOutput("普通版本", Path.Combine("artifacts", "publish", "GGEA.exe"), false);

    /// <summary>在资源管理器中查看普通包与正式候选包所在目录。</summary>
    private void OpenPackageButton_Click(object sender, RoutedEventArgs e) =>
        OpenOutput("打包文件路径", Path.Combine("artifacts", "dist"), true);

    /// <summary>启动所选源码仓库生成的开发版本。</summary>
    private void OpenDevButton_Click(object sender, RoutedEventArgs e) =>
        OpenOutput("开发版本", Path.Combine("dev", "GGEA.exe"), false);

    /// <summary>只打开已有产物，并以产物目录为工作目录；缺失或启动失败时保留明确反馈。</summary>
    private void OpenOutput(string title, string relativePath, bool directory)
    {
        if (_busy) return;
        try
        {
            string path = Path.GetFullPath(Path.Combine(SourceRoot, relativePath));
            if (!(directory ? Directory.Exists(path) : File.Exists(path)))
                throw new FileNotFoundException($"{title}尚未生成，请先执行对应的建立或打包操作：{path}");
            using Process? process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = directory ? path : Path.GetDirectoryName(path)!
            });
            StateText.Text = "已打开" + title;
            AppendLog($"已打开{title}：{path}");
        }
        catch (Exception ex)
        {
            StateText.Text = "打开" + title + "失败";
            AppendLog($"打开{title}失败：{ex.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try { _runningProcess?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* 进程已经结束，等待退出回调更新界面。 */ }
    }
}
