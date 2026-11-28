using System.IO;
using GGEA.App.Updates;
using GGEA.Updater;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    [Fact]
    public void VersionSelection_ImmediateLaunchNeverObservesPartlyWrittenProgram()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp.Path, "1.4.26.0", "old");
        var source = Stage(temp.Path, "1.4.26.1");
        // 复现用户双击旧 EXE：整个准备与切换期间都持有禁止写入和删除的句柄。
        using var mapped = File.Open(Path.Combine(temp.Path, "GGEA.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var active = VersionStore.Publish(temp.Path, source, "1.4.26.1", _ =>
            Assert.Equal(temp.Path, VersionStore.SelectedDirectory(temp.Path)), default);
        Assert.Equal(active, VersionStore.SelectedDirectory(temp.Path, true));
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "GGEA.exe")));
        Assert.Equal(temp.Path, VersionStore.RootOf(active));
        var next = Stage(temp.Path, "1.4.26.2");
        Assert.Throws<IOException>(() => VersionStore.Publish(temp.Path, next, "1.4.26.2", _ => throw new IOException(), default));
        Assert.Equal(active, VersionStore.SelectedDirectory(temp.Path, true));
        var newer = VersionStore.Publish(temp.Path, next, "1.4.26.2", _ => { }, default);
        File.WriteAllText(Path.Combine(newer, "GGEA.exe"), "corrupted");
        Assert.Equal(active, VersionStore.SelectedDirectory(temp.Path, true));
    }

    [Fact]
    public async Task VersionSelection_ConcurrentPublicationAndAbandonedStagingCanBeRetried()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp.Path, "1.4.26.0", "old");
        var inputs = new[] { Stage(temp.Path, "1.4.26.1"), Stage(temp.Path, "1.4.26.1") };
        var selected = await Task.WhenAll(inputs.Select(source => Task.Run(() =>
            VersionStore.Publish(temp.Path, source, "1.4.26.1", _ => { }, default))));
        Assert.Equal(selected[0], selected[1]);
        var orphan = Stage(temp.Path, "1.4.26.2");
        Directory.Move(orphan, Path.Combine(temp.Path, ".ggea-update", "versions", "1.4.26.2"));
        Assert.Equal(selected[0], VersionStore.SelectedDirectory(temp.Path));
        var retried = VersionStore.Publish(temp.Path, Stage(temp.Path, "1.4.26.2"), "1.4.26.2", _ => { }, default);
        Assert.Equal(retried, VersionStore.SelectedDirectory(temp.Path));
    }

    private static string Stage(string root, string version)
    {
        var source = Path.Combine(root, ".ggea-update", "download-" + Guid.NewGuid().ToString("N"), "package");
        CreatePackage(source, version, "new");
        return source;
    }
}
