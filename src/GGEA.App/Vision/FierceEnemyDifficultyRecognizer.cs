using GGEA.App.Tasks;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>在用户指定的窄列中读取难度标签，避免把卡片正文或 CHALLENGE 当作可略过难度。</summary>
public sealed class FierceEnemyDifficultyRecognizer(OnnxTextRecognizer ocr, Roi? region = null)
{
    public static readonly Roi Region = new(966, 168, 311, 794);
    private readonly Roi _region = region ?? Region;

    /// <summary>亮色和锁定暗态字符按基线分组；只接受完整单词及高置信度的唯一结果。</summary>
    public Roi? Find(ScreenImage screen, FierceEnemyDifficulty difficulty, CancellationToken ct = default)
    {
        var matches = FindAll(screen, difficulty, ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>历史 BOSS 同难度有两关，返回全部标签；原强敌入口仍要求唯一，不能任取第一张卡片。</summary>
    public IReadOnlyList<Roi> FindAll(ScreenImage screen, FierceEnemyDifficulty difficulty, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(difficulty)) throw new ArgumentOutOfRangeException(nameof(difficulty));
        var binary = CreateLabelMask(screen, ct);
        using var source = Mat.FromPixelData(_region.Height, _region.Width, MatType.CV_8UC1, binary);
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(source, labels, stats, centers);
        var lines = new List<List<Roi>>();
        for (var i = 1; i < count; i++)
        {
            var b = new Roi(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            if (b.Height is < 14 or > 36 || b.Width < 2 || b.Width > b.Height || stats.At<int>(i, 4) < 15) continue;
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Y + l[0].Height / 2 - b.Y - b.Height / 2) <= 4);
            if (line is null) lines.Add([b]); else line.Add(b);
        }
        var matches = new List<Roi>();
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Count is < 4 or > 10) continue;
            var x = line.Min(b => b.X); var y = line.Min(b => b.Y);
            var right = line.Max(b => b.X + b.Width); var bottom = line.Max(b => b.Y + b.Height);
            if (x == 0 || y == 0 || right == _region.Width || bottom == _region.Height) continue;
            var bounds = new Roi(x, y, right - x, bottom - y);
            var result = ocr.Recognize(binary, _region.Width, bounds, ct);
            if (result.Text.Trim() == difficulty.ToString() && result.Confidence >= .90 &&
                (result.MinimumContentConfidence ?? result.MinimumConfidence) >= .85)
                matches.Add(new(_region.X + x, _region.Y + y, bounds.Width, bounds.Height));
        }
        return matches.OrderBy(match => match.Y).ToArray();
    }

    /// <summary>锁定卡片把字形压暗至约 85 灰阶；仅本难度列补取中性灰字，不改变共享 OCR 或置信度门槛。</summary>
    private byte[] CreateLabelMask(ScreenImage screen, CancellationToken ct)
    {
        var binary = OnnxTextRecognizer.CreateWhiteMask(screen, _region, ct);
        for (var y = 0; y < _region.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < _region.Width; x++)
            {
                var offset = ((_region.Y + y) * screen.Width + _region.X + x) * 4;
                var b = screen.Bgra[offset]; var g = screen.Bgra[offset + 1]; var r = screen.Bgra[offset + 2];
                var min = Math.Min(r, Math.Min(g, b)); var max = Math.Max(r, Math.Max(g, b));
                if (min >= 70 && max - min <= 15) binary[y * _region.Width + x] = 255;
            }
        }
        return binary;
    }

    /// <summary>略过按钮在难度右下方；被底部导航裁切时不点击，交给调用方滚动后重新 OCR。</summary>
    public static Roi? GetSkipRegion(Roi label)
        => label.Y + 260 > 950 ? null : new Roi(1280, label.Y + 160, 280, 100);
}
