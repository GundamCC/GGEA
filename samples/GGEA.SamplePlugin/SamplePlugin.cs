using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GGEA.Plugin.Abstractions;

namespace GGEA.SamplePlugin;

/// <summary>可独立构建的最小真实插件；不引用 GGEA.App，也不对设备发出命令。</summary>
public sealed class SamplePlugin : IGgeaPlugin
{
    private IPluginContext? _context;
    private string _message = "插件已就绪";
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        if (context.ReadSettings() is { } json) _message = JsonSerializer.Deserialize<string>(json) ?? _message;
        context.Log(PluginLogLevel.Info, "示例插件已加载。");
        return Task.CompletedTask;
    }
    public IPluginSettingsEditor CreateSettingsEditor() => new Editor(this);
    public Task SetDeviceAsync(IPluginDevice? device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context!.Log(PluginLogLevel.Info, device is null ? "已释放设备。" : _message);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        _context?.Log(PluginLogLevel.Info, "示例插件已停止。");
        _context = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>编辑值只保存在当前视图，用户取消时不影响插件状态。</summary>
    private sealed class Editor : IPluginSettingsEditor
    {
        private readonly SamplePlugin _plugin;
        private readonly TextBox _input;
        public FrameworkElement View => _input;
        public Editor(SamplePlugin plugin)
        {
            _plugin = plugin;
            _input = new TextBox { Text = plugin._message, Margin = new Thickness(0, 8, 0, 0), MaxLength = 200,
                ToolTip = "连接设备时写入日志的文字" };
            _input.SetResourceReference(FrameworkElement.StyleProperty, "TextBoxDark");
        }
        public void Save()
        {
            _plugin._context!.WriteSettings(JsonSerializer.Serialize(_input.Text));
            _plugin._message = _input.Text;
        }
    }
}
