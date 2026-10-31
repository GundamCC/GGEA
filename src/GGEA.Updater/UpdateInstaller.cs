namespace GGEA.Updater;

/// <summary>一项文件操作的原始状态；恢复不依赖安装过程走到了第几个文件。</summary>
public sealed record UpdateBackupFile(string Path, bool Existed, string? Hash);

/// <summary>先于目标修改持久化的恢复记录；备份保留在安装目录内，不跨盘搬移。</summary>
public sealed record UpdateJournal(string BackupDirectory, string OldVersion, string NewVersion, List<UpdateBackupFile> Files);

/// <summary>完整包事务安装：只管理清单中的文件，未知文件和用户数据始终保留。</summary>
public static class UpdateInstaller
{
    /// <summary>调用方必须持有安装锁并确保主进程退出。自检委托用于真实 EXE 自检及故障回归。</summary>
    public static string Install(string target, string package, string expectedVersion, Action<string> verifyExecutable)
    {
        UpdateFiles.RejectLinks(target);
        UpdateFiles.RejectLinks(package);
        var pending = UpdateFiles.SafePath(target, UpdateFiles.JournalName);
        if (File.Exists(pending)) throw new IOException("上次更新尚未恢复，请先恢复旧版。");
        if (File.Exists(UpdateFiles.SafePath(target, "development-info.json")))
            throw new InvalidDataException("开发包不能作为在线更新的安装目标。");
        var old = UpdateManifest.ReadInstallation(target);
        var next = UpdateManifest.Read(package);
        if (next.Version != expectedVersion || UpdateFiles.ParseVersion(next.Version) <= UpdateFiles.ParseVersion(old.Version))
            throw new InvalidDataException("更新包版本不匹配或不是更新版本。");
        next.Verify(package);
        verifyExecutable(package);

        var replacements = next.Files.Keys.Append("build-info.json")
            .Where(path => !UpdateFiles.IsUserFile(path) || !File.Exists(UpdateFiles.SafePath(target, path)))
            // 下载阶段已重建完整包；安装只备份并替换实际变化的文件，减少大模型和运行库的磁盘写入。
            .Where(path => !File.Exists(UpdateFiles.SafePath(target, path)) ||
                !UpdateFiles.Hash(UpdateFiles.SafePath(target, path)).Equals(
                    UpdateFiles.Hash(UpdateFiles.SafePath(package, path)), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var removals = old.Files.Keys.Except(next.Files.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !UpdateFiles.IsUserFile(path)).ToArray();
        var affected = replacements.Concat(removals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var backupBytes = affected.Select(path => new FileInfo(UpdateFiles.SafePath(target, path)))
            .Where(file => file.Exists).Sum(file => file.Length);
        var installBytes = replacements.Sum(path => new FileInfo(UpdateFiles.SafePath(package, path)).Length);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!).AvailableFreeSpace < backupBytes + installBytes + 64L * 1024 * 1024)
            throw new IOException("磁盘空间不足以备份并安装更新，请释放空间后重试。");
        var backupRelative = UpdateFiles.StateDirectory + "/backup-" + Guid.NewGuid().ToString("N");
        var backup = UpdateFiles.SafePath(target, backupRelative);
        var journal = new UpdateJournal(backupRelative, old.Version, next.Version, []);

        // 全部预检与备份完成后才公布 pending；在此之前失败，原安装完全不变。
        foreach (var relative in affected)
        {
            var source = UpdateFiles.SafePath(target, relative);
            if (Directory.Exists(source)) throw new IOException($"目标文件被同名目录占用：{relative}");
            if (!File.Exists(source)) { journal.Files.Add(new(relative, false, null)); continue; }
            using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var saved = UpdateFiles.SafePath(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(source, saved, false);
            using (var durable = new FileStream(saved, FileMode.Open, FileAccess.Write, FileShare.Read)) durable.Flush(true);
            var hash = UpdateFiles.Hash(saved);
            if (hash != UpdateFiles.Hash(source)) throw new IOException("备份期间目标文件发生变化，已停止更新。");
            journal.Files.Add(new(relative, true, hash));
        }
        UpdateFiles.WriteJson(pending, journal, UpdateJsonContext.Default.UpdateJournal);
        try
        {
            foreach (var relative in replacements)
            {
                var destination = UpdateFiles.SafePath(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                UpdateFiles.CopyAtomically(UpdateFiles.SafePath(package, relative), destination, target);
            }
            foreach (var relative in removals) File.Delete(UpdateFiles.SafePath(target, relative));
            next.Verify(target, preserveUserFiles: true);
            verifyExecutable(target);
            // 只有完整性与可执行自检通过才解除启动拦截；备份保留供人工找回自定义资源。
            File.Move(pending, UpdateFiles.SafePath(backup, "completed.json"));
            return backup;
        }
        catch (Exception installError)
        {
            try { Recover(target); }
            catch (Exception recoveryError)
            {
                throw new IOException("更新失败且自动恢复尚未完成。请关闭占用程序后重新启动 GGEA 恢复；备份保留在 .ggea-update。",
                    new AggregateException(installError, recoveryError));
            }
            throw new IOException("更新失败，已恢复旧版本。", installError);
        }
    }

    /// <summary>幂等恢复：先验证所有备份，再恢复；中途再次断电仍可根据同一记录重试。</summary>
    public static void Recover(string target)
    {
        var pending = UpdateFiles.SafePath(target, UpdateFiles.JournalName);
        if (!File.Exists(pending)) return;
        if (new FileInfo(pending).Length > 8 * 1024 * 1024) throw new InvalidDataException("更新恢复记录过大。");
        var journal = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(pending), UpdateJsonContext.Default.UpdateJournal)
            ?? throw new InvalidDataException("更新恢复记录损坏。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(journal.BackupDirectory, @"^\.ggea-update/backup-[a-f0-9]{32}$") ||
            journal.Files.Count > 20001)
            throw new InvalidDataException("更新恢复目录无效。");
        var backup = UpdateFiles.SafePath(target, journal.BackupDirectory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Files)
        {
            if (entry.Path != "build-info.json") UpdateFiles.ValidateManagedPath(entry.Path);
            _ = UpdateFiles.SafePath(target, entry.Path);
            if (!seen.Add(entry.Path)) throw new InvalidDataException("恢复记录存在重复路径。");
            if (entry.Existed && !string.Equals(UpdateFiles.Hash(UpdateFiles.SafePath(backup, entry.Path)), entry.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("旧版备份摘要不一致，已停止恢复。");
        }
        foreach (var entry in journal.Files)
        {
            var destination = UpdateFiles.SafePath(target, entry.Path);
            if (entry.Existed)
            {
                // 未变化文件无需重写，允许日志查看器等只读访问不相关文件。
                if (File.Exists(destination) && UpdateFiles.Hash(destination).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                UpdateFiles.CopyAtomically(UpdateFiles.SafePath(backup, entry.Path), destination, target);
            }
            else File.Delete(destination);
        }
        File.Move(pending, UpdateFiles.SafePath(backup, "recovered.json"), true);
    }
}
