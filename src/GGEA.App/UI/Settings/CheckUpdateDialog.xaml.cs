using System.Windows;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Updates;

namespace GGEA.App.UI;

public partial class CheckUpdateDialog : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<UpdateRelease?>> _check;
    private readonly string? _installRestriction;
    private bool _canInstall;
    private readonly IGgeaLog _log;
    public bool CheckSucceeded { get; private set; }
    public UpdateRelease? Release { get; private set; }

    public CheckUpdateDialog(bool allowInstall, IGgeaLog log)
        : this(log, QueryAsync, allowInstall ? UpdateLauncher.InstallationUnavailableReason(AppContext.BaseDirectory)
            : L.Text("请完成首次设置后再安装更新。")) { }

    internal CheckUpdateDialog(IGgeaLog log, Func<CancellationToken, Task<UpdateRelease?>> check, string? installRestriction)
    {
        InitializeComponent();
        _log = log;
        _check = check;
        _installRestriction = installRestriction;
        ContentRendered += async (_, _) => await CheckAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private static async Task<UpdateRelease?> QueryAsync(CancellationToken token)
    {
        using var client = new OnlineUpdateClient(AppContext.BaseDirectory);
        return await client.CheckAsync(UpdateLauncher.CurrentVersion, token);
    }

    private async Task CheckAsync()
    {
        var token = _lifetime.Token;
        ActionButton.Visibility = Visibility.Collapsed;
        Status.Text = L.Text("正在检查更新…");
        Details.Text = "";
        try
        {
            Release = await _check(token);
            if (token.IsCancellationRequested) return;
            CheckSucceeded = true;
            Status.Text = Release is null ? L.Text("已是最新版本") : L.Format($"发现新版本 v{Release.Version}");
            Details.Text = Release is null ? $"GGEA v{UpdateLauncher.CurrentVersion.ToString(4)}" :
                string.Join("\n", new[] { Release.Notes, _installRestriction }.Where(text => !string.IsNullOrWhiteSpace(text)));
            _canInstall = Release is not null && _installRestriction is null;
            ActionButton.Content = L.Text(_canInstall ? "立即更新" : "知道了");
            Dismiss.Visibility = _canInstall ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _log.Warn(ex.Message);
            Status.Text = L.Text("检查更新失败");
            Details.Text = ex.Message;
            ActionButton.Content = L.Text("重试");
        }
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.Focus();
    }

    private async void Act(object sender, RoutedEventArgs e)
    {
        if (!CheckSucceeded) await CheckAsync();
        else DialogResult = _canInstall;
    }
}
