using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 测试用日志实现，只收集日志不写文件。
/// </summary>
public sealed class TestLog : IGgeaLog
{
    public List<LogEntry> Entries { get; } = new();

    // 测试中不订阅日志事件，显式空实现避免编译器警告
    public event EventHandler<LogEntry>? Logged { add { } remove { } }

    public void Debug(string message) => Entries.Add(new LogEntry(DateTime.Now, "DEBUG", message));
    public void Info(string message) => Entries.Add(new LogEntry(DateTime.Now, "INFO", message));
    public void Warn(string message) => Entries.Add(new LogEntry(DateTime.Now, "WARN", message));
    public void Error(string message) => Entries.Add(new LogEntry(DateTime.Now, "ERROR", message));
}

/// <summary>
/// 测试用假 ADB 服务：总是返回预设截图，并记录点击坐标。
/// </summary>
public sealed class FakeAdbService : IAdbService
{
    private readonly List<byte[]> _screenshotSequence;
    private readonly List<string> _serials;
    private int _screenshotIndex;

    public List<(int X, int Y)> Taps { get; } = new();
    public List<(int X1, int Y1, int X2, int Y2, int DurationMs)> Swipes { get; } = new();
    public List<int> PinchInDurations { get; } = new();
    public List<(string Serial, string Package)> LaunchedApps { get; } = new();

    /// <summary>测试期间请求设备截图的次数。</summary>
    public int ScreenshotCount { get; private set; }

    /// <summary>按已经发送的手势生成画面，测试不依赖实现内部的截图次数。</summary>
    public Func<byte[]>? ScreenshotProvider { get; set; }

    /// <summary>按调用次数注入截图故障；返回 null 时使用正常截图序列。</summary>
    public Func<int, Exception?>? ScreenshotFailure { get; set; }

    /// <summary>按调用次数异步延迟截图，模拟 MuMu 原生截图等较慢通道。</summary>
    public Func<int, CancellationToken, Task>? BeforeScreenshotAsync { get; set; }

    /// <summary>按设备设置品牌属性，用于混合品牌发现测试。</summary>
    public Dictionary<string, string> DeviceProperties { get; } = new();

    /// <summary>getprop 返回的启动状态值，默认已启动完成。</summary>
    public string BootCompletedValue { get; set; } = "1";

    public FakeAdbService(byte[] screenshotPng, IEnumerable<string>? initialSerials = null)
    {
        _screenshotSequence = new List<byte[]> { screenshotPng };
        _serials = initialSerials?.ToList() ?? new List<string>();
    }

    /// <summary>追加后续截图，用于模拟“拖动后画面变化”。</summary>
    public void EnqueueScreenshots(params byte[][] screenshots)
    {
        foreach (var screenshot in screenshots)
        {
            _screenshotSequence.Add(screenshot);
        }
    }

    /// <summary>模拟模拟器上线：向设备列表追加一个序列号。</summary>
    public void AddDevice(string serial)
    {
        if (!_serials.Contains(serial))
        {
            _serials.Add(serial);
        }
    }

    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AdbDevice>>(
            _serials.Select(s => new AdbDevice(s, "device", string.Empty)).ToList());

    public async Task<byte[]> ScreenshotAsync(string serial, CancellationToken ct = default)
    {
        ScreenshotCount++;
        ct.ThrowIfCancellationRequested();
        if (BeforeScreenshotAsync is { } beforeScreenshot)
            await beforeScreenshot(ScreenshotCount, ct);
        if (ScreenshotFailure?.Invoke(ScreenshotCount) is { } failure)
        {
            throw failure;
        }
        if (ScreenshotProvider is { } provider) return provider();
        var png = _screenshotSequence[Math.Min(_screenshotIndex, _screenshotSequence.Count - 1)];
        _screenshotIndex++;
        return png;
    }

    /// <summary>模拟输入已发送但回执丢失，检验任务不会因异常重复消费。</summary>
    public Func<int, int, Exception?>? TapFailure { get; set; }

    public Task TapAsync(string serial, int x, int y, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Taps.Add((x, y));
        if (TapFailure?.Invoke(x, y) is { } error) throw error;
        return Task.CompletedTask;
    }

    public Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default)
    {
        Swipes.Add((x1, y1, x2, y2, durationMs));
        return Task.CompletedTask;
    }

    public Task PinchInAsync(string serial, int durationMs = 500, CancellationToken ct = default)
    {
        PinchInDurations.Add(durationMs);
        return Task.CompletedTask;
    }

    public Task KeyEventAsync(string serial, string keyCode, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string> GetSystemPropertyAsync(string serial, string property, CancellationToken ct = default)
        => Task.FromResult(property == "sys.boot_completed" ? BootCompletedValue : DeviceProperties.GetValueOrDefault(serial, string.Empty));

    /// <summary>模拟同一实例的多个连接地址。</summary>
    public Dictionary<string, string> BootIds { get; } = new();
    public Task<string> GetBootIdAsync(string serial, CancellationToken ct = default)
        => Task.FromResult(BootIds.GetValueOrDefault(serial, string.Empty));

    public Task LaunchAppAsync(string serial, string package, CancellationToken ct = default)
    {
        LaunchedApps.Add((serial, package));
        return Task.CompletedTask;
    }

    /// <summary>注入连接故障或取消，验证发现失败不会丢失已有设备。</summary>
    public Func<string, int, CancellationToken, Task<string>>? ConnectOverride { get; set; }

    public Task<string> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ConnectOverride is not null) return ConnectOverride(host, port, ct);
        var serial = $"{host}:{port}";
        AddDevice(serial);
        return Task.FromResult($"connected to {serial}");
    }
}

/// <summary>测试用作品识别器：按调用顺序返回预设的单模板匹配结果。</summary>
public sealed class FakeStageWorkRecognizer : IStageWorkRecognizer
{
    private readonly Queue<StageWorkMatch?> _findResults = new();

    /// <summary>局部模板查找的调用次数。</summary>
    public int FindCallCount { get; private set; }

    /// <summary>依次追加局部模板查找结果，null 表示本次未识别到锚点。</summary>
    public void EnqueueFindResults(params StageWorkMatch?[] matches)
    {
        foreach (var match in matches)
        {
            _findResults.Enqueue(match);
        }
    }

    /// <summary>导航不应使用全量作品识别，测试替身始终返回空列表。</summary>
    public Task<IReadOnlyList<StageWorkMatch>> RecognizeAsync(
        string serial,
        double threshold = 0.85,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StageWorkMatch>>(Array.Empty<StageWorkMatch>());

    /// <summary>返回队列中的下一项锚点匹配结果。</summary>
    public Task<StageWorkMatch?> FindAsync(
        string serial,
        string templateName,
        Roi roi,
        double threshold = 0.78,
        CancellationToken ct = default)
    {
        FindCallCount++;
        var result = _findResults.Count > 0 ? _findResults.Dequeue() : null;
        return Task.FromResult(result);
    }
}

/// <summary>测试用 HARD STAGE 标题识别器：按调用顺序返回预设结果。</summary>
public sealed class FakeStageTitleRecognizer : IStageTitleRecognizer
{
    private readonly Queue<StageTitleMatch?> _results = new();

    /// <summary>识别调用次数。</summary>
    public int CallCount { get; private set; }

    /// <summary>依次追加识别结果，null 表示本次未识别到标题。</summary>
    public void EnqueueResults(params StageTitleMatch?[] matches)
    {
        foreach (var match in matches)
        {
            _results.Enqueue(match);
        }
    }

    /// <summary>返回队列中的下一项标题识别结果。</summary>
    public Task<StageTitleMatch?> RecognizeAsync(
        string serial,
        Roi titleRoi,
        double threshold = 0.85,
        CancellationToken ct = default)
    {
        CallCount++;
        var result = _results.Count > 0 ? _results.Dequeue() : null;
        return Task.FromResult(result);
    }
}

/// <summary>测试用左侧关卡节点识别器：按调用顺序返回预设的选中节点。</summary>
public sealed class FakeStageNodeRecognizer : IStageNodeRecognizer
{
    private readonly Queue<StageNodeRecognition?> _results = new();

    /// <summary>识别调用次数。</summary>
    public int CallCount { get; private set; }

    /// <summary>依次追加节点识别结果，null 表示未能确认当前节点。</summary>
    public void EnqueueResults(params StageNodeRecognition?[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    /// <summary>返回队列中的下一项节点识别结果。</summary>
    public Task<StageNodeRecognition?> RecognizeSelectedAsync(
        string serial,
        GGEA.App.Config.HardStageEntry work,
        CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : null);
    }
}

/// <summary>
/// 测试图像构造辅助：生成纯色 BGRA 图像，并可在指定位置“贴”一块亮色矩形。
/// </summary>
public static class TestImageFactory
{
    /// <summary>生成带真实关卡菜单标题的合成截图，供入口与后续流程验证使用。</summary>
    public static byte[] CreateStageMenuPng(int titleX = 210, int titleY = 20)
    {
        var pixels = CreateBgra(1920, 1080, 20);
        PasteStageMenuTitle(pixels, 1920, titleX, titleY);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>将随包标题原样贴入测试画面，不用单色块代替用户素材。</summary>
    public static void PasteStageMenuTitle(byte[] pixels, int screenWidth, int x = 210, int y = 20)
    {
        // 与生产入口共用语言/通用资源解析，兼容已迁移的模板目录。
        var path = GGEA.App.Config.ConfigService.ResolvePath(GGEA.App.Tasks.StageMenuEntryService.TitleTemplate);
        var title = ImageCodec.DecodePng(System.IO.File.ReadAllBytes(path));
        for (var row = 0; row < title.Height; row++)
            Buffer.BlockCopy(title.Bgra, row * title.Width * 4, pixels,
                ((y + row) * screenWidth + x) * 4, title.Width * 4);
    }

    public static byte[] CreateBgra(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;       // B
            pixels[i + 1] = value;   // G
            pixels[i + 2] = value;   // R
            pixels[i + 3] = 255;     // A
        }

        return pixels;
    }

    public static void PasteBlock(byte[] pixels, int width, int blockWidth, int blockHeight, int x, int y, byte value)
    {
        for (var py = 0; py < blockHeight; py++)
        {
            for (var px = 0; px < blockWidth; px++)
            {
                var offset = ((y + py) * width + (x + px)) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
    }

    public static void PasteColoredBlock(
        byte[] pixels,
        int width,
        int blockWidth,
        int blockHeight,
        int x,
        int y,
        byte r,
        byte g,
        byte b)
    {
        for (var py = 0; py < blockHeight; py++)
        {
            for (var px = 0; px < blockWidth; px++)
            {
                var offset = ((y + py) * width + (x + px)) * 4;
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }
    }
}
