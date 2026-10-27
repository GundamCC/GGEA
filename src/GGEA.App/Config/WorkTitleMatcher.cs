namespace GGEA.App.Config;

/// <summary>
/// 作品英文标题匹配器。
/// OCR 会把 “Mobile Suit” 识别成 “M0biIe Suit”，这里统一做字符归一化
/// （0→O、1/l→I、去空格标点、转大写），再与 hard_stages.json 的作品名匹配。
/// </summary>
public static class WorkTitleMatcher
{
    /// <summary>归一化 OCR / 作品名文本。</summary>
    public static string Normalize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = char.ToUpperInvariant(ch);
            switch (c)
            {
                case '0':
                    builder.Append('O');
                    break;
                case '1':
                case 'L':
                    builder.Append('I');
                    break;
                default:
                    if (char.IsLetterOrDigit(c))
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 将 OCR 文本匹配到作品名列表。
    /// 规则：完全相等优先；其次是“期望名包含 OCR”或“OCR 包含期望名”，
    /// 但要求 OCR 长度足够、且候选唯一，避免 “MOBILE SUIT GUNDAM” 同时匹配多部作品。
    /// </summary>
    public static string? Match(string ocrText, IReadOnlyList<string> workNames)
    {
        var ocr = Normalize(ocrText);
        if (ocr.Length < 4)
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (var work in workNames)
        {
            var expected = Normalize(work);
            if (expected == ocr)
            {
                return work;
            }

            // 期望名以 OCR 开头，且 OCR 覆盖期望名 70% 以上（如 OCR=“MOBILE SUIT GUNDAM ZZ” 与期望一致的前缀）
            if (expected.StartsWith(ocr, StringComparison.Ordinal) && ocr.Length >= expected.Length * 0.7)
            {
                candidates.Add(work);
                continue;
            }

            // OCR 以期望名开头，说明 OCR 比期望名更长（一般 OCR 更全）
            if (ocr.StartsWith(expected, StringComparison.Ordinal) && ocr.Length - expected.Length < 15)
            {
                candidates.Add(work);
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
