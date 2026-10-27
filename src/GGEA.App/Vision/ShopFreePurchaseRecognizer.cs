using System.IO;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>同一商品价格栏必须同时出现钻石和免费文字，才能授权其下方购买按钮。</summary>
public sealed class ShopFreePurchaseRecognizer
{
    private readonly TemplateImages _prices, _buys;
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<ScreenImage, (ScreenImage Diamond, ScreenImage Free)> _features = new();

    /// <summary>主素材在导航前校验；价格两侧特征始终从同一份素材提取。</summary>
    public ShopFreePurchaseRecognizer(string freePath, string buyPath)
    {
        _prices = new(freePath);
        _buys = new(buyPath);
        _ = Features(_prices.Primary);
    }

    /// <summary>按像素实例缓存派生特征，通用价格第一次参与识别时才解码及裁剪。</summary>
    private (ScreenImage Diamond, ScreenImage Free) Features(ScreenImage price)
    {
        if (_features.TryGetValue(price, out var features)) return features;
        if (price.Width < 160 || price.Height < 10)
            throw new InvalidOperationException("免费价格模板必须同时包含钻石图标与免费文字");
        return _features[price] = (Crop(price, 0, 60, price.Height - 4),
            Crop(price, price.Width - 80, 80, price.Height - 4));
    }

    /// <summary>只接受同帧同卡片的价格和按钮，数字价格、其他卡片及底部免费提示均不能授权购买。</summary>
    public TemplateMatchResult? Find(byte[] png, Roi priceRoi, Roi buyRoi, double threshold)
    {
        var screen = ImageCodec.DecodePng(png);
        if (screen.Width != 1920 || screen.Height != 1080 || priceRoi.Width < 160 ||
            buyRoi.X < priceRoi.X || buyRoi.X + buyRoi.Width > priceRoi.X + priceRoi.Width ||
            buyRoi.Y < priceRoi.Y + priceRoi.Height)
            return null;
        var price = _prices.Match(template =>
        {
            var (diamond, free) = Features(template);
            var candidate = MatchPrice(screen, template, priceRoi, Math.Max(0.92, threshold));
            if (candidate is null) return null;
            // 左右证据必须属于当前完整价格栏，不能跨语言或跨卡片组合。
            var iconRoi = new Roi(candidate.X, candidate.Y, diamond.Width, diamond.Height);
            var textRoi = new Roi(candidate.X + template.Width - free.Width, candidate.Y, free.Width, free.Height);
            return MatchPrice(screen, diamond, iconRoi, Math.Max(0.92, threshold)) is not null &&
                MatchPrice(screen, free, textRoi, Math.Max(0.92, threshold)) is not null ? candidate : null;
        });
        return price is null ? null : _buys.Match(_matcher, screen, threshold, buyRoi);
    }

    /// <summary>从唯一的免费价格素材生成内存特征，不依赖已删除的文字模板或额外图片文件。</summary>
    private static ScreenImage Crop(ScreenImage image, int x, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Array.Copy(image.Bgra, (row * image.Width + x) * 4, pixels, row * width * 4, width * 4);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, width, height));
    }

    /// <summary>价格校验同时要求像素和文字纹理相符，防止灰底数字价格误匹配灰底“免费”。</summary>
    private TemplateMatchResult? MatchPrice(ScreenImage screen, ScreenImage template, Roi roi, double threshold)
    {
        var match = _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
            template.Width, template.Height, threshold, roi);
        if (match is null) return null;
        using var source = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var target = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
        using var patch = new Mat(source, new Rect(match.X, match.Y, template.Width, template.Height));
        using var gray = new Mat(); using var targetGray = new Mat(); using var score = new Mat();
        Cv2.CvtColor(patch, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.CvtColor(target, targetGray, ColorConversionCodes.BGRA2GRAY);
        Cv2.MatchTemplate(gray, targetGray, score, TemplateMatchModes.CCoeffNormed);
        var correlation = score.At<float>(0, 0);
        return float.IsFinite(correlation) && correlation >= 0.90 ? match : null;
    }
}
