using System.Windows;
using System.Windows.Controls;

namespace GGEA.App.UI;

/// <summary>AP 方案与作品列表面板。</summary>
public partial class ApConsumptionView : Grid
{
    public ApConsumptionView() => InitializeComponent();

    internal event RoutedEventHandler? DeleteProfileRequested;

    private void OnDeleteProfileRequested(object sender, RoutedEventArgs e) => DeleteProfileRequested?.Invoke(sender, e);
}
