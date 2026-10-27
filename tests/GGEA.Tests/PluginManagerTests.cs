using System.IO;
using System.Text.Json;
using GGEA.App.Plugins;
using GGEA.Plugin.Abstractions;
using GGEA.Updater;

namespace GGEA.Tests;

/// <summary>验证真实 DLL 接入与设备生命周期；全部使用临时目录，不加载用户插件或连接设备。</summary>
public sealed class PluginManagerTests
{
    [Fact]
    public async Task DisabledPackage_IsNotExecuted_AndRealDllLoadsOnlyAfterRestart()
    {
        using var files = new Package();
        await using (var first = new PluginManager(files.Root, new TestLog()))
        {
            await first.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default);
            Assert.Null(Assert.Single(first.Entries).Instance);
            first.SaveEnabled(["ggea.sample"]);
            Assert.Null(first.Entries[0].Instance);
        }
        await using var restarted = new PluginManager(files.Root, new TestLog());
        await restarted.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default);
        var instance = Assert.Single(restarted.Entries).Instance;
        Assert.NotNull(instance);
        Assert.IsAssignableFrom<IGgeaPlugin>(instance);
        Assert.NotSame(typeof(IGgeaPlugin).Assembly, instance.GetType().Assembly);
        await restarted.SetDeviceAsync(Device("a"), default);
        await restarted.SetDeviceAsync(null, default);
    }

    [Theory]
    [InlineData("../outside.dll", 1, "1.3.2.0")]
    [InlineData("GGEA.SamplePlugin.dll", 2, "1.3.2.0")]
    [InlineData("GGEA.SamplePlugin.dll", 1, "99.0.0.0")]
    public async Task InvalidManifest_NeverExecutesFactory(string assembly, int api, string minimum)
    {
        using var files = new Package();
        files.Manifest(assembly, api, minimum);
        files.Enable();
        var calls = 0;
        await using var manager = new PluginManager(files.Root, new TestLog(), (_, _) => { calls++; return new Probe(); });
        await manager.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default);
        Assert.Equal(0, calls);
        Assert.Null(Assert.Single(manager.Entries).Manifest);
    }

    [Fact]
    public async Task SwitchingDevice_WaitsForCleanup_AndRevokesOldCommandChannel()
    {
        using var files = new Package(); files.Enable();
        var probe = new Probe();
        await using var manager = new PluginManager(files.Root, new TestLog(), (_, _) => probe);
        await manager.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default);
        var old = Device("a");
        await manager.SetDeviceAsync(old, default);
        await manager.SetDeviceAsync(Device("a"), default);
        Assert.Equal(["a"], probe.Targets);
        probe.Cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = manager.SetDeviceAsync(Device("b"), default);
        Assert.False(switching.IsCompleted);
        Assert.Equal(["a"], probe.Targets);
        probe.Cleanup.SetResult();
        await switching;
        Assert.Equal(["a", "stop", "b"], probe.Targets);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => old.ExecuteAsync(["shell", "id"], default));
    }

    [Fact]
    public async Task FailedCleanup_BlocksNewDevice_AndDisposeCanRetry()
    {
        using var files = new Package(); files.Enable();
        var probe = new Probe();
        var manager = new PluginManager(files.Root, new TestLog(), (_, _) => probe);
        await manager.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default);
        await manager.SetDeviceAsync(Device("a"), default);
        probe.FailCleanup = true;
        await Assert.ThrowsAsync<AggregateException>(() => manager.SetDeviceAsync(Device("b"), default));
        Assert.DoesNotContain("b", probe.Targets);
        await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
        Assert.False(probe.Disposed);
        probe.FailCleanup = false;
        await manager.DisposeAsync();
        Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task InitializationFailure_DisposesPartialInstance_AndKeepsHostUsable()
    {
        using var files = new Package(); files.Enable();
        var probe = new Probe { FailInitialize = true };
        await using var manager = new PluginManager(files.Root, new TestLog(), (_, _) => probe);
        await manager.InitializeAsync(new Version(1, 3, 2, 0), "zh-CN", default);
        Assert.True(probe.Disposed);
        Assert.Null(Assert.Single(manager.Entries).Instance);
        await manager.SetDeviceAsync(Device("a"), default);
        Assert.Empty(probe.Targets);
    }

    [Fact]
    public async Task DeviceLease_RejectsGlobalAdbCommands_AndCancelsPendingCommands()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = new PluginDevice("a", "game.package", "ja", Guid.Empty, async (_, token) =>
        { pending.SetResult(); await Task.Delay(Timeout.Infinite, token); return ""; });
        await Assert.ThrowsAsync<ArgumentException>(() => lease.ExecuteAsync(["-s", "b", "shell", "id"], default));
        var execution = lease.ExecuteAsync(["shell", "id"], default);
        await pending.Task;
        lease.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public void PluginSettings_AreIndependent_AndInvalidJsonDoesNotOverwrite()
    {
        using var files = new Package();
        var path = Path.Combine(files.Root, "config", "plugins", "ggea.sample", "settings.json");
        var context = new PluginContext(files.Directory, path, "ja", "ggea.sample", new TestLog());
        context.WriteSettings("{\"enabled\":true}");
        Assert.ThrowsAny<JsonException>(() => context.WriteSettings("invalid"));
        using var document = JsonDocument.Parse(context.ReadSettings()!);
        Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(File.Exists(Path.Combine(files.Root, "config", "ggea.config.json")));
        Assert.Throws<InvalidDataException>(() => UpdateFiles.ValidateManagedPath("plugins/ggea.sample/plugin.json"));
        Assert.True(UpdateFiles.IsUserFile("config/plugins/ggea.sample/settings.json"));
    }

    private static PluginDevice Device(string serial) => new(serial, "game.package", "ja", Guid.Empty, (_, _) => Task.FromResult("ok"));

    /// <summary>可控插件故障与异步清理闸门，不用延时猜测任务执行顺序。</summary>
    private sealed class Probe : IGgeaPlugin
    {
        public List<string> Targets { get; } = [];
        public TaskCompletionSource? Cleanup { get; set; }
        public bool FailCleanup { get; set; }
        public bool FailInitialize { get; set; }
        public bool Disposed { get; private set; }
        public Task InitializeAsync(IPluginContext context, CancellationToken ct) => FailInitialize ? Task.FromException(new IOException()) : Task.CompletedTask;
        public IPluginSettingsEditor? CreateSettingsEditor() => null;
        public async Task SetDeviceAsync(IPluginDevice? device, CancellationToken ct)
        {
            if (device is null)
            {
                if (FailCleanup) throw new IOException();
                if (Cleanup is not null) await Cleanup.Task;
            }
            Targets.Add(device?.Serial ?? "stop");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>每例独立真实插件目录，清理范围始终是本例创建的临时根目录。</summary>
    internal sealed class Package : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ggea-plugin-test-" + Guid.NewGuid().ToString("N"));
        public string Directory => Path.Combine(Root, "plugins", "ggea.sample");
        public Package()
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var file in new[] { "GGEA.SamplePlugin.dll", "GGEA.SamplePlugin.deps.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plugins", file), Path.Combine(Directory, file));
            Manifest("GGEA.SamplePlugin.dll", 1, "1.3.2.0");
        }
        public void Manifest(string assembly, int api, string minimum) => UpdateFiles.WriteJson(Path.Combine(Directory, "plugin.json"),
            new PluginManifest { Id = "ggea.sample", Name = "Example", Version = "1.0.0", ApiVersion = api,
                MinimumHostVersion = minimum, Assembly = assembly, EntryPoint = "GGEA.SamplePlugin.SamplePlugin" });
        public void Enable() => UpdateFiles.WriteJson(Path.Combine(Root, "config", "plugins", "enabled.json"), new[] { "ggea.sample" });
        public void Dispose() => System.IO.Directory.Delete(Root, true);
    }
}
