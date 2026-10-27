namespace GGEA.App.Vision;

/// <summary>
/// 纯 C# 模板匹配器。
/// 使用灰度平方差（SSD）相似度 + 图像金字塔加速：
/// 小搜索区域精确搜索；大区域先用金字塔定位，未达阈值时以误差下界剪枝做原图复查，避免粗层漏检。
/// 支持可选遮罩：遮罩为 0 的模板像素不参与匹配，
/// 用于屏蔽“呼吸”红点、动态数字、随语言变化的文字等不稳定区域；
/// 未显式传入遮罩时，透明模板会自动把 Alpha 小于 128 的背景排除。
/// </summary>
public sealed class TemplateMatcher
{
    /// <summary>
    /// 在屏幕 BGRA 图像中查找模板。
    /// </summary>
    /// <param name="screenBgra">屏幕 BGRA 像素。</param>
    /// <param name="screenWidth">屏幕宽度。</param>
    /// <param name="screenHeight">屏幕高度。</param>
    /// <param name="templateBgra">模板 BGRA 像素。</param>
    /// <param name="templateWidth">模板宽度。</param>
    /// <param name="templateHeight">模板高度。</param>
    /// <param name="threshold">置信度阈值，低于该值视为未找到。</param>
    /// <param name="roi">可选搜索区域；为空表示全屏搜索。</param>
    /// <param name="mask">可选遮罩（大小与模板一致，0 表示忽略该像素，非 0 表示参与匹配）。</param>
    /// <returns>匹配结果；未达到阈值时返回 null。</returns>
    public TemplateMatchResult? Match(
        byte[] screenBgra,
        int screenWidth,
        int screenHeight,
        byte[] templateBgra,
        int templateWidth,
        int templateHeight,
        double threshold,
        Roi? roi = null,
        byte[]? mask = null,
        CancellationToken ct = default)
        => MatchCore(screenBgra, screenWidth, screenHeight, templateBgra, templateWidth, templateHeight,
            threshold, roi, mask, ct, includeRejectedCandidate: false);

    /// <summary>诊断也按业务阈值执行复查，但允许带回未达标候选；调用方必须再次过滤，低分绝不授权点击。</summary>
    internal TemplateMatchResult? MatchCandidate(byte[] screenBgra, int screenWidth, int screenHeight,
        byte[] templateBgra, int templateWidth, int templateHeight, double threshold, Roi roi, CancellationToken ct)
        => MatchCore(screenBgra, screenWidth, screenHeight, templateBgra, templateWidth, templateHeight,
            threshold, roi, null, ct, includeRejectedCandidate: true);

    /// <summary>业务识别和诊断共享同一搜索过程，避免用阈值零获取分数时绕过漏检复查。</summary>
    private TemplateMatchResult? MatchCore(byte[] screenBgra, int screenWidth, int screenHeight,
        byte[] templateBgra, int templateWidth, int templateHeight, double threshold, Roi? roi, byte[]? mask,
        CancellationToken ct, bool includeRejectedCandidate)
    {
        ct.ThrowIfCancellationRequested();
        Validate(screenBgra, screenWidth, screenHeight, templateBgra, templateWidth, templateHeight, mask);
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "匹配阈值必须为 0 到 1 的有限数值");

        // 透明 PNG 本身就是最贴近素材的遮罩来源；显式遮罩仍具有更高优先级，
        // 以便调用方继续精确控制参与匹配的区域。
        var effectiveMask = mask ?? CreateAlphaMask(templateBgra, templateWidth, templateHeight);

        // 1. 裁剪并规范化搜索区域
        var region = roi ?? new Roi(0, 0, screenWidth, screenHeight);
        region = ClipToBounds(region, screenWidth, screenHeight);
        if (region.Width < templateWidth || region.Height < templateHeight)
        {
            return null;
        }

        // 2. 提取 ROI 与模板的灰度数据（匹配在灰度空间进行）
        var screenGray = new byte[region.Width * region.Height];
        FillGrayRegion(screenBgra, screenWidth, screenHeight, region, screenGray);
        var templateGray = new byte[templateWidth * templateHeight];
        FillGray(templateBgra, templateGray);

        var validPixels = effectiveMask is null ? templateWidth * templateHeight : CountValid(effectiveMask);
        if (validPixels == 0) return null;
        // 导航小 ROI 的候选很少，直接精确搜索同时保证诊断分数不受粗层单候选影响。
        if ((long)(region.Width - templateWidth + 1) * (region.Height - templateHeight + 1) <= 4096)
        {
            var exact = FullSearch(screenGray, region.Width, region.Height, templateGray, templateWidth, templateHeight, effectiveMask, 1, ct);
            var score = Confidence(exact.Ssd, validPixels);
            return score < threshold && !includeRejectedCandidate ? null : new TemplateMatchResult(
                new Roi(region.X + exact.X, region.Y + exact.Y, templateWidth, templateHeight), score);
        }

        // 宽而矮的文字条只能建立很浅的金字塔，粗层穷举反而成为下载/弹窗检查的主要耗时。
        // 无遮罩时直接用原图误差下界剪枝，仍遍历全部合法位置、返回阈值内最优结果；不牺牲漏检保护。
        // 诊断需要阈值外候选，继续走原路径；稀疏遮罩也不适用完整像素块的加速收益。
        if (!includeRejectedCandidate && effectiveMask is null && templateWidth >= 4L * templateHeight)
        {
            var bounded = SearchWithinThreshold(screenGray, region.Width, region.Height,
                templateGray, templateWidth, templateHeight, null,
                (1 - threshold) * (1 - threshold) * validPixels * 255.0 * 255.0, ct);
            if (bounded is null) return null;
            var score = Confidence(bounded.Value.Ssd, validPixels);
            return score < threshold ? null : new TemplateMatchResult(
                new Roi(region.X + bounded.Value.X, region.Y + bounded.Value.Y, templateWidth, templateHeight), score);
        }

        // 3. 构建金字塔（模板与遮罩层级数保持一致）
        var screenLevels = BuildLevels(screenGray, region.Width, region.Height);
        var templateLevels = BuildLevels(templateGray, templateWidth, templateHeight);
        var maskLevels = effectiveMask is null ? null : BuildMaskLevels(effectiveMask, templateWidth, templateHeight);
        var levelCount = Math.Min(screenLevels.Count, templateLevels.Count);
        if (maskLevels is not null)
        {
            levelCount = Math.Min(levelCount, maskLevels.Count);
        }

        // 4. 从最粗层级开始粗搜
        var coarseLevel = screenLevels[levelCount - 1];
        var coarseTemplate = templateLevels[levelCount - 1];
        var coarseMask = maskLevels?[levelCount - 1];
        var coarseValidCount = coarseMask is null
            ? coarseTemplate.Width * coarseTemplate.Height
            : CountValid(coarseMask.Data);
        if (coarseValidCount == 0)
        {
            return null;
        }

        var best = FullSearch(
            coarseLevel.Data, coarseLevel.Width, coarseLevel.Height,
            coarseTemplate.Data, coarseTemplate.Width, coarseTemplate.Height,
            coarseMask?.Data,
            step: 1, ct);

        // 5. 逐层细化到原分辨率
        for (var level = levelCount - 2; level >= 0; level--)
        {
            var screenLevel = screenLevels[level];
            var templateLevel = templateLevels[level];
            var maskLevel = maskLevels?[level];

            // 上一层坐标放大一倍后，在当前层局部搜索
            best.X *= 2;
            best.Y *= 2;
            var radius = level == 0 ? 4 : 2;
            best = LocalSearch(
                screenLevel.Data, screenLevel.Width, screenLevel.Height,
                templateLevel.Data, templateLevel.Width, templateLevel.Height,
                maskLevel?.Data,
                best.X, best.Y, radius, ct);
        }

        // 6. 原分辨率精确置信度
        var finalMask = maskLevels?[0];
        var validCount = finalMask is null
            ? templateWidth * templateHeight
            : CountValid(finalMask.Data);
        if (validCount == 0)
        {
            return null;
        }

        var confidence = Confidence(best.Ssd, validCount);
        if (confidence < threshold && levelCount > 1)
        {
            // 粗层平均会抹去纹理，且透明边缘的降采样会混入无效像素；不能据此宣告全区域未命中。
            var verified = SearchWithinThreshold(screenGray, region.Width, region.Height,
                templateGray, templateWidth, templateHeight, effectiveMask,
                (1 - threshold) * (1 - threshold) * validCount * 255.0 * 255.0, ct);
            if (verified is null && !includeRejectedCandidate) return null;
            if (verified is not null) best = verified.Value;
            confidence = Confidence(best.Ssd, validCount);
            if (confidence < threshold && !includeRejectedCandidate) return null;
        }
        if (confidence < threshold && !includeRejectedCandidate) return null;

        var matchRegion = new Roi(region.X + best.X, region.Y + best.Y, templateWidth, templateHeight);
        return new TemplateMatchResult(matchRegion, confidence);
    }

    private static void Validate(
        byte[] screenBgra, int screenWidth, int screenHeight,
        byte[] templateBgra, int templateWidth, int templateHeight,
        byte[]? mask)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || templateWidth <= 0 || templateHeight <= 0)
        {
            throw new ArgumentException("图像尺寸必须为正数");
        }

        if (screenBgra.Length < screenWidth * screenHeight * 4)
        {
            throw new ArgumentException("屏幕像素数据长度不足");
        }

        if (templateBgra.Length < templateWidth * templateHeight * 4)
        {
            throw new ArgumentException("模板像素数据长度不足");
        }

        if (mask is not null && mask.Length < templateWidth * templateHeight)
        {
            throw new ArgumentException("遮罩数据长度不足");
        }
    }

    /// <summary>
    /// 从模板 Alpha 通道生成二值遮罩；完全不透明的普通截图返回 null，
    /// 保持原有全像素匹配路径与性能。
    /// </summary>
    private static byte[]? CreateAlphaMask(byte[] templateBgra, int width, int height)
    {
        var pixelCount = width * height;
        var hasTransparency = false;
        for (var i = 0; i < pixelCount; i++)
        {
            if (templateBgra[i * 4 + 3] < 255)
            {
                hasTransparency = true;
                break;
            }
        }

        if (!hasTransparency)
        {
            return null;
        }

        var alphaMask = new byte[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            alphaMask[i] = templateBgra[i * 4 + 3] >= 128 ? (byte)255 : (byte)0;
        }

        return alphaMask;
    }

    /// <summary>把 ROI 裁剪到屏幕范围内。</summary>
    private static Roi ClipToBounds(Roi roi, int screenWidth, int screenHeight)
    {
        var x = Math.Clamp(roi.X, 0, Math.Max(0, screenWidth - 1));
        var y = Math.Clamp(roi.Y, 0, Math.Max(0, screenHeight - 1));
        var right = Math.Clamp(roi.X + roi.Width, x + 1, screenWidth);
        var bottom = Math.Clamp(roi.Y + roi.Height, y + 1, screenHeight);
        return new Roi(x, y, right - x, bottom - y);
    }

    /// <summary>整幅图像 BGRA 转灰度。</summary>
    private static void FillGray(byte[] bgra, byte[] gray)
    {
        for (var i = 0; i < gray.Length; i++)
        {
            var offset = i * 4;
            gray[i] = ToGray(bgra[offset + 2], bgra[offset + 1], bgra[offset]);
        }
    }

    /// <summary>ROI 区域 BGRA 转灰度（只处理目标区域）。</summary>
    private static void FillGrayRegion(byte[] bgra, int screenWidth, int screenHeight, Roi roi, byte[] gray)
    {
        for (var y = 0; y < roi.Height; y++)
        {
            var srcRow = (roi.Y + y) * screenWidth + roi.X;
            var dstRow = y * roi.Width;
            for (var x = 0; x < roi.Width; x++)
            {
                var offset = (srcRow + x) * 4;
                gray[dstRow + x] = ToGray(bgra[offset + 2], bgra[offset + 1], bgra[offset]);
            }
        }
    }

    /// <summary>标准灰度转换（整数运算）。</summary>
    private static byte ToGray(byte r, byte g, byte b)
    {
        return (byte)((r * 299 + g * 587 + b * 114) / 1000);
    }

    /// <summary>构建图像金字塔，每层宽高减半（2x2 平均）。</summary>
    private static List<ImageLevel> BuildLevels(byte[] data, int width, int height)
    {
        var levels = new List<ImageLevel> { new(data, width, height) };

        // 模板过小时不再继续降采样，避免丢失特征
        while (levels[^1].Width >= 48 && levels[^1].Height >= 48 && levels.Count < 4)
        {
            var prev = levels[^1];
            var newWidth = (prev.Width + 1) / 2;
            var newHeight = (prev.Height + 1) / 2;
            var next = new byte[newWidth * newHeight];

            for (var y = 0; y < newHeight; y++)
            {
                for (var x = 0; x < newWidth; x++)
                {
                    var sum = 0;
                    var count = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var sy = y * 2 + dy;
                            var sx = x * 2 + dx;
                            if (sy < prev.Height && sx < prev.Width)
                            {
                                sum += prev.Data[sy * prev.Width + sx];
                                count++;
                            }
                        }
                    }

                    next[y * newWidth + x] = (byte)(sum / count);
                }
            }

            levels.Add(new ImageLevel(next, newWidth, newHeight));
        }

        return levels;
    }

    /// <summary>构建遮罩金字塔：只要 2x2 中有一个像素有效，该像素就参与匹配。</summary>
    private static List<ImageLevel> BuildMaskLevels(byte[] mask, int width, int height)
    {
        var levels = new List<ImageLevel> { new(mask, width, height) };

        while (levels[^1].Width >= 48 && levels[^1].Height >= 48 && levels.Count < 4)
        {
            var prev = levels[^1];
            var newWidth = (prev.Width + 1) / 2;
            var newHeight = (prev.Height + 1) / 2;
            var next = new byte[newWidth * newHeight];

            for (var y = 0; y < newHeight; y++)
            {
                for (var x = 0; x < newWidth; x++)
                {
                    var valid = false;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var sy = y * 2 + dy;
                            var sx = x * 2 + dx;
                            if (sy < prev.Height && sx < prev.Width && prev.Data[sy * prev.Width + sx] != 0)
                            {
                                valid = true;
                            }
                        }
                    }

                    next[y * newWidth + x] = valid ? (byte)255 : (byte)0;
                }
            }

            levels.Add(new ImageLevel(next, newWidth, newHeight));
        }

        return levels;
    }

    /// <summary>统计遮罩中参与匹配的像素数。</summary>
    private static int CountValid(byte[] mask)
    {
        var count = 0;
        foreach (var value in mask)
        {
            if (value != 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>在整幅图上按步长粗搜索，返回 SSD 最小的位置。</summary>
    private static (int X, int Y, double Ssd) FullSearch(
        byte[] screen, int screenWidth, int screenHeight,
        byte[] template, int templateWidth, int templateHeight,
        byte[]? mask,
        int step, CancellationToken ct)
    {
        var bestSsd = double.MaxValue;
        var bestX = 0;
        var bestY = 0;
        var limitX = screenWidth - templateWidth;
        var limitY = screenHeight - templateHeight;

        for (var y = 0; y <= limitY; y += step)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x <= limitX; x += step)
            {
                var ssd = ComputeSsd(screen, screenWidth, template, templateWidth, templateHeight, x, y, bestSsd, mask);
                if (ssd < bestSsd)
                {
                    bestSsd = ssd;
                    bestX = x;
                    bestY = y;
                    if (ssd == 0) return (x, y, ssd);
                }
            }
        }

        return (bestX, bestY, bestSsd);
    }

    /// <summary>在中心点附近做局部全像素搜索。</summary>
    private static (int X, int Y, double Ssd) LocalSearch(
        byte[] screen, int screenWidth, int screenHeight,
        byte[] template, int templateWidth, int templateHeight,
        byte[]? mask,
        int centerX, int centerY, int radius, CancellationToken ct)
    {
        var bestSsd = double.MaxValue;
        var bestX = centerX;
        var bestY = centerY;
        var minX = Math.Max(0, centerX - radius);
        var maxX = Math.Min(screenWidth - templateWidth, centerX + radius);
        var minY = Math.Max(0, centerY - radius);
        var maxY = Math.Min(screenHeight - templateHeight, centerY + radius);

        for (var y = minY; y <= maxY; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = minX; x <= maxX; x++)
            {
                var ssd = ComputeSsd(screen, screenWidth, template, templateWidth, templateHeight, x, y, bestSsd, mask);
                if (ssd < bestSsd)
                {
                    bestSsd = ssd;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        return (bestX, bestY, bestSsd);
    }

    /// <summary>
    /// 计算模板在 (x, y) 处的灰度平方差（遮罩为 0 的像素跳过）；
    /// 当累计误差超过当前最优值时提前退出（剪枝）。
    /// </summary>
    private static double ComputeSsd(
        byte[] screen, int screenWidth,
        byte[] template, int templateWidth, int templateHeight,
        int x, int y, double bestSsd,
        byte[]? mask)
    {
        var ssd = 0.0;
        for (var ty = 0; ty < templateHeight; ty++)
        {
            var screenRow = (y + ty) * screenWidth + x;
            var templateRow = ty * templateWidth;
            for (var tx = 0; tx < templateWidth; tx++)
            {
                if (mask is not null && mask[templateRow + tx] == 0)
                {
                    continue;
                }

                var diff = screen[screenRow + tx] - template[templateRow + tx];
                ssd += diff * diff;
                if (ssd > bestSsd)
                {
                    return double.MaxValue;
                }
            }
        }

        return ssd;
    }

    /// <summary>
    /// 将 SSD 转换为 0~1 置信度：完全一致为 1，差异越大越接近 0。
    /// 使用遮罩时按参与匹配的像素数归一化。
    /// </summary>
    private static double Confidence(double ssd, int validCount)
    {
        if (ssd <= 0)
        {
            return 1.0;
        }

        var ratio = ssd / (validCount * 255.0 * 255.0);
        return Math.Clamp(1.0 - Math.Sqrt(ratio), 0.0, 1.0);
    }

    /// <summary>
    /// 在原图所有合法位置复查阈值。柯西不等式给出均值差与中心化像素范数差的平方和下界。
    /// 只使用互不重叠且完全有效的模板块，因此遮罩和奇数尺寸也不会错误剪掉真实目标。
    /// </summary>
    private static (int X, int Y, double Ssd)? SearchWithinThreshold(byte[] screen, int width, int height,
        byte[] template, int templateWidth, int templateHeight, byte[]? mask, double maximumSsd, CancellationToken ct)
    {
        var stride = width + 1;
        var integral = new long[stride * (height + 1)];
        var squaredIntegral = new long[integral.Length];
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            long row = 0;
            long squaredRow = 0;
            for (var x = 0; x < width; x++)
            {
                var value = screen[y * width + x];
                row += value;
                squaredRow += value * value;
                integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1] + row;
                squaredIntegral[(y + 1) * stride + x + 1] = squaredIntegral[y * stride + x + 1] + squaredRow;
            }
        }
        var blocks = new List<SumBlock>();
        var blockWidth = Math.Max(1, (templateWidth + 7) / 8);
        var blockHeight = Math.Max(1, (templateHeight + 7) / 8);
        for (var y = 0; y < templateHeight; y += blockHeight)
            for (var x = 0; x < templateWidth; x += blockWidth)
            {
                var right = Math.Min(templateWidth, x + blockWidth);
                var bottom = Math.Min(templateHeight, y + blockHeight);
                var allValid = true;
                long sum = 0;
                long squaredSum = 0;
                for (var by = y; by < bottom; by++)
                    for (var bx = x; bx < right; bx++)
                    {
                        var index = by * templateWidth + bx;
                        allValid &= mask is null || mask[index] != 0;
                        sum += template[index];
                        squaredSum += template[index] * template[index];
                    }
                var count = (right - x) * (bottom - y);
                if (allValid) blocks.Add(new SumBlock(x, y, right, bottom, sum, count,
                    Math.Sqrt(Math.Max(0, squaredSum - (double)sum * sum / count))));
            }

        (int X, int Y, double Ssd)? best = null;
        // 极小容差仅防止浮点边界误剪枝；返回前仍执行原始阈值比较，不放宽业务阈值。
        var bound = maximumSsd + 1e-6;
        for (var y = 0; y <= height - templateHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x <= width - templateWidth; x++)
            {
                double lowerBound = 0;
                foreach (var block in blocks)
                {
                    var left = x + block.Left;
                    var right = x + block.Right;
                    var top = y + block.Top;
                    var bottom = y + block.Bottom;
                    var sum = integral[bottom * stride + right] - integral[top * stride + right]
                        - integral[bottom * stride + left] + integral[top * stride + left];
                    var difference = (double)sum - block.Sum;
                    lowerBound += difference * difference / block.Count;
                    if (lowerBound > bound) break;
                    // 均值相同的纯色背景和高频纹理也能安全排除，避免逐位置扫描大量原始像素。
                    var squaredSum = squaredIntegral[bottom * stride + right] - squaredIntegral[top * stride + right]
                        - squaredIntegral[bottom * stride + left] + squaredIntegral[top * stride + left];
                    var norm = Math.Sqrt(Math.Max(0, squaredSum - (double)sum * sum / block.Count));
                    var normDifference = norm - block.CenteredNorm;
                    lowerBound += normDifference * normDifference;
                    if (lowerBound > bound) break;
                }
                if (lowerBound > bound) continue;
                var ssd = ComputeSsd(screen, width, template, templateWidth, templateHeight, x, y, bound, mask);
                if (ssd <= bound)
                {
                    best = (x, y, ssd);
                    bound = ssd;
                    if (ssd == 0) return best;
                }
            }
        }
        return best;
    }

    /// <summary>互不重叠的有效像素块，用于计算可证明安全的 SSD 下界。</summary>
    private readonly record struct SumBlock(int Left, int Top, int Right, int Bottom, long Sum, int Count, double CenteredNorm);

    /// <summary>金字塔中的一层图像。</summary>
    private sealed record ImageLevel(byte[] Data, int Width, int Height);
}
