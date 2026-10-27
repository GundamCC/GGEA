using GGEA.App.Config;

namespace GGEA.App.Vision;

/// <summary>只在识别未命中时补试同名通用素材；异常、取消与安全否决由调用方保留。</summary>
internal static class TemplateFallback
{
    /// <summary>各候选使用调用方捕获的同一帧与同一判定规则；首个有效结果立即返回。</summary>
    public static T? Match<T>(string path, Func<string, T?> match, CancellationToken ct = default,
        ResourceResolver? resources = null) where T : class
    {
        foreach (var candidate in (resources ?? ConfigService.Resources).TemplateCandidates(path))
        {
            ct.ThrowIfCancellationRequested();
            var result = match(candidate);
            ct.ThrowIfCancellationRequested();
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>布尔观察遵循相同短路顺序；用于标题和风险检测，不解释业务授权。</summary>
    public static bool Any(string path, Func<string, bool> match, CancellationToken ct = default)
    {
        foreach (var candidate in ConfigService.Resources.TemplateCandidates(path))
        {
            ct.ThrowIfCancellationRequested();
            var found = match(candidate);
            ct.ThrowIfCancellationRequested();
            if (found) return true;
        }
        return false;
    }
}
