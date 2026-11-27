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
var archiveHash = index.RootElement.GetProperty("archiveHash").GetString();
var manifest = File.ReadAllBytes(Path.Combine(targetRoot, "build-info.json"));
var expectedManifest = index.RootElement.GetProperty("manifest").GetBytesFromBase64();
if (!manifest.AsSpan().SequenceEqual(expectedManifest)) throw new InvalidDataException("Target manifest mismatch");
using var target = JsonDocument.Parse(File.ReadAllText(Path.Combine(targetRoot, "build-info.json")));
var targetVersion = Version.Parse(target.RootElement.GetProperty("version").GetString()!);
var entries = index.RootElement.GetProperty("entries");
Save(".index.json.br", Compress(fullIndex));

var explicitBase = args.Length == 3;
if (explicitBase)
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
if (!explicitBase && bases.Length == 0)
{
    // 新构建机也须为线上用户产出可用的增量。下载只发生在发布端，临时 ZIP 用完即删。
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GGEA-Release/1");
    using var releases = JsonDocument.Parse(await client.GetByteArrayAsync("https://api.github.com/repositories/1435670943/releases?per_page=30"));
    foreach (var release in releases.RootElement.EnumerateArray())
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version >= targetVersion) continue;
        string name = $"GGEA-{tag}-win-x64.zip";
        var asset = release.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
        var digest = asset.GetProperty("digest").GetString();
        var url = asset.GetProperty("browser_download_url").GetString();
        if (url != $"https://github.com/GundamCC/GGEA-SD-Gundam-G-Generation-Eternal-Assistant/releases/download/{tag}/{name}")
            throw new InvalidDataException("Invalid official baseline URL");
        var temp = Path.GetTempFileName();
        try
        {
            using (var remote = await client.GetStreamAsync(url))
            using (var output = File.Create(temp)) await remote.CopyToAsync(output);
            using var file = File.OpenRead(temp);
            if ("sha256:" + Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != digest)
                throw new InvalidDataException("Official baseline checksum mismatch");
            ReadBaselineArchive(temp);
        }
        finally { File.Delete(temp); }
        return;
    }
    throw new InvalidDataException("No previous public release found. Supply -BaseDirectory to package an offline baseline.");
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
    var changed = new Dictionary<string, JsonElement>();
    foreach (var entry in entries.EnumerateObject())
    {
        var name = entry.Name;
        var nextHash = nextFiles.GetProperty(name).GetString();
        bool present = oldFiles.TryGetProperty(name, out var previous);
        // 用户配置可能在运行中改变，需要保留从完整包取回发布默认文件的入口。
        bool mutable = name.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
            name is "resources/stage_work_positions.json" or "resources/common/stage_work_positions.json";
        if (present && previous.GetString() == nextHash && !mutable) continue;
        changed.Add(name, entry.Value);
        var size = entry.Value.GetProperty("size").GetInt64();
        if (!present || previous.GetString() == nextHash || size > FileCopyPatch.MaximumFileBytes) continue;
        byte[] source = readBase(name);
        if (source.Length == 0) continue;
        if (Hash(source) != previous.GetString()) throw new InvalidDataException("Invalid baseline file: " + name);
        byte[] next = File.ReadAllBytes(Path.Combine(targetRoot, name));
        if (Hash(next) != nextHash) throw new InvalidDataException("Invalid target file: " + name);
        byte[] patch = MakePatch(source, next);
        if (patch.Length < entry.Value.GetProperty("compressed").GetInt64() && patch.Length <= FileCopyPatch.MaximumPatchBytes)
            Save($".{previous.GetString()}.{nextHash}.copy.br", patch);
    }
    var descriptor = new { schema = 2, archiveHash, manifestSize = manifest.Length, manifestHash = Hash(manifest),
        manifestPatch = MakePatch(previousManifest, manifest), entries = changed };
    Save("." + Hash(previousManifest) + ".index.br", Compress(JsonSerializer.SerializeToUtf8Bytes(descriptor)));
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
