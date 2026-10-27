using System.IO;
using GGEA.App.Config;

namespace GGEA.Tests;

/// <summary>
/// 配置加载测试：正常读取、文件缺失与损坏 JSON 都应安全回退。
/// </summary>
public class ConfigServiceTests
{
    /// <summary>宿主保存配置不能丢弃尚未由独立扩展导入的数据。</summary>
    [Fact]
    public void UnknownExtensionData_SurvivesLoadAndSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ggea-extension-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"retiredExtension\":{\"enabled\":true,\"pending\":[\"device-a\"]}}");
            var service = new ConfigService(new TestLog());
            var config = service.Load(path);
            service.Save(config, path);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var extension = json.RootElement.GetProperty("retiredExtension");
            Assert.True(extension.GetProperty("enabled").GetBoolean());
            Assert.Equal("device-a", extension.GetProperty("pending")[0].GetString());
        }
        finally { File.Delete(path); }
    }

    /// <summary>新配置与缺失字段默认允许下载，已有选择在加载及保存重载后保持不变。</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"allowGgeaDownload\":false}", false)]
    [InlineData("{\"allowGgeaDownload\":true}", true)]
    public void Load_AllowGgeaDownload_DefaultsToTrueAndPreservesChoice(string? json, bool expected)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "download.json");
        if (json is not null) File.WriteAllText(path, json);
        var service = new ConfigService(new TestLog());
        var config = service.Load(path);
        Assert.Equal(expected, config.AllowGgeaDownload);
        service.Save(config, path);
        Assert.Equal(expected, service.Load(path).AllowGgeaDownload);
    }

    /// <summary>只修复主界面旧五秒默认值，保留其他手动间隔，保存重载不会再次变化。</summary>
    [Theory]
    [InlineData(5000, 5000, 500, 500)]
    [InlineData(150, 1000, 150, 1000)]
    [InlineData(0, 3000, 0, 3000)]
    public void Load_HomeTiming_MigratesOnlyLegacyDefaults(int interval, int settle, int expectedInterval, int expectedSettle)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "home-timing.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            { mainScreenConfirmationIntervalMs = interval, mainScreenBackSettleDelayMs = settle }));
        var service = new ConfigService(new TestLog());
        for (var load = 0; load < 2; load++)
        {
            var config = service.Load(path);
            Assert.Equal(expectedInterval, config.MainScreenConfirmationIntervalMs);
            Assert.Equal(expectedSettle, config.MainScreenBackSettleDelayMs);
            Assert.Equal(90000, config.MainScreenWaitMs);
            service.Save(config, path);
        }
    }

    /// <summary>三处关闭素材合并后，官方旧路径自动升级且外部路径保留。</summary>
    [Theory]
    [InlineData("resources/templates/", true)]
    [InlineData("resources/common/templates/", true)]
    [InlineData("custom/", false)]
    public void Load_SharedClose_MigratesShopAndRecovery(string prefix, bool migrate)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "shared-close.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new {
            shopResultCloseTemplate = prefix + "shop/button_result_close.png",
            apRecoveryCloseTemplate = prefix + "stage/ap_recovery_close.png"
        }));
        var config = new ConfigService(new TestLog()).Load(path);
        Assert.Equal(migrate ? "resources/templates/common/close.png" : prefix + "shop/button_result_close.png", config.ShopResultCloseTemplate);
        Assert.Equal(migrate ? "resources/templates/common/close.png" : prefix + "stage/ap_recovery_close.png", config.ApRecoveryCloseTemplate);
    }

    /// <summary>关闭按钮与暗色按钮重命名只迁移官方路径，保留自定义文件。</summary>
    [Theory]
    [InlineData("resources/templates/", true)]
    [InlineData("resources/common/templates/", true)]
    [InlineData("resources/zh-CN/templates/", true)]
    [InlineData("custom/", false)]
    public void Load_RenamedButtons_MigratesOnlyBundledPaths(string prefix, bool migrate)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "renamed.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new {
            giftCloseTemplate = prefix + "gift/button_close.png",
            againCloseTemplate = prefix + "stage/sortie/again_close.png",
            stageWorkSelectCloseTemplate = prefix + "stage/select_close.png"
        }));
        var service = new ConfigService(new TestLog());
        var config = service.Load(path);
        Assert.Equal(migrate ? "resources/templates/common/close.png" : prefix + "gift/button_close.png", config.GiftCloseTemplate);
        Assert.Equal(migrate ? "resources/templates/stage/sortie/again_disabled.png" : prefix + "stage/sortie/again_close.png", config.AgainCloseTemplate);
        Assert.Equal(migrate ? "resources/templates/stage/select_disabled.png" : prefix + "stage/select_close.png", config.StageWorkSelectCloseTemplate);
        service.Save(config, path);
        Assert.Equal(config.GiftCloseTemplate, service.Load(path).GiftCloseTemplate);
    }

    /// <summary>已删除的官方 OK 路径逐字段升级，自定义路径保持不变。</summary>
    [Theory]
    [InlineData("resources/templates/base/button_ok.png", true)]
    [InlineData("resources/common/templates/base/button_ok.png", true)]
    [InlineData("custom/button_ok.png", false)]
    public void Load_OkTemplate_MigratesRetiredPaths(string old, bool migrate)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ok.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            { harvestOkTemplate = old, giftOkTemplate = old, missionClaimResultOkTemplate = old }));
        var config = new ConfigService(new TestLog()).Load(path);
        var expected = migrate ? "resources/templates/dialog/button_ok.png" : old;
        Assert.Equal(expected, config.HarvestOkTemplate);
        Assert.Equal(expected, config.GiftOkTemplate);
        Assert.Equal(expected, config.MissionClaimResultOkTemplate);
    }

    /// <summary>短动画设置原样保留；只迁移识别预算，保存重载不改变用户等待。</summary>
    [Fact]
    public void Load_Timing_PreservesShortAnimationAndUpgradesBudget()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "timing.json");
        File.WriteAllText(path, """
            {"splashDelayMs":3000,"mainScreenConfirmationIntervalMs":150,
             "mainScreenBackSettleDelayMs":1000,"stageSwipeSettleMs":3000,
             "stageWorkTapSettleMs":3000,"stagePageSettleMs":0,
             "loginRewardFallbackDelayMs":7500,"developTitleWaitMs":10000}
            """);
        var service = new ConfigService(new TestLog());
        for (var load = 0; load < 2; load++)
        {
            var config = service.Load(path);
            Assert.Equal(3000, config.SplashDelayMs);
            Assert.Equal(150, config.MainScreenConfirmationIntervalMs);
            Assert.Equal(1000, config.MainScreenBackSettleDelayMs);
            Assert.Equal(3000, config.StageSwipeSettleMs);
            Assert.Equal(3000, config.StageWorkTapSettleMs);
            Assert.Equal(0, config.StagePageSettleMs);
            Assert.Equal(7500, config.LoginRewardFallbackDelayMs);
            Assert.Equal(15000, config.DevelopTitleWaitMs);
            service.Save(config, path);
        }
    }

    /// <summary>两张原生素材独立迁移，覆盖两字段和路径形式；保存重载后稳定且自定义路径不受影响。</summary>
    [Theory]
    [InlineData("ogk_g0070u00100.png", "relative")]
    [InlineData("ogk_g0070u00100.png", "windows")]
    [InlineData("ogk_g0070u00100.png", "absolute")]
    [InlineData("ogk_g0010u00700.png", "relative")]
    [InlineData("ogk_g0010u00700.png", "windows")]
    [InlineData("ogk_g0010u00700.png", "absolute")]
    [InlineData("ogk_g0010u00700.png", "custom")]
    public void Load_DevelopUnitDirectory_MigratesOfficialPaths(string name, string kind)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        var old = "resources/templates/develop/" + name;
        var original = kind switch
        {
            "windows" => old.ToUpperInvariant().Replace('/', '\\'),
            "absolute" => Path.Combine(AppContext.BaseDirectory, old),
            "custom" => "custom/" + name,
            _ => old,
        };
        var expected = kind == "custom" ? original : "resources/templates/develop/dev_unit/" + name;
        var service = new ConfigService(new TestLog());
        foreach (var field in new[] { "developMsTemplate", "developMsLargeTemplate" })
        {
            var values = new Dictionary<string, string>
            {
                ["developMsTemplate"] = "custom/small.png",
                ["developMsLargeTemplate"] = "custom/large.png",
                [field] = original,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(values));
            var config = service.Load(path);
            Assert.Equal(field == "developMsTemplate" ? expected : "custom/small.png", config.DevelopMsTemplate);
            Assert.Equal(field == "developMsLargeTemplate" ? expected : "custom/large.png", config.DevelopMsLargeTemplate);
            Assert.False(config.DevelopMsFixedScales);
            if (kind != "custom") Assert.True(File.Exists(ConfigService.ResolvePath(expected)));
            service.Save(config, path);
            var reloaded = service.Load(path);
            Assert.Equal(config.DevelopMsTemplate, reloaded.DevelopMsTemplate);
            Assert.Equal(config.DevelopMsLargeTemplate, reloaded.DevelopMsLargeTemplate);
        }
    }

    /// <summary>兼容旧默认区域，同时保留用户自定义的搜索范围。</summary>
    [Theory]
    [InlineData(0, 975, 282, 105, 300)]
    [InlineData(1, 975, 282, 105, 282)]
    [InlineData(0, 974, 282, 105, 282)]
    [InlineData(0, 975, 280, 105, 280)]
    [InlineData(0, 975, 282, 104, 282)]
    public void Load_HomeRoi_MigratesOnlyOldDefault(int x, int y, int width, int height, int expected)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            mainScreenButtonRoi = new { x, y, width, height }
        }));
        var config = new ConfigService(new TestLog()).Load(path);
        Assert.Equal(expected, config.MainScreenButtonRoi.Width);
        Assert.Equal(x, config.MainScreenButtonRoi.X);
        Assert.Equal(y, config.MainScreenButtonRoi.Y);
        Assert.Equal(height, config.MainScreenButtonRoi.Height);
        Assert.Equal(0.85, config.MainScreenThreshold);
    }

    /// <summary>仅迁移已移除的官方文字模板，保留用户的自定义路径。</summary>
    [Theory]
    [InlineData("resources/templates/shop/text_free.png", "resources/templates/shop/free.png")]
    [InlineData("custom/free-price.png", "custom/free-price.png")]
    public void Load_ShopPriceTemplate_MigratesOldDefault(string original, string expected)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { shopFreeTextTemplate = original }));
        Assert.Equal(expected, new ConfigService(new TestLog()).Load(path).ShopFreeTextTemplate);
    }

    /// <summary>退役资源的两字段独立迁移，保留自定义路径与阈值，原生入口不受旧策略开关阻碍。</summary>
    [Theory]
    [InlineData("resources/templates/develop/dev_prototype_gundam.png", "resources/templates/develop/dev_prototype_gundam_large.png", true, true)]
    [InlineData("custom/unit.png", "resources/templates/develop/dev_prototype_gundam_large.png", false, true)]
    [InlineData("resources/templates/develop/dev_prototype_gundam.png", "custom/large.png", true, false)]
    [InlineData("resources/templates/develop/dev_prototype_gundam.png", "", true, false)]
    [InlineData("resources/templates/develop/dev_prototype_gundam_large.png", "", true, false)]
    [InlineData("custom/dev_prototype_gundam.png", "custom/dev_prototype_gundam_large.png", false, false)]
    public void Load_RoadmapTemplates_MigratesRetiredPathsIndependently(string small, string large, bool migrateSmall, bool migrateLarge)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            developMsTemplate = small, developMsLargeTemplate = large,
            developMsFixedScales = false, developMsThreshold = .91
        }));
        var service = new ConfigService(new TestLog());
        var config = service.Load(path);
        Assert.Equal(migrateSmall ? new AppConfig().DevelopMsTemplate : small, config.DevelopMsTemplate);
        Assert.Equal(migrateLarge ? "" : large, config.DevelopMsLargeTemplate);
        Assert.Equal(migrateSmall && string.IsNullOrEmpty(config.DevelopMsLargeTemplate), config.DevelopMsFixedScales);
        Assert.Equal(.91, config.DevelopMsThreshold);
        service.Save(config, path);
        var reloaded = service.Load(path);
        Assert.Equal(config.DevelopMsTemplate, reloaded.DevelopMsTemplate);
        Assert.Equal(config.DevelopMsLargeTemplate, reloaded.DevelopMsLargeTemplate);
        Assert.Equal(config.DevelopMsFixedScales, reloaded.DevelopMsFixedScales);
        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("resources/templates/develop/dev_prototype_gundam", saved);
    }

    /// <summary>单字段、大小写及 Windows/绝对官方资源路径均升级到可加载的新素材。</summary>
    [Theory]
    [InlineData("small")]
    [InlineData("large")]
    [InlineData("windows")]
    [InlineData("absolute")]
    public void Load_RoadmapTemplates_HandlesPartialAndWindowsConfigurations(string kind)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        var old = "resources/templates/develop/dev_prototype_gundam.png";
        var values = new Dictionary<string, object>();
        values[kind == "large" ? "developMsLargeTemplate" : "developMsTemplate"] = kind switch
        {
            "windows" => old.ToUpperInvariant().Replace('/', '\\'),
            "absolute" => Path.Combine(AppContext.BaseDirectory, old),
            "large" => "resources/templates/develop/dev_prototype_gundam_large.png",
            _ => old
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(values));
        var config = new ConfigService(new TestLog()).Load(path);
        Assert.Equal(new AppConfig().DevelopMsTemplate, config.DevelopMsTemplate);
        Assert.Empty(config.DevelopMsLargeTemplate);
        Assert.True(config.DevelopMsFixedScales);
        Assert.True(File.Exists(ConfigService.ResolvePath(config.DevelopMsTemplate)));
    }

    /// <summary>明暗偏好可跨保存/加载恢复；旧配置继续默认夜间。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThemePreference_RoundTrips(bool dark)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "theme.json");
        var service = new ConfigService(new TestLog());
        Assert.True(service.Load(path).IsDarkMode);
        File.WriteAllText(path, "{}");
        Assert.True(service.Load(path).IsDarkMode);
        service.Save(new AppConfig { IsDarkMode = dark }, path);
        Assert.Equal(dark, service.Load(path).IsDarkMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Load_ValidJson_ReadsValues()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ggea.config.json");
        File.WriteAllText(path, """{"adbPath":"C:/adb/adb.exe","commandTimeoutMs":999}""");

        var config = new ConfigService(new TestLog()).Load(path);

        Assert.Equal("C:/adb/adb.exe", config.AdbPath);
        Assert.Equal(999, config.CommandTimeoutMs);
        Assert.Equal(0.85, config.DefaultMatchThreshold);
        Assert.True(config.EnableWindowsNotifications);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var config = new ConfigService(new TestLog()).Load(Path.Combine(temp.Path, "not-exists.json"));

        Assert.Equal("adb", config.AdbPath);
        Assert.Equal(15000, config.CommandTimeoutMs);
        Assert.True(config.EnableWindowsNotifications);
        Assert.Equal("resources/templates/entry/connection_error.png", config.ConnectionErrorTemplate);
        Assert.Equal(
            "resources/templates/develop/title_roadmap_display_settings.png",
            config.DevelopRoadmapDisplaySettingsTitleTemplate);
        Assert.Equal("resources/templates/common/skip.png", config.LoginRewardSkipTemplate);
        Assert.Equal(5000, config.LoginRewardFallbackDelayMs);
        Assert.Equal("resources/templates/home/announcement.png", config.LoginRewardAnnouncementTemplate);
        Assert.Equal("resources/templates/common/close.png", config.LoginRewardAnnouncementCloseTemplate);
        Assert.Equal(4000, config.LoginRewardAnnouncementWaitMs);
        Assert.Equal("resources/templates/home/home_screen_button_activated.png", config.MainScreenActivatedTemplate);
        Assert.Equal(300, config.MainScreenButtonRoi.Width);
        Assert.Equal(2, config.MainScreenConfirmationFrames);
        Assert.Equal(1920, config.StageSwipeCanvasDelta);
        Assert.Equal(200, config.StageHalfSwipeLeftTapCorrection);
        Assert.Equal(-200, config.StageHalfSwipeRightTapCorrection);
        Assert.Equal(3000, config.StageWorkTapSettleMs);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Load_InvalidJson_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ggea.config.json");
        File.WriteAllText(path, "{ broken json !!!");

        var config = new ConfigService(new TestLog()).Load(path);

        Assert.Equal("adb", config.AdbPath);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Save_WritesJson_ThenLoadCanRead()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ggea.config.json");
        var log = new TestLog();
        var service = new ConfigService(log);
        var config = new AppConfig
        {
            AdbPath = "D:/tools/adb.exe",
            CommandTimeoutMs = 5000,
            StageHalfSwipeLeftTapCorrection = -350,
            StageHalfSwipeRightTapCorrection = 200,
        };

        service.Save(config, path);
        var loaded = service.Load(path);

        Assert.Equal("D:/tools/adb.exe", loaded.AdbPath);
        Assert.Equal(5000, loaded.CommandTimeoutMs);
        Assert.Equal(-350, loaded.StageHalfSwipeLeftTapCorrection);
        Assert.Equal(200, loaded.StageHalfSwipeRightTapCorrection);
        Assert.Equal(0.85, loaded.DefaultMatchThreshold);
    }

    /// <summary>通知开关的两种状态都应保存并正确加载，关闭后不得回退到默认开启。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_WindowsNotificationPreference_RoundTrips(bool enabled)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        var service = new ConfigService(new TestLog());
        service.Save(new AppConfig { EnableWindowsNotifications = enabled }, path);
        Assert.Equal(enabled, service.Load(path).EnableWindowsNotifications);
    }

    /// <summary>旧配置不自动添加强敌任务；失效单位和非法难度不能回退为其他可执行选择。</summary>
    [Fact]
    public void FierceEnemyDaily_InvalidSelectionDoesNotBecomeExecutable()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, "{}");
        Assert.Null(new ConfigService(new TestLog()).Load(path).FierceEnemyDaily);
        var units = GGEA.App.Tasks.FierceEnemyUnit.Load();
        Assert.Throws<InvalidOperationException>(() => new FierceEnemyDailySettings { UnitId = -1 }.Restore(units));
        Assert.Throws<InvalidOperationException>(() => new FierceEnemyDailySettings
            { Difficulty = (GGEA.App.Tasks.FierceEnemyDifficulty)999 }.Restore(units));
    }
    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }
}
