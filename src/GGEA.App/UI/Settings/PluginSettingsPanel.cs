using System.Windows;
using System.Windows.Controls;
using GGEA.App.Plugins;
using GGEA.Plugin.Abstractions;

namespace GGEA.App.UI;

/// <summary>普通设置中的本机插件列表；未启用的包只显示清单，不为预览执行插件代码。</summary>
public sealed class PluginSettingsPanel : StackPanel
{
    private PluginManager? _manager;
    private readonly List<(string Id, CheckBox Enabled)> _choices = [];
    private readonly List<IPluginSettingsEditor> _editors = [];

    /// <summary>每个设置窗口生成独立编辑视图，取消时不调用 Save。</summary>
    public void Initialize(PluginManager? manager)
    {
        _manager = manager;
        Children.Clear(); _choices.Clear(); _editors.Clear();
        AddText("本机插件", "CardTitle");
        AddText("仅安装可信来源的插件。插件具有与 GGEA 相同的本机权限；启用、停用、安装或更新后需要重启。", "HintText");
        if (manager is null || manager.Entries.Count == 0)
        {
            AddText("尚未发现插件。关闭程序后，将完整插件目录放入 plugins/，再重新启动。", "HintText");
            return;
        }
        foreach (var entry in manager.Entries)
        {
            var manifest = entry.Manifest;
            var name = manifest is null ? System.IO.Path.GetFileName(entry.Directory) : $"{manifest.Name}  {manifest.Version}";
            var row = new Grid { Margin = new Thickness(0, 14, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var choice = new CheckBox { IsChecked = entry.EnabledNextStart,
                IsEnabled = manifest is not null, VerticalAlignment = VerticalAlignment.Center };
            choice.SetResourceReference(StyleProperty, "TaskCheckBox");
            System.Windows.Automation.AutomationProperties.SetName(choice, name);
            row.Children.Add(choice);
            var label = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0) };
            label.SetResourceReference(StyleProperty, "InlineLabel");
            label.MouseLeftButtonUp += (_, _) => { if (choice.IsEnabled) choice.IsChecked = choice.IsChecked != true; };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            Children.Add(row);
            if (manifest is not null)
            {
                _choices.Add((manifest.Id, choice));
                if (!string.IsNullOrWhiteSpace(manifest.Description)) AddText(manifest.Description, "HintText");
            }
            var editor = manager.CreateSettingsEditor(entry);
            AddText(entry.Status, "HintText");
            if (editor is not null)
            {
                _editors.Add(editor);
                Children.Add(editor.View);
            }
        }
    }

    /// <summary>插件自己验证并保存独立设置；失败冒泡到设置页，启用列表最后落盘。</summary>
    public void Save()
    {
        foreach (var editor in _editors) editor.Save();
        _manager?.SaveEnabled(_choices.Where(choice => choice.Enabled.IsChecked == true).Select(choice => choice.Id));
    }

    private void AddText(string text, string style)
    {
        var block = new TextBlock { Text = Localization.L.Text(text), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        block.SetResourceReference(StyleProperty, style);
        Children.Add(block);
    }
}
