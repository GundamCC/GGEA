using System.Text;
using System.Text.Json;

namespace GGEA.App.Device;

/// <summary>限定兼容管理器字符串裸空白与末尾 name 缺引号；不放宽配置文件和身份字段校验。</summary>
internal static class MuMuManagerJson
{
    /// <summary>统一 info 响应的单实例对象、编号集合及显式编号数组；元素仅在所属 JsonDocument 存活期间使用。</summary>
    internal static IEnumerable<(int Index, JsonElement Data)> EnumerateInstances(JsonElement root)
    {
        // 兼容显式 index 的数组响应，仍不根据数组位置推算编号。
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new AdbException("MuMu 实例集合包含无效元素");
                foreach (var instance in EnumerateInstances(item)) yield return instance;
            }
            yield break;
        }
        if (root.ValueKind != JsonValueKind.Object) throw new AdbException("MuMu 实例响应未包含有效对象或集合");
        if (root.TryGetProperty("error_code", out var error) &&
            (error.ValueKind != JsonValueKind.Number || !error.TryGetInt32(out var code) || code != 0))
            throw new AdbException("MuMu 管理器报告实例查询失败，未使用其地址或编号");
        if (root.TryGetProperty("index", out var singleIndex))
        {
            if (TryReadIndex(singleIndex, out var index)) yield return (index, root);
            yield break;
        }

        foreach (var entry in root.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (entry.Value.TryGetProperty("error_code", out var entryError) &&
                (entryError.ValueKind != JsonValueKind.Number || !entryError.TryGetInt32(out var entryCode) || entryCode != 0))
                throw new AdbException("MuMu 管理器报告实例查询失败，未使用其地址或编号");
            // 集合键与内嵌编号同时存在时必须一致，不猜测实际原生截图实例。
            if (entry.Value.TryGetProperty("index", out var declared) &&
                (!TryReadIndex(declared, out var declaredIndex) || declaredIndex != index)) continue;
            yield return (index, entry.Value);
        }
    }

    /// <summary>管理器版本可能使用字符串或整数编号；拒绝负数、小数及其他类型。</summary>
    private static bool TryReadIndex(JsonElement value, out int index)
    {
        index = -1;
        return value.ValueKind switch
        {
            JsonValueKind.String => int.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out index),
            JsonValueKind.Number => value.TryGetInt32(out index) && index >= 0,
            _ => false,
        };
    }

    /// <summary>只接受完整合法的名称内容；拒绝悬空反斜杠、内嵌裸引号和其他控制字符。</summary>
    private static readonly System.Text.RegularExpressions.Regex UnclosedName = new(
        "^[ \\t]*\"name\"[ \\t]*:[ \\t]*\"(?:[^\"\\\\\\r\\n\\x00-\\x1F]|\\\\[\"\\\\/bfnrt]|\\\\u[0-9a-fA-F]{4})*$");

    /// <summary>只补独占一行且紧邻对象结束的 name；跨行字符串中的相似文本不参与修复。</summary>
    private static string RepairTerminalName(string json)
    {
        var lines = json.Split('\n');
        var inString = false;
        var escaped = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!inString && index + 1 < lines.Length && UnclosedName.IsMatch(line) &&
                lines[index + 1].Trim() is "}" or "},")
            {
                // 不修改名称或身份字段的值，只补终止符；整份响应仍须通过严格 JSON 校验。
                lines[index] = line + "\"" + lines[index][line.Length..];
            }
            foreach (var character in lines[index] + "\n")
            {
                if (escaped) { escaped = false; continue; }
                if (inString && character == '\\') { escaped = true; continue; }
                if (character == '"') inString = !inString;
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>保留字符串实际内容与已有转义；其他损坏仍停止设备识别，不猜测实例身份。</summary>
    internal static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(NormalizeWhitespace(json)); }
        catch (JsonException) { /* 只在正常兼容解析失败后尝试已确认的名称缺引号缺陷。 */ }
        try { return JsonDocument.Parse(NormalizeWhitespace(RepairTerminalName(json))); }
        catch (JsonException ex)
        {
            throw new AdbException($"MuMu 管理器实例信息 JSON 无效（行 {ex.LineNumber + 1}，字节位置 {ex.BytePositionInLine}），无法确认设备通道；已停止操作", ex);
        }
    }

    /// <summary>仅转义字符串内裸空白，保留已有转义及结构字符。</summary>
    private static string NormalizeWhitespace(string json)
    {
        var normalized = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        foreach (var character in json)
        {
            // 反斜杠后的字符交给严格解析器验证，避免把无效转义修成另一种含义。
            if (inString && !escaped && character is '\r' or '\n' or '\t')
            {
                normalized.Append(character switch { '\r' => "\\r", '\n' => "\\n", _ => "\\t" });
                continue;
            }
            normalized.Append(character);
            if (escaped) { escaped = false; continue; }
            if (inString && character == '\\') { escaped = true; continue; }
            if (character == '"') inString = !inString;
        }
        return normalized.ToString();
    }
}
