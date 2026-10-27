using GGEA.App.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Logging;
using Microsoft.Win32;
using GGEA.App.Updates;

namespace GGEA.App.UI;

/// <summary>
/// 设置窗口：提供 GitHub 在线更新、ADB 路径及模拟器配置。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly IGgeaLog _log;
    private AppConfig _config;
    private string? _previousEmulatorType;

    /// <summary>模拟器类型下拉项。</summary>
    private sealed record EmulatorTypeItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>保存后的最新配置，主窗口据此重建服务。</summary>
    public AppConfig Config => _config;

    /// <summary>主窗口仅在更新器就绪后关闭应用。</summary>
    public bool UpdateRequested => Updates.UpdateRequested;

    public SettingsWindow(AppConfig config, IGgeaLog log, bool allowUpdateInstall = false,
        UpdateRelease? availableUpdate = null, Action<bool>? saveUpdatePreference = null, Plugins.PluginManager? plugins = null)
    {
        InitializeComponent();
        _config = config;
        _log = log;
        PluginSettings.Initialize(plugins);
        UiLanguageCombo.ItemsSource = GGEA.App.Localization.UiLanguages.All;
        UiLanguageCombo.SelectedItem = GGEA.App.Localization.UiLanguages.All.First(option =>
            option.Id == GGEA.App.Localization.UiLanguages.Resolve(config.UiLanguage, System.Globalization.CultureInfo.CurrentUICulture));
        Updates.Initialize(log, allowUpdateInstall, availableUpdate, config.AutoInstallUpdates, enabled =>
        {
            // 仅更新软件更新偏好；其他编辑仍由底部“保存”负责，取消窗口也不会回滚即时偏好。
            if (saveUpdatePreference is not null) saveUpdatePreference(enabled);
            else new ConfigService(_log).SaveAutoInstallUpdates(enabled,
                Path.Combine(AppContext.BaseDirectory, "config", "ggea.config.json"));
            _config.AutoInstallUpdates = enabled;
        });
        Updates.InstallationReady += (_, _) => DialogResult = false;
        foreach (var path in (config.AdbPaths ?? []).Append(config.AdbPath).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            AdbPathBox.Items.Add(path);
        SelectedAdbPath = config.AdbPath;
        EmulatorPathBox.Text = config.EmulatorPath;
        EmulatorArgsBox.Text = config.EmulatorArgs;
        GameProfiles.Normalize(config);
        GameServerCombo.ItemsSource = GameProfiles.Servers;
        GameServerCombo.SelectedItem = GameProfiles.Servers.Single(s => s.Id == config.GameServer);
        // 旧配置中的未适配语言仅在编辑器回退；用户保存之前不覆盖原有选择。
        var selectableLanguages = GameProfiles.SelectableLanguagesForServer(config.GameServer);
        GameLanguageCombo.SelectedItem = selectableLanguages.FirstOrDefault(l => l.Id == config.GameLanguage)
            ?? selectableLanguages[0];
        AllowGgeaDownloadCheckBox.IsChecked = config.AllowGgeaDownload;
        EnableWindowsNotificationsCheckBox.IsChecked = config.EnableWindowsNotifications;

        // 下拉项由模拟器档案表驱动：新增模拟器时只需在 EmulatorTypes.Profiles 追加记录
        EmulatorTypeCombo.ItemsSource = EmulatorTypes.Profiles
            .Select(p => new EmulatorTypeItem(p.Type, L.Text(p.DisplayName)))
            .ToArray();
        EmulatorTypeCombo.SelectedItem = EmulatorTypeCombo.Items
            .Cast<EmulatorTypeItem>()
            .FirstOrDefault(i => i.Value == config.EmulatorType)
            ?? EmulatorTypeCombo.Items.Cast<EmulatorTypeItem>()
                .Single(i => i.Value == EmulatorTypes.Custom);
        ApplyEmulatorTypePreset();
    }

    /// <summary>统一读写选择项；手动浏览与自动配对都进入当前编辑列表。</summary>
    private string SelectedAdbPath
    {
        get => AdbPathBox.SelectedItem as string ?? "";
        set
        {
            var existing = AdbPathBox.Items.Cast<string>().FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
            if (existing is null) AdbPathBox.Items.Add(value);
            AdbPathBox.SelectedItem = existing ?? value;
        }
    }

    /// <summary>切换候选时同步模拟器类型，清除上一安装的启动路径，避免跨安装混用。</summary>
    private void AdbPathBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_config is null || EmulatorTypeCombo.Items.Count == 0) return;
        var type = EmulatorTypes.DetectFromAdbPath(SelectedAdbPath) ?? EmulatorTypes.Custom;
        EmulatorPathBox.Text = AdbDiscovery.FindLauncher(SelectedAdbPath, type) ?? "";
        EmulatorArgsBox.Text = "";
        EmulatorTypeCombo.SelectedItem = EmulatorTypeCombo.Items.Cast<EmulatorTypeItem>().First(i => i.Value == type);
        ApplyEmulatorTypePreset();
    }

    /// <summary>浏览已知命名的 ADB 客户端，具体可用性由“测试”按钮验证。</summary>
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Text("选择 ADB 可执行文件"),
            Filter = L.Format($"ADB 可执行文件|{string.Join(';', EmulatorTypes.AdbClientFileNames)}"),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            if (!EmulatorTypes.IsSupportedAdbPath(dialog.FileName))
            {
                StatusText.Text = L.Format($"支持的 ADB 文件名：{string.Join('、', EmulatorTypes.AdbClientFileNames)}");
                return;
            }

            SelectedAdbPath = dialog.FileName;
            // ADB 选择为 MuMu / 雷电 / BlueStacks 时，自动设置对应的模拟器类型
            AutoDetectEmulatorTypeFromAdb();
        }
    }

    /// <summary>打开文件对话框选择模拟器主程序。</summary>
    private void BrowseEmulatorButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Text("选择模拟器主程序"),
            Filter = L.Text("可执行文件 (*.exe)|*.exe|所有文件|*.*"),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            EmulatorPathBox.Text = dialog.FileName;
            AutoDetectEmulatorTypeFromLauncher();
        }
    }

    /// <summary>切换模拟器类型时自动补全 MuMu 的路径与参数。</summary>
    private void EmulatorTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmulatorTypeCombo.SelectedItem is EmulatorTypeItem item)
        {
            // 用户或自动识别切换品牌时清理上一品牌的启动参数；首次加载配置时保持用户原值。
            if (_previousEmulatorType is not null &&
                !string.Equals(_previousEmulatorType, item.Value, StringComparison.OrdinalIgnoreCase))
            {
                EmulatorArgsBox.Text = EmulatorTypes.GetDefaultArgs(item.Value);
            }

            _previousEmulatorType = item.Value;
        }

        ApplyEmulatorTypePreset();
    }

    /// <summary>
    /// 按所选模拟器类型预填启动参数（来自档案表默认值）：
    /// MuMu 额外自动检测安装路径。
    /// </summary>
    private void ApplyEmulatorTypePreset()
    {
        if (EmulatorTypeCombo.SelectedItem is not EmulatorTypeItem item)
        {
            return;
        }

        if (item.Value == EmulatorTypes.MuMu)
        {
            if (string.IsNullOrWhiteSpace(EmulatorPathBox.Text))
            {
                var detected = AdbDiscovery.FindLauncher(SelectedAdbPath, item.Value);
                if (!string.IsNullOrEmpty(detected))
                {
                    EmulatorPathBox.Text = detected;
                }
            }
        }
        else if (item.Value == EmulatorTypes.BlueStacks)
        {
            var installation = EmulatorLocator.FindBlueStacksNearPaths(
                                   SelectedAdbPath,
                                   EmulatorPathBox.Text)
                               ?? (!Path.IsPathRooted(SelectedAdbPath) ? EmulatorLocator.FindBlueStacksInstallation() : null);
            if (installation is not null)
            {
                // BlueStacks 的 ADB 与播放器必须来自同一安装，避免混用其它 ADB 服务端。
                SelectedAdbPath = installation.AdbPath;
                EmulatorPathBox.Text = installation.LauncherPath;
            }
        }

        var defaultArgs = EmulatorTypes.GetDefaultArgs(item.Value);
        if (string.IsNullOrWhiteSpace(EmulatorArgsBox.Text) &&
            !string.IsNullOrWhiteSpace(defaultArgs))
        {
            EmulatorArgsBox.Text = defaultArgs;
        }
    }

    /// <summary>
    /// 根据当前 ADB 路径自动识别模拟器类型并同步到下拉框。
    /// 仅当识别到已知模拟器且与当前选择不同时才切换，并给出提示。
    /// </summary>
    private void AutoDetectEmulatorTypeFromAdb()
    {
        var detected = EmulatorTypes.DetectFromAdbPath(SelectedAdbPath);
        if (detected is null)
        {
            return;
        }

        var item = EmulatorTypeCombo.Items
            .Cast<EmulatorTypeItem>()
            .FirstOrDefault(i => i.Value == detected);
        if (item is not null && !Equals(EmulatorTypeCombo.SelectedItem, item))
        {
            EmulatorTypeCombo.SelectedItem = item;
            StatusText.Text = L.Format($"已根据 ADB 路径自动识别为: {EmulatorTypes.GetProfile(detected).DisplayName}");
            _log.Info(L.Format($"根据 ADB 路径自动识别模拟器类型: {EmulatorTypes.GetProfile(detected).DisplayName}"));
        }
    }

    /// <summary>根据启动程序路径识别模拟器，并让档案预设补全配套 ADB。</summary>
    private void AutoDetectEmulatorTypeFromLauncher()
    {
        var detected = EmulatorTypes.DetectFromEmulatorPath(EmulatorPathBox.Text);
        if (detected is null)
        {
            return;
        }

        var item = EmulatorTypeCombo.Items
            .Cast<EmulatorTypeItem>()
            .FirstOrDefault(candidate => candidate.Value == detected);
        if (item is not null)
        {
            EmulatorTypeCombo.SelectedItem = item;
            ApplyEmulatorTypePreset();
            StatusText.Text = L.Format($"已根据模拟器路径识别为: {EmulatorTypes.GetProfile(detected).DisplayName}");
        }
    }

    /// <summary>测试当前输入的 ADB 路径是否可用（执行 adb version）。</summary>
    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedAdbPath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = L.Text("请输入 ADB 路径");
            return;
        }

        if (!EmulatorTypes.IsSupportedAdbPath(path))
        {
            StatusText.Text = L.Format($"支持 adb 或以下文件名：{string.Join('、', EmulatorTypes.AdbClientFileNames)}");
            return;
        }

        TestButton.IsEnabled = false;
        StatusText.Text = L.Text("正在测试…");
        try
        {
            var adb = new AdbService(path, _log);
            var version = await adb.GetVersionAsync();
            var firstLine = version.Split('\n')[0].Trim();
            StatusText.Text = L.Format($"可用: {firstLine}");
            _log.Info(L.Format($"ADB 测试成功: {firstLine}"));
        }
        catch (Exception ex)
        {
            StatusText.Text = L.Format($"不可用: {ex.Message}");
            _log.Error(L.Format($"ADB 测试失败: {ex.Message}"));
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>保存配置到 config/ggea.config.json 并关闭窗口。</summary>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedAdbPath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = L.Text("ADB 路径不能为空");
            return;
        }

        if (!EmulatorTypes.IsSupportedAdbPath(path))
        {
            StatusText.Text = L.Format($"ADB 路径须使用 adb 或以下文件名：{string.Join('、', EmulatorTypes.AdbClientFileNames)}");
            return;
        }

        try
        {
            if (Path.IsPathRooted(path) && !File.Exists(path))
            {
                StatusText.Text = L.Text("ADB 文件已不存在，请重新浏览选择。");
                return;
            }
            _config.AdbPath = path;
            _config.AdbPaths = AdbPathBox.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _config.AdbDiscoveryCompleted = true;
            // 若尚未明确选择类型（自定义），则根据 ADB 路径自动识别模拟器类型
            var selectedType = (EmulatorTypeCombo.SelectedItem as EmulatorTypeItem)?.Value ?? EmulatorTypes.Custom;
            if (selectedType == EmulatorTypes.Custom)
            {
                AutoDetectEmulatorTypeFromAdb();
                selectedType = (EmulatorTypeCombo.SelectedItem as EmulatorTypeItem)?.Value ?? EmulatorTypes.Custom;
            }

            _config.EmulatorType = selectedType;
            _config.EmulatorPath = EmulatorPathBox.Text.Trim();
            _config.EmulatorArgs = EmulatorArgsBox.Text.Trim();
            _config.GameServer = ((GameProfiles.Option)GameServerCombo.SelectedItem).Id;
            _config.UiLanguage = ((GGEA.App.Localization.UiLanguages.Option)UiLanguageCombo.SelectedItem).Id;
            _config.GameLanguage = ((GameProfiles.Option)GameLanguageCombo.SelectedItem).Id;
            GameProfiles.Normalize(_config);
            _config.AllowGgeaDownload = AllowGgeaDownloadCheckBox.IsChecked == true;
            _config.EnableWindowsNotifications = EnableWindowsNotificationsCheckBox.IsChecked == true;
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "ggea.config.json");
            PluginSettings.Save();
            new ConfigService(_log).Save(_config, configPath);
            _log.Info(L.Format($"设置已保存: ADB 路径 = {path}, 模拟器 = {_config.EmulatorPath}, 游戏包 = {_config.GamePackage}"));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = L.Format($"保存失败: {ex.Message}");
            _log.Error(L.Format($"保存设置失败: {ex.Message}"));
        }
    }

    /// <summary>切换区服只提供该区服支持的语言，未保存时不改变运行配置。</summary>
    private void GameServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameServerCombo.SelectedItem is not GameProfiles.Option server) return;
        var previous = (GameLanguageCombo.SelectedItem as GameProfiles.Option)?.Id;
        var languages = GameProfiles.SelectableLanguagesForServer(server.Id);
        GameLanguageCombo.ItemsSource = languages;
        GameLanguageCombo.SelectedItem = languages.FirstOrDefault(language => language.Id == previous) ?? languages[0];
    }

    /// <summary>取消并关闭窗口。</summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
