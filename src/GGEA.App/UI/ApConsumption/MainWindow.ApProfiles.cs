using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>AP 作品配置的选择、编辑、保存和排序。</summary>
public partial class MainWindow
{
    private void ConnectApConsumptionView()
    {
        ApConsumptionView.ApWorkList.ItemsSource = _apWorkItems;
        _ = new ListReorder<ApConsumptionWorkItem>(ApConsumptionView.ApWorkList, _apWorkItems, item => item.Work,
            message => ApConsumptionView.ApPlanStatusText.Text = message);
        ApConsumptionView.ApProfileCombo.SelectionChanged += ApProfileCombo_SelectionChanged;
        ApConsumptionView.DeleteProfileRequested += DeleteApProfileButton_Click;
        ApConsumptionView.SaveApProfileButton.Click += SaveApProfileButton_Click;
    }

    private ApConsumptionProfileService? _apProfileService;
    private ApConsumptionProfilesData _apProfiles = new();
    private List<HardStageEntry> _hardStages = new();
    private StageWorkPositionsData _stagePositions = new();
    private readonly ObservableCollection<ApConsumptionWorkItem> _apWorkItems = new();
    private bool _loadingApProfile;

    /// <summary>从本地 JSON 加载 AP 消耗命名配置，并恢复最后选择的方案。</summary>
    private void LoadApProfiles()
    {
        if (_apProfileService is null)
        {
            return;
        }

        _apProfiles = _apProfileService.Load(ConfigService.ResolvePath(_config.ApConsumptionProfilesFile));
        RefreshApProfileCombo(_apProfiles.LastSelectedProfile);
    }

    /// <summary>刷新方案下拉框，并在存在指定名称时自动应用。</summary>
    private void RefreshApProfileCombo(string? selectedName)
    {
        _loadingApProfile = true;
        try
        {
            ApConsumptionView.ApProfileCombo.ItemsSource = _apProfiles.Profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = _apProfiles.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                ApConsumptionView.ApProfileCombo.SelectedItem = selected;
                ApConsumptionView.ApProfileNameBox.Text = selected.Name;
                ApConsumptionPlanner.ApplyProfile(selected, _apWorkItems);
                ApConsumptionView.ApPlanStatusText.Text = L.Format($"已加载配置：{selected.Name}");
            }
            else
            {
                ApConsumptionView.ApProfileCombo.SelectedIndex = -1;
                ApConsumptionView.ApProfileNameBox.Clear();
            }
        }
        finally
        {
            _loadingApProfile = false;
        }
    }

    /// <summary>用户从下拉框选择配置时恢复作品顺序与 Hard 勾选状态。</summary>
    private void ApProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_loadingApProfile || _apProfileService is null || ApConsumptionView.ApProfileCombo.SelectedItem is not ApConsumptionProfile profile)
        {
            return;
        }

        ApConsumptionPlanner.ApplyProfile(profile, _apWorkItems);
        ApConsumptionView.ApProfileNameBox.Text = profile.Name;
        try
        {
            _apProfiles = _apProfileService.Edit(ConfigService.ResolvePath(_config.ApConsumptionProfilesFile), latest =>
            {
                if (ApConsumptionProfileService.Find(latest, profile.Name) is null)
                    throw new IOException(L.Text("此设置已被其他窗口修改，请重新打开后再保存。"));
                latest.LastSelectedProfile = profile.Name;
            });
            RefreshApProfileCombo(profile.Name);
            ApConsumptionView.ApPlanStatusText.Text = L.Format($"已切换配置：{profile.Name}");
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"保存 AP 消耗配置失败: {ex.Message}"));
            ApConsumptionView.ApPlanStatusText.Text = L.Format($"保存失败：{ex.Message}");
        }
    }

    /// <summary>保存当前完整作品顺序及 Hard 勾选状态；同名配置直接覆盖。</summary>
    private void SaveApProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_apProfileService is null)
        {
            return;
        }

        var name = ApConsumptionView.ApProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ApConsumptionView.ApPlanStatusText.Text = L.Text("请输入配置名称后再保存。");
            return;
        }

        try
        {
            var profile = ApConsumptionPlanner.CreateProfile(name, _apWorkItems);
            _apProfiles = _apProfileService.Edit(ConfigService.ResolvePath(_config.ApConsumptionProfilesFile), latest =>
            {
                ApConsumptionProfileService.CheckProfile(ApConsumptionProfileService.Find(_apProfiles, name),
                    ApConsumptionProfileService.Find(latest, name));
                _apProfileService.Upsert(latest, profile);
            });
            RefreshApProfileCombo(profile.Name);
            ApConsumptionView.ApPlanStatusText.Text = L.Format($"配置“{profile.Name}”已保存。");
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"保存 AP 消耗配置失败: {ex.Message}"));
            ApConsumptionView.ApPlanStatusText.Text = L.Format($"保存失败：{ex.Message}");
        }
    }

    /// <summary>删除下拉框中指定名称的 AP 消耗方案并刷新列表。</summary>
    private void DeleteApProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_apProfileService is null ||
            (sender as FrameworkElement)?.Tag is not ApConsumptionProfile profile)
        {
            return;
        }

        try
        {
            _apProfiles = _apProfileService.Edit(ConfigService.ResolvePath(_config.ApConsumptionProfilesFile), latest =>
            {
                var current = ApConsumptionProfileService.Find(latest, profile.Name);
                if (current is null) return;
                ApConsumptionProfileService.CheckProfile(profile, current);
                latest.Profiles.Remove(current);
                if (string.Equals(latest.LastSelectedProfile, profile.Name, StringComparison.OrdinalIgnoreCase))
                    latest.LastSelectedProfile = string.Empty;
            });
            RefreshApProfileCombo(_apProfiles.LastSelectedProfile);
            ApConsumptionView.ApPlanStatusText.Text = L.Format($"方案“{profile.Name}”已删除。");
            _logger?.Info(L.Format($"已删除 AP 消耗方案: {profile.Name}"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"删除 AP 消耗配置失败: {ex.Message}"));
            ApConsumptionView.ApPlanStatusText.Text = L.Format($"删除失败：{ex.Message}");
        }
    }

    /// <summary>加载作品列表与画布位置，并填充任务下拉框。</summary>
    private void LoadStageWorks()
    {
        LoadAutoProgressSettings();
        if (_logger is null)
        {
            return;
        }

        var hardService = new HardStagesService(_logger);
        _hardStages = hardService.Load(ConfigService.ResolvePath(_config.HardStagesFile)).HardStages;
        RefreshAutoProgressTargets();
        _stagePositions = new StageWorkPositionsService(_logger)
            .Load(ConfigService.ResolvePath(_config.StageWorkPositionsFile));

        _apWorkItems.Clear();
        foreach (var work in _hardStages)
        {
            _apWorkItems.Add(new ApConsumptionWorkItem
            {
                WorkId = work.Id,
                Work = work.Work,
                HardCount = work.HardCount,
            });
        }

        RefreshStagePositionItems();
        ApConsumptionView.ApPlanStatusText.Text = L.Format($"已加载 {_apWorkItems.Count} 个作品；拖拽可调整执行顺序。");
        _logger.Info(L.Format($"已加载 {_hardStages.Count} 个作品、{_stagePositions.Positions.Count} 个画布位置"));
    }
}
