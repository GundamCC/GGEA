using System.IO;
using System.Text.Json;
using GGEA.App.Logging;
using GGEA.Plugin.Abstractions;
using GGEA.Updater;

namespace GGEA.App.Plugins;

/// <summary>设置页使用的插件状态；加载失败保留诊断，不能伪装成已启用。</summary>
public sealed class PluginEntry
{
    public required string Directory { get; init; }
    public PluginManifest? Manifest { get; init; }
    public string Status { get; internal set; } = "未启用";
    public bool EnabledNextStart { get; internal set; }
    internal IGgeaPlugin? Instance { get; set; }
}

/// <summary>进程级插件所有者；加载一次、串行切换设备、逆序退出，启停设置仅在下次启动生效。</summary>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly string _baseDirectory;
    private readonly IGgeaLog _log;
    private readonly Func<string, PluginManifest, IGgeaPlugin> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<PluginEntry> _entries = [];
    private readonly List<IGgeaPlugin> _cleanupPending = [];
    private PluginDevice? _device;
    private bool _initialized, _stopping, _disposed, _faulted;
    public IReadOnlyList<PluginEntry> Entries => _entries.AsReadOnly();
    /// <summary>清理或设备回调异常后禁止普通任务继续使用可能仍被插件占用的设备。</summary>
    public bool HasDeviceFault => _faulted;

    /// <summary>普通启动从应用目录 plugins 读取；测试可替换工厂验证生命周期而无需执行第三方代码。</summary>
    public PluginManager(string baseDirectory, IGgeaLog log)
        : this(baseDirectory, log, LoadPlugin) { }
    internal PluginManager(string baseDirectory, IGgeaLog log, Func<string, PluginManifest, IGgeaPlugin> factory)
    { _baseDirectory = baseDirectory; _log = log; _factory = factory; }

    /// <summary>必须在 UI 线程调用，保证插件创建的 WPF 对象归宿主 Dispatcher 所有。</summary>
    public async Task InitializeAsync(Version hostVersion, string language, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            ObjectDisposedException.ThrowIf(_stopping, this);
            _initialized = true;
            var root = UpdateFiles.SafePath(_baseDirectory, "plugins");
            if (!System.IO.Directory.Exists(root)) return;
            var enabled = ReadEnabled();
            foreach (var directory in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                PluginEntry entry;
                try
                {
                    var manifest = PluginManifest.Read(directory, hostVersion);
                    entry = new() { Directory = directory, Manifest = manifest, EnabledNextStart = enabled.Contains(manifest.Id) };
                }
                catch (Exception ex)
                {
                    _entries.Add(new() { Directory = directory, Status = $"不可用：{ex.GetType().Name}" });
                    _log.Warn($"插件清单校验失败（{Path.GetFileName(directory)}，{ex.GetType().Name}）。");
                    continue;
                }
                _entries.Add(entry);
                if (!entry.EnabledNextStart) continue;
                IGgeaPlugin? instance = null;
                try
                {
                    instance = _factory(directory, entry.Manifest!);
                    var settings = UpdateFiles.SafePath(_baseDirectory, $"config/plugins/{entry.Manifest!.Id}/settings.json");
                    await instance.InitializeAsync(new PluginContext(directory, settings, language, entry.Manifest.Id, _log), ct);
                    entry.Instance = instance;
                    entry.Status = "已加载";
                }
                catch (Exception ex)
                {
                    entry.Status = $"加载失败：{ex.GetType().Name}";
                    _log.Warn($"插件 {entry.Manifest!.Id} 加载失败（{ex.GetType().Name}）。");
                    if (instance is not null)
                    {
                        try { await instance.DisposeAsync(); }
                        catch
                        {
                            // 保留所有权，退出时继续收尾；状态未知时不允许接入设备。
                            _cleanupPending.Add(instance);
                            _faulted = true;
                            entry.Status = "加载失败，清理未完成，请重启";
                        }
                    }
                    if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>更新目标前先等待全部旧会话释放；失败保留旧引用，拒绝启动新设备。</summary>
    internal async Task SetDeviceAsync(PluginDevice? device, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch { device?.Dispose(); throw; }
        var accepted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_faulted && device is not null) throw new InvalidOperationException("插件故障，请重启后重试。");
            if (_device is not null && device is not null && _device.Serial == device.Serial &&
                _device.BootId == device.BootId && _device.GamePackage == device.GamePackage &&
                _device.GameLanguage == device.GameLanguage && _device.ConnectionKey == device.ConnectionKey)
                return;
            await StopDeviceCoreAsync(ct);
            _device = device;
            accepted = true;
            if (device is null) return;
            try
            {
                foreach (var entry in _entries.Where(entry => entry.Instance is not null))
                    await entry.Instance!.SetDeviceAsync(device, ct);
            }
            catch
            {
                _faulted = true;
                try { await StopDeviceCoreAsync(CancellationToken.None); }
                catch (Exception ex) { _log.Warn($"插件设备清理未完成（{ex.GetType().Name}），禁止切换目标。"); }
                throw;
            }
        }
        finally
        {
            if (!accepted) device?.Dispose();
            _gate.Release();
        }
    }

    /// <summary>所有插件都有清理机会；任一失败都不能撤销它完成清理所需的旧设备通道。</summary>
    private async Task StopDeviceCoreAsync(CancellationToken ct)
    {
        if (_device is null) return;
        List<Exception> errors = [];
        foreach (var entry in _entries.AsEnumerable().Reverse().Where(entry => entry.Instance is not null))
        {
            try { await entry.Instance!.SetDeviceAsync(null, ct); }
            catch (Exception ex) { entry.Status = "设备清理失败，请重启"; errors.Add(ex); }
        }
        if (errors.Count > 0) { _faulted = true; throw new AggregateException(errors); }
        _device.Dispose();
        _device = null;
    }

    /// <summary>设置视图失败不执行设备命令，不破坏普通设置页。</summary>
    public IPluginSettingsEditor? CreateSettingsEditor(PluginEntry entry)
    {
        if (_faulted) return null;
        try
        {
            var editor = entry.Instance?.CreateSettingsEditor();
            if (editor is not null && (editor.View is null || editor.View.Parent is not null))
                throw new InvalidOperationException("插件必须为每个设置窗口创建独立视图。");
            return editor;
        }
        catch (Exception ex)
        {
            entry.Status = $"设置加载失败：{ex.GetType().Name}";
            _log.Warn($"插件设置加载失败（{entry.Manifest?.Id}，{ex.GetType().Name}）。");
            return null;
        }
    }

    /// <summary>只保存已发现且有效的插件标识；不即时加载或卸载。</summary>
    public void SaveEnabled(IEnumerable<string> ids)
    {
        var enabled = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (enabled.Any(id => !_entries.Any(entry => entry.Manifest?.Id == id)))
            throw new InvalidDataException("未知插件标识。");
        UpdateFiles.WriteJson(UpdateFiles.SafePath(_baseDirectory, "config/plugins/enabled.json"), enabled);
        foreach (var entry in _entries) entry.EnabledNextStart = enabled.Contains(entry.Manifest?.Id);
    }

    private HashSet<string> ReadEnabled()
    {
        var path = UpdateFiles.SafePath(_baseDirectory, "config/plugins/enabled.json");
        if (!File.Exists(path)) return [];
        try { return new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [], StringComparer.Ordinal); }
        catch (JsonException) { _log.Warn("插件启用配置损坏，本次不加载插件；请在设置中重新启用。"); return []; }
    }

    private static IGgeaPlugin LoadPlugin(string directory, PluginManifest manifest)
    {
        var path = UpdateFiles.SafePath(directory, manifest.Assembly);
        var context = new PluginLoadContext(path);
        var assembly = context.LoadManaged(path);
        var type = assembly.GetType(manifest.EntryPoint, throwOnError: true)!;
        if (!typeof(IGgeaPlugin).IsAssignableFrom(type) || type.IsAbstract || !type.IsPublic)
            throw new InvalidDataException("插件入口必须是公开的 IGgeaPlugin 实现。");
        return (IGgeaPlugin)Activator.CreateInstance(type)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _stopping = true;
            await StopDeviceCoreAsync(CancellationToken.None);
            List<Exception> errors = [];
            foreach (var entry in _entries.AsEnumerable().Reverse().Where(entry => entry.Instance is not null))
            {
                try { await entry.Instance!.DisposeAsync(); entry.Instance = null; entry.Status = "已停止"; }
                catch (Exception ex) { errors.Add(ex); }
            }
            foreach (var instance in _cleanupPending.ToArray())
            {
                try { await instance.DisposeAsync(); _cleanupPending.Remove(instance); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new AggregateException(errors);
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}
