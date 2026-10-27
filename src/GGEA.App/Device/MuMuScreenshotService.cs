using System.IO;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>确认选中 ADB 设备对应的 MuMu 实例，再使用隔离进程读取目标游戏的原生画面。</summary>
internal sealed class MuMuScreenshotService
{
    internal sealed record Instance(int Index, string Serial, string? AndroidVersion);
    private readonly AdbService _adb;
    private readonly IGgeaLog _log;
    private readonly string _manager;
    private readonly string _package;
    private int _reported;

    /// <summary>实例映射与命令执行依赖现有 ADB 服务，绝不按 emulator-N 序号猜测多开编号。</summary>
    internal MuMuScreenshotService(AdbService adb, IGgeaLog log, string manager, string package)
    { _adb = adb; _log = log; _manager = manager; _package = package; }

    /// <summary>整个原生截图链路使用同一超时令牌；SDK 卡死只会终止本次截图子进程。</summary>
    internal async Task<byte[]> CaptureAsync(string serial, int timeoutMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var token = timeout.Token;
            var manager = new AdbService(_manager, _log);
            var json = await MuMuDeviceIdentity.QueryAllAsync(
                (args, cancellation) => manager.RunTextAsync(args, timeoutMs, cancellation), token);
            var instance = await ResolveAsync(ParseInstances(json), serial, _adb.GetBootIdAsync, token,
                async (address, cancellation) =>
                {
                    // 属性已识别为 MuMu 时不会经过通道路由的连接分支，截图必须自行准备别名端点。
                    var separator = address.LastIndexOf(':');
                    await _adb.ConnectAsync(address[..separator], int.Parse(address[(separator + 1)..]), cancellation);
                });
            var root = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(_manager))!)!.FullName;
            var library = await ResolveLibraryAsync(root, instance.AndroidVersion,
                cancellation => _adb.GetSystemPropertyAsync(serial, "ro.build.version.release", cancellation), token);
            var helperPath = Path.Combine(AppContext.BaseDirectory, "GGEA.exe");
            if (!File.Exists(helperPath)) throw new AdbException("MuMu 截图辅助程序 GGEA.exe 不存在，请使用完整构建或发布目录");
            var output = Path.Combine(Path.GetTempPath(), $"ggea-mumu-{Guid.NewGuid():N}.png");
            try
            {
                // 复用经过取消/超时测试的子进程回收代码；输出文件避免原生 DLL 的 stdout 污染 PNG。
                var helper = new AdbService(helperPath, _log);
                var detail = await helper.RunTextAsync(["--mumu-capture", library, root, instance.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), _package, output], timeoutMs, token);
                token.ThrowIfCancellationRequested();
                var png = await File.ReadAllBytesAsync(output, token);
                AdbService.ValidateScreenshot(png);
                var summary = System.Text.RegularExpressions.Regex.Match(detail, @"(?m)^MuMuNative instance=\d+, display=\d+, size=\d+x\d+\r?$").Value.Trim();
                if (string.IsNullOrEmpty(summary)) throw new AdbException("MuMu 截图辅助进程未返回有效显示信息");
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                    _log.Info($"截图通道=MuMuNative，serial={serial}，instance={instance.Index}，package={_package}；{summary}");
                return png;
            }
            finally { try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AdbException($"MuMu 原生截图超时（{timeoutMs}ms）"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not AdbException)
        { throw new AdbException($"MuMu 原生截图不可用（{ex.GetType().Name}）", ex); }
    }

    /// <summary>实例身份只依赖在线状态、索引和地址；SDK 版本元数据缺失不能让在线实例消失。</summary>
    internal static IReadOnlyList<Instance> ParseInstances(string json)
    {
        using var document = MuMuManagerJson.Parse(json);
        var result = new List<Instance>();
        foreach (var (index, data) in MuMuManagerJson.EnumerateInstances(document.RootElement))
        {
            if (!data.TryGetProperty("is_android_started", out var started) || started.ValueKind != JsonValueKind.True ||
                MuMuDeviceIdentity.ReadEndpoint(data) is not { } endpoint) continue;
            var android = data.TryGetProperty("android_version", out var version)
                ? NormalizeVersion(version.ValueKind == JsonValueKind.String ? version.GetString()
                    : version.ValueKind == JsonValueKind.Number ? version.GetRawText() : null) : null;
            result.Add(new Instance(index, endpoint.Serial, android));
        }
        return result;
    }

    /// <summary>别名通过启动标识匹配；不缓存映射，防止模拟器重启或多开切换后读取旧实例。</summary>
    internal static async Task<Instance> ResolveAsync(IReadOnlyList<Instance> instances, string selected,
        Func<string, CancellationToken, Task<string>> readBootId, CancellationToken ct,
        Func<string, CancellationToken, Task>? connectCandidate = null)
    {
        ct.ThrowIfCancellationRequested();
        if (instances.Count == 0)
            throw new AdbException("MuMu 管理器未返回可用在线实例（需在线状态、实例索引和 ADB 地址）；请将本次日志反馈开发者");
        var exact = instances.Where(i => i.Serial.Equals(selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        var boot = await readBootId(selected, ct);
        if (!Guid.TryParse(boot, out var selectedId) || selectedId == Guid.Empty)
            throw new AdbException("无法确认所选设备的 MuMu 实例：启动标识无效");
        var matches = new List<Instance>();
        var unreadable = 0;
        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string candidateBoot;
                try { candidateBoot = await readBootId(instance.Serial, ct); }
                catch (AdbException) when (connectCandidate is not null)
                {
                    // 仅端点读取失败时补连，避免每帧额外执行 adb connect 并刷屏连接日志。
                    await connectCandidate(instance.Serial, ct);
                    candidateBoot = await readBootId(instance.Serial, ct);
                }
                if (!Guid.TryParse(candidateBoot, out var id) || id == Guid.Empty) unreadable++;
                else if (id == selectedId) matches.Add(instance);
            }
            catch (AdbException) { unreadable++; /* 离线或断开的候选不参与匹配，禁止据此猜测实例。 */ }
        }
        if (matches.Count != 1) throw new AdbException($"无法唯一确认所选设备的 MuMu 实例（候选={instances.Count}，匹配={matches.Count}，不可读={unreadable}）；请将本次日志反馈开发者");
        return matches[0];
    }

    /// <summary>仅把受限数字版本转换为目录名；无效元数据不参与路径拼接。</summary>
    private static string? NormalizeVersion(string? value)
    {
        if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,3}(\.\d{1,3}){0,2}$")) return null;
        return value.Contains('.') ? value : value + ".0";
    }

    /// <summary>旧安装布局无需版本信息；新版布局缺少元数据时自动查询已确认设备，不猜测其他 Android 目录。</summary>
    internal static async Task<string> ResolveLibraryAsync(string root, string? androidVersion,
        Func<CancellationToken, Task<string>> readVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (androidVersion is not null) return FindLibrary(root, androidVersion);
        var common = CommonLibraries(root).FirstOrDefault(File.Exists);
        if (common is not null) return common;
        var version = NormalizeVersion((await readVersion(ct)).Trim());
        ct.ThrowIfCancellationRequested();
        if (version is null) throw new AdbException("MuMu 实例已确认，但无法读取有效 Android 版本以定位截图 SDK；请将本次日志反馈开发者");
        return FindLibrary(root, version);
    }

    /// <summary>旧版 MuMu 使用安装目录共用 SDK，不需要按 Android 版本选择。</summary>
    private static string[] CommonLibraries(string root) =>
        [Path.Combine(root, "shell", "sdk", "external_renderer_ipc.dll"), Path.Combine(root, "nx_main", "sdk", "external_renderer_ipc.dll")];

    /// <summary>仅从当前实例 Android 版本或管理器安装目录加载 SDK，不扫描或猜选其他版本。</summary>
    internal static string FindLibrary(string root, string androidVersion)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(androidVersion, @"^\d+(\.\d+)*$")) throw new AdbException("MuMu Android 版本无效");
        string[] candidates = [Path.Combine(root, "nx_device", androidVersion, "shell", "sdk", "external_renderer_ipc.dll"),
            .. CommonLibraries(root)];
        return candidates.FirstOrDefault(File.Exists) ?? throw new AdbException("当前 MuMu 安装未提供原生截图 SDK");
    }
}
