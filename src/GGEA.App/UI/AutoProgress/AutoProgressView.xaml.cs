using System.Windows;
using System.Windows.Controls;

namespace GGEA.App.UI;

/// <summary>自动推图面板。</summary>
public partial class AutoProgressView : Grid
{
    public AutoProgressView() => InitializeComponent();

    internal event RoutedEventHandler? RemoveTargetRequested;

    private void OnRemoveTargetRequested(object sender, RoutedEventArgs e) => RemoveTargetRequested?.Invoke(sender, e);
}
