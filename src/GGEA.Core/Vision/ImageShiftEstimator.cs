namespace GGEA.App.Vision;

/// <summary>
/// 两张截图之间的水平位移估算器。
/// 通过“列灰度签名”做一维互相关，用于判断画布是否发生了滚动、
/// 是否已到最左/最右端（回弹时位移≈0）。
/// </summary>
public static class ImageShiftEstimator
{
    /// <summary>
    /// 估算第二张图相对第一张图的水平内容位移。
    /// </summary>
    /// <param name="a">前一张截图。</param>
    /// <param name="b">后一张截图。</param>
    /// <returns>
    /// ShiftPx：内容位移（正=内容右移/视口左移，负=内容左移/视口右移）；
    /// Quality：匹配质量（0~1，越高越可靠）。
    /// </returns>
    public static (int ShiftPx, double Quality) MeasureHorizontalShift(ScreenImage a, ScreenImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return (0, 0);
        }

        // 取画面中两条水平带（避开顶部标题栏和底部按钮栏）
        var bands = new[] { (Y0: 220, Y1: 620), (Y0: 480, Y1: 900) };
        (int Shift, double Quality)? best = null;
        foreach (var (y0, y1) in bands)
        {
            var sigA = ColumnSignature(a, y0, y1);
            var sigB = ColumnSignature(b, y0, y1);

            var bestShift = 0;
            var bestScore = double.MaxValue;
            for (var shift = -2200; shift <= 2200; shift += 2)
            {
                var score = SsdOverlap(sigA, sigB, shift);
                if (score < bestScore ||
                    (score == bestScore && Math.Abs(shift) < Math.Abs(bestShift)))
                {
                    bestScore = score;
                    bestShift = shift;
                }
            }

            // 在最佳位移附近按 1px 步长细化
            for (var shift = bestShift - 3; shift <= bestShift + 3; shift++)
            {
                var score = SsdOverlap(sigA, sigB, shift);
                if (score < bestScore ||
                    (score == bestScore && Math.Abs(shift) < Math.Abs(bestShift)))
                {
                    bestScore = score;
                    bestShift = shift;
                }
            }

            var quality = Math.Clamp(1.0 - bestScore / 1e6, 0, 1);
            if (best is null || quality > best.Value.Quality)
            {
                best = (bestShift, quality);
            }
        }

        return best ?? (0, 0);
    }

    /// <summary>计算水平带内每一列的平均灰度，得到一维“列签名”。</summary>
    private static int[] ColumnSignature(ScreenImage img, int y0, int y1)
    {
        var sig = new int[img.Width];
        for (var x = 0; x < img.Width; x++)
        {
            long sum = 0;
            var count = 0;
            for (var y = y0; y < y1; y += 2)
            {
                var i = (y * img.Width + x) * 4;
                var b = img.Bgra[i];
                var g = img.Bgra[i + 1];
                var r = img.Bgra[i + 2];
                sum += (r * 3 + g * 6 + b) / 10;
                count++;
            }

            sig[x] = (int)(sum / Math.Max(1, count));
        }

        return sig;
    }

    /// <summary>计算两个一维信号在指定位移下的重叠均方误差。</summary>
    private static double SsdOverlap(int[] a, int[] b, int shift)
    {
        var start = Math.Max(0, -shift);
        var end = Math.Min(a.Length, b.Length - shift);
        if (end - start < 64)
        {
            return double.MaxValue;
        }

        double sum = 0;
        var count = 0;
        for (var i = start; i < end; i++)
        {
            var diff = a[i] - b[i + shift];
            sum += diff * diff;
            count++;
        }

        return sum / Math.Max(1, count);
    }
}
