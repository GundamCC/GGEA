using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GGEA.App.Logging;
using GGEA.App.Automation;

namespace GGEA.App.Config;

/// <summary>
/// 配置加载服务。
/// 配置文件缺失或损坏时回退到默认配置，保证程序仍可启动。
/// </summary>
public sealed class ConfigService
{
    // 主窗口持有设备执行互斥锁，在启动或保存设置、旧服务释放后替换；运行中的任务不切换语言。
    public static string UserDirectory { get; private set; } = AppContext.BaseDirectory;
    public static string ContentDirectory { get; private set; } = AppContext.BaseDirectory;
    private static IReadOnlyDictionary<string, string>? _originalFiles;

    public static void SelectDirectories(string userDirectory, string contentDirectory, IReadOnlyDictionary<string, string>? originalFiles)
    {
        UserDirectory = userDirectory;
        ContentDirectory = contentDirectory;
        _originalFiles = originalFiles;
        Resources = new ResourceResolver(UserDirectory, Resources.Language, ContentDirectory, _originalFiles);
    }
    public static ResourceResolver Resources { get; private set; } = new(AppContext.BaseDirectory, "zh-CN");

    /// <summary>仅由应用组合根在没有运行任务时应用语言，之后重建所有持有模板缓存的服务。</summary>
    public static void ApplyResourceLanguage(AppConfig config)
    {
        GameProfiles.Normalize(config);
        Resources = new ResourceResolver(UserDirectory, config.GameLanguage, ContentDirectory, _originalFiles);
    }

    private readonly IGgeaLog _log;

    public ConfigService(IGgeaLog log)
    {
        _log = log;
    }

    /// <summary>
    /// 从 JSON 文件加载配置。
    /// </summary>
    /// <param name="path">配置文件完整路径。</param>
    public AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _log.Warn($"配置文件不存在，使用默认配置: {path}");
                return Track(new AppConfig());
            }

            string json;
            using (var file = new SharedConfigFile(path)) json = file.Read();
            return Track(ParseConfig(json));
        }
        catch (Exception ex)
        {
            _log.Warn($"读取配置失败，使用默认配置: {ex.Message}");
            return Track(new AppConfig());
        }
    }

    private static AppConfig ParseConfig(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<AppConfig>(json, options);
        // 旧区域右界裁掉了关卡页主画面按钮的完整匹配位置；仅迁移精确旧默认值。
        if (config?.MainScreenButtonRoi is { X: 0, Y: 975, Width: 282, Height: 105 } homeRoi)
            homeRoi.Width = 300;
        // 旧默认模板仅含文字且已移除，迁移到完整价格栏；不改动用户自定义模板路径。
        if (config?.ShopFreeTextTemplate?.Replace('\\', '/').Equals(
            "resources/templates/shop/text_free.png", StringComparison.OrdinalIgnoreCase) == true)
            config.ShopFreeTextTemplate = "resources/templates/shop/free.png";
        // 两张随包旧截图已移除：逐项迁移，覆盖缺失另一字段、混合自定义及 Windows 分隔符的旧配置。
        if (config is not null)
        {
            // 修复 1.1.6 的主界面五秒默认值叠加；精确迁移两项旧值，其他手动间隔保持不变。
            if (config.MainScreenConfirmationIntervalMs == 5000)
                config.MainScreenConfirmationIntervalMs = GameTiming.ConfirmationIntervalMs;
            if (config.MainScreenBackSettleDelayMs == 5000)
                config.MainScreenBackSettleDelayMs = GameTiming.ConfirmationIntervalMs;
            // 其他动画时长保留用户值；只迁移识别总预算，不再把短动画自动延长。
            if (config.DevelopTitleWaitMs == 10000)
                config.DevelopTitleWaitMs = GameTiming.ShortRecognitionTimeoutMs;
            GameProfiles.Normalize(config);
            config.AutoProgress ??= new AutoProgressSettings();
            config.AutoProgress.Normalize();
            config.DevelopExecuteTemplate = MigrateDevelopButtonPath(config.DevelopExecuteTemplate);
            config.DevelopExecuteConfirmTemplate = MigrateDevelopButtonPath(config.DevelopExecuteConfirmTemplate);
            // 替换随包批量开发按钮，兼容旧配置，但保留用户自定义素材。
            const string oldBulk = "resources/templates/develop/button_develop_all.png";
            var bulk = config.DevelopAllTemplate?.Replace('\\', '/');
            if (string.Equals(bulk, oldBulk, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bulk, Path.Combine(AppContext.BaseDirectory, oldBulk).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                config.DevelopAllTemplate = new AppConfig().DevelopAllTemplate;
            // 仅迁移随包素材的精确旧路径，两字段独立处理，保留同名自定义文件。
            config.DevelopMsTemplate = MigrateDevelopUnitPath(config.DevelopMsTemplate);
            config.DevelopMsLargeTemplate = MigrateDevelopUnitPath(config.DevelopMsLargeTemplate);
            if (IsRetiredDevelopTemplate(config.DevelopMsTemplate))
                config.DevelopMsTemplate = new AppConfig().DevelopMsTemplate;
            if (IsRetiredDevelopTemplate(config.DevelopMsLargeTemplate))
                config.DevelopMsLargeTemplate = "";
            var nativeOnly = string.Equals(config.DevelopMsTemplate?.Replace('\\', '/'),
                new AppConfig().DevelopMsTemplate, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(config.DevelopMsLargeTemplate);
            // 原生素材的任务入口统一使用两档；只有自定义截图继续尊重显式识别策略。
            using var document = JsonDocument.Parse(json);
            if (nativeOnly || !document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals("developMsFixedScales", StringComparison.OrdinalIgnoreCase)))
                config.DevelopMsFixedScales = nativeOnly;
        }
        if (config is not null)
        {
            // 删除旧素材后逐项迁移，保留用户自定义路径。
            config.StageTemplateDirectory = MigrateBundledTemplate(config.StageTemplateDirectory,
                "stage/main_stage", "logo");
            config.BattleshipTemplate = MigrateBundledTemplate(config.BattleshipTemplate,
                "base/battleship.png", "base/battleship_cruise/battleship.png");
            config.RecycleAllTemplate = MigrateBundledTemplate(config.RecycleAllTemplate,
                "base/button_recycle_all.png", "base/battleship_cruise/button_recycle_all.png");
            config.RecycleAllDisabledTemplate = MigrateBundledTemplate(config.RecycleAllDisabledTemplate,
                "base/button_recycle_all_disabled.png", "base/battleship_cruise/button_recycle_all_disabled.png");
            config.HarvestOkTemplate = MigrateOkTemplate(config.HarvestOkTemplate);
            config.ShopResultCloseTemplate = MigrateBundledTemplate(config.ShopResultCloseTemplate,
                "shop/button_result_close.png", "common/close.png");
            config.ApRecoveryCloseTemplate = MigrateBundledTemplate(config.ApRecoveryCloseTemplate,
                "stage/ap_recovery_close.png", "common/close.png");
            config.GiftCloseTemplate = MigrateBundledTemplate(config.GiftCloseTemplate,
                "gift/button_close.png", "common/close.png");
            config.AgainCloseTemplate = MigrateBundledTemplate(config.AgainCloseTemplate,
                "stage/sortie/again_close.png", "stage/sortie/again_disabled.png");
            config.StageWorkSelectCloseTemplate = MigrateBundledTemplate(config.StageWorkSelectCloseTemplate,
                "stage/select_close.png", "stage/select_disabled.png");
            config.GiftOkTemplate = MigrateOkTemplate(config.GiftOkTemplate);
            config.MissionClaimResultOkTemplate = MigrateOkTemplate(config.MissionClaimResultOkTemplate);
        }
        return config ?? new AppConfig();
    }

    /// <summary>官方资源重命名兼容逻辑路径、语言路径及安装目录绝对路径；外部自定义文件不迁移。</summary>
    private static string MigrateBundledTemplate(string path, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var normalized = path.Replace('\\', '/');
        foreach (var prefix in new[] { "resources/", "resources/common/" }
                     .Concat(GameProfiles.Languages.Select(language => $"resources/{language.Id}/")))
        {
            var old = prefix + "templates/" + oldName;
            if (string.Equals(normalized, old, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, Path.Combine(AppContext.BaseDirectory, old).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return "resources/templates/" + newName;
        }
        return path;
    }

    /// <summary>仅迁移已删除的官方 OK 模板；兼容程序目录绝对路径。</summary>
    private static string MigrateOkTemplate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var normalized = path.Replace('\\', '/');
        foreach (var old in new[] { "resources/templates/base/button_ok.png", "resources/common/templates/base/button_ok.png" })
            if (string.Equals(normalized, old, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, Path.Combine(AppContext.BaseDirectory, old).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return "resources/templates/dialog/button_ok.png";
        return path;
    }

    /// <summary>只迁移官方旧按钮路径，不改动用户自定义同名素材。</summary>
    private static string MigrateDevelopButtonPath(string path)
    {
        foreach (var name in new[] { "button_execute.png", "button_execute_develop.png" })
            path = MigrateBundledTemplate(path, "develop/" + name, "develop/button/" + name);
        return path;
    }

    /// <summary>原生机体素材移入 dev_unit；兼容相对路径、Windows 分隔符及程序目录下的绝对路径。</summary>
    private static string MigrateDevelopUnitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var normalized = path.Replace('\\', '/');
        foreach (var name in new[] { "ogk_g0070u00100.png", "ogk_g0010u00700.png" })
        {
            var oldPath = "resources/templates/develop/" + name;
            if (string.Equals(normalized, oldPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, Path.Combine(AppContext.BaseDirectory, oldPath).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return "resources/templates/develop/dev_unit/" + name;
        }
        return path;
    }

    /// <summary>只识别已退役的官方资源路径；同名自定义目录不误迁移，程序目录下的绝对路径也能升级。</summary>
    private static bool IsRetiredDevelopTemplate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        foreach (var name in new[] { "dev_prototype_gundam.png", "dev_prototype_gundam_large.png" })
        {
            var relative = "resources/templates/develop/" + name;
            if (string.Equals(normalized, relative, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, Path.Combine(AppContext.BaseDirectory, relative).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 解析可能相对程序目录的路径。
    /// </summary>
    public static string ResolvePath(string path)
    {
        return Resources.Resolve(path);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IgnoreReadOnlyProperties = true,
        WriteIndented = true,
    };

    private static JsonObject Values(AppConfig config) => JsonSerializer.SerializeToNode(config, JsonOptions)!.AsObject();

    private static AppConfig Track(AppConfig config)
    {
        config.SavedValues = Values(config);
        return config;
    }

    internal static AppConfig Snapshot(AppConfig config)
    {
        var values = Values(config);
        var copy = values.Deserialize<AppConfig>(JsonOptions)!;
        copy.SavedValues = values;
        return copy;
    }

    /// <summary>在同一文件锁内应用当前动作，返回最新配置。用于无需编辑会话的即时开关及启动初始化。</summary>
    internal AppConfig Update(string path, Action<AppConfig> change)
    {
        using var file = new SharedConfigFile(path);
        var raw = ReadObject(file);
        var config = ParseConfig(raw.ToJsonString());
        change(config);
        Write(file, raw, config);
        return Track(config);
    }

    /// <summary>将编辑副本相对基线的改动合入磁盘最新值；冲突时保留原文件，供用户重新打开设置。</summary>
    public void Save(AppConfig config, string path) => SaveEdited(config, path);

    internal AppConfig SaveEdited(AppConfig config, string path)
    {
        using var file = new SharedConfigFile(path);
        var raw = ReadObject(file);
        var local = Values(config);
        var latest = Values(ParseConfig(raw.ToJsonString()));
        // Load 和界面编辑副本携带基线；直接传入新建配置保留原有完整写入契约。
        if (file.Exists && config.SavedValues is { } baseline)
            JsonSettingChanges.Apply(baseline, local, latest);
        else
            latest = local;
        var merged = latest.Deserialize<AppConfig>(JsonOptions)!;
        Write(file, raw, merged);
        // 当前窗口仍使用自己的副本；若采用合并后的值作基线，下次保存会把其他窗口的改动反向覆盖。
        config.SavedValues = local;
        return Track(merged);
    }

    private static JsonObject ReadObject(SharedConfigFile file) => file.Exists
        ? JsonNode.Parse(file.Read(), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new InvalidDataException("配置文件不是 JSON 对象。")
        : new JsonObject();

    private static void Write(SharedConfigFile file, JsonObject raw, AppConfig config)
    {
        GameProfiles.Normalize(config);
        config.AutoProgress?.Normalize();
        JsonSettingChanges.Overlay(raw, Values(config));
        file.Write(raw.ToJsonString(JsonOptions));
    }
}
