using System.Windows;
using System.Windows.Controls;
using GGEA.App.Device;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>设备扫描、重连、选择与执行前的在线身份校验。</summary>
public partial class MainWindow
{
    /// <summary>设备刷新时每个适配器最多探测前 3 个实例，避免空端口拖慢刷新。</summary>
    private const int EmulatorScanMaxInstances = 3;
    private bool _refreshingDeviceSelection;
    private string? _preferredDeviceSerial;

    /// <summary>与用户选择绑定的启动身份，防止串口被另一实例复用或去重别名导致误选。</summary>
    private Guid _preferredDeviceBootId;

    /// <summary>刷新按钮：重新检测 ADB 设备。</summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => RefreshButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task RefreshButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        await RefreshDevicesAsync();
    }

    /// <summary>重连纳入窗口生命周期与设备互斥，只处理点击时明确选中的离线地址。</summary>
    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(() => ReconnectButton_ClickCoreAsync(sender, e));

    /// <summary>恢复通道后刷新身份并要求重新选择，避免端口复用时自动操作其他账号。</summary>
    private async Task ReconnectButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null || _adb is null || DeviceCombo.SelectedItem is not AdbDevice selected || !selected.CanReconnect)
            return;
        ConnectionStateText.Text = L.Text("正在重连…");
        try
        {
            // 重连不继承旧的启动身份，也不允许刷新后自动绑定重用该端口的新实例。
            _refreshingDeviceSelection = true;
            try { DeviceCombo.SelectedItem = null; }
            finally { _refreshingDeviceSelection = false; }
            _preferredDeviceSerial = selected.Serial;
            _preferredDeviceBootId = selected.BootId;
            await SyncDeviceServicesAsync(null, ct: _windowTasks.Token);
            await _adb.Transport.ReconnectDeviceAsync(selected, _windowTasks.Token);
            if (await RefreshDevicesAsync(_windowTasks.Token, preserveSelection: false))
                ConnectionStateText.Text = L.Text("重连成功，请重新选择设备");
        }
        catch (OperationCanceledException)
        {
            ConnectionStateText.Text = L.Text("重连已取消");
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"设备重连未完成 [{selected.Serial}]: {ex.Message}"));
            ConnectionStateText.Text = L.Format($"重连未完成: {ex.Message}");
        }
    }

    /// <summary>
    /// 为已知模拟器补全配套程序路径。BlueStacks 的 HD-Adb 与 HD-Player 必须来自同一安装，
    /// 旧配置在升级后也可直接获得新的自动探测能力。
    /// </summary>
    private void CompleteKnownEmulatorPaths()
    {
        if (string.Equals(_config.EmulatorType, EmulatorTypes.Custom, StringComparison.OrdinalIgnoreCase))
        {
            // 兼容旧配置：此前可能只保存路径而未保存类型，升级后按路径恢复正确适配器。
            _config.EmulatorType = EmulatorTypes.DetectFromAdbPath(_config.AdbPath)
                                   ?? EmulatorTypes.DetectFromEmulatorPath(_config.EmulatorPath)
                                   ?? EmulatorTypes.Custom;
        }

        if (EmulatorTypes.IsBlueStacks(_config.EmulatorType))
        {
            var installation = EmulatorLocator.FindBlueStacksNearPaths(
                                   _config.AdbPath,
                                   _config.EmulatorPath)
                               ?? EmulatorLocator.FindBlueStacksInstallation();
            if (installation is not null)
            {
                _config.AdbPath = installation.AdbPath;
                _config.EmulatorPath = installation.LauncherPath;
            }
        }
        else if (EmulatorTypes.IsMuMu(_config.EmulatorType) &&
                 string.IsNullOrWhiteSpace(_config.EmulatorPath))
        {
            _config.EmulatorPath = EmulatorLocator.ResolveMuMuManager(_config.AdbPath, _config.EmulatorPath) ?? string.Empty;
        }
    }

    /// <summary>开发任务使用通用 ADB，不再按品牌禁用；保留用户已有的勾选意愿。</summary>
    private void ApplyEmulatorCapabilities()
    {
        var developTask = _dailyTasks.FirstOrDefault(task => task.Kind == DailyTaskKind.DevelopOnce);
        if (developTask is null) return;
        developTask.IsSupported = true;
        developTask.UnsupportedReason = null;
    }

    /// <summary>刷新与手动选择共用连接文案和设备能力更新，避免显示旧设备状态。</summary>
    private void UpdateSelectedDeviceState(AdbDevice? selected)
    {
        ConnectionStateText.Text = selected?.IsAvailable == true ? L.Text("已连接") :
            selected?.IsOffline == true ? L.Text("设备离线") : selected is null ? L.Text("未连接") : L.Format($"设备不可用 ({selected.State})");
        AccountStatusText.Text = selected?.IsAvailable == true ? L.Text("设备已连接") : L.Text("等待连接设备");
        if (selected is not null)
            DeviceStatusText.Text = selected.CanReconnect ? L.Text("ADB 连接失效，请点击重连") :
                selected.IsAvailable ? L.Text("目标设备已选中") : L.Text("请检查模拟器与 ADB 状态");
        else
            DeviceStatusText.Text = DeviceCombo.Items.Count == 0 ? L.Text("未检测设备") : L.Text("请在设备列表中选择目标设备");
        UpdateBusyState();
        ApplyEmulatorCapabilities();
    }

    /// <summary>刷新 ADB 设备列表。</summary>
    private async Task<bool> RefreshDevicesAsync(CancellationToken ct = default, bool preserveSelection = true)
    {
        using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _windowTasks.Token);
        ct = refreshCts.Token;
        ct.ThrowIfCancellationRequested();
        if (_adb is null || _emulatorAdapter is null)
        {
            return false;
        }

        UpdateBusyState();
        DeviceStatusText.Text = L.Text("正在检测设备…");
        try
        {
            // 端口探测、去重与品牌归属都由当前适配器处理，UI 只消费标准设备列表。
            var previousSerial = _preferredDeviceSerial ??
                                 (DeviceCombo.SelectedItem as AdbDevice)?.Serial;
            var devices = (await _emulatorAdapter.DiscoverDevicesAsync(EmulatorScanMaxInstances, ct, previousSerial))
                .ToList();
            AdbDevice? selected;
            _refreshingDeviceSelection = true;
            try
            {
                DeviceCombo.ItemsSource = devices;
                selected = preserveSelection
                    ? EmulatorDeviceSelector.SelectForRefresh(devices, previousSerial, _preferredDeviceBootId)
                    : null;
                DeviceCombo.SelectedItem = selected;
            }
            finally
            {
                _refreshingDeviceSelection = false;
            }

            // 目标离线时保留意图，重复刷新不能把丢失目标转换成一次新的默认启动。
            if (selected is not null)
            {
                _preferredDeviceSerial = selected.Serial;
                _preferredDeviceBootId = selected.BootId;
            }
            UpdateSelectedDeviceState(selected);
            DeviceStatusText.Text = devices.Count == 0
                ? _emulatorAdapter.NoDeviceMessage
                : selected is null ? devices.All(device => device.IsOffline)
                    ? L.Text("设备离线，请选择目标后重连") : L.Text("请在设备列表中选择目标设备")
                : L.Format($"检测到 {devices.Count} 个设备");

            try
            {
                await SyncDeviceServicesAsync(selected?.Serial, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Error(L.Format($"同步设备服务失败: {ex.Message}"));
                ConnectionStateText.Text = L.Text("设备服务处理失败，请检查日志与设置");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"设备检测失败: {ex.Message}"));
            ConnectionStateText.Text = L.Text("连接失败");
            AccountStatusText.Text = L.Text("等待连接设备");
            return false;
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>用户手动切换设备时，记录为后续每日任务使用的设备。</summary>
    private async void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowTaskAsync(() => DeviceCombo_SelectionChangedCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task DeviceCombo_SelectionChangedCoreAsync(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDeviceSelection) return;
        using var execution = TryBeginExecution();
        if (execution is null)
        {
            // 即使程序触发 SelectionChanged，也恢复会话目标；不能只依赖控件禁用。
            _refreshingDeviceSelection = true;
            try
            {
                DeviceCombo.SelectedItem = DeviceCombo.Items.Cast<AdbDevice>()
                    .FirstOrDefault(device => device.Serial == _preferredDeviceSerial);
            }
            finally { _refreshingDeviceSelection = false; }
            return;
        }
        var selected = DeviceCombo.SelectedItem as AdbDevice;
        _preferredDeviceSerial = selected?.Serial;
        _preferredDeviceBootId = selected?.BootId ?? Guid.Empty;
        UpdateSelectedDeviceState(selected);
        try
        {
            await SyncDeviceServicesAsync(selected?.IsAvailable == true ? selected.Serial : null, ct: _windowTasks.Token);
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"切换设备服务失败: {ex.Message}"));
            ConnectionStateText.Text = L.Text("设备服务处理失败，请检查日志与设置");
        }
    }

    /// <summary>开始时核对用户选择的实例，失效后刷新列表，交由用户重新选择。</summary>
    private async Task<string?> GetOnlineTaskSerialAsync(DeviceExecutionGate.Lease execution, TextBlock taskStatus, CancellationToken ct)
    {
        void Report(string message)
        {
            taskStatus.Text = message;
            AccountStatusText.Text = message;
            _logger?.Warn(message);
        }

        if (_adb is null || DeviceCombo.SelectedItem is not AdbDevice selected)
        {
            Report(L.Text("请先刷新并选择可用的模拟器设备"));
            return null;
        }

        string? issue;
        try
        {
            issue = await EmulatorDeviceSelector.GetStartIssueAsync(_adb, selected, ct);
            if (issue is null)
            {
                var busy = await ClaimTaskDeviceAsync(execution, selected.Serial, selected.BootId, ct);
                if (busy is null) return selected.Serial;
                Report(busy);
                return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(L.Format($"无法核验设备 {selected.Serial}，请检查 ADB 连接后重试。详情：{ex.Message}"));
            return null;
        }

        // 先撤销旧选择。即使重新发现设备失败，旧行也不能继续作为有效目标。
        _refreshingDeviceSelection = true;
        try { DeviceCombo.ItemsSource = null; }
        finally { _refreshingDeviceSelection = false; }
        UpdateSelectedDeviceState(null);
        Report(issue);
        await SyncDeviceServicesAsync(null, ct);
        await RefreshDevicesAsync(ct, preserveSelection: false);
        taskStatus.Text = issue;
        AccountStatusText.Text = issue;
        return null;
    }

    /// <summary>已知启动身份由开始前的在线核验确认。旧列表无身份时，再向目标查询一次。</summary>
    private async Task<string?> ClaimTaskDeviceAsync(DeviceExecutionGate.Lease execution, string serial, Guid bootId, CancellationToken ct)
    {
        if (bootId == Guid.Empty)
        {
            try { Guid.TryParse(await _adb!.GetBootIdAsync(serial, ct), out bootId); }
            catch (AdbException) { /* 部分模拟器不能读取 boot_id，仍按 ADB 地址互斥。 */ }
        }
        ct.ThrowIfCancellationRequested();
        return execution.TryClaimDevice(serial, bootId) ? null
            : L.Format($"设备正在使用：{serial}。请等待另一个 GGEA 窗口的任务结束，或选择其他模拟器。");
    }

    /// <summary>获取当前选中的设备序列号。</summary>
    private bool TryGetDeviceSerial(out string serial)
    {
        serial = string.Empty;
        if (DeviceCombo.SelectedItem is not AdbDevice { IsAvailable: true } device)
        {
            _logger?.Warn(L.Text("请先刷新并选择可用的模拟器设备"));
            return false;
        }

        serial = device.Serial;
        return true;
    }

    /// <summary>
    /// 验证当前截图属于支持的四档横屏尺寸。不兼容时仅阻止业务自动化，
    /// 手动截图仍保持可用，方便用户调整模拟器设置后再次检查。
    /// </summary>
    private async Task<bool> EnsureDeviceCompatibilityAsync(string serial, CancellationToken ct)
    {
        if (_deviceCompatibility is null)
        {
            return false;
        }

        AccountStatusText.Text = L.Text("正在检查设备画面…");
        var result = await _deviceCompatibility.CheckAsync(serial, ct);
        if (result.IsCompatible)
        {
            _logger?.Info(result.Message);
            return true;
        }

        _logger?.Warn(result.Message);
        ConnectionStateText.Text = result.Width == 0 ? L.Text("等待设备就绪") : L.Text("设备画面不兼容");
        AccountStatusText.Text = result.Message;
        return false;
    }
}
