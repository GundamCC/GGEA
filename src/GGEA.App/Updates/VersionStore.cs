using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GGEA.App.Config;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>正式包在各自的版本目录内通过完整自检后，才原子切换下一次启动的目标。</summary>
internal static class VersionStore
{
    internal sealed record Slot(string Version, string ManifestHash);
    internal sealed record Selection(Slot Active, Slot? Previous);
    private const string Slots = ".ggea-update/versions/";
    private const string Selector = ".ggea-update/active.json";

    internal static string RootOf(string program)
    {
        var dir = new DirectoryInfo(program);
        if (dir.Parent?.Name == "versions" && dir.Parent.Parent?.Name == UpdateFiles.StateDirectory &&
            Version.TryParse(dir.Name, out var v) && v.Revision >= 0 && v.ToString() == dir.Name)
            return dir.Parent.Parent.Parent!.FullName;
        return dir.FullName;
    }

    private static string SlotPath(string root, Slot slot) =>
        UpdateFiles.SafePath(root, Slots + UpdateFiles.ParseVersion(slot.Version).ToString(4));

    private static Selection? Read(string root)
    {
        var file = UpdateFiles.SafePath(root, Selector);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 4096) throw new InvalidDataException("版本选择记录过大。");
        var record = JsonSerializer.Deserialize<Selection>(File.ReadAllBytes(file), UpdateFiles.JsonOptions);
        static bool Valid(Slot? slot) => slot is not null && Version.TryParse(slot.Version, out var version) &&
            version.Revision >= 0 && version.ToString() == slot.Version && slot.ManifestHash is { Length: 64 } hash &&
            hash.All(Uri.IsHexDigit);
        if (record is null || !Valid(record.Active) || (record.Previous is not null && !Valid(record.Previous)))
            throw new InvalidDataException("版本选择记录无效。");
        return record;
    }

    private static bool Available(string root, Slot slot, bool verifyProgram = false)
    {
        var dir = SlotPath(root, slot);
        var manifest = UpdateFiles.SafePath(dir, "build-info.json");
        if (!File.Exists(manifest) || UpdateFiles.Hash(manifest) != slot.ManifestHash ||
            !File.Exists(UpdateFiles.SafePath(dir, "GGEA.exe"))) return false;
        if (!verifyProgram) return true;
        var info = UpdateManifest.Read(dir);
        return info.Files.Where(f => f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            f.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).All(f =>
        {
            var file = UpdateFiles.SafePath(dir, f.Key);
            return File.Exists(file) && UpdateFiles.Hash(file).Equals(f.Value, StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static string SelectedDirectory(string root, bool verifyProgram = false)
    {
        Selection? selection;
        try { selection = Read(root); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return root; }
        if (selection is null) return root;
        if (Usable(selection.Active)) return SlotPath(root, selection.Active);
        if (selection.Previous is { } old && Usable(old)) return SlotPath(root, old);
        return root;

        bool Usable(Slot slot)
        {
            try { return Available(root, slot, verifyProgram); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return false; }
        }
    }

    // 隔离窗口的业务运行租约与版本发布锁。下载、自检和 UI 生命周期可以独立完成。
    private static FileStream Lock(string root)
    {
        var path = UpdateFiles.SafePath(root, ".ggea-update/versions.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && timer.Elapsed < TimeSpan.FromSeconds(30))
            { Thread.Sleep(50); }
        }
    }

    internal static string Publish(string root, string source, string version, Action<string> selfTest, CancellationToken token)
    {
        var stage = new DirectoryInfo(source);
        if (stage.Name != "package" || stage.Parent?.Parent?.Name != UpdateFiles.StateDirectory ||
            !System.Text.RegularExpressions.Regex.IsMatch(stage.Parent.Name, "^download-[a-f0-9]{32}$") ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(stage.Parent.Parent.Parent!.FullName), Path.TrimEndingDirectorySeparator(root)))
            throw new InvalidDataException("更新包必须位于本次安装的下载目录。");
        UpdateFiles.RejectLinks(source);
        var next = UpdateManifest.Read(source);
        if (next.Version != version) throw new InvalidDataException("下载版本与预期不一致。");
        next.Verify(source, cancellationToken: token);
        selfTest(source);
        var slot = new Slot(version, UpdateFiles.Hash(UpdateFiles.SafePath(source, "build-info.json")));
        using var gate = Lock(root);
        var current = SelectedDirectory(root);
        var old = UpdateManifest.ReadInstallation(current);
        var target = SlotPath(root, slot);
        if (UpdateFiles.ParseVersion(old.Version) >= UpdateFiles.ParseVersion(version))
        {
            if (current == target && Available(root, slot)) return current;
            throw new InvalidDataException("已有更新的版本就绪。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(target))
        {
            // 上次在目录移动之后、指针写入之前断电；同一份完整产物允许直接重试。
            if (!Available(root, slot)) throw new InvalidDataException("已暂存的同版本内容不一致。");
            next.Verify(target, cancellationToken: token);
        }
        else Directory.Move(source, target);
        Slot? previous = current == root ? null : new(old.Version, UpdateFiles.Hash(UpdateFiles.SafePath(current, "build-info.json")));
        UpdateFiles.WriteJson(UpdateFiles.SafePath(root, Selector), new Selection(slot, previous));
        Prune(target, current);
        return target;
    }

    // 窗口可能在载入新素材后继续执行旧程序集，两种目录都持有独立的读租约。
    internal static FileStream? Hold(string root, string directory)
    {
        if (Path.TrimEndingDirectorySeparator(directory).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return null;
        return new FileStream(UpdateFiles.SafePath(directory, ".in-use"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
    }

    private static void Prune(string active, string previous)
    {
        foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(active)!))
        {
            if (dir.Equals(active, StringComparison.OrdinalIgnoreCase) || dir.Equals(previous, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var info = UpdateManifest.Read(dir);
                if (Path.GetFileName(dir) != info.Version) continue;
                var marker = UpdateFiles.SafePath(dir, ".in-use");
                using (new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (var name in info.Files.Keys) File.Delete(UpdateFiles.SafePath(dir, name));
                    File.Delete(UpdateFiles.SafePath(dir, "build-info.json"));
                }
                File.Delete(marker);
                RemoveEmptyDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
    }

    private static void RemoveEmptyDirectories(string dir)
    {
        UpdateFiles.RejectLinks(dir);
        foreach (var child in Directory.GetDirectories(dir)) RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    // 设置在新版本首次使用前合并。写入原子且合并幂等，中途中止下次继续；旧窗口退出时不执行安装。
    internal static void ApplyUserDefaults(string root, string active)
    {
        if (string.Equals(root, active, StringComparison.OrdinalIgnoreCase)) return;
        using var gate = Lock(root);
        var selection = Read(root);
        if (selection is null || SlotPath(root, selection.Active) != active) return;
        var marker = UpdateFiles.SafePath(root, ".ggea-update/applied-defaults.json");
        if (File.Exists(marker) && File.ReadAllText(marker) == JsonSerializer.Serialize(selection.Active.ManifestHash)) return;
        var prior = selection.Previous is null ? root : SlotPath(root, selection.Previous);
        var old = UpdateManifest.ReadInstallation(prior);
        var next = UpdateManifest.Read(active);
        foreach (var name in next.Files.Keys.Where(UpdateFiles.IsUserFile))
        {
            var file = UpdateFiles.SafePath(root, name);
            using var settingsLock = new SharedConfigFile(file);
            if (!File.Exists(file)) UpdateFiles.CopyAtomically(UpdateFiles.SafePath(active, name), file, root);
            else if (PublishedDefaults.ChangedSettings(root, active, old, next, name).TryGetValue(name, out var merged))
                UpdateFiles.WriteJson(file, merged);
        }
        PublishedDefaults.Remember(root, active);
        UpdateFiles.WriteJson(marker, selection.Active.ManifestHash);
    }
}
