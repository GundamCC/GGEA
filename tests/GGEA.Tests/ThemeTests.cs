using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>在独立 STA 线程验证实际窗口的主题切换，不显示窗口或触发设备探测。</summary>
[Collection("WpfLanguage")]
public sealed class ThemeTests
{
    /// <summary>切换按钮须更新现有资源并保持输入和任务对象，随后可恢复默认深色。</summary>
    [Fact]
    public void ToggleTheme_UpdatesExistingControls_AndPreservesInput()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new GGEA.App.App();
                app.InitializeComponent();
                VerifyControlGeometry(app);
                var window = new MainWindow();
                VerifyFierceEnemyTasks(window);
                VerifyAutoProgressSelector(window);
                VerifyBallFactoryTab(window);
                VerifyMasterLeagueTab(window);
                VerifyHardColumnAlignment(window);
                VerifyDeviceSelectionState(window);
                VerifyOfflineDeviceRecovery(window);
                VerifyJapanFierceEnemyVisibility();
                var toggle = (Button)window.FindName("ThemeToggleButton");
                var input = (TextBox)window.FindName("AutoFarmCountBox");
                var tasks = (ListBox)window.FindName("DailyTaskList");
                var firstTask = tasks.Items[0];
                input.Text = "27";
                Assert.True(app.IsDarkMode);
                var darkBackground = ((SolidColorBrush)window.Background).Color;
                var darkInput = ((SolidColorBrush)input.Background).Color;
                var darkKeys = app.Resources.MergedDictionaries[0].Keys.Cast<string>().OrderBy(k => k).ToArray();

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(app.IsDarkMode);
                VerifyAutoProgressSelector(window);
                VerifyBallFactoryTab(window);
                VerifyMasterLeagueTab(window);
                Assert.Equal(Visibility.Visible, ((System.Windows.Shapes.Path)window.FindName("BulbRays")).Visibility);
                Assert.NotEqual(darkBackground, ((SolidColorBrush)window.Background).Color);
                Assert.NotEqual(darkInput, ((SolidColorBrush)input.Background).Color);
                Assert.Equal(darkKeys, app.Resources.MergedDictionaries[0].Keys.Cast<string>().OrderBy(k => k).ToArray());
                Assert.Equal("27", input.Text);
                Assert.Same(firstTask, tasks.Items[0]);

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(app.IsDarkMode);
                Assert.Equal(Visibility.Collapsed, ((System.Windows.Shapes.Path)window.FindName("BulbRays")).Visibility);
                Assert.Equal(darkBackground, ((SolidColorBrush)window.Background).Color);
                Assert.Equal(darkInput, ((SolidColorBrush)input.Background).Color);
                Assert.Equal("27", input.Text);
                app.SetTheme(false);
                app.SetTheme(false);
                Assert.False(app.IsDarkMode);
                Assert.NotEqual(darkBackground, ((SolidColorBrush)window.Background).Color);
                app.SetTheme(true);
                VerifyExecutionIsolation();
                VerifyBallFactoryEntrySafety();
                VerifyMissingTargetBlocksDefaultLaunch();
                VerifyTaskShutdown();
                VerifyGameRegionSettings(app);
                VerifyPluginSettings(app);
                VerifyExternalPluginPreview(app);
                VerifyJapaneseLocalization(app);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>真实插件视图验证编辑隔离、保存与两种主题；只渲染窗口，不启动设备探测。</summary>
    private static void VerifyPluginSettings(GGEA.App.App app)
    {
        using var files = new PluginManagerTests.Package();
        files.Enable();
        var manager = new GGEA.App.Plugins.PluginManager(files.Root, new TestLog());
        try
        {
            manager.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default).GetAwaiter().GetResult();
            var config = new GGEA.App.Config.AppConfig();
            var window = new SettingsWindow(config, new TestLog(), plugins: manager);
            var panel = (PluginSettingsPanel)window.FindName("PluginSettings");
            var input = panel.Children.OfType<TextBox>().Single();
            input.Text = "edited locally";
            var path = System.IO.Path.Combine(files.Root, "config/plugins/ggea.sample/settings.json");
            Assert.False(System.IO.File.Exists(path));
            panel.Save();
            Assert.Equal("edited locally", System.Text.Json.JsonSerializer.Deserialize<string>(System.IO.File.ReadAllText(path)));
            RenderLocalizationPreview(window, 584, 660, "plugins-top-offscreen");
            Descendants((FrameworkElement)window.Content).OfType<ScrollViewer>().First().ScrollToVerticalOffset(330);
            RenderLocalizationPreview(window, 584, 660, "plugins-dark-offscreen");
            Assert.True(input.ActualWidth > 200);
            app.SetTheme(false);
            RenderLocalizationPreview(window, 584, 660, "plugins-light-offscreen");
            app.SetTheme(true);
            var empty = new SettingsWindow(config, new TestLog());
            Assert.Contains(((PluginSettingsPanel)empty.FindName("PluginSettings")).Children.OfType<TextBlock>(),
                text => text.Text.Contains("尚未发现插件"));
            RenderLocalizationPreview(empty, 584, 660, "plugins-empty-offscreen");
        }
        finally { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <summary>可选本地插件验收入口：在临时安装目录加载真实 DLL，不让主仓库依赖私有工程。</summary>
    private static void VerifyExternalPluginPreview(GGEA.App.App app)
    {
        var source = Environment.GetEnvironmentVariable("GGEA_PLUGIN_PREVIEW_PACKAGE");
        if (string.IsNullOrEmpty(source)) return;
        var version = typeof(GGEA.App.App).Assembly.GetName().Version!;
        var manifest = GGEA.App.Plugins.PluginManifest.Read(source, version);
        using var files = new PluginManagerTests.Package();
        var target = System.IO.Path.Combine(files.Root, "plugins", manifest.Id);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", System.IO.SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(source, file).Replace('\\', '/');
            var destination = GGEA.Updater.UpdateFiles.SafePath(target, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(file, destination);
        }
        GGEA.Updater.UpdateFiles.WriteJson(System.IO.Path.Combine(files.Root, "config/plugins/enabled.json"), new[] { manifest.Id });
        var manager = new GGEA.App.Plugins.PluginManager(files.Root, new TestLog());
        try
        {
            manager.InitializeAsync(version, "zh-CN", default).GetAwaiter().GetResult();
            Assert.NotNull(manager.Entries.Single(entry => entry.Manifest?.Id == manifest.Id).Instance);
            var window = new SettingsWindow(new GGEA.App.Config.AppConfig(), new TestLog(), plugins: manager);
            var panel = (PluginSettingsPanel)window.FindName("PluginSettings");
            Assert.True(panel.Children.Count > 2);
            RenderLocalizationPreview(window, 584, 660, "external-plugin-top-offscreen");
            Descendants((FrameworkElement)window.Content).OfType<ScrollViewer>().First().ScrollToVerticalOffset(330);
            RenderLocalizationPreview(window, 584, 660, "external-plugin-dark-offscreen");
            app.SetTheme(false);
            RenderLocalizationPreview(window, 584, 660, "external-plugin-light-offscreen");
            app.SetTheme(true);
            // 默认配置必须能够接收并释放设备；测试通道禁止任何未声明的真实 I/O。
            var device = new GGEA.App.Plugins.PluginDevice("fixture", "game.package", "ja", Guid.Empty,
                (_, _) => throw new InvalidOperationException("预览插件不得默认操作设备。"));
            manager.SetDeviceAsync(device, default).GetAwaiter().GetResult();
            manager.SetDeviceAsync(null, default).GetAwaiter().GetResult();
        }
        finally { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <summary>实际控件验证日文静态文案、运行状态、独立语言选项及重启恢复；不触发设备 Loaded。</summary>
    private static void VerifyJapaneseLocalization(GGEA.App.App app)
    {
        var previousLanguage = GGEA.App.Localization.L.Language;
        try
        {
            GGEA.App.Localization.L.Initialize("ja");
            var window = new MainWindow();
            Assert.Equal("設定", ((Button)window.FindName("SettingsButton")).Content);
            Assert.Equal("Yu Gothic UI", window.FontFamily.Source);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var start = (Task)typeof(MainWindow).GetMethod("StartBallFactoryAsync", flags)!.Invoke(window, null)!;
            Assert.True(start.IsCompletedSuccessfully);
            Assert.Equal("先にエミュレーターを接続・選択してください。", ((TextBlock)window.FindName("BallFactoryStatusText")).Text);
            var tabs = (TabControl)window.FindName("MainTabControl");
            tabs.SelectedItem = window.FindName("AutoProgressTab");
            RenderLocalizationPreview(window, 1280, 780, "main-ja-offscreen");
            RenderLocalizationPreview(window, 1064, 660, "main-ja-min-offscreen");
            var config = new GGEA.App.Config.AppConfig { UiLanguage = "ja", GameLanguage = "zh-CN", AdbPath = "adb" };
            var settings = new SettingsWindow(config, new TestLog());
            Assert.Equal("GGEA 設定", settings.Title);
            Assert.Equal("日本語", ((ComboBox)settings.FindName("UiLanguageCombo")).SelectedItem.ToString());
            Assert.Equal("zh-CN", ((GGEA.App.Config.GameProfiles.Option)((ComboBox)settings.FindName("GameLanguageCombo")).SelectedItem).Id);
            RenderLocalizationPreview(settings, 584, 660, "settings-ja-offscreen");
            var settingsContent = (FrameworkElement)settings.Content;
            var scroll = Descendants(settingsContent).OfType<ScrollViewer>().First();
            scroll.ScrollToEnd();
            settingsContent.UpdateLayout();
            RenderLocalizationPreview(settings, 584, 660, "settings-ja-bottom-offscreen");
            // 选择不同语言但未保存，不得修改运行配置；当前界面保持启动时语言。
            ((ComboBox)settings.FindName("UiLanguageCombo")).SelectedIndex = 0;
            Assert.Equal("ja", config.UiLanguage);
            Assert.Equal("ja", GGEA.App.Localization.L.Language);
            app.SetTheme(false);
            RenderLocalizationPreview(window, 1280, 780, "main-ja-light-offscreen");
            app.SetTheme(true);
            GGEA.App.Localization.L.Initialize("zh-CN");
            var restarted = new MainWindow();
            Assert.Equal("设置", ((Button)restarted.FindName("SettingsButton")).Content);
            Assert.Equal("Microsoft YaHei UI", restarted.FontFamily.Source);
            RenderLocalizationPreview(restarted, 1280, 780, "main-zh-offscreen");
        }
        finally { GGEA.App.Localization.L.Initialize(previousLanguage); }
    }

    /// <summary>仅在提供预览目录时渲染真实 WPF 内容；文件名明确标识离屏证据。</summary>
    private static void RenderLocalizationPreview(Window window, int width, int height, string name)
    {
        var output = Environment.GetEnvironmentVariable("GGEA_UI_PREVIEW_DIR");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        if (string.IsNullOrEmpty(output)) return;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // 内容根本身透明，先绘制窗口的真实背景色，避免离屏 PNG 在浅色主题中显示黑底。
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(output);
        using var stream = System.IO.File.Create(System.IO.Path.Combine(output, name + ".png"));
        encoder.Save(stream);
    }

    /// <summary>日服隐藏强敌入口与旧队列但不删数据；切回国际服恢复，隐藏任务不进入执行快照。</summary>
    private static void VerifyJapanFierceEnemyVisibility()
    {
        var window = new MainWindow();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Invoke(string name) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, null);
        var config = (GGEA.App.Config.AppConfig)Get("_config");
        var daily = (System.Collections.ObjectModel.ObservableCollection<GGEA.App.Tasks.DailyTaskItem>)Get("_dailyTasks");
        var queue = (System.Collections.ObjectModel.ObservableCollection<GGEA.App.Tasks.AutoProgressTarget>)Get("_autoProgressQueue");
        var enemy = new GGEA.App.Tasks.AutoProgressTarget(null, GGEA.App.Tasks.FierceEnemyUnit.Latest);
        var main = new GGEA.App.Tasks.AutoProgressTarget(new GGEA.App.Config.HardStageEntry { Id = 2, Work = "主线测试目标" }, null);
        queue.Add(enemy);
        queue.Add(main);
        var dailyEnemy = new GGEA.App.Tasks.DailyTaskItem { Kind = GGEA.App.Tasks.DailyTaskKind.FierceEnemySkip,
            Name = "强敌来袭略过", IsChecked = true };
        daily.Add(dailyEnemy);
        var tabs = (TabControl)window.FindName("MainTabControl");
        var enemyTab = (TabItem)window.FindName("FierceEnemyAssaultTab");
        var type = (ComboBox)window.FindName("AutoProgressTypeCombo");
        var enemyType = (ComboBoxItem)window.FindName("AutoProgressFierceEnemyTypeItem");
        tabs.SelectedItem = enemyTab;
        type.SelectedIndex = 2;
        config.GameServer = "japan";
        config.GameLanguage = "ja";
        Invoke("UpdateFooterText");
        Assert.Equal(Visibility.Collapsed, enemyTab.Visibility);
        Assert.Equal(Visibility.Collapsed, enemyType.Visibility);
        Assert.False(enemyType.IsEnabled);
        Assert.Equal(0, type.SelectedIndex);
        Assert.Same(window.FindName("AutoProgressTab"), tabs.SelectedItem);
        var shownQueue = (ListBox)window.FindName("AutoProgressQueueList");
        var shownDaily = (ListBox)window.FindName("DailyTaskList");
        Assert.Same(main, Assert.Single(shownQueue.Items.Cast<object>()));
        Assert.DoesNotContain(dailyEnemy, shownDaily.Items.Cast<object>());
        Assert.Same(main, Assert.Single((GGEA.App.Tasks.AutoProgressTarget[])Invoke("AvailableAutoProgressTargets")!));
        Assert.False((bool)typeof(MainWindow).GetMethod("IsDailyTaskAvailable", flags)!.Invoke(window, [dailyEnemy])!);
        Assert.DoesNotContain("强敌", ((TextBlock)window.FindName("AutoProgressStatusText")).Text);
        var selectAll = (CheckBox)window.FindName("DailySelectAllCheckBox");
        selectAll.IsChecked = false;
        selectAll.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert.True(dailyEnemy.IsChecked); // 隐藏项不参与日服全选，也不能被取消勾选后写回配置。
        var count = daily.Count;
        ((Button)window.FindName("FierceEnemyAddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(count, daily.Count);
        Assert.Same(dailyEnemy, daily.Last());
        RenderServerFeaturePreview(window, "japan");
        config.GameServer = "worldwide";
        config.GameLanguage = "zh-CN";
        Invoke("UpdateFooterText");
        Assert.Equal(Visibility.Visible, enemyTab.Visibility);
        Assert.Equal(Visibility.Visible, enemyType.Visibility);
        Assert.True(enemyType.IsEnabled);
        Assert.Equal(new[] { enemy, main }, shownQueue.Items.Cast<GGEA.App.Tasks.AutoProgressTarget>());
        Assert.Contains(dailyEnemy, shownDaily.Items.Cast<object>());
        Assert.True(dailyEnemy.IsChecked);
        Assert.Equal(2, ((GGEA.App.Tasks.AutoProgressTarget[])Invoke("AvailableAutoProgressTargets")!).Length);
        RenderServerFeaturePreview(window, "worldwide");
    }

    /// <summary>离屏渲染真实主窗口控件，避免触发 Loaded 的设备探测；只在指定预览目录时写图。</summary>
    private static void RenderServerFeaturePreview(MainWindow window, string server)
    {
        var output = Environment.GetEnvironmentVariable("GGEA_UI_PREVIEW_DIR");
        if (string.IsNullOrEmpty(output)) return;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1280, 900));
        content.Arrange(new Rect(0, 0, 1280, 900));
        content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(output);
        using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"server-features-{server}-offscreen.png"));
        encoder.Save(stream);
    }

    /// <summary>验证真实设置控件的区服联动、语言保持与未保存隔离，并可输出实际控件离屏预览。</summary>
    private static void VerifyGameRegionSettings(GGEA.App.App app)
    {
        var config = new GGEA.App.Config.AppConfig
        {
            GameServer = "worldwide", GameLanguage = "zh-HK", GamePackage = GGEA.App.Config.GameProfiles.WorldwidePackage,
            UiLanguage = "ja"
        };
        var settings = new SettingsWindow(config, new TestLog());
        var uiLanguage = (ComboBox)settings.FindName("UiLanguageCombo");
        Assert.Equal("ja", ((GGEA.App.Localization.UiLanguages.Option)uiLanguage.SelectedItem).Id);
        Assert.Equal(new[] { "简体中文", "日本語" }, uiLanguage.Items.Cast<GGEA.App.Localization.UiLanguages.Option>().Select(item => item.ToString()));
        uiLanguage.SelectedIndex = 0;
        Assert.Equal("ja", config.UiLanguage); // 尚未保存的编辑不得改变运行语言或配置。
        var server = (ComboBox)settings.FindName("GameServerCombo");
        var language = (ComboBox)settings.FindName("GameLanguageCombo");
        Assert.Null(settings.FindName("GamePackageBox"));
        Assert.Equal(2, server.Items.Count);
        Assert.Single(language.Items);
        Assert.DoesNotContain(language.Items.Cast<GGEA.App.Config.GameProfiles.Option>(), item => item.Id == "ja");
        Assert.Equal("zh-CN", ((GGEA.App.Config.GameProfiles.Option)language.SelectedItem).Id);
        server.SelectedItem = GGEA.App.Config.GameProfiles.Servers.Single(s => s.Id == "japan");
        Assert.Single(language.Items.Cast<GGEA.App.Config.GameProfiles.Option>());
        Assert.Equal("ja", ((GGEA.App.Config.GameProfiles.Option)language.SelectedItem).Id);
        Assert.Equal("worldwide", config.GameServer);
        Assert.Equal("zh-HK", config.GameLanguage);
        var card = (Border)settings.FindName("GameRegionCard");
        var panel = (StackPanel)card.Parent;
        panel.Children.Remove(card);
        // 只挂载设置卡片，确保控件模板完成布局，不触发主窗口的 ADB Loaded 流程。
        var host = new Window { Content = card, Width = 520, Height = 450, ShowInTaskbar = false };
        host.Show();
        host.UpdateLayout();
        foreach (var dark in new[] { true, false })
        {
            app.SetTheme(dark);
            card.Measure(new Size(480, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 480, card.DesiredSize.Height));
            card.UpdateLayout();
            Assert.InRange(language.ActualWidth, 200, 480);
            Assert.Contains(Descendants(server).OfType<TextBlock>(), t => t.Text == "日服");
            Assert.Contains(Descendants(language).OfType<TextBlock>(), t => t.Text == "日本語");
            var output = Environment.GetEnvironmentVariable("GGEA_UI_PREVIEW_DIR");
            if (string.IsNullOrEmpty(output)) continue;
            System.IO.Directory.CreateDirectory(output);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(480,
                (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"game-region-{(dark ? "dark" : "light")}-offscreen.png"));
            encoder.Save(stream);
        }
        app.SetTheme(true);
        server.SelectedItem = GGEA.App.Config.GameProfiles.Servers.Single(s => s.Id == "worldwide");
        Assert.Single(language.Items);
        Assert.DoesNotContain(language.Items.Cast<GGEA.App.Config.GameProfiles.Option>(), item => item.Id == "ja");
        Assert.Equal("zh-CN", ((GGEA.App.Config.GameProfiles.Option)language.SelectedItem).Id);
        host.Content = null;
        host.Close();
        settings.Close();
    }

    /// <summary>回归类型名显示与下拉选择：验证真实模板、弹出状态及选择后加入队列的作品。</summary>
    private static void VerifyAutoProgressSelector(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("MainTabControl");
        var previous = tabs.SelectedItem;
        tabs.SelectedItem = window.FindName("AutoProgressTab");
        var combo = (ComboBox)window.FindName("AutoProgressWorkCombo");
        var works = new[]
        {
            new GGEA.App.Tasks.AutoProgressTarget(new GGEA.App.Config.HardStageEntry { Id = 20, Work = "Victory Gundam", NormalCount = 20, HardCount = 3 }, null),
            new GGEA.App.Tasks.AutoProgressTarget(new GGEA.App.Config.HardStageEntry { Id = 1, Work = "Mobile Suit Gundam", NormalCount = 14, HardCount = 3 }, null)
        };
        combo.ItemsSource = works;
        combo.SelectedItem = works.MaxBy(work => work.MainWork!.Id);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1280, 780));
        content.Arrange(new Rect(0, 0, 1280, 780));
        content.UpdateLayout();
        Assert.Contains(Descendants(combo).OfType<TextBlock>(), text => text.Text == works[0].Work);
        // ComboBox 在未 Loaded 时会强制关闭弹窗；仅挂载当前页，避免主窗口 Loaded 触发 ADB。
        var tab = (TabItem)window.FindName("AutoProgressTab");
        var page = (FrameworkElement)tab.Content;
        tab.Content = null;
        var host = new Window { Content = page, Width = 900, Height = 550, ShowInTaskbar = false };
        host.Show();
        host.UpdateLayout();
        VerifyAutoProgressOptions(window);
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("ToggleButton", combo);
        toggle.IsChecked = true;
        Assert.True(combo.IsDropDownOpen);
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        Assert.True(popup.IsOpen);
        Assert.Equal(combo.MaxDropDownHeight, ((FrameworkElement)popup.Child).MaxHeight);
        combo.UpdateLayout();
        var second = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1);
        Assert.NotNull(second);
        second.IsSelected = true;
        combo.IsDropDownOpen = false;
        host.UpdateLayout();
        Assert.Same(works[1], combo.SelectedItem);
        Assert.Contains(Descendants(combo).OfType<TextBlock>(), text => text.Text == works[1].Work);
        var add = (Button)window.FindName("AutoProgressAddButton");
        add.IsEnabled = true;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var queue = (ListBox)window.FindName("AutoProgressQueueList");
        Assert.Same(works[1], Assert.Single(queue.Items.Cast<object>()));
        host.UpdateLayout();
        Assert.Contains(Descendants(queue).OfType<TextBlock>(), text => text.Text == "主线 · 普通（含 BOSS）14 关 · 支线 0 关 · HARD 3 关");
        // 离屏渲染只用于布局检查，不冒充真实运行窗口截图。
        var output = Environment.GetEnvironmentVariable("GGEA_UI_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(output))
        {
            System.IO.Directory.CreateDirectory(output);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle((Brush)window.FindResource("Brush.Card"), null, new Rect(0, 0, page.ActualWidth, page.ActualHeight));
                drawing.DrawRectangle(new VisualBrush(page), null, new Rect(0, 0, page.ActualWidth, page.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var theme = ((GGEA.App.App)Application.Current).IsDarkMode ? "dark" : "light";
            using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"auto-progress-{theme}-offscreen.png"));
            encoder.Save(stream);
        }
        Assert.Single(Descendants(queue).OfType<Button>()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(queue.Items);
        combo.ItemsSource = null;
        add.IsEnabled = false;
        host.Content = null;
        host.Close();
        tab.Content = page;
        tabs.SelectedItem = previous;
    }

    /// <summary>在真实控件上操作多选权限，确认两种策略即时回退且重选后不会恢复旧的自动授权。</summary>
    private static void VerifyAutoProgressOptions(MainWindow window)
    {
        var series = (CheckBox)window.FindName("AutoProgressSeriesCheck");
        var free = (CheckBox)window.FindName("AutoProgressFreeCheck");
        var empty = (ComboBox)window.FindName("AutoProgressEmptyActionCombo");
        var terrain = (ComboBox)window.FindName("AutoProgressTerrainActionCombo");
        var teams = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("AutoProgressTeamsButton");
        Assert.True(series.IsChecked);
        Assert.True(free.IsChecked);
        Assert.Equal(1, empty.SelectedIndex);
        Assert.Equal(1, terrain.SelectedIndex);
        teams.IsChecked = true;
        Assert.True(series.IsVisible);
        empty.SelectedIndex = 1;
        terrain.SelectedIndex = 1;
        series.IsChecked = false;
        Assert.Equal(1, empty.SelectedIndex);
        free.IsChecked = false;
        Assert.Equal(0, empty.SelectedIndex);
        Assert.Equal(0, terrain.SelectedIndex);
        Assert.False(((ComboBoxItem)empty.Items[1]).IsEnabled);
        Assert.False(((ComboBoxItem)terrain.Items[1]).IsEnabled);
        series.IsChecked = true;
        free.IsChecked = true;
        Assert.True(((ComboBoxItem)empty.Items[1]).IsEnabled);
        Assert.Equal(0, empty.SelectedIndex);
        Assert.Equal(0, terrain.SelectedIndex);
        teams.IsChecked = false;
        // 明确重新授权，下一次主题切换继续验证自动编制选项的呈现。
        empty.SelectedIndex = 1;
        terrain.SelectedIndex = 1;
    }

    /// <summary>实际按钮更新的配置经磁盘往返后，在新窗口恢复；验证添加、替换、取消勾选及删除。</summary>
    private static void VerifyFierceEnemyRestart(MainWindow window, GGEA.App.Tasks.DailyTaskItem? expected, int index)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var field = typeof(MainWindow).GetField("_config", flags)!;
        var config = (GGEA.App.Config.AppConfig)field.GetValue(window)!;
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ggea-fierce-{Guid.NewGuid():N}.json");
        var service = new GGEA.App.Config.ConfigService(new TestLog());
        MainWindow? restarted = null;
        try
        {
            service.Save(config, path);
            restarted = new MainWindow();
            field.SetValue(restarted, service.Load(path));
            typeof(MainWindow).GetMethod("RestoreFierceEnemyDailyTask", flags)!.Invoke(restarted, null);
            var list = (ListBox)restarted.FindName("DailyTaskList");
            var tasks = list.Items.Cast<GGEA.App.Tasks.DailyTaskItem>().Where(task => task.CanRemove);
            if (expected is null)
            {
                Assert.Null(service.Load(path).FierceEnemyDaily);
                Assert.Empty(tasks);
            }
            else
            {
                var restored = Assert.Single(tasks);
                Assert.Equal(expected.FierceEnemySelection, restored.FierceEnemySelection);
                Assert.Equal(expected.IsChecked, restored.IsChecked);
                Assert.Equal(index, list.Items.IndexOf(restored));
            }
        }
        finally
        {
            restarted?.Close();
            System.IO.File.Delete(path);
        }
    }
    /// <summary>实际按钮验证强敌任务唯一替换、默认勾选、队列位置和删除，不调用 ADB。</summary>
    private static void VerifyFierceEnemyTasks(MainWindow window)
    {
        var list = (ListBox)window.FindName("DailyTaskList");
        var units = (ComboBox)window.FindName("FierceEnemyUnitBox");
        var difficulty = (ComboBox)window.FindName("FierceEnemyDifficultyBox");
        Assert.Equal(GGEA.App.Tasks.FierceEnemyUnit.Latest, units.Items[0]);
        Assert.Equal(GGEA.App.Tasks.FierceEnemyUnit.Load().Max(u => u.Id), ((GGEA.App.Tasks.FierceEnemyUnit)units.SelectedItem).Id);
        var add = (Button)window.FindName("FierceEnemyAddButton");
        var status = (TextBlock)window.FindName("FierceEnemyAssaultStatusText");
        Assert.Equal("执行单次略过", ((Button)window.FindName("FierceEnemyAssaultStartButton")).Content);
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var first = Assert.Single(list.Items.Cast<GGEA.App.Tasks.DailyTaskItem>(), t => t.CanRemove);
        var index = list.Items.IndexOf(first);
        Assert.Equal(GGEA.App.Tasks.DailyTaskKind.ConsumeAp, ((GGEA.App.Tasks.DailyTaskItem)list.Items[index + 1]).Kind);
        VerifyFierceEnemyRestart(window, first, index);
        first.IsChecked = false;
        VerifyFierceEnemyRestart(window, first, index);
        units.SelectedIndex = 0;
        difficulty.SelectedIndex = 2;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var replacement = Assert.Single(list.Items.Cast<GGEA.App.Tasks.DailyTaskItem>(), t => t.CanRemove);
        Assert.NotSame(first, replacement);
        Assert.Equal(index, list.Items.IndexOf(replacement));
        Assert.True(replacement.IsChecked);
        Assert.Equal(units.SelectedItem, replacement.FierceEnemySelection!.Unit);
        Assert.Equal(GGEA.App.Tasks.FierceEnemyDifficulty.NORMAL, replacement.FierceEnemySelection.Difficulty);
        Assert.StartsWith("已替换", status.Text);
        VerifyFierceEnemyRestart(window, replacement, index);
        list.Measure(new Size(320, 700));
        list.Arrange(new Rect(0, 0, 320, 700));
        list.UpdateLayout();
        // 名称和行空白均跳转强敌页；删除图标仍独立，不变成齿轮。
        var tabs = (TabControl)window.FindName("MainTabControl");
        var originalTab = tabs.SelectedItem ?? tabs.Items[0];
        var fierceTab = (TabItem)window.FindName("FierceEnemyAssaultTab");
        var name = Assert.Single(Descendants(list).OfType<TextBlock>(), t => t.Text == "强敌来袭略过");
        name.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.True(fierceTab.IsSelected);
        tabs.SelectedItem = originalTab;
        var row = Assert.Single(Descendants(list).OfType<Border>(), b => ReferenceEquals(b.DataContext, replacement) && b.Child is Grid);
        row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.True(fierceTab.IsSelected);
        Assert.False(replacement.HasSettings);
        tabs.SelectedItem = originalTab;
        var remove = Assert.Single(Descendants(list).OfType<Button>(), b => System.Windows.Automation.AutomationProperties.GetName(b) == "删除强敌来袭任务" && b.Visibility == Visibility.Visible);
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(list.Items.Cast<GGEA.App.Tasks.DailyTaskItem>(), t => t.CanRemove);
        Assert.Contains("已删除", status.Text);
        VerifyFierceEnemyRestart(window, null, 0);
        Assert.True(((CheckBox)window.FindName("DailySelectAllCheckBox")).IsChecked);
    }

    /// <summary>用实际 WPF 控件验证工厂页签顺序、切换和窄窗口布局；可选导出离屏渲染供视觉检查。</summary>
    private static void VerifyBallFactoryTab(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("MainTabControl");
        var scout = (TabItem)window.FindName("AutoScoutTab");
        var factory = (TabItem)window.FindName("BallFactoryTab");
        // 离屏窗口尚未执行首次布局，默认首项可能尚未被选中；恢复时明确回到首项。
        var previous = tabs.SelectedItem ?? tabs.Items[0];
        var progress = (TabItem)window.FindName("AutoProgressTab");
        Assert.Equal(tabs.Items.IndexOf(scout) + 1, tabs.Items.IndexOf(progress));
        var fierce = (TabItem)window.FindName("FierceEnemyAssaultTab");
        Assert.Equal(tabs.Items.IndexOf(progress) + 1, tabs.Items.IndexOf(fierce));
        var league = (TabItem)window.FindName("MasterLeagueTab");
        Assert.Equal(tabs.Items.IndexOf(fierce) + 1, tabs.Items.IndexOf(league));
        Assert.Equal(tabs.Items.IndexOf(league) + 1, tabs.Items.IndexOf(factory));
        Assert.Equal(Visibility.Visible, factory.Visibility);
        tabs.SelectedItem = factory;
        VerifyBallFactoryGpInput(window);
        var content = (FrameworkElement)window.Content;
        foreach (var width in new[] { 1080, 1280 })
        {
            content.Measure(new Size(width, 780));
            content.Arrange(new Rect(0, 0, width, 780));
            content.UpdateLayout();
            Assert.Same(factory.Content, tabs.SelectedContent);
            var panel = (StackPanel)factory.Content;
            Assert.Equal("开始制造", Assert.Single(Descendants(panel).OfType<Button>()).Content);
            Assert.Equal("请在非战斗界面使用。", ((TextBlock)panel.Children[0]).Text);
            Assert.Contains(Descendants(panel).OfType<TextBlock>(), t => t.Text == "尚未开始。");
            foreach (var text in Descendants(panel).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible))
            {
                Assert.True(text.ActualHeight > 0);
                Assert.InRange(text.ActualWidth, 1, panel.ActualWidth);
            }
            // 仅显式要求时输出真实控件离屏渲染，不将其冒充桌面截图。
            var output = Environment.GetEnvironmentVariable("GGEA_VISUAL_OUTPUT_DIRECTORY");
            if (!string.IsNullOrEmpty(output))
            {
                System.IO.Directory.CreateDirectory(output);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 780, 96, 96, PixelFormats.Pbgra32);
                // 窗口背景不属于 Content；按实际主题一并渲染，避免透明预览显示成黑底。
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, 780));
                    drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, 780));
                }
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var theme = ((GGEA.App.App)Application.Current).IsDarkMode ? "dark" : "light";
                using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"ball-factory-{theme}-{width}.png"));
                encoder.Save(stream);
            }
        }
        tabs.SelectedItem = previous;
    }

    /// <summary>验证真实输入控件的默认值、上下限、非法输入反馈及提交恢复，不启动游戏操作。</summary>
    private static void VerifyBallFactoryGpInput(MainWindow window)
    {
        var input = (TextBox)window.FindName("BallFactoryGpBox");
        var hint = (TextBlock)window.FindName("BallFactoryGpHint");
        Assert.Equal("30000", input.Text);
        foreach (var (text, expected) in new[]
        {
            ("20", "20"), ("100000", "100000"), ("21", "20"), ("29", "20"),
            ("30", "40"), ("31", "40"), ("30009", "30000"), ("30010", "30020"),
            ("30011", "30020"), ("99989", "99980"), ("99990", "100000"),
            ("99999", "100000"), ("30000", "30000"),
        })
        {
            input.Text = text;
            input.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(expected, input.Text);
            // 再次提交保持稳定，不在失焦与 Enter 重复触发时继续改变目标。
            input.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(expected, input.Text);
            Assert.DoesNotContain("恢复", hint.Text);
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
        }
        foreach (var text in new[] { "", "19", "100001", "999999999999999999", "-20", "20.5", "abc", " 20" })
        {
            input.Text = text;
            Assert.Contains("恢复", hint.Text);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            input.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal("30000", input.Text);
        }
        foreach (var text in new[] { "abc", "20.5", "-20", "20 GP", "100000" })
        {
            var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText)
            { RoutedEvent = DataObject.PastingEvent };
            input.RaiseEvent(paste);
            Assert.Equal(text != "100000", paste.CommandCancelled);
        }
    }

    /// <summary>验证大师联盟真实控件的初始值、任务互斥与取消，并按需导出明暗主题离屏预览。</summary>
    private static void VerifyMasterLeagueTab(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("MainTabControl");
        var previous = tabs.SelectedItem ?? tabs.Items[0];
        var league = (TabItem)window.FindName("MasterLeagueTab");
        var check = (CheckBox)window.FindName("MasterLeagueAutoFormationCheck");
        var retry = (CheckBox)window.FindName("MasterLeagueRetryDefeatedCheck");
        Assert.False(retry.IsChecked);
        retry.IsChecked = true;
        Assert.True(retry.IsChecked);
        retry.IsChecked = false;
        var button = (Button)window.FindName("MasterLeagueStartButton");
        var panel = (StackPanel)league.Content;
        Assert.False(check.IsChecked);
        Assert.Equal("请在非战斗界面使用", ((TextBlock)panel.Children[0]).Text);
        var optionRow = (StackPanel)panel.Children[1];
        Assert.Same(check, optionRow.Children[0]);
        Assert.Equal("进行自动编制", ((Label)optionRow.Children[1]).Content);
        Assert.Equal("刷新挑战失败的对手", ((Label)((StackPanel)panel.Children[2]).Children[1]).Content);
        check.IsChecked = true;
        Assert.True(check.IsChecked);
        check.IsChecked = false;
        tabs.SelectedItem = league;
        var content = (FrameworkElement)window.Content;
        foreach (var width in new[] { 1080, 1280 })
        {
            content.Measure(new Size(width, 780));
            content.Arrange(new Rect(0, 0, width, 780));
            content.UpdateLayout();
            Assert.Same(league.Content, tabs.SelectedContent);
            Assert.True(check.ActualHeight > 0);
            Assert.InRange(check.ActualWidth, 1, panel.ActualWidth);
            var output = Environment.GetEnvironmentVariable("GGEA_VISUAL_OUTPUT_DIRECTORY");
            if (!string.IsNullOrEmpty(output))
            {
                System.IO.Directory.CreateDirectory(output);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, 780));
                    drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, 780));
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 780, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var theme = ((GGEA.App.App)Application.Current).IsDarkMode ? "dark" : "light";
                using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"master-league-{theme}-{width}.png"));
                encoder.Save(stream);
            }
        }
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Invoke(string name) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, null);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        // 未连接设备时只更新提示；其他任务持有租约时禁止新的联盟操作。
        Assert.True(((Task)Invoke("StartMasterLeagueAsync")!).IsCompletedSuccessfully);
        Assert.Contains("连接", ((TextBlock)window.FindName("MasterLeagueStatusText")).Text);
        using (var lease = (IDisposable)Invoke("TryBeginExecution")!)
        {
            Assert.False(check.IsEnabled);
            Assert.False(retry.IsEnabled);
            Assert.False(button.IsEnabled);
            using var cancel = new CancellationTokenSource();
            Set("_masterLeagueCts", cancel);
            Invoke("UpdateBusyState");
            Assert.True(button.IsEnabled);
            Assert.False(((Button)window.FindName("FierceEnemyAssaultStartButton")).IsEnabled);
            Assert.True(((Task)Invoke("StartMasterLeagueAsync")!).IsCompletedSuccessfully);
            Assert.True(cancel.IsCancellationRequested);
            Assert.False(button.IsEnabled);
            Set("_masterLeagueCts", null);
        }
        button.Content = "开始大师联盟";
        ((TextBlock)window.FindName("MasterLeagueStatusText")).Text = "尚未开始。";
        Assert.True(check.IsEnabled);
        Assert.True(button.IsEnabled);
        tabs.SelectedItem = previous;
    }

    /// <summary>真实入口验证缺少设备、任务互斥及中止；不接触游戏或启动额外执行。</summary>
    private static void VerifyBallFactoryEntrySafety()
    {
        var window = new MainWindow();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Invoke(string name) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, null);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        var input = (TextBox)window.FindName("BallFactoryGpBox");
        var button = (Button)window.FindName("BallFactoryStartButton");
        var status = (TextBlock)window.FindName("BallFactoryStatusText");
        input.Text = "30010";
        Assert.True(((Task)Invoke("StartBallFactoryAsync")!).IsCompletedSuccessfully);
        Assert.Equal("30020", input.Text);
        Assert.Contains("连接", status.Text);
        Assert.True(button.IsEnabled);
        using (var lease = (IDisposable)Invoke("TryBeginExecution")!)
        {
            Assert.False(input.IsEnabled);
            Assert.False(button.IsEnabled);
            status.Text = "existing-operation";
            Assert.True(((Task)Invoke("StartBallFactoryAsync")!).IsCompletedSuccessfully);
            Assert.Equal("existing-operation", status.Text);
            using var cancel = new CancellationTokenSource();
            Set("_ballFactoryRunning", true);
            Set("_ballFactoryCts", cancel);
            Invoke("UpdateBusyState");
            Assert.True(button.IsEnabled);
            Assert.False(((Button)window.FindName("DailyStartButton")).IsEnabled);
            Assert.False(((Button)window.FindName("ScoutStartButton")).IsEnabled);
            Assert.True(((Task)Invoke("StartBallFactoryAsync")!).IsCompletedSuccessfully);
            Assert.True(cancel.IsCancellationRequested);
            Assert.Equal("正在停止…", button.Content);
            Assert.False(button.IsEnabled);
            Set("_ballFactoryRunning", false);
            Set("_ballFactoryCts", null);
        }
        Assert.True(input.IsEnabled);
        Assert.True(button.IsEnabled);
    }

    /// <summary>绕过禁用控件直接调用入口，验证设备、配置和执行租约仍不改变。</summary>
    private static void VerifyExecutionIsolation()
    {
        var window = new MainWindow();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Invoke(string name, params object[] args) =>
            typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        var combo = (ComboBox)window.FindName("DeviceCombo");
        var devices = new[]
        {
            new GGEA.App.Device.AdbDevice("device-a", "device", "") { Brand = "MuMu" },
            new GGEA.App.Device.AdbDevice("device-b", "device", "") { Brand = "雷电" },
        };
        combo.ItemsSource = devices;
        combo.SelectedItem = devices[0];
        var originalConfig = (GGEA.App.Config.AppConfig)Get("_config")!;
        using (var lease = (IDisposable)Invoke("TryBeginExecution")!)
        {
            var snapshot = (GGEA.App.Config.AppConfig)Get("_config")!;
            Assert.NotSame(originalConfig, snapshot);
            originalConfig.AdbPath = "changed-outside-session";
            Assert.NotEqual(originalConfig.AdbPath, snapshot.AdbPath);
            Assert.False(combo.IsEnabled);
            Assert.False(((Button)window.FindName("SettingsButton")).IsEnabled);
            Assert.False(((Button)window.FindName("AutoProgressStartButton")).IsEnabled);
            Assert.False(((Grid)window.FindName("AutoProgressOptionsPanel")).IsEnabled);
            Assert.True(((Task)Invoke("StartAutoProgressAsync")!).IsCompletedSuccessfully);
            Assert.True(((Button)window.FindName("ThemeToggleButton")).IsEnabled);
            combo.SelectedItem = devices[1];
            Assert.Same(devices[0], combo.SelectedItem);
            Assert.Equal("device-a", Get("_preferredDeviceSerial"));
            foreach (var name in new[] { "SettingsButton_Click", "RefreshButton_Click", "DailyStartButton_Click",
                         "AutoFarmStartButton_Click", "StartGameButton_Click", "ScreenshotButton_Click",
                         "TapButton_Click", "SwipeButton_Click", "TestTemplateButton_Click" })
            {
                var task = (Task)Invoke(name + "CoreAsync", window, new RoutedEventArgs())!;
                Assert.True(task.IsCompletedSuccessfully);
                Assert.Same(snapshot, Get("_config"));
            }
            // 内部刷新/收尾更新 UI 不拥有租约，不能提前开放自动化入口。
            Invoke("UpdateBusyState");
            Assert.Null(Invoke("TryBeginExecution"));
            Assert.False(((Button)window.FindName("DailyStartButton")).IsEnabled);
            ((Button)window.FindName("ThemeToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(snapshot, Get("_config"));
        }
        Assert.True(combo.IsEnabled);
        Assert.True(((Button)window.FindName("SettingsButton")).IsEnabled);
        using var next = (IDisposable?)Invoke("TryBeginExecution");
        Assert.NotNull(next);
    }

    /// <summary>原目标消失后，真实按钮入口必须保留目标意图并拒绝默认启动，不能接触其他实例。</summary>
    private static void VerifyMissingTargetBlocksDefaultLaunch()
    {
        var window = new MainWindow();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        var adb = new FakeAdbService([]);
        var launched = false;
        Set("_adb", new GGEA.App.Device.AdbService("must-not-run.exe", new TestLog()));
        Set("_emulator", new GGEA.App.Device.EmulatorService(adb, new TestLog(), (_, _) => launched = true));
        Set("_preferredDeviceSerial", "missing-target");
        var task = (Task)typeof(MainWindow).GetMethod("StartGameButton_ClickCoreAsync", flags)!
            .Invoke(window, [window, new RoutedEventArgs()])!;
        Assert.True(task.IsCompletedSuccessfully);
        Assert.False(launched);
        Assert.Empty(adb.LaunchedApps);
        Assert.Null(((ComboBox)window.FindName("DeviceCombo")).SelectedItem);
        Assert.Contains("明确选择", ((TextBlock)window.FindName("ConnectionStateText")).Text);
        Assert.Equal("missing-target", typeof(MainWindow).GetField("_preferredDeviceSerial", flags)!.GetValue(window));
    }

    /// <summary>在真实 WPF 控件上验证停止按钮、重启门禁及关闭等待期间的状态。</summary>
    private static void VerifyTaskShutdown()
    {
        using var context = new TestUiContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Exception? failure = null;
        var window = new MainWindow();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        async Task Verify()
        {
            try
            {
                using var daily = new CancellationTokenSource();
                using var farm = new CancellationTokenSource();
                using var start = new CancellationTokenSource();
                Set("_dailyCts", daily);
                Set("_autoFarmCts", farm);
                Set("_startCts", start);
                Set("_dailyRunning", true);
                var button = (Button)window.FindName("DailyStartButton");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(daily.IsCancellationRequested);
                Assert.Equal("正在停止…", button.Content);
                typeof(MainWindow).GetMethod("UpdateBusyState", flags)!.Invoke(window, null);
                Assert.False(button.IsEnabled);
                Assert.False(((Button)window.FindName("StartGameButton")).IsEnabled);
                Assert.False(((Button)window.FindName("SettingsButton")).IsEnabled);

                var lifetime = (WindowTaskLifetime)Get("_windowTasks");
                var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = lifetime.RunAsync(async () =>
                {
                    try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
                    catch (OperationCanceledException) { }
                    finally { await cleanup.Task; }
                });
                var closing = new System.ComponentModel.CancelEventArgs();
                typeof(MainWindow).GetMethod("Window_Closing", flags)!.Invoke(window, [window, closing]);
                Assert.True(closing.Cancel);
                Assert.True(lifetime.IsStopping);
                Assert.True(farm.IsCancellationRequested);
                Assert.True(start.IsCancellationRequested);
                Assert.False(window.IsEnabled);
                Assert.False((bool)Get("_windowClosed"));
                Assert.False(task.IsCompleted);
                cleanup.SetResult();
                await task;
                // async void 关闭事件可能经过多层续体；一次 Yield 不代表事件已完成。
                await context.WaitForOperationsAsync();
                Assert.True((bool)Get("_windowClosed"));
            }
            catch (Exception ex) { failure = ex; }

        }
        try
        {
            var verification = Verify();
            while (!verification.IsCompleted) context.RunNext();
            verification.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>仅调度受测任务续体，避免触发 Application 的全局启动和真实设备探测。</summary>
    private sealed class TestUiContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private int _operations;
        private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>跟踪 async void 事件生命周期，防止事件尚未结束就销毁测试上下文。</summary>
        public override void OperationStarted()
        {
            if (_operations++ == 0)
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public override void OperationCompleted()
        {
            if (--_operations == 0) _idle.SetResult();
        }
        public Task WaitForOperationsAsync() => _operations == 0
            ? Task.CompletedTask
            : _idle.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public void RunNext()
        {
            if (!_queue.TryTake(out var action, TimeSpan.FromSeconds(10)))
                throw new TimeoutException("窗口收尾续体未返回");
            action();
        }
        public void Dispose() => _queue.Dispose();
    }

    /// <summary>实际下拉切换须同步任务兼容性与简短状态，并保留默认和手动勾选意愿。</summary>
    private static void VerifyDeviceSelectionState(MainWindow window)
    {
        var combo = (ComboBox)window.FindName("DeviceCombo");
        var task = ((ListBox)window.FindName("DailyTaskList")).Items.Cast<GGEA.App.Tasks.DailyTaskItem>()
            .Single(item => item.Kind == GGEA.App.Tasks.DailyTaskKind.DevelopOnce);
        Assert.True(task.IsChecked);
        Assert.True(task.IsSupported);
        var devices = new[]
        {
            new GGEA.App.Device.AdbDevice("ld", "device", "") { Brand = "雷电" },
            new GGEA.App.Device.AdbDevice("mumu", "device", "") { Brand = "MuMu" },
        };
        combo.ItemsSource = devices;
        combo.SelectedItem = devices[0];
        Assert.True(task.IsSupported);
        combo.SelectedItem = devices[1];
        Assert.True(task.IsSupported);
        Assert.True(task.IsChecked);
        Assert.Equal("已连接", ((TextBlock)window.FindName("ConnectionStateText")).Text);
        Assert.Equal("设备已连接", ((TextBlock)window.FindName("AccountStatusText")).Text);
        task.IsChecked = false;
        combo.SelectedItem = devices[0];
        combo.SelectedItem = devices[1];
        Assert.False(task.IsChecked);
        combo.ItemsSource = null;
        task.IsChecked = true;
    }

    /// <summary>真实控件显示离线原因，启动入口拒绝离线目标，重连按钮受设备执行锁约束。</summary>
    private static void VerifyOfflineDeviceRecovery(MainWindow window)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var combo = (ComboBox)window.FindName("DeviceCombo");
        var reconnect = (Button)window.FindName("ReconnectButton");
        var offline = new GGEA.App.Device.AdbDevice("127.0.0.1:16384", "offline", "SM_S9210");
        combo.ItemsSource = new[] { offline };
        combo.SelectedItem = offline;
        Assert.Contains("设备离线", ((TextBlock)window.FindName("ConnectionStateText")).Text);
        Assert.Contains("重连", ((TextBlock)window.FindName("DeviceStatusText")).Text);
        Assert.True(reconnect.IsEnabled);
        using ((IDisposable)Invoke("TryBeginExecution")!)
        {
            Assert.False(reconnect.IsEnabled);
            Assert.True(((Task)Invoke("ReconnectButton_ClickCoreAsync", window, new RoutedEventArgs())!).IsCompletedSuccessfully);
            Assert.Same(offline, combo.SelectedItem);
        }
        Assert.True(reconnect.IsEnabled);
        typeof(MainWindow).GetField("_adb", flags)!.SetValue(window, new GGEA.App.Device.AdbService("must-not-run.exe", new TestLog()));
        Assert.True(((Task)Invoke("StartGameButton_ClickCoreAsync", window, new RoutedEventArgs())!).IsCompletedSuccessfully);
        Assert.Contains("设备离线", ((TextBlock)window.FindName("ConnectionStateText")).Text);
        typeof(MainWindow).GetField("_adb", flags)!.SetValue(window, null);

        // 导出真实 WPF 设备卡片，检查按钮加入后的宽度与错误提示；不访问模拟器。
        var output = Environment.GetEnvironmentVariable("GGEA_VISUAL_OUTPUT_DIRECTORY");
        if (!string.IsNullOrEmpty(output))
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1080, 780));
            content.Arrange(new Rect(0, 0, 1080, 780));
            content.UpdateLayout();
            var card = (FrameworkElement)((FrameworkElement)((FrameworkElement)combo.Parent).Parent).Parent;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),
                (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(output, "device-offline.png"));
            encoder.Save(stream);
        }
        combo.SelectedItem = null;
        Assert.False(reconnect.IsEnabled);
        combo.ItemsSource = null;
    }

    /// <summary>按下状态不得移动内容或降低整个内容的透明度；选中标记与外框必须同心。</summary>
    private static void VerifyControlGeometry(Application app)
    {
        foreach (var content in new object[] { "设置", new Border { Width = 20, Height = 22 } })
        {
            var button = new PressProbeButton { Style = (Style)app.FindResource("ButtonSecondary"), Content = content };
            button.Measure(new Size(100, 40));
            button.Arrange(new Rect(0, 0, 100, 40));
            button.UpdateLayout();
            var border = (Border)button.Template.FindName("ButtonBorder", button);
            var presenter = (ContentPresenter)border.Child;
            var before = presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
            button.Press(true);
            button.UpdateLayout();
            var after = presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
            Assert.Equal(before, after);
            Assert.Equal(1.0, border.Opacity);
            button.Press(false);
        }

        var checkBox = new CheckBox { Style = (Style)app.FindResource("TaskCheckBox"), IsChecked = true };
        checkBox.Measure(new Size(30, 30));
        checkBox.Arrange(new Rect(0, 0, 30, 30));
        checkBox.UpdateLayout();
        var box = (Border)checkBox.Template.FindName("BoxBorder", checkBox);
        var mark = (System.Windows.Shapes.Path)checkBox.Template.FindName("CheckMark", checkBox);
        var boxRect = box.TransformToAncestor(checkBox).TransformBounds(new Rect(box.RenderSize));
        var markRect = mark.TransformToAncestor(checkBox).TransformBounds(mark.Data.Bounds);
        Assert.Equal(boxRect.Left + boxRect.Width / 2, markRect.Left + markRect.Width / 2);
        Assert.Equal(boxRect.Top + boxRect.Height / 2, markRect.Top + markRect.Height / 2);
    }

    /// <summary>长短列表及不同窗口宽度下，表头与每一列的实际勾选框中心应保持一致。</summary>
    private static void VerifyHardColumnAlignment(MainWindow window)
    {
        var list = (ListBox)window.FindName("ApWorkList");
        var originalItems = list.ItemsSource;
        var content = (FrameworkElement)window.Content;
        foreach (var width in new[] { 1080, 1280 })
        foreach (var count in new[] { 1, 12 })
        {
            list.ItemsSource = Enumerable.Range(1, count).Select(i => new GGEA.App.Tasks.ApConsumptionWorkItem
                { WorkId = i, Work = "测试作品", HardCount = 5 }).ToArray();
            content.Measure(new Size(width, 780));
            content.Arrange(new Rect(0, 0, width, 780));
            content.UpdateLayout();
            var boxes = Descendants(list).OfType<CheckBox>().Take(5).ToArray();
            Assert.Equal(5, boxes.Length);
            for (var i = 0; i < 5; i++)
            {
                var header = Descendants(content).OfType<TextBlock>().Single(t => t.Text == $"Hard{i + 1}");
                var labelBounds = header.TransformToAncestor(content).TransformBounds(new Rect(header.RenderSize));
                var boxBounds = boxes[i].TransformToAncestor(content).TransformBounds(new Rect(boxes[i].RenderSize));
                // 奇数宽文字居中时会有半像素舍入，容差限定为 1 DIP。
                Assert.InRange(Math.Abs(labelBounds.Left + labelBounds.Width / 2 - boxBounds.Left - boxBounds.Width / 2), 0, 1);
            }
        }
        list.ItemsSource = originalItems;
    }

    /// <summary>遍历实际生成的控件树，避免通过写死位置推断列是否对齐。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>离屏设置 ButtonBase 的真实按下状态，验证模板触发器而不模拟外部输入。</summary>
    private sealed class PressProbeButton : Button
    {
        public void Press(bool pressed) => IsPressed = pressed;
    }

}
