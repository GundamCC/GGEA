using System.IO;

namespace GGEA.App.Device;

/// <summary>
/// 单个模拟器的适配档案：类型标识、下拉显示名、默认启动参数与路径识别关键字。
/// 新增模拟器时只需在 <see cref="EmulatorTypes.Profiles"/> 增加一条记录。
/// </summary>
public sealed record EmulatorProfile(
    string Type,
    string DisplayName,
    string DefaultArgs,
    IReadOnlyList<string> DetectionKeywords,
    IReadOnlyList<string> AdbExecutableNames,
    IReadOnlyList<string> LauncherExecutableNames)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// 模拟器类型定义与从 ADB / 模拟器路径自动识别的辅助方法。
/// 支持 MuMu、雷电（LDPlayer）、BlueStacks 与自定义类型；
/// 模拟器类型不应默认写死为 MuMu，而是根据用户选择的 ADB 路径自动识别。
/// </summary>
public static class EmulatorTypes
{
    /// <summary>已知厂商 ADB 文件名；发现、浏览及保存共同使用，避免入口规则不一致。</summary>
    public static readonly IReadOnlyList<string> AdbClientFileNames =
        Array.AsReadOnly(new[] { "adb.exe", "HD-Adb.exe", "nox_adb.exe", "adb_server.exe" });

    /// <summary>仅用于设备端品牌与通道路由；启动仍采用自定义配置。</summary>
    internal const string Nox = "nox";
    internal const string Memu = "memu";

    /// <summary>MuMu 模拟器：使用 MuMuManager 启动，并按实例端口精确连接 ADB。</summary>
    public const string MuMu = "mumu";

    /// <summary>雷电模拟器（LDPlayer）：使用 ldconsole 启动，默认参数 launch --index 0。</summary>
    public const string Leidian = "ld";

    /// <summary>BlueStacks 模拟器。</summary>
    public const string BlueStacks = "bs";

    /// <summary>自定义模拟器：仅按配置的路径与参数启动。</summary>
    public const string Custom = "custom";

    /// <summary>
    /// 已注册的模拟器档案列表。
    /// 设置页下拉、ADB/模拟器路径自动识别、默认启动参数都由它驱动；
    /// 新增模拟器在此追加一条记录即可。
    /// </summary>
    public static readonly IReadOnlyList<EmulatorProfile> Profiles = new[]
    {
        new EmulatorProfile(
            MuMu,
            "MuMu 模拟器（默认打开第一个安卓设备）",
            "control launch --vmindex 0",
            new[] { "MuMu", "MuMuManager" },
            new[] { "adb.exe" },
            new[] { "MuMuManager.exe" }),
        new EmulatorProfile(
            Leidian,
            "雷电模拟器（LDPlayer，默认启动第一个实例）",
            "launch --index 0",
            new[] { "LDPlayer", "dnplayer", "ldconsole" },
            new[] { "adb.exe" },
            new[] { "ldconsole.exe", "dnplayer.exe" }),
        new EmulatorProfile(
            BlueStacks,
            "BlueStacks 模拟器",
            "",
            new[] { "BlueStacks", "HD-Player", "HD-Adb" },
            new[] { "HD-Adb.exe" },
            new[] { "HD-Player.exe" }),
        new EmulatorProfile(
            Custom,
            "自定义",
            "",
            Array.Empty<string>(),
            new[] { "adb.exe" },
            Array.Empty<string>()),
    };

    /// <summary>判断是否为 MuMu 类型（MuMu 使用专属的实例端口连接与桌面缩放输入）。</summary>
    public static bool IsMuMu(string? emulatorType)
        => IsType(emulatorType, MuMu);

    /// <summary>判断是否为雷电类型。</summary>
    public static bool IsLeidian(string? emulatorType)
        => IsType(emulatorType, Leidian);

    /// <summary>判断是否为 BlueStacks 类型。</summary>
    public static bool IsBlueStacks(string? emulatorType)
        => IsType(emulatorType, BlueStacks);

    /// <summary>返回类型对应的简短品牌名（用于设备列表前缀）；未知类型返回 null。</summary>
    public static string? GetBrandLabel(string? emulatorType)
    {
        if (IsType(emulatorType, Nox)) return "夜神";
        if (IsType(emulatorType, Memu)) return "逍遥";
        if (IsMuMu(emulatorType))
        {
            return "MuMu";
        }

        if (IsLeidian(emulatorType))
        {
            return "雷电";
        }

        if (IsBlueStacks(emulatorType))
        {
            return "BlueStacks";
        }

        return null;
    }

    /// <summary>
    /// 根据 ADB 可执行文件路径识别模拟器类型；无法识别返回 null。
    /// 规则：路径含 MuMu → mumu；含 LDPlayer / dnplayer → ld；含 BlueStacks / HD-Player → bs。
    /// </summary>
    public static string? DetectFromAdbPath(string? adbPath)
        => DetectFromPath(adbPath);

    /// <summary>
    /// 根据模拟器主程序路径识别类型；无法识别返回 null。
    /// 规则：含 MuMuManager → mumu；含 ldconsole → ld；含 HD-Player → bs。
    /// </summary>
    public static string? DetectFromEmulatorPath(string? emulatorPath)
        => DetectFromPath(emulatorPath);

    /// <summary>按关键字匹配模拟器类型，路径为空或无法识别时返回 null。</summary>
    private static string? DetectFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var profile in Profiles)
        {
            if (profile.Type == Custom)
            {
                continue; // 自定义类型不参与关键字识别
            }

            foreach (var keyword in profile.DetectionKeywords)
            {
                if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return profile.Type;
                }
            }
        }

        return null;
    }

    /// <summary>按类型标识查找档案；未知类型回退到“自定义”档案。</summary>
    public static EmulatorProfile GetProfile(string? emulatorType)
        => Profiles.FirstOrDefault(p => IsType(emulatorType, p.Type))
           ?? Profiles.Single(p => p.Type == Custom);

    /// <summary>返回该类型的默认启动参数（未定义时返回空字符串）。</summary>
    public static string GetDefaultArgs(string? emulatorType)
        => GetProfile(emulatorType).DefaultArgs;

    /// <summary>
    /// 判断用户输入是否为当前允许的 ADB 客户端名称。
    /// 允许明确列出的厂商命名，不放开任意可执行文件；名称不作为设备品牌证据。
    /// </summary>
    public static bool IsSupportedAdbPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path.Trim());
        return fileName.Equals("adb", StringComparison.OrdinalIgnoreCase)
               || AdbClientFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>忽略大小写比较类型标识。</summary>
    private static bool IsType(string? value, string type)
        => string.Equals(value, type, StringComparison.OrdinalIgnoreCase);
}
