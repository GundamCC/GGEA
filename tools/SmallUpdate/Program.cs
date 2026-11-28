using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GGEA.App.Updates;

// 消费已验收的目标包，新的小附件均可独立重建和复验，旧 ZIP 和旧索引仍由原发布入口生成。
var archive = Path.GetFullPath(args[0]);
var targetRoot = args[1];
var fullIndex = File.ReadAllBytes(archive + ".index.json");
using var index = JsonDocument.Parse(fullIndex);
var manifest = File.ReadAllBytes(Path.Combine(targetRoot, "build-info.json"));
var expectedManifest = index.RootElement.GetProperty("manifest").GetBytesFromBase64();
if (!manifest.AsSpan().SequenceEqual(expectedManifest)) throw new InvalidDataException("Target manifest mismatch");
using var target = JsonDocument.Parse(File.ReadAllText(Path.Combine(targetRoot, "build-info.json")));
var targetVersion = Version.Parse(target.RootElement.GetProperty("version").GetString()!);
var entries = index.RootElement.GetProperty("entries");
Save(".index.json.br", Compress(fullIndex));

if (args.Length == 3)
{
    var root = args[2];
    if (File.Exists(root))
        ReadBaselineArchive(root);
    else
        Generate(File.ReadAllBytes(Path.Combine(root, "build-info.json")), name => ReadBounded(File.OpenRead(Path.Combine(root, name))));
}

var archivePattern = new Regex(@"^GGEA-v(\d+\.\d+\.\d+\.\d+)-win-x64-[a-f0-9]{12}\.zip$");
var bases = Directory.EnumerateFiles(Path.GetDirectoryName(archive)!, "*.zip")
    .Select(path => (path, match: archivePattern.Match(Path.GetFileName(path))))
    .Where(b => b.match.Success && File.Exists(b.path + ".sha256"))
    .Select(b => (b.path, version: Version.Parse(b.match.Groups[1].Value)))
    .Where(b => b.version < targetVersion).OrderByDescending(b => b.version).ThenBy(b => b.path, StringComparer.Ordinal)
    .DistinctBy(b => b.version).Take(2).ToArray();
foreach (var (path, _) in bases)
{
    using var stream = File.OpenRead(path);
    var sum = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    if (File.ReadAllText(path + ".sha256").Trim() != sum + "  " + Path.GetFileName(path))
        throw new InvalidDataException("Invalid baseline checksum: " + path);
    ReadBaselineArchive(path);
}
void ReadBaselineArchive(string path)
{
    using var zip = ZipFile.OpenRead(path);
    var files = zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), StringComparer.Ordinal);
    var manifestPath = files.Keys.Single(n => n.Split('/') is [_, "build-info.json"]);
    string prefix = manifestPath[..^"build-info.json".Length];
    byte[] Read(string name)
    {
        var e = files[prefix + name];
        if (e.Length > FileCopyPatch.MaximumFileBytes) return [];
        return ReadBounded(e.Open(), e.Length);
    }
    Generate(Read("build-info.json"), Read);
}

void Generate(byte[] previousManifest, Func<string, byte[]> readBase)
{
    using var old = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(previousManifest).TrimStart('\uFEFF'));
    if (Version.Parse(old.RootElement.GetProperty("version").GetString()!) >= targetVersion)
        throw new InvalidDataException("Expected an older release");
    var oldFiles = old.RootElement.GetProperty("files");
    var nextFiles = target.RootElement.GetProperty("files");
    if (old.RootElement.TryGetProperty("program", out var oldProgram) &&
        target.RootElement.TryGetProperty("program", out var newProgram) &&
        oldProgram.GetProperty("commit").GetString() == newProgram.GetProperty("commit").GetString())
    {
        // 同一程序构建身份必须逐字节一致，防止构建机的仓库信息混入纯素材更新。
        var names = oldFiles.EnumerateObject().Select(f => f.Name).Union(nextFiles.EnumerateObject().Select(f => f.Name));
        foreach (var name in names.Where(n => n != "GGEA.ReleaseIdentity.dll" &&
                     (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))))
            if (!oldFiles.TryGetProperty(name, out var prior) || !nextFiles.TryGetProperty(name, out var current) ||
                prior.GetString() != current.GetString())
                throw new InvalidDataException("Content-only release changed a program binary: " + name);
    }
    if (target.RootElement.TryGetProperty("templateEngine", out var engine) &&
        engine.GetString() is { Length: 64 } id && old.RootElement.TryGetProperty("templateEngine", out var before) && before.GetString() == id)
        WriteSmallIndex(previousManifest, oldFiles, nextFiles, readBase);
    foreach (var entry in entries.EnumerateObject())
    {
        var name = entry.Name;
        var nextHash = nextFiles.GetProperty(name).GetString();
        var size = entry.Value.GetProperty("size").GetInt64();
        if (size is < FileCopyPatch.MinimumFileBytes or > FileCopyPatch.MaximumFileBytes ||
            !oldFiles.TryGetProperty(name, out var previous) || previous.GetString() == nextHash) continue;
        byte[] source = readBase(name);
        if (source.Length == 0) continue;
        if (Hash(source) != previous.GetString()) throw new InvalidDataException("Invalid baseline file: " + name);
        byte[] next = File.ReadAllBytes(Path.Combine(targetRoot, name));
        if (Hash(next) != nextHash) throw new InvalidDataException("Invalid target file: " + name);
        byte[] patch = MakePatch(source, next);
        if (patch.Length < entry.Value.GetProperty("compressed").GetInt64() && patch.Length <= FileCopyPatch.MaximumPatchBytes)
            Save($".{previous.GetString()}.{nextHash}.copy.br", patch);
    }

}

// 旧版和新版的程序相同时，小索引仅传目标清单的改动与变化文件的位置，图片直接从正式 ZIP 读取。
void WriteSmallIndex(byte[] baseline, JsonElement previous, JsonElement next, Func<string, byte[]> readBase)
{
    var changed = new Dictionary<string, JsonElement>();
    var patches = new Dictionary<string, object>();
    foreach (var entry in entries.EnumerateObject())
    {
        var name = entry.Name;
        var oldHash = previous.TryGetProperty(name, out var old) ? old.GetString() : null;
        if (oldHash == next.GetProperty(name).GetString()) continue;
        changed.Add(name, entry.Value);
        if (changed.Count > 64) return;
        // 说明和设置常常只改一行；可以直接从已校验的旧文本重建。
        if (oldHash is null || entry.Value.GetProperty("size").GetInt64() > 1024 * 1024 ||
            !(name == "GGEA.ReleaseIdentity.dll" || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) continue;
        var bytes = readBase(name);
        if (bytes.Length > 1024 * 1024 || Hash(bytes) != oldHash) continue;
        var patch = MakePatch(bytes, File.ReadAllBytes(Path.Combine(targetRoot, name)));
        if (patch.Length <= 4096 && patch.Length < entry.Value.GetProperty("compressed").GetInt64())
            patches[name] = new { source = oldHash, bytes = patch };
    }
    var descriptor = new { schema = 1, archiveHash = index.RootElement.GetProperty("archiveHash").GetString(),
        manifestSize = manifest.Length, manifestHash = Hash(manifest), manifestPatch = MakePatch(baseline, manifest),
        entries = changed, patches };
    var payload = Compress(JsonSerializer.SerializeToUtf8Bytes(descriptor));
    if (payload.Length < 64 * 1024) Save("." + Hash(baseline) + ".small.br", payload);
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static byte[] MakePatch(byte[] baseline, byte[] next)
{
    using var output = new MemoryStream();
    FileCopyPatchBuilder.Write(baseline, next, output);
    output.Position = 0;
    using var source = new MemoryStream(baseline);
    using var rebuilt = new MemoryStream(next.Length);
    FileCopyPatch.Apply(source, output, rebuilt, next.Length, CancellationToken.None);
    if (!rebuilt.GetBuffer().AsSpan(0,next.Length).SequenceEqual(next)) throw new InvalidDataException("Patch reconstruction failed");
    return output.ToArray();
}

static byte[] ReadBounded(Stream input, long? size = null)
{
    using (input)
    {
        var length = size ?? input.Length;
        if (length > FileCopyPatch.MaximumFileBytes) return [];
        var result = new byte[checked((int)length)];
        input.ReadExactly(result);
        return result;
    }
}

static byte[] Compress(byte[] bytes)
{
    using var output = new MemoryStream();
    using (var encoder = new BrotliStream(output, CompressionLevel.Optimal, true)) encoder.Write(bytes);
    return output.ToArray();
}

void Save(string suffix, byte[] bytes)
{
    var path = archive + suffix;
    if (File.Exists(path))
    {
        if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path))) throw new InvalidDataException("Existing attachment differs: " + path);
        return;
    }
    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try { File.WriteAllBytes(temp, bytes); File.Move(temp, path); }
    finally { File.Delete(temp); }
}
