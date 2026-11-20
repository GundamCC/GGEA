using System.Collections.ObjectModel;
using System.Windows;
using GGEA.App.Config;
using GGEA.App.Localization;

namespace GGEA.App.UI;

/// <summary>开发版作品画布坐标的加载、编辑与保存。</summary>
public partial class MainWindow
{
    private void ConnectStagePositionView()
    {
        StagePositionView.StagePositionRefreshButton.Click += StagePositionRefreshButton_Click;
        StagePositionView.StagePositionSaveButton.Click += StagePositionSaveButton_Click;
    }

    private readonly ObservableCollection<StagePositionItem> _stagePositionItems = new();

    /// <summary>按作品 ID 关联 hard_stages 名称，刷新位置调整列表。</summary>
    private void RefreshStagePositionItems()
    {
        var names = _hardStages.ToDictionary(work => work.Id, work => work.Work);
        _stagePositionItems.Clear();
        foreach (var position in _stagePositions.Positions.OrderBy(p => p.Id))
        {
            _stagePositionItems.Add(new StagePositionItem
            {
                Id = position.Id,
                Work = names.GetValueOrDefault(position.Id, L.Format($"作品 {position.Id}")),
                CanvasX = position.CanvasX,
                CanvasY = position.CanvasY,
            });
        }

        if (StagePositionView.StagePositionStatusText is not null)
        {
            StagePositionView.StagePositionStatusText.Text = L.Format($"共 {_stagePositionItems.Count} 个作品位置；修改后点击“保存”写入坐标文件。");
        }
    }

    /// <summary>重新从坐标文件加载作品位置并刷新列表。</summary>
    private void StagePositionRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_logger is null)
        {
            return;
        }

        _stagePositions = new StageWorkPositionsService(_logger)
            .Load(ConfigService.ResolvePath(_config.StageWorkPositionsFile));
        RefreshStagePositionItems();
        _logger.Info(L.Format($"已重新加载作品位置: {_stagePositions.Positions.Count} 个"));
    }

    /// <summary>将位置调整列表中的坐标保存回作品位置文件。</summary>
    private void StagePositionSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_logger is null)
        {
            return;
        }

        try
        {
            var edited = new StageWorkPositionsData();
            foreach (var item in _stagePositionItems)
            {
                if (item.CanvasX <= 0 || item.CanvasY <= 0)
                {
                    _logger.Warn(L.Format($"作品 {item.Id} 坐标无效，已跳过保存: ({item.CanvasX},{item.CanvasY})"));
                    continue;
                }
                edited.Positions.Add(new StageWorkPosition { Id = item.Id, CanvasX = item.CanvasX, CanvasY = item.CanvasY });
            }
            _stagePositions = new StageWorkPositionsService(_logger).SaveEdits(_stagePositions, edited,
                ConfigService.ResolvePath(_config.StageWorkPositionsFile));
            RefreshStagePositionItems();
            _logger.Info(L.Format($"已保存 {_stagePositions.Positions.Count} 个作品位置"));
            StagePositionView.StagePositionStatusText.Text = L.Format($"已保存 {_stagePositions.Positions.Count} 个作品位置。");
        }
        catch (Exception ex)
        {
            _logger.Error(L.Format($"保存作品位置失败: {ex.Message}"));
            StagePositionView.StagePositionStatusText.Text = L.Format($"保存失败: {ex.Message}");
        }
    }

    /// <summary>位置调整列表行：包装画布坐标，便于直接编辑并保存。</summary>
    private sealed class StagePositionItem
    {
        public int Id { get; init; }
        public string Work { get; init; } = string.Empty;
        public int CanvasX { get; set; }
        public int CanvasY { get; set; }
    }
}
