namespace GGEA.App.Vision;

/// <summary>按用途命名的识别区域，避免不同流程各自维护相同按钮的坐标。</summary>
public enum RecognitionRegion
{
    /// <summary>底部往上 200 像素，横向覆盖整个画面。</summary>
    BottomButtons,
    /// <summary>开发数量按钮：距右边 0–855，距底边 200–365。</summary>
    DevelopmentQuantity,
    /// <summary>界面标题：左上角 1000×120 像素。</summary>
    PageTitle,
    /// <summary>强化页面的解体入口，包含按钮上缘，不能复用底部 200 像素区域。</summary>
    DismantleEntry,
    /// <summary>解体页面左下方的自动解体按钮。</summary>
    AutoDismantle,
    /// <summary>解体页面右侧的筛选状态图标。</summary>
    DismantleFilter,
    /// <summary>排序对话框标题：画面顶部居中 286×140。</summary>
    SortTitle,
    /// <summary>排序对话框右下方重置按钮，避开底部确定和取消。</summary>
    SortReset,
    /// <summary>对话框提示：画面中央 1920×500。</summary>
    DialogPrompt,
    /// <summary>对话框按钮：全宽，距底部 160–300 像素。</summary>
    DialogButtons,
    /// <summary>自动解体滚动到底后的开发单位选项。</summary>
    DismantleDevelopOption,
    /// <summary>解体页面右侧 430 像素，距底部 120–285。</summary>
    DismantleButton,
    /// <summary>继续文字专用区域：底部 300 像素，包含功能解放提示；不扩大其他底部按钮区域。</summary>
    ProgressTapToNext,
}

/// <summary>识别区域统一由实际截图尺寸计算；不改变按钮的点击语义。</summary>
public static class RecognitionRegions
{
    /// <summary>全宽底部搜索排除浅色背景误匹配；实机执行按钮约为 92%。</summary>
    public const double BottomButtonThreshold = .90;

    /// <summary>以画面右边、底边为基准解析区域，防止模板更换后沿用窄文字区域。</summary>
    public static Roi Resolve(RecognitionRegion region, int width, int height) => region switch
    {
        RecognitionRegion.BottomButtons => new(0, Math.Max(0, height - 200), width, Math.Min(200, height)),
        RecognitionRegion.ProgressTapToNext => new(0, Math.Max(0, height - 300), width, Math.Min(300, height)),
        RecognitionRegion.PageTitle => new(0, 0, Math.Min(1000, width), Math.Min(120, height)),
        RecognitionRegion.DismantleEntry => Clip(780, 840, 310, 120, width, height),
        RecognitionRegion.AutoDismantle => Clip(60, 830, 290, 110, width, height),
        RecognitionRegion.DismantleFilter => Clip(1630, 695, 120, 110, width, height),
        RecognitionRegion.SortTitle => new(Math.Max(0, (width - 286) / 2), 0, Math.Min(286, width), Math.Min(140, height)),
        RecognitionRegion.SortReset => Clip(1530, 800, 300, 115, width, height),
        RecognitionRegion.DialogPrompt => new(Math.Max(0, (width - 1920) / 2), Math.Max(0, (height - 500) / 2), Math.Min(1920, width), Math.Min(500, height)),
        RecognitionRegion.DialogButtons => new(0, Math.Max(0, height - 300), width, Math.Max(0, Math.Min(300, height) - 160)),
        RecognitionRegion.DismantleDevelopOption => Clip(90, 530, 285, 110, width, height),
        RecognitionRegion.DismantleButton => new(Math.Max(0, width - 430), Math.Max(0, height - 285), Math.Min(430, width), Math.Max(0, Math.Min(285, height) - 120)),
        RecognitionRegion.DevelopmentQuantity => new(Math.Max(0, width - 855), Math.Max(0, height - 365),
            Math.Min(855, width), Math.Max(0, Math.Min(365, height) - 200)),
        _ => throw new ArgumentOutOfRangeException(nameof(region)),
    };

    /// <summary>固定区域按项目要求的 1920×1080 布局定义，小截图仅裁切，不推断缩放坐标。</summary>
    private static Roi Clip(int x, int y, int width, int height, int screenWidth, int screenHeight)
        => new(Math.Min(x, screenWidth), Math.Min(y, screenHeight),
            Math.Max(0, Math.Min(width, screenWidth - x)), Math.Max(0, Math.Min(height, screenHeight - y)));
}
