using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>路线图模板的会话缓存；固定档位立绘直接从原图生成，缓存托管 PNG，避免跨线程共享或泄漏 Mat。</summary>
public sealed class RoadmapUnitTemplate
{
    internal byte[] OriginalPng { get; }
    internal IReadOnlyDictionary<int, (byte[] Image, byte[] Mask)> Preferred { get; }

    /// <summary>原生素材的大、小显示比例；小图是大图的 64%，不经过大图二次缩放。</summary>
    public const double LargeScale = 1.3;
    public const double SmallScale = 0.832;

    /// <summary>MuMu 首次进入路线图的小单位，204px 原生素材对应 149px 显示宽度。</summary>
    public const double InitialSmallScale = 149.0 / 204;

    /// <summary>缓存固定档位默认比例的 RGB 和内部透明遮罩，供固定比例路径直接使用。</summary>
    public RoadmapUnitTemplate(byte[] png)
    {
        OriginalPng = (byte[])png.Clone();
        var preferred = new Dictionary<int, (byte[] Image, byte[] Mask)>();
        Preferred = preferred;
        using var original = Cv2.ImDecode(OriginalPng, ImreadModes.Unchanged);
        if (original.Empty()) throw new InvalidOperationException("机体模板不是有效图片");
        if (original.Channels() != 4) return;
        using var color = new Mat();
        using var alpha = new Mat();
        Cv2.CvtColor(original, color, ColorConversionCodes.BGRA2BGR);
        Cv2.ExtractChannel(original, alpha, 3);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        // MuMu 首次进入的小单位实测对应 149px；保留旧小图档位以兼容已有画面。
        foreach (var scale in new[] { InitialSmallScale, SmallScale, LargeScale })
        {
            var width = Math.Max(1, (int)Math.Round(original.Width * scale));
            var height = Math.Max(1, (int)Math.Round(original.Height * (double)width / original.Width));
            using var image = new Mat();
            using var mask = new Mat();
            Cv2.Resize(color, image, new Size(width, height), 0, 0, InterpolationFlags.Linear);
            Cv2.Resize(alpha, mask, image.Size(), 0, 0, InterpolationFlags.Linear);
            Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
            Cv2.Erode(mask, mask, kernel);
            preferred[width] = (image.ToBytes(".png"), mask.ToBytes(".png"));
        }
    }
}
