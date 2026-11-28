using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GGEA.Updater;

/// <summary>保留已核验的出厂默认值。用户修改设置后仍能在本机组装完整的待安装包。</summary>
public static class PublishedDefaults
{
    public static string CacheRoot(string installation) =>
        UpdateFiles.SafePath(installation, UpdateFiles.StateDirectory + "/defaults");

    // 只把新版相对旧出厂配置的变化合入用户文件。旧默认值缺失时无法判断用户意图，保留已有值。
    public static Dictionary<string, JsonObject> ChangedSettings(string target, string package,
        UpdateManifest old, UpdateManifest next, string? only = null)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in next.Files)
        {
            if ((only is not null && only != name) || !UpdateFiles.IsUserFile(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !old.Files.TryGetValue(name, out var previousHash) || hash.Equals(previousHash, StringComparison.OrdinalIgnoreCase)) continue;
            var path = UpdateFiles.SafePath(target, name);
            if (!File.Exists(path)) continue;
            var original = UpdateFiles.Hash(path).Equals(previousHash, StringComparison.OrdinalIgnoreCase) ? path : UpdateFiles.SafePath(CacheRoot(target), name);
            if (!File.Exists(original) || !UpdateFiles.Hash(original).Equals(previousHash, StringComparison.OrdinalIgnoreCase)) continue;
            var nextDefaults = ReadObject(UpdateFiles.SafePath(package, name));
            var before = ReadObject(original);
            var local = ReadObject(path);
            if (nextDefaults is null || before is null || local is null) continue;
            var merged = local.DeepClone().AsObject();
            MergeObject(before, nextDefaults, merged);
            if (!JsonNode.DeepEquals(local, merged)) result.Add(name, merged);
        }
        return result;
    }

    private static JsonObject? ReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; } // 未能解析的旧默认值或用户数据沿用原有保留规则。
    }

    private static void MergeObject(JsonObject before, JsonObject after, JsonObject local)
    {
        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)))
        {
            var had = before.TryGetPropertyValue(key, out var old);
            var has = after.TryGetPropertyValue(key, out var next);
            if (had == has && JsonNode.DeepEquals(old, next)) continue;
            var exists = local.TryGetPropertyValue(key, out var current);
            if (had == exists && JsonNode.DeepEquals(old, current))
            {
                if (has) local[key] = next?.DeepClone();
                else local.Remove(key);
            }
            else if (old is JsonObject a && next is JsonObject b && current is JsonObject c)
                MergeObject(a, b, c);
            else if (old is JsonArray prior && next is JsonArray updated && current is JsonArray user &&
                key is "profiles" or "positions")
                MergeEntries(prior, updated, user, key == "profiles" ? "name" : "id");
        }
    }

    // 已编辑的 AP 方案与作品坐标按业务主键合并，用户的顺序、自建项和主动删除都保留。
    private static void MergeEntries(JsonArray before, JsonArray after, JsonArray local, string key)
    {
        static Dictionary<string, JsonObject>? Index(JsonArray items, string key)
        {
            var index = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                if (item is not JsonObject entry || entry[key] is not JsonValue id ||
                    !index.TryAdd(id.ToJsonString(), entry)) return null;
            return index;
        }
        var prior = Index(before, key);
        var updated = Index(after, key);
        var user = Index(local, key);
        if (prior is null || updated is null || user is null) return;
        foreach (var (id, entry) in updated)
        {
            if (prior.TryGetValue(id, out var old))
            {
                if (user.TryGetValue(id, out var current)) MergeObject(old, entry, current);
            }
            else if (!user.ContainsKey(id)) local.Add(entry.DeepClone());
        }
        foreach (var (id, entry) in prior)
            if (!updated.ContainsKey(id) && user.TryGetValue(id, out var current) && JsonNode.DeepEquals(entry, current))
                local.Remove(current);
    }

    public static void Remember(string installation, string source)
    {
        try
        {
            if (!File.Exists(UpdateFiles.SafePath(source, "build-info.json"))) return;
            var manifest = UpdateManifest.Read(source);
            var cache = CacheRoot(installation);
            Directory.CreateDirectory(cache);
            foreach (var (name, hash) in manifest.Files)
            {
                if (!UpdateFiles.IsUserFile(name)) continue;
                var original = UpdateFiles.SafePath(source, name);
                var saved = UpdateFiles.SafePath(cache, name);
                if (File.Exists(saved) && UpdateFiles.Hash(saved).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(original)) continue;
                using var snapshot = File.OpenRead(original);
                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshot)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                UpdateFiles.CopyAtomically(original, saved, installation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // 缓存不可用仍可下载已认证的默认文件；不会把缓存当作版本或完整性的依据。
        }
    }
}
