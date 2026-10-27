using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>
/// ADB 服务：直接调用 adb.exe 完成设备检测、截图、点击、滑动与按键。
/// </summary>
public sealed class AdbService : IAdbService
{
    private readonly Func<MuMuScreenshotService?> _mumuScreenshot;
    private readonly MuMuScreenshotChannel _mumuScreenshotChannel;
    private readonly MuMuInputService _mumuInput;
    private readonly bool _preferMuMuNativeScreenshot;
    /// <summary>统一设备命令边界；测试仅替换进程 I/O，仍经过生产截图和输入决策。</summary>
    internal Func<IReadOnlyList<string>, int, CancellationToken, Task<string>> DeviceTextAsync { get; set; }
    internal Func<IReadOnlyList<string>, int, CancellationToken, Task<byte[]>> DeviceBytesAsync { get; set; }
    private readonly BlueStacksShell? _blueStacksShell;
    private int _prefixRecoveryReported;
    /// <summary>组合根绑定一次，覆盖普通与原生截图以及直接 ADB 调用；设备层不依赖具体异常识别实现。</summary>
    internal IAdbExecutionObserver? ExecutionObserver { get; set; }
    private readonly EnvironmentDiagnostics? _diagnostics;
    private readonly string _emulatorPath;
    private readonly string _adbPath;
    private readonly IGgeaLog _log;
    private readonly int _commandTimeoutMs;
    private readonly int _screenshotTimeoutMs;

    /// <summary>注入可选环境采集器；由窗口共享其限频状态，设置变更后保留诊断关联。</summary>
    public AdbService(string adbPath, IGgeaLog log, int commandTimeoutMs = 15000, int screenshotTimeoutMs = 30000, EnvironmentDiagnostics? diagnostics = null, string emulatorPath = "", bool preferMuMuNativeScreenshot = true, string gamePackage = "com.bandainamcoent.gget_WW")
    {
        // 仅为蓝叠配套客户端启用兼容探测，不改变其它模拟器的输入协议。
        if (Path.GetFileName(adbPath).Equals("HD-Adb.exe", StringComparison.OrdinalIgnoreCase))
            _blueStacksShell = new BlueStacksShell(
                (args, timeout, ct) => RunRawTextAsync(args, timeout, ct, includeStandardError: true), log);
        _preferMuMuNativeScreenshot = preferMuMuNativeScreenshot;
        DeviceTextAsync = (args, timeout, ct) => RunTextAsync(args, timeout, ct, includeStandardError: true);
        DeviceBytesAsync = RunBytesAsync;
        Func<string?> manager = () =>
            Path.GetFileName(emulatorPath).Equals("MuMuManager.exe", StringComparison.OrdinalIgnoreCase)
                ? emulatorPath : EmulatorLocator.ResolveMuMuManager(adbPath, emulatorPath);
        // 只缓存已创建的截图服务；首次安装发现为空时，后续刷新/启动模拟器后仍可重新发现。
        MuMuScreenshotService? screenshot = null;
        var screenshotGate = new object();
        _mumuScreenshot = () =>
        {
            lock (screenshotGate)
                return screenshot ??= manager() is { } path
                    ? new MuMuScreenshotService(this, log, path, gamePackage) : null;
        };
        // 所有品牌走相同命令链；仅非默认游戏显示屏需要特殊投递，不读取品牌或管理器身份。
        _mumuInput = new MuMuInputService(gamePackage,
            (args, ct) => DeviceTextAsync(args, args.Contains("dumpsys") ? Math.Min(commandTimeoutMs, 1500) : commandTimeoutMs, ct));
        _diagnostics = diagnostics;
        _emulatorPath = emulatorPath;
        _adbPath = adbPath;
        _log = log;
        _commandTimeoutMs = commandTimeoutMs;
        _screenshotTimeoutMs = screenshotTimeoutMs;
        _mumuScreenshotChannel = new MuMuScreenshotChannel(
            (serial, ct) => (_mumuScreenshot() ?? throw new AdbException("未找到 MuMu 管理器，无法使用原生截图"))
                .CaptureAsync(serial, Math.Max(1, _screenshotTimeoutMs / 2), ct),
            CaptureAdbScreenshotAsync, _mumuInput.ResolveDisplayAsync, GetBootIdAsync, log);
    }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var output = await RunTextAsync(new[] { "devices", "-l" }, _commandTimeoutMs, ct);
        var devices = ParseDevices(output);
        _log.Info($"检测到 {devices.Count} 个 ADB 设备");
        foreach (var device in devices)
        {
            _log.Info($"设备: {device.Serial} ({device.State})");
        }

        return devices;
    }

    /// <summary>只重建用户明确选择的离线 TCP 通道；重新读状态验证结果，不重启共享 ADB 服务。</summary>
    public async Task ReconnectDeviceAsync(AdbDevice selected, CancellationToken ct = default)
    {
        if (!selected.CanReconnect)
            throw new AdbException("仅支持重连带明确 IP 和端口的离线设备，请先刷新并选择目标");
        ct.ThrowIfCancellationRequested();
        var devices = ParseDevices(await DeviceTextAsync(["devices", "-l"], _commandTimeoutMs, ct));
        var current = devices.FirstOrDefault(device => device.Serial == selected.Serial);
        if (current?.IsAvailable == true) return;
        if (current?.IsOffline != true)
            throw new AdbException("目标已消失或状态改变，请刷新后重新选择；未重连其他设备");

        _log.Info($"正在重连离线设备: {selected.Serial}");
        await DeviceTextAsync(["disconnect", selected.Serial], _commandTimeoutMs, ct);
        ct.ThrowIfCancellationRequested();
        await DeviceTextAsync(["connect", selected.Serial], _commandTimeoutMs, ct);
        ct.ThrowIfCancellationRequested();
        // connect 的成功文字不等于设备可操作，必须再次确认同一地址的 device 状态。
        devices = ParseDevices(await DeviceTextAsync(["devices", "-l"], _commandTimeoutMs, ct));
        if (!devices.Any(device => device.Serial == selected.Serial && device.IsAvailable))
            throw new AdbException("重连后目标仍未就绪，请等待模拟器启动完成或重启该实例后刷新");
        _log.Info($"设备重连成功: {selected.Serial}；请重新选择目标后再启动任务");
    }

    /// <summary>
    /// 获取 ADB 版本信息，用于设置页验证所选 adb.exe 是否可用。
    /// </summary>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var output = await RunTextAsync(new[] { "version" }, _commandTimeoutMs, ct);
        return output.Trim();
    }

    /// <summary>截图失败时保留实际目标与阶段，便于仅凭用户日志定位；取消仍按取消传播。</summary>
    public async Task<byte[]> ScreenshotAsync(string serial, CancellationToken ct = default)
    {
        var png = await CaptureScreenshotAsync(serial, ct);
        if (ExecutionObserver is { } observer)
            await observer.OnScreenshotAsync(serial, png, ct);
        return png;
    }

    /// <summary>截图传输与兼容校验有独立预算；异常诊断在预算外执行，不能被包装成 ADB 捕获失败。</summary>
    private async Task<byte[]> CaptureScreenshotAsync(string serial, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var stage = "读取游戏显示屏";
        var command = "exec-out screencap -p";
        var requestToken = ct;
        using var screenshotTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        screenshotTimeout.CancelAfter(_screenshotTimeoutMs);
        ct = screenshotTimeout.Token;
        try
        {
            var display = await _mumuInput.TryResolveDisplayAsync(serial, ct);
            if (display is > 0)
            {
                if (!_preferMuMuNativeScreenshot)
                    throw new AdbException("游戏位于独立显示屏，请启用原生截图；未截取安卓桌面");
                stage = "MuMu 截图能力探测及安全回退";
                command = "MuMuNative -> ADB（校验后回退）";
                // 身份查询与两种截图共用总预算，防止多个内部超时叠加。
                return await _mumuScreenshotChannel.CaptureAsync(serial, ct);
            }
            stage = "执行命令";
            var bytes = await DeviceBytesAsync(
                new[] { "-s", serial, "exec-out", "screencap", "-p" },
                _screenshotTimeoutMs, ct);
            stage = "校验输出（exit=0）";
            ct.ThrowIfCancellationRequested();
            var png = AdbScreenshotOutput.Normalize(bytes, out var prefixLength, out var newlineRecovered);
            if (await _mumuInput.TryResolveDisplayAsync(serial, ct) is > 0)
                throw new AdbException("截图期间游戏切换至独立显示屏，本帧已丢弃");
            ct.ThrowIfCancellationRequested();
            if ((prefixLength > 0 || newlineRecovered) && Interlocked.Exchange(ref _prefixRecoveryReported, 1) == 0)
                _log.Warn($"截图输出兼容恢复 [serial={serial}]: 已移除 {prefixLength} 字节前缀，换行转换恢复={newlineRecovered}，PNG 结构、CRC 与像素解码验证通过；后续同类恢复不重复提示，未记录前缀内容");
            return png;
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            throw new AdbException($"截图准备超过总时限（{_screenshotTimeoutMs}ms），阶段={stage}；本次截图停止");
        }
        catch (AdbException ex)
        {
            timer.Stop(); // 命令耗时不包含环境日志采集时间。
            var environment = _diagnostics is null ? "environment=disabled"
                : await _diagnostics.CaptureAsync("screenshot-failure", _adbPath, _emulatorPath, serial, stage);
            throw new AdbException(
                $"截图失败 [serial={serial}, adb={_adbPath}, command={command}, " +
                $"{environment}, stage={stage}, elapsedMs={timer.ElapsedMilliseconds}, timeoutMs={_screenshotTimeoutMs}]: {ex.Message}", ex);
        }
    }

    /// <summary>校验完整签名并仅记录结构特征，不将未知输出文本或截图内容写入日志。</summary>
    internal static void ValidateScreenshot(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.AsSpan().StartsWith(signature)) return;

        // 有界扫描用于失败诊断；前缀恢复由 AdbScreenshotOutput 严格验证后处理。
        var prefix = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        var offset = prefix.IndexOf(signature);
        ReadOnlySpan<byte> crlfSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0D, 0x0A, 0x1A, 0x0D, 0x0A];
        var kind = bytes.Length == 0 ? "空输出"
            : offset > 0 ? "PNG 前存在额外输出"
            : prefix.StartsWith(crlfSignature) ? "PNG 签名疑似发生换行转换"
            : bytes.Length < 8 ? "输出不足完整签名长度"
            : "未知格式或签名损坏";
        throw new AdbException(
            $"返回内容不是 PNG（长度 {bytes.Length} 字节，类型={kind}，前4096字节内PNG偏移={offset}）；未记录原始内容");
    }

    /// <summary>点击当前设备，独立游戏显示屏在每次执行前重新确认。</summary>
    public async Task TapAsync(string serial, int x, int y, CancellationToken ct = default)
    {
        _log.Info($"点击: ({x}, {y})");
        await ExecuteInputAsync(serial, ["tap", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)], ct);
    }

    /// <summary>滑动与点击共用显示屏路由，避免列表滑动落入模拟器桌面。</summary>
    public async Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default)
    {
        _log.Info($"滑动: ({x1}, {y1}) -> ({x2}, {y2}) 时长 {durationMs}ms");
        await ExecuteInputAsync(serial, ["swipe", x1.ToString(CultureInfo.InvariantCulture), y1.ToString(CultureInfo.InvariantCulture),
            x2.ToString(CultureInfo.InvariantCulture), y2.ToString(CultureInfo.InvariantCulture), durationMs.ToString(CultureInfo.InvariantCulture)], ct);
    }

    /// <summary>
    /// 通过 Linux 多点触控事件执行双指向内捏合。
    /// Android 的 input swipe 只能产生单指事件，因此先从 getevent 能力信息中定位触摸设备，
    /// 再用 Protocol B 的两个触点同步移动，确保游戏收到真正的缩小手势。
    /// </summary>
    public async Task PinchInAsync(string serial, int durationMs = 500, CancellationToken ct = default)
    {
        if (durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs), "双指缩放时长必须大于 0");
        }

        // 原始触摸设备事件无法指定 Android Display，不能在独立游戏显示屏上冒险投递。
        if (await _mumuInput.TryResolveDisplayAsync(serial, ct) is > 0)
            throw new AdbException("MuMu 独立游戏显示屏不支持原始双指事件，已停止，避免操作安卓桌面");

        var capabilities = await RunTextAsync(
            new[] { "-s", serial, "shell", "getevent", "-pl" },
            _commandTimeoutMs,
            ct);
        var touchDevice = ParseMultiTouchDevice(capabilities)
            ?? throw new AdbException("未找到支持 ABS_MT_SLOT 的多点触控设备，无法执行双指缩放");

        var script = BuildPinchInScript(touchDevice, durationMs);
        _log.Info($"双指缩小: {touchDevice.Path}，时长 {durationMs}ms");
        await RunTextAsync(
            // adb shell 会重新拼接参数；显式保留单引号，避免脚本首条 sendevent 被 sh -c 拆散。
            new[] { "-s", serial, "shell", "sh", "-c", $"'{script}'" },
            Math.Max(_commandTimeoutMs, durationMs + 5000),
            ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    /// <summary>按键也绑定游戏显示屏，避免依赖当前桌面焦点。</summary>
    public async Task KeyEventAsync(string serial, string keyCode, CancellationToken ct = default)
    {
        _log.Info($"按键: {keyCode}");
        await ExecuteInputAsync(serial, ["keyevent", keyCode], ct);
    }

    /// <summary>备用截图使用同一二进制通道及换行兼容逻辑，不切换设备、不修改持久配置。</summary>
    private async Task<byte[]> CaptureAdbScreenshotAsync(string serial, CancellationToken ct)
    {
        var bytes = await DeviceBytesAsync(["-s", serial, "exec-out", "screencap", "-p"], _screenshotTimeoutMs, ct);
        ct.ThrowIfCancellationRequested();
        return AdbScreenshotOutput.Normalize(bytes, out _);
    }

    /// <summary>输入仅依据游戏所在显示屏；品牌、端口范围与管理器可用性不参与准入。</summary>
    private async Task ExecuteInputAsync(string serial, IReadOnlyList<string> input, CancellationToken ct)
    {
        if (await _mumuInput.TryResolveDisplayAsync(serial, ct) is > 0)
            await _mumuInput.ExecuteAsync(serial, input, ct);
        else
            await DeviceTextAsync(["-s", serial, "shell", "input", .. input], _commandTimeoutMs, ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    public async Task<string> GetSystemPropertyAsync(string serial, string property, CancellationToken ct = default)
    {
        var value = await RunTextAsync(new[] { "-s", serial, "shell", "getprop", property }, _commandTimeoutMs, ct);
        return value.Trim();
    }

    /// <summary>同一内核的不同 ADB 连接共享 boot_id；不使用可伪装的型号进行去重。</summary>
    public async Task<string> GetBootIdAsync(string serial, CancellationToken ct = default)
        => (await RunTextAsync(new[] { "-s", serial, "shell", "cat", "/proc/sys/kernel/random/boot_id" }, _commandTimeoutMs, ct)).Trim();

    public async Task LaunchAppAsync(string serial, string package, CancellationToken ct = default)
    {
        _log.Info($"打开应用: {package} (设备 {serial})");
        await RunTextAsync(
            new[] { "-s", serial, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1" },
            _commandTimeoutMs,
            ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    public async Task<string> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var target = $"{host}:{port}";
        var output = await RunTextAsync(new[] { "connect", target }, _commandTimeoutMs, ct);
        var message = output.Trim();
        _log.Info($"ADB 连接: {target} -> {message}");

        // adb connect 失败时返回文本而非非零退出码，需要显式判断
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdbException($"无法连接设备 {target}: {message}");
        }

        return message;
    }

    /// <summary>
    /// 解析 adb devices -l 的文本输出。
    /// 单独提取便于单元测试。
    /// </summary>
    internal static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var rawLine in output.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] == "*" ||
                parts[1] is not ("device" or "offline" or "unauthorized" or "recovery" or "sideload" or "bootloader" or "host" or "no"))
            {
                continue;
            }

            // 从扩展字段（如 model:xxx）中提取型号，尽量友好展示
            var model = string.Empty;
            for (var i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                {
                    model = parts[i]["model:".Length..];
                }
            }

            devices.Add(new AdbDevice(parts[0], parts[1], model));
        }

        return devices;
    }

    /// <summary>从 getevent -pl 输出中提取第一个 Protocol B 多点触控设备及其坐标范围。</summary>
    internal static MultiTouchDeviceInfo? ParseMultiTouchDevice(string output)
    {
        var deviceMatches = Regex.Matches(
            output,
            @"add device \d+:\s*(?<path>/dev/input/event\d+)(?<body>.*?)(?=add device \d+:|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match deviceMatch in deviceMatches)
        {
            var body = deviceMatch.Groups["body"].Value;
            if (!body.Contains("ABS_MT_SLOT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var x = ParseAxisRange(body, "ABS_MT_POSITION_X");
            var y = ParseAxisRange(body, "ABS_MT_POSITION_Y");
            if (x is null || y is null || x.Value.Max <= x.Value.Min || y.Value.Max <= y.Value.Min)
            {
                continue;
            }

            return new MultiTouchDeviceInfo(
                deviceMatch.Groups["path"].Value,
                x.Value.Min,
                x.Value.Max,
                y.Value.Min,
                y.Value.Max);
        }

        return null;
    }

    /// <summary>读取指定多点触控坐标轴的最小值与最大值。</summary>
    private static (int Min, int Max)? ParseAxisRange(string body, string axisName)
    {
        var match = Regex.Match(
            body,
            $@"{Regex.Escape(axisName)}\s*:.*?min\s+(-?\d+),\s*max\s+(-?\d+)",
            RegexOptions.IgnoreCase);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var min) ||
            !int.TryParse(match.Groups[2].Value, out var max))
        {
            return null;
        }

        return (min, max);
    }

    /// <summary>生成一次包含两个同步触点的向内捏合 shell 脚本。</summary>
    private static string BuildPinchInScript(MultiTouchDeviceInfo device, int durationMs)
    {
        const int steps = 6;
        var centerX = (device.MinX + device.MaxX) / 2;
        var yRange = device.MaxY - device.MinY;
        // MuMu 横屏时原始 Y 轴对应屏幕 X 轴。两根手指必须都落在路线图画布内：
        // 过于靠左（约 15%）会命中作品列表，游戏便只把另一根手指识别成单指拖动。
        var startLowY = device.MinY + yRange * 30 / 100;
        var startHighY = device.MinY + yRange * 70 / 100;
        var endLowY = device.MinY + yRange * 45 / 100;
        var endHighY = device.MinY + yRange * 55 / 100;
        var stepDelaySeconds = durationMs / (double)(steps + 1) / 1000d;
        var script = new StringBuilder();

        AppendEvent(script, device.Path, 3, 47, 0);   // ABS_MT_SLOT 0
        AppendEvent(script, device.Path, 3, 57, 100); // ABS_MT_TRACKING_ID
        AppendEvent(script, device.Path, 3, 53, centerX);
        AppendEvent(script, device.Path, 3, 54, startLowY);
        AppendEvent(script, device.Path, 3, 47, 1);   // ABS_MT_SLOT 1
        AppendEvent(script, device.Path, 3, 57, 101);
        AppendEvent(script, device.Path, 3, 53, centerX);
        AppendEvent(script, device.Path, 3, 54, startHighY);
        AppendEvent(script, device.Path, 1, 330, 1);  // BTN_TOUCH
        AppendEvent(script, device.Path, 0, 0, 0);    // SYN_REPORT

        for (var step = 1; step <= steps; step++)
        {
            AppendSleep(script, stepDelaySeconds);
            var lowY = startLowY + (endLowY - startLowY) * step / steps;
            var highY = startHighY + (endHighY - startHighY) * step / steps;
            AppendEvent(script, device.Path, 3, 47, 0);
            AppendEvent(script, device.Path, 3, 54, lowY);
            AppendEvent(script, device.Path, 3, 47, 1);
            AppendEvent(script, device.Path, 3, 54, highY);
            AppendEvent(script, device.Path, 0, 0, 0);
        }

        AppendSleep(script, stepDelaySeconds);
        // 以 SYN_DROPPED 取消本次手势，避免画布已到最小缩放时把最后一次抬手误判为节点点击。
        AppendEvent(script, device.Path, 0, 3, 0);
        AppendEvent(script, device.Path, 3, 47, 0);
        AppendEvent(script, device.Path, 3, 57, 4294967295L); // TRACKING_ID = -1
        AppendEvent(script, device.Path, 3, 47, 1);
        AppendEvent(script, device.Path, 3, 57, 4294967295L);
        AppendEvent(script, device.Path, 1, 330, 0);
        AppendEvent(script, device.Path, 0, 0, 0);
        return script.ToString();
    }

    /// <summary>向 shell 脚本追加一个 sendevent 命令。</summary>
    private static void AppendEvent(StringBuilder script, string path, int type, int code, long value)
        => script.Append("sendevent ").Append(path).Append(' ')
            .Append(type).Append(' ').Append(code).Append(' ').Append(value).Append("; ");

    /// <summary>向 shell 脚本追加短暂等待，使 Unity 能在连续帧中接收触点移动。</summary>
    private static void AppendSleep(StringBuilder script, double seconds)
        => script.Append("sleep ")
            .Append(seconds.ToString("0.000", CultureInfo.InvariantCulture))
            .Append("; ");

    /// <summary>多点触控事件设备与原始坐标范围。</summary>
    internal sealed record MultiTouchDeviceInfo(string Path, int MinX, int MaxX, int MinY, int MaxY);

    /// <summary>启动 adb 进程并返回进程对象。</summary>
    private Process StartProcess(IReadOnlyList<string> args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ct.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = startInfo };
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw new AdbException($"无法启动 ADB 进程: {_adbPath}");
            }

            // 所有调用均为完整的非交互命令，不从宿主窗口继承输入句柄。
            // 显式 EOF 保证非交互命令不等待宿主输入。
            process.StandardInput.Close();
            return process;
        }
        catch (OperationCanceledException)
        {
            process.Dispose();
            throw;
        }
        catch (AdbException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new AdbException($"无法启动 ADB（请检查 adbPath 配置）: {_adbPath}", ex);
        }
    }

    /// <summary>执行 ADB 命令；输入路由可合并 stderr 以检查 Android input 零退出码的参数错误。</summary>
    internal Task<string> RunTextAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct, bool includeStandardError = false)
    {
        if (_blueStacksShell is not null && args.Count > 3 && args[0] == "-s" && args[2] == "shell")
            return _blueStacksShell.ExecuteAsync(args, timeoutMs, ct);
        return RunRawTextAsync(args, timeoutMs, ct, includeStandardError);
    }

    /// <summary>原始文本通道供协议探测调用，避免兼容路由递归；截图字节通道保持独立。</summary>
    private async Task<string> RunRawTextAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct, bool includeStandardError = false)
    {
        args = await BlueStacksAdbServer.RouteAsync(_adbPath, args, _log, ct);
        using var process = StartProcess(args, ct);
        using var reads = new CancellationTokenSource();
        var stdoutTask = AdbTextOutput.ReadAsync(process.StandardOutput.BaseStream, reads.Token);
        var stderrTask = AdbTextOutput.ReadAsync(process.StandardError.BaseStream, reads.Token);

        await CompleteCommandAsync(process, timeoutMs, ct, reads, Task.WhenAll(stdoutTask, stderrTask));
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        EnsureSuccess(process, stderr);
        return includeStandardError && !string.IsNullOrEmpty(stderr) ? stdout + "\n" + stderr : stdout;
    }

    /// <summary>执行 ADB 命令并返回原始字节输出（用于截图）。</summary>
    internal async Task<byte[]> RunBytesAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        args = await BlueStacksAdbServer.RouteAsync(_adbPath, args, _log, ct);
        using var process = StartProcess(args, ct);
        using var reads = new CancellationTokenSource();
        using var output = new MemoryStream();

        // 直接读取原始输出流，避免 StreamReader 提前缓冲破坏二进制数据
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, reads.Token);
        var stderrTask = AdbTextOutput.ReadAsync(process.StandardError.BaseStream, reads.Token);

        await CompleteCommandAsync(process, timeoutMs, ct, reads, Task.WhenAll(copyTask, stderrTask));
        await copyTask;
        var stderr = await stderrTask;
        EnsureSuccess(process, stderr);
        return output.ToArray();
    }

    /// <summary>取消和超时都先回收本次客户端并观察输出任务，之后才向调用方报告结束。</summary>
    private static async Task CompleteCommandAsync(
        Process process, int timeoutMs, CancellationToken ct,
        CancellationTokenSource reads, Task outputTask)
    {
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            await outputTask.WaitAsync(linkedCts.Token);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new AdbException($"ADB 命令超时（{timeoutMs} ms）");
        }
        finally
        {
            // 只终止本次启动的客户端；它可能启动共享 ADB server，不能杀整个进程树。
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            finally
            {
                // 即使终止调用异常，也不能在客户端实际退出前释放任务所有权。
                try { await process.WaitForExitAsync(); }
                finally
                {
                    reads.Cancel();
                    try { await outputTask; }
                    catch (OperationCanceledException) when (reads.IsCancellationRequested) { }
                }
            }
        }
    }

    /// <summary>检查退出码，非 0 时抛出带 stderr 的错误。</summary>
    private void EnsureSuccess(Process process, string stderr)
    {
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? "无错误输出" : stderr.Trim();
            throw new AdbException($"ADB 执行失败（exit {process.ExitCode}）: {detail}");
        }
    }
}
