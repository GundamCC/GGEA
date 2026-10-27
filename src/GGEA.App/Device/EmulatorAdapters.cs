using GGEA.App.Localization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>
/// 内置模拟器适配器边界。它只隔离安装、启动、设备发现与专属输入差异，
/// 不提供动态加载能力，避免把当前需求扩展成插件系统。
/// </summary>
public interface IEmulatorAdapter
{
    /// <summary>适配器对应的静态模拟器档案。</summary>
    EmulatorProfile Profile { get; }

    /// <summary>没有发现设备时显示的针对性诊断。</summary>
    string NoDeviceMessage { get; }

    /// <summary>准备模拟器连接并返回去重、带品牌信息的设备列表。</summary>
    Task<IReadOnlyList<AdbDevice>> DiscoverDevicesAsync(
        int maxInstances = 3,
        CancellationToken ct = default);

    /// <summary>启动目标模拟器、确定唯一设备并打开游戏。</summary>
    Task<EmulatorStartResult> StartAndOpenGameAsync(
        EmulatorLaunchOptions options,
        CancellationToken ct = default);
}

/// <summary>创建项目内置的模拟器适配器。</summary>
public static class EmulatorAdapterFactory
{
    /// <summary>
    /// 按类型创建适配器；可注入启动器与 MuMu 探测函数，保证真实进程行为可被单元测试隔离。
    /// </summary>
    public static IEmulatorAdapter Create(
        string? emulatorType,
        IAdbService adb,
        IGgeaLog log,
        Action<string, string>? emulatorLauncher = null,
        Func<string?>? mumuPathResolver = null,
        Func<string, int, CancellationToken, Task<bool>>? mumuRunningCheck = null)
    {
        var launcher = emulatorLauncher ?? EmulatorProcessLauncher.Launch;
        var pathResolver = mumuPathResolver ?? EmulatorLocator.FindMuMuManager;
        GenericEmulatorAdapter adapter = EmulatorTypes.IsMuMu(emulatorType)
            ? new MuMuEmulatorAdapter(adb, log, launcher, pathResolver, mumuRunningCheck)
            : EmulatorTypes.IsBlueStacks(emulatorType)
                ? new BlueStacksEmulatorAdapter(adb, log, launcher)
                : new GenericEmulatorAdapter(EmulatorTypes.GetProfile(emulatorType), adb, log, launcher);
        adapter.ReadMuMuSerials = ct => MuMuDeviceIdentity.ReadRunningSerialsAsync(pathResolver(), log, ct);
        if (EmulatorTypes.IsMuMu(emulatorType)) adapter.ReadMuMuListeningPorts = LocalAdbDiscovery.ReadMuMuPorts;
        adapter.ReadShadowedAdbPorts = LocalAdbDiscovery.ReadShadowedPorts;
        return adapter;
    }
}

/// <summary>设备刷新只保留已有目标；列表顺序不能代表用户的账号选择。</summary>
internal static class EmulatorDeviceSelector
{
    /// <summary>没有明确选择或原目标离线时保持未选择，不切换到其他在线账号。</summary>
    internal static AdbDevice? SelectForRefresh(
        IReadOnlyList<AdbDevice> devices,
        string? previousSerial,
        Guid previousBootId = default)
    {
        var previous = devices.FirstOrDefault(device =>
            device.IsAvailable &&
            string.Equals(device.Serial, previousSerial, StringComparison.OrdinalIgnoreCase) &&
            (previousBootId == Guid.Empty || device.BootId == previousBootId));
        if (previous is not null)
        {
            return previous;
        }

        // 冷启动返回的 TCP 地址可能被去重为 emulator-*；只允许同一启动身份的唯一别名。
        var aliases = previousBootId == Guid.Empty ? [] : devices.Where(device =>
            device.IsAvailable && device.BootId == previousBootId).ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }
}

/// <summary>
/// 通用模拟器适配器：适用于标准 ADB 客户端以及无需专属端口规则的启动程序。
/// </summary>
public class GenericEmulatorAdapter : IEmulatorAdapter
{
    private readonly Action<string, string> _emulatorLauncher;

    /// <summary>可替换宿主查询，测试无需访问真实管理器。</summary>
    internal Func<CancellationToken, Task<IReadOnlySet<string>>> ReadMuMuSerials { get; set; } =
        _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    /// <summary>宿主监听查询可替换，单元测试不访问真实 ADB 服务。</summary>
    internal Func<int, IReadOnlyList<int>> ReadShadowedAdbPorts { get; set; } = _ => [];

    /// <summary>MuMu 管理器缺失或查询失败时的只读监听探测；候选不进入管理器身份集合。</summary>
    internal Func<IReadOnlyList<int>> ReadMuMuListeningPorts { get; set; } = () => [];

    protected IAdbService Adb { get; }
    protected IGgeaLog Log { get; }

    public EmulatorProfile Profile { get; }
    public virtual string NoDeviceMessage => L.Text("等待模拟器设备上线超时，请确认模拟器已启动并开启 ADB 调试");

    public GenericEmulatorAdapter(
        EmulatorProfile profile,
        IAdbService adb,
        IGgeaLog log,
        Action<string, string> emulatorLauncher)
    {
        Profile = profile;
        Adb = adb;
        Log = log;
        _emulatorLauncher = emulatorLauncher;
    }

    /// <summary>通用设备发现不猜测端口，只信任所选 ADB 客户端实际返回的设备。</summary>
    public virtual async Task<IReadOnlyList<AdbDevice>> DiscoverDevicesAsync(
        int maxInstances = 3,
        CancellationToken ct = default)
    {
        var devices = await Adb.ListDevicesAsync(ct);
        IReadOnlyList<int> ports;
        try { ports = ReadShadowedAdbPorts(maxInstances); }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or System.Net.Sockets.SocketException)
        {
            Log.Warn("本机 ADB 监听信息读取失败，保留现有设备列表");
            ports = [];
        }
        var attempted = false;
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            var serial = $"127.0.0.2:{port}";
            if (devices.Any(device => device.IsAvailable && device.Serial == serial)) continue;
            attempted = true;
            // 精确绑定的蓝叠与通配绑定的雷电可共用端口；备用回环地址只用于连接，不证明品牌。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            try { await Adb.ConnectAsync("127.0.0.2", port, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { Log.Warn($"本机备用 ADB 通道连接超时: {serial}"); }
            catch (AdbException)
            { Log.Debug($"本机备用 ADB 通道不可用: {serial}"); }
        }
        if (attempted) devices = await Adb.ListDevicesAsync(ct);
        return await IdentifyDevicesAsync(devices, ct);
    }

    /// <summary>ADB 客户端可访问不同品牌；逐台读取属性，未知设备保持未知，不套用客户端品牌。</summary>
    protected async Task<IReadOnlyList<AdbDevice>> IdentifyDevicesAsync(
        IReadOnlyList<AdbDevice> devices, CancellationToken ct)
    {
        IReadOnlySet<string> mumuSerials = new HashSet<string>();
        try { mumuSerials = await ReadMuMuSerials(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Warn($"MuMu 实例信息不可用（{ex.GetType().Name}），尝试本机监听端口发现；品牌仍由设备属性确认"); }
        // 连接后重新枚举，使高编号或非默认端口实例在首次刷新中即可出现。
        var connectedNewEndpoint = false;
        foreach (var serial in mumuSerials.Where(serial => !devices.Any(device => device.IsAvailable &&
                     string.Equals(device.Serial, serial, StringComparison.OrdinalIgnoreCase))))
        {
            ct.ThrowIfCancellationRequested();
            var separator = serial.LastIndexOf(':');
            if (separator <= 0 || !int.TryParse(serial[(separator + 1)..], out var port) || port is < 1 or > 65535) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            try
            {
                await Adb.ConnectAsync(serial[..separator], port, timeout.Token);
                connectedNewEndpoint = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { Log.Warn("MuMu 管理器地址连接失败，保留已有设备"); }
        }
        if (connectedNewEndpoint)
        {
            try { devices = await Adb.ListDevicesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { Log.Warn("连接后读取设备列表失败，保留已有设备"); }
        }
        // 管理器可能只返回部分实例；先连接明确地址，再补查遗漏，避免重复连接已在线端点。
        devices = await DiscoverMuMuListenersAsync(devices, ct);
        // 管理器只提供 TCP 地址；用本次启动 UUID 验证 emulator-* 别名，避免按端口猜品牌。
        var identities = await Task.WhenAll(devices.Select(async device =>
        {
            try
            {
                var value = device.IsAvailable ? await Adb.GetBootIdAsync(device.Serial, ct) : string.Empty;
                return Guid.TryParse(value, out var id) ? id : Guid.Empty;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return Guid.Empty; }
        }));
        var mumuBootIds = devices.Select((device, index) => mumuSerials.Contains(device.Serial) ? identities[index] : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
        var identified = await Task.WhenAll(devices.Select(async (device, index) =>
        {
            if (!device.IsAvailable) return device with { Brand = null };
            if (mumuSerials.Contains(device.Serial) || mumuBootIds.Contains(identities[index]))
                return device with { Brand = "MuMu", BootId = identities[index] };
            try
            {
                // 空属性名读取完整 getprop，兼容伪装手机型号的模拟器。
                var properties = await Adb.GetSystemPropertyAsync(device.Serial, string.Empty, ct);
                var type = DetectDeviceType(properties);
                return device with { Brand = EmulatorTypes.GetBrandLabel(type), BootId = identities[index] };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                Log.Warn($"设备 {device.Serial} 的品牌属性读取失败，保留为未知设备");
                return device with { Brand = null, BootId = identities[index] };
            }
        }));
        // 相同启动 UUID 才去重，优先保留 emulator 别名以兼容已保存的选择。
        return identified.Where((device, index) => identities[index] == Guid.Empty ||
            identified.Select((candidate, other) => (candidate, other))
                .Where(pair => identities[pair.other] == identities[index])
                .OrderByDescending(pair => pair.candidate.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
                .First().other == index).ToList();
    }

    /// <summary>自动补连实际监听的候选，整批最多五秒、四路并发，不启动模拟器或选择账号。</summary>
    private async Task<IReadOnlyList<AdbDevice>> DiscoverMuMuListenersAsync(IReadOnlyList<AdbDevice> devices, CancellationToken ct)
    {
        IReadOnlyList<int> ports;
        try { ports = ReadMuMuListeningPorts(); }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or System.Net.Sockets.SocketException)
        {
            Log.Warn("MuMu 本机监听信息不可用，保留现有设备列表");
            return devices;
        }
        var candidates = ports.Where(port => port is >= 16384 and <= 32768 && (port - 16384) % 32 == 0)
            .Distinct().Where(port => !devices.Any(device => device.IsAvailable && device.Serial == $"127.0.0.1:{port}"))
            .Take(16).ToArray();
        if (candidates.Length == 0) return devices;
        Log.Info($"MuMu 设备补充发现：自动检查 {candidates.Length} 个尚未在线的本机监听端口；不自动选择设备");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(5000);
        using var gate = new SemaphoreSlim(4);
        var connected = await Task.WhenAll(candidates.Select(async port =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(budget.Token);
                entered = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                timeout.CancelAfter(1500);
                await Adb.ConnectAsync("127.0.0.1", port, timeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
            catch (AdbException) { return false; }
            finally { if (entered) gate.Release(); }
        }));
        ct.ThrowIfCancellationRequested();
        if (!connected.Any(success => success)) return devices;
        try { return await Adb.ListDevicesAsync(ct); }
        catch (AdbException) { Log.Warn("MuMu 监听端口补连后读取列表失败，保留现有设备"); return devices; }
    }

    /// <summary>仅依据设备端品牌标记判断；普通手机型号和共享端口不能作为品牌证据。</summary>
    internal static string DetectDeviceType(string properties)
    {
        // 旧版可能仅暴露 NetEase 厂商名；限定完整制造商字段和值，避免游戏包名误判。
        if (Regex.IsMatch(properties,
                @"(?im)^\s*\[ro\.product\.(?:(?:vendor|system|odm|product|system_ext)\.)?manufacturer\]:\s*\[netease\]\s*$"))
            return EmulatorTypes.MuMu;
        if (new[] { "mumu", "nemu" }.Any(key => properties.Contains(key, StringComparison.OrdinalIgnoreCase)))
            return EmulatorTypes.MuMu;
        if (Regex.IsMatch(properties, @"(?im)^\s*\[init\.svc\.bstsvcmgrtest\]:\s*\[(?:running|stopped|restarting)\]\s*$") ||
            new[] { "bluestacks", "bst." }.Any(key => properties.Contains(key, StringComparison.OrdinalIgnoreCase)))
            return EmulatorTypes.BlueStacks;
        if (new[] { "ldplayer", "leidian", "ldinit", "ld_service", "ro.ld." }.Any(key => properties.Contains(key, StringComparison.OrdinalIgnoreCase)))
            return EmulatorTypes.Leidian;
        // 限定设备厂商字段，不把游戏包名、手机型号或宿主 ADB 文件名当作通道证据。
        if (Regex.IsMatch(properties,
                @"(?im)^\s*\[ro\.product\.(?:(?:vendor|system|odm|product|system_ext)\.)?manufacturer\]:\s*\[nox\]\s*$"))
            return EmulatorTypes.Nox;
        if (Regex.IsMatch(properties,
                @"(?im)^\s*\[ro\.product\.(?:(?:vendor|system|odm|product|system_ext)\.)?manufacturer\]:\s*\[microvirt\]\s*$"))
            return EmulatorTypes.Memu;
        return EmulatorTypes.Custom;
    }

    /// <summary>
    /// 通用启动流程只接受唯一设备：多设备时无法证明哪一个属于刚启动的实例，必须让用户先手动选择。
    /// </summary>
    public virtual async Task<EmulatorStartResult> StartAndOpenGameAsync(
        EmulatorLaunchOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolveEmulatorPath(options.EmulatorPath);
        var args = string.IsNullOrWhiteSpace(options.EmulatorArgs)
            ? Profile.DefaultArgs
            : options.EmulatorArgs;
        var before = await GetAvailableSerialsAsync(ct);
        if (before.Count > 0)
        {
            // 未指定目标设备时先拒绝启动，避免启动副作用后仍无法证明目标实例归属。
            return EmulatorStartResult.Fail(L.Text("检测到已连接设备（可能有多个），无法确定目标模拟器；请先在设备列表中明确选择"));
        }

        Log.Info($"启动模拟器: {path} {args}");
        ct.ThrowIfCancellationRequested();
        _emulatorLauncher(path, args);

        var selection = await WaitForUniqueDeviceAsync(
            options.DeviceWaitTimeoutMs, options.PollIntervalMs, ct);
        if (selection.MultipleDevices)
        {
            return EmulatorStartResult.Fail(L.Text("模拟器启动后出现多个设备，无法安全确定目标；请刷新后手动选择"));
        }
        var serial = selection.Serial;

        if (serial is null)
        {
            Log.Error(NoDeviceMessage);
            return EmulatorStartResult.Fail(NoDeviceMessage);
        }

        return await WaitForBootAndLaunchGameAsync(serial, options, ct);
    }

    /// <summary>解析启动路径；通用适配器要求用户明确配置。</summary>
    protected virtual string ResolveEmulatorPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("未配置模拟器路径，请先在设置中选择模拟器");
        }

        return configuredPath;
    }

    /// <summary>获取当前所有可操作设备的序列号。</summary>
    protected async Task<HashSet<string>> GetAvailableSerialsAsync(CancellationToken ct)
    {
        var devices = await Adb.ListDevicesAsync(ct);
        return devices
            .Where(device => device.IsAvailable)
            .Select(device => device.Serial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>等待设备上线；若同时出现多个设备则拒绝猜测目标。</summary>
    protected async Task<(string? Serial, bool MultipleDevices)> WaitForUniqueDeviceAsync(
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var serials = await GetAvailableSerialsAsync(ct);
            if (serials.Count == 1)
            {
                return (serials.Single(), false);
            }

            if (serials.Count > 1)
            {
                return (null, true);
            }

            await Task.Delay(pollIntervalMs, ct);
        }

        return (null, false);
    }

    /// <summary>等待 Android 启动完成后打开游戏；启动状态超时不会阻止最后一次打开尝试。</summary>
    protected async Task<EmulatorStartResult> WaitForBootAndLaunchGameAsync(
        string serial,
        EmulatorLaunchOptions options,
        CancellationToken ct)
    {
        Log.Info($"模拟器设备已就绪: {serial}");
        var booted = await WaitForBootCompletedAsync(
            serial,
            options.BootCompletedTimeoutMs,
            options.PollIntervalMs,
            ct);
        if (!booted)
        {
            Log.Warn("系统启动完成检测超时，仍尝试打开游戏");
        }

        if (options.BeforeGameLaunchAsync is not null)
        {
            await options.BeforeGameLaunchAsync(serial, ct);
        }

        ct.ThrowIfCancellationRequested();
        await Adb.LaunchAppAsync(serial, options.GamePackage, ct);
        return EmulatorStartResult.Ok(serial, L.Format($"设备 {serial} 已打开游戏 {options.GamePackage}"));
    }

    /// <summary>轮询 sys.boot_completed，等待 Android 系统完成启动。</summary>
    protected async Task<bool> WaitForBootCompletedAsync(
        string serial,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await Adb.GetSystemPropertyAsync(serial, "sys.boot_completed", ct) == "1")
                {
                    return true;
                }
            }
            catch (AdbException ex)
            {
                Log.Warn($"读取系统启动状态失败: {ex.Message}");
            }

            await Task.Delay(pollIntervalMs, ct);
        }

        return false;
    }
}

/// <summary>BlueStacks 5 适配器：使用 HD-Adb.exe，并提供 ADB 开关的针对性诊断。</summary>
public sealed class BlueStacksEmulatorAdapter : GenericEmulatorAdapter
{
    public override string NoDeviceMessage =>
        L.Text("未检测到 BlueStacks ADB 设备；请在 BlueStacks 设置 → 高级中启用 Android Debug Bridge 并保存");

    public BlueStacksEmulatorAdapter(
        IAdbService adb,
        IGgeaLog log,
        Action<string, string> emulatorLauncher)
        : base(EmulatorTypes.GetProfile(EmulatorTypes.BlueStacks), adb, log, emulatorLauncher)
    {
    }

    /// <summary>配置为空时自动探测 HD-Player.exe，保留手动路径作为最高优先级。</summary>
    protected override string ResolveEmulatorPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return EmulatorLocator.FindBlueStacksInstallation()?.LauncherPath
               ?? throw new InvalidOperationException("未检测到 BlueStacks 5，请先在设置中选择 HD-Player.exe");
    }
}

/// <summary>MuMu 内置适配器：封装实例端口连接、设备去重、运行状态与后台缩放能力。</summary>
public sealed class MuMuEmulatorAdapter : GenericEmulatorAdapter
{
    /// <summary>单实例响应查询可替换；测试不启动真实管理器。</summary>
    internal Func<string, int, CancellationToken, Task<string>> ReadInstanceInfoAsync { get; set; }

    private readonly Func<string?> _pathResolver;
    private readonly Func<string, int, CancellationToken, Task<bool>> _runningCheck;
    private readonly Action<string, string> _emulatorLauncher;

    public MuMuEmulatorAdapter(
        IAdbService adb,
        IGgeaLog log,
        Action<string, string> emulatorLauncher,
        Func<string?> pathResolver,
        Func<string, int, CancellationToken, Task<bool>>? runningCheck = null)
        : base(EmulatorTypes.GetProfile(EmulatorTypes.MuMu), adb, log, emulatorLauncher)
    {
        _pathResolver = pathResolver;
        _emulatorLauncher = emulatorLauncher;
        _runningCheck = runningCheck ?? IsInstanceRunningAsync;
        ReadInstanceInfoAsync = QueryInstanceInfoAsync;
    }

    /// <summary>启动指定 MuMu 实例并查询管理器真实地址，不依赖设备列表顺序或端口公式。</summary>
    public override async Task<EmulatorStartResult> StartAndOpenGameAsync(
        EmulatorLaunchOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolveEmulatorPath(options.EmulatorPath);
        var args = string.IsNullOrWhiteSpace(options.EmulatorArgs)
            ? Profile.DefaultArgs
            : options.EmulatorArgs;
        var index = ParseVmIndex(args);

        if (await _runningCheck(path, index, ct))
        {
            Log.Info($"MuMu 实例 {index} 已在运行，将查询真实 ADB 地址");
        }
        else
        {
            Log.Info($"MuMu 实例 {index} 未运行，正在启动…");
            ct.ThrowIfCancellationRequested();
            _emulatorLauncher(path, args);
        }

        var serial = await WaitForDevicePortAsync(
            path,
            index,
            options.DeviceWaitTimeoutMs,
            options.PollIntervalMs,
            ct);
        if (serial is null)
        {
            return EmulatorStartResult.Fail(L.Format($"等待 MuMu 实例 {index} 真实 ADB 地址及设备上线超时，未连接推算端口"));
        }

        return await WaitForBootAndLaunchGameAsync(serial, options, ct);
    }

    /// <summary>配置为空时自动探测 MuMuManager.exe。</summary>
    protected override string ResolveEmulatorPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return _pathResolver()
               ?? throw new InvalidOperationException("未检测到 MuMu 模拟器，请先在设置中选择 MuMuManager.exe");
    }

    /// <summary>在总时限内查询指定实例的真实端点；地址缺失时等待，不操作其他在线设备。</summary>
    private async Task<string?> WaitForDevicePortAsync(
        string managerPath, int index, int timeoutMs, int pollIntervalMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, timeoutMs));
        var token = timeout.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // 管理器损坏或报错立即停止，不能用猜测的端口掩盖查询失败。
                var endpoint = MuMuDeviceIdentity.ParseEndpoint(await ReadInstanceInfoAsync(managerPath, index, token));
                if (endpoint is not null)
                {
                    try
                    {
                        await Adb.ConnectAsync(endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host, endpoint.Port, token);
                        var devices = await Adb.ListDevicesAsync(token);
                        if (devices.Any(device => device.IsAvailable &&
                            device.Serial.Equals(endpoint.Serial, StringComparison.OrdinalIgnoreCase)))
                        {
                            token.ThrowIfCancellationRequested();
                            return endpoint.Serial;
                        }
                    }
                    catch (AdbException ex) { Log.Warn($"连接 MuMu 实例 {index} 失败: {ex.Message}"); }
                }
                await Task.Delay(Math.Max(1, pollIntervalMs), token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>从启动参数解析 MuMu 实例序号；缺省为第一个实例。</summary>
    internal static int ParseVmIndex(string args)
    {
        var match = Regex.Match(args ?? string.Empty, @"(?:--vmindex|-v)\s+(\d+)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>复用限时进程执行器并排空双管道；查询失败停止启动，不能把未知状态当作未运行。</summary>
    internal async Task<bool> IsInstanceRunningAsync(string managerPath, int vmIndex, CancellationToken ct)
    {
        return ParseRunningState(await ReadInstanceInfoAsync(managerPath, vmIndex, ct));
    }

    /// <summary>状态和地址共享同一查询命令及超时机制，避免不同路径解释出不同实例。</summary>
    private async Task<string> QueryInstanceInfoAsync(string managerPath, int vmIndex, CancellationToken ct)
    {
        var manager = new AdbService(managerPath, Log);
        return await manager.RunTextAsync(
            ["info", "--vmindex", vmIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)], 5000, ct);
    }

    /// <summary>运行状态必须由管理器明确给出；错误、缺字段与损坏响应都不能触发启动。</summary>
    internal static bool ParseRunningState(string output)
    {
        try
        {
            using var json = MuMuManagerJson.Parse(output);
            var root = json.RootElement;
            if (root.TryGetProperty("error_code", out var errorCode) && errorCode.GetInt32() != 0)
                throw new AdbException("MuMu 管理器报告实例查询失败，已停止启动");
            if (!root.TryGetProperty("is_android_started", out var started) ||
                started.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AdbException("MuMu 管理器未返回有效运行状态，已停止启动");
            return started.GetBoolean();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new AdbException("MuMu 实例状态格式无效，已停止启动", ex);
        }
    }
}

/// <summary>统一启动模拟器进程，避免各适配器重复处理 Windows 进程选项。</summary>
internal static class EmulatorProcessLauncher
{
    public static void Launch(string path, string args)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("未配置模拟器路径，请先在设置中选择模拟器");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = args ?? string.Empty,
            UseShellExecute = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动模拟器进程: {path}");
    }
}
