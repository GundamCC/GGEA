using System.IO;
using System.Text.Json.Nodes;
using GGEA.App.Localization;

namespace GGEA.App.Config;

internal static class JsonSettingChanges
{
    // 对象逐字段比较；数组表示有序选择，作为一个值处理。发生冲突时整个编辑都不落盘。
    internal static void Apply(JsonObject before, JsonObject edited, JsonObject latest)
    {
        foreach (var key in before.Select(pair => pair.Key).Union(edited.Select(pair => pair.Key)))
        {
            bool had = before.TryGetPropertyValue(key, out var old);
            bool has = edited.TryGetPropertyValue(key, out var value);
            if (had == has && JsonNode.DeepEquals(old, value)) continue;
            bool exists = latest.TryGetPropertyValue(key, out var current);
            if (has == exists && JsonNode.DeepEquals(current, value)) continue;
            if (old is JsonObject oldObject && value is JsonObject newObject && current is JsonObject currentObject)
            {
                Apply(oldObject, newObject, currentObject);
                continue;
            }
            if (had != exists || !JsonNode.DeepEquals(current, old))
                throw new IOException(L.Text("此设置已被其他窗口修改，请重新打开后再保存。"));
            if (has) latest[key] = value?.DeepClone();
            else latest.Remove(key);
        }
    }

    // 保留旧版程序不认识的字段，包括嵌套对象的扩展字段。
    internal static void Overlay(JsonObject target, JsonObject values)
    {
        foreach (var (key, value) in values)
        {
            if (target[key] is JsonObject existing && value is JsonObject nested) Overlay(existing, nested);
            else target[key] = value?.DeepClone();
        }
    }
}
