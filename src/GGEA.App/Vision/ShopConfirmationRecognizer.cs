using System.IO;
using GGEA.App.UI;

namespace GGEA.App.Vision;

/// <summary>未识别与风险都不允许购买，只有正面确认免费才能授权。</summary>
public enum PurchaseSafety { Unknown, RiskDetected, SafeFree }

/// <summary>一帧证据只授权本帧按钮，年龄从截图请求前计时，包含截图和识别耗时。</summary>
public sealed record PurchaseEvidence(PurchaseSafety Safety, string Reason,
    TemplateMatchResult? Button, long CaptureStarted)
{
    // 包含截图、解码与安全识别，允许模拟器短时卡顿；超过五秒仍须丢弃并重新取证。
    public const int LifetimeMs = 5000;

    /// <summary>仅完整免费校验通过后的超时可重新取证；不能重试风险或未知画面来绕过否决。</summary>
    public bool Expired { get; init; }

    /// <summary>单调时钟避免系统时间调整延长购买授权。</summary>
    public bool CanConfirm(long now) => Safety == PurchaseSafety.SafeFree && Button is not null &&
        now >= CaptureStarted && now - CaptureStarted <= LifetimeMs;
}

/// <summary>用真实弹窗标题、零钻石声明、目标商品、所需零数量与按钮联合授权，不依赖图标存在。</summary>
public sealed class ShopConfirmationRecognizer
{
    // 风险检查与免费证据通过后，按钮的明暗和颜色匹配统一使用 90% 容差。
    private const double ButtonThreshold = 0.90;
    private readonly DiamondSafetyChecker _riskChecker;
    private readonly TemplateMatcher _matcher = new();

    /// <summary>风险检测只作为否决条件，免费授权由本识别器独立建立。</summary>
    public ShopConfirmationRecognizer(DiamondSafetyChecker riskChecker) => _riskChecker = riskChecker;

    /// <summary>所有信号必须出自本帧；缺文件、布局不合法、解码失败统一返回 Unknown。</summary>
    public PurchaseEvidence Evaluate(byte[] png, long captureStarted, UiLayout layout,
        string buttonPath, string diamondPath)
    {
        PurchaseEvidence Unknown(string reason) => new(PurchaseSafety.Unknown, reason, null, captureStarted);
        try
        {
            var screen = ImageCodec.DecodePng(png);
            if (screen.Width != 1920 || screen.Height != 1080 ||
                layout.ScreenWidth != screen.Width || layout.ScreenHeight != screen.Height)
                return Unknown("最终弹窗截图或布局尺寸异常");
            var risk = Required(layout, "diamondRiskRegion", screen);
            if (_riskChecker.HasRisk(screen, risk.Roi, diamondPath, iconThreshold: 0.98))
                return new(PurchaseSafety.RiskDetected, "钻石图标与红色数量同时命中", null, captureStarted);

            // 图标漏检不能掩盖余额变红；该区域专属已知培育包弹窗，红字作为独立否决信号。
            var remaining = Required(layout, "confirmRemaining", screen);
            if (DiamondSafetyChecker.CountRedPixels(screen.Bgra, screen.Width, remaining.Roi) >=
                DiamondSafetyChecker.DefaultRedPixelThreshold)
                return new(PurchaseSafety.RiskDetected, "最终确认余额区域出现红色数量", null, captureStarted);

            var directory = Path.GetDirectoryName(Path.GetFullPath(buttonPath))!;
            foreach (var name in new[] { "confirmDialog", "confirmFreeStatement", "confirmItem", "confirmZeroCost" })
            {
                var anchor = Required(layout, name, screen);
                if (string.IsNullOrWhiteSpace(anchor.Template)) return Unknown($"缺少 {name} 模板配置");
                // 每份素材独立通过完整价格区域校验，不借不同候选的局部价格拼出免费证据。
                if (!TemplateFallback.Any(Path.Combine(directory, anchor.Template), path =>
                {
                    var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
                    return template.Width == anchor.Width && template.Height == anchor.Height &&
                        MatchesPatch(screen, template, anchor.X, anchor.Y);
                }))
                    return Unknown($"最终弹窗证据不匹配：{name}");
            }

            var button = Required(layout, "confirmBuy", screen);
            var match = TemplateFallback.Match(buttonPath, path =>
            {
                var buy = ImageCodec.DecodePng(File.ReadAllBytes(path));
                // 每份候选的文字与颜色在同一个位置复核，保持原有免费授权门槛。
                var mask = CreateButtonLabelMask(buy);
                var candidate = _matcher.Match(screen.Bgra, screen.Width, screen.Height, buy.Bgra,
                    buy.Width, buy.Height, ButtonThreshold, button.Roi, mask);
                return candidate is not null && MatchesButtonColor(screen, buy, candidate.X, candidate.Y, mask)
                    ? candidate : null;
            });
            if (match is null)
                return Unknown("最终购买按钮未可靠识别");
            var evidence = new PurchaseEvidence(PurchaseSafety.SafeFree,
                "同帧确认购买弹窗、免费培育包、钻石0个、所需0及购买按钮均匹配", match, captureStarted);
            return evidence.CanConfirm(Environment.TickCount64) ? evidence :
                Unknown("最终确认截图证据过期") with { Expired = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 不把外部异常文本写进诊断，避免路径或设备输出意外包含敏感信息。
            return Unknown($"最终确认识别失败（{ex.GetType().Name}）");
        }
    }

    /// <summary>不允许匹配器自动裁切越界 ROI；缺锚点也必须明确停止。</summary>
    private static UiAnchor Required(UiLayout layout, string name, ScreenImage screen)
    {
        var anchor = layout.Find(name) ?? throw new InvalidOperationException($"缺少 {name}");
        if (anchor.X < 0 || anchor.Y < 0 || anchor.Width <= 0 || anchor.Height <= 0 ||
            (long)anchor.X + anchor.Width > screen.Width || (long)anchor.Y + anchor.Height > screen.Height)
            throw new InvalidOperationException($"{name} 越界");
        return anchor;
    }

    /// <summary>从按钮中央白字生成遮罩并向外扩一像素；保留字边背景，防止整片白色遮挡被当作文字。</summary>
    private static byte[] CreateButtonLabelMask(ScreenImage template)
    {
        var mask = new byte[template.Width * template.Height];
        // 透明素材不能自行决定哪些证据被忽略；遮罩只由已知按钮的文字颜色生成。
        for (var i = 3; i < template.Bgra.Length; i += 4)
            if (template.Bgra[i] != 255) throw new InvalidDataException("购买按钮模板必须不透明");
        for (var y = template.Height / 4; y < template.Height * 3 / 4; y++)
        for (var x = template.Width / 5; x < template.Width * 4 / 5; x++)
        {
            var offset = (y * template.Width + x) * 4;
            if (template.Bgra[offset] <= 220 || template.Bgra[offset + 1] <= 210 ||
                template.Bgra[offset + 2] <= 180) continue;
            for (var row = Math.Max(0, y - 1); row <= Math.Min(template.Height - 1, y + 1); row++)
            for (var col = Math.Max(0, x - 1); col <= Math.Min(template.Width - 1, x + 1); col++)
                mask[row * template.Width + col] = 255;
        }
        return mask;
    }

    /// <summary>按钮颜色也按 90% 整体相似度校验，兼容光效并排除灰色禁用按钮，不再逐块要求近乎一致。</summary>
    private static bool MatchesButtonColor(ScreenImage screen, ScreenImage template, int x, int y, byte[] mask)
    {
        long squaredDifference = 0;
        var samples = 0;
        for (var row = 0; row < template.Height; row++)
        for (var col = 0; col < template.Width; col++)
        {
            if (mask[row * template.Width + col] == 0) continue;
            var source = ((y + row) * screen.Width + x + col) * 4;
            var target = (row * template.Width + col) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var difference = screen.Bgra[source + channel] - template.Bgra[target + channel];
                squaredDifference += difference * difference;
                samples++;
            }
        }
        return samples > 0 && 1 - Math.Sqrt((double)squaredDifference / samples) / 255 >= ButtonThreshold;
    }

    /// <summary>逐个小块比较颜色，防止价格局部变化被大片相似底色稀释；透明模板不能屏蔽价格。</summary>
    private static bool MatchesPatch(ScreenImage screen, ScreenImage template, int x, int y)
    {
        for (var top = 0; top < template.Height; top += 8)
        for (var left = 0; left < template.Width; left += 8)
        {
            long difference = 0;
            var samples = 0;
            for (var row = top; row < Math.Min(top + 8, template.Height); row++)
            for (var col = left; col < Math.Min(left + 8, template.Width); col++)
            {
                var source = ((y + row) * screen.Width + x + col) * 4;
                var target = (row * template.Width + col) * 4;
                if (template.Bgra[target + 3] < 255) return false;
                for (var channel = 0; channel < 3; channel++)
                {
                    difference += Math.Abs(screen.Bgra[source + channel] - template.Bgra[target + channel]);
                    samples++;
                }
            }
            if (difference > samples * 8) return false;
        }
        return true;
    }
}
