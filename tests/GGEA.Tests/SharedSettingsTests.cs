using System.IO;
using System.Text.Json.Nodes;
using GGEA.App.Config;
using GGEA.App.Plugins;

namespace GGEA.Tests;

public sealed class SharedSettingsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ggea-shared-settings-");
    private string FilePath => Path.Combine(_directory.FullName, "settings.json");
    private readonly ConfigService _service = new(new TestLog());

    [Fact]
    public async Task ConcurrentWindows_MergeIndependentFieldsAndPreserveUnknownKeys()
    {
        File.WriteAllText(FilePath, """{"AdbPath":"original","autoProgress":{"future":7},"futureSetting":true}""");
        var settings = _service.Load(FilePath);
        var other = new ConfigService(new TestLog());
        var task = other.Load(FilePath);
        settings.AdbPath = "from-settings";
        task.AutoProgress.AdjustFreeTeam = false;
        await Task.WhenAll(Task.Run(() => _service.Save(settings, FilePath)), Task.Run(() => other.Save(task, FilePath)));
        other.Update(FilePath, config => config.IsDarkMode = false);
        task.AllowGgeaDownload = !task.AllowGgeaDownload;
        other.Save(task, FilePath);
        var final = _service.Load(FilePath);
        Assert.Equal("from-settings", final.AdbPath);
        Assert.False(final.AutoProgress.AdjustFreeTeam);
        Assert.False(final.IsDarkMode);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.True((bool)json["futureSetting"]!);
        Assert.Equal(7, (int)json["autoProgress"]!["future"]!);
    }

    [Fact]
    public void DialogConflict_RejectsWholeEditAndPreservesNewerValue()
    {
        _service.Save(new AppConfig { AdbPath = "original" }, FilePath);
        var edit = ConfigService.Snapshot(_service.Load(FilePath));
        _service.Update(FilePath, cfg => cfg.AdbPath = "first");
        edit.AdbPath = "second";
        edit.EmulatorArgs = "also changed";
        var saved = File.ReadAllText(FilePath);
        Assert.Throws<IOException>(() => _service.SaveEdited(edit, FilePath));
        Assert.Equal(saved, File.ReadAllText(FilePath));
    }

    [Fact]
    public void CorruptConfig_IsNotReplacedByAFallbackSnapshot()
    {
        File.WriteAllText(FilePath, "{");
        var config = _service.Load(FilePath);
        config.IsDarkMode = false;
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => _service.Save(config, FilePath));
        Assert.Equal("{", File.ReadAllText(FilePath));
    }

    [Fact]
    public void PluginWindows_SavingOneFieldRetainsTheOther()
    {
        PluginContext Window() => new(_directory.FullName, FilePath, "zh-CN", "test", new TestLog());
        var a = Window();
        var b = Window();
        a.WriteSettings("""{"first":0,"second":0}""");
        a.ReadSettings();
        b.ReadSettings();
        a.WriteSettings("""{"first":1,"second":0}""");
        b.WriteSettings("""{"first":0,"second":2}""");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"first":1,"second":2}"""), JsonNode.Parse(a.ReadSettings()!)));
    }

    [Fact]
    public void Coordinates_MergeIndependentAxesForTheSameStage()
    {
        var service = new StageWorkPositionsService(new TestLog());
        StageWorkPositionsData Position(int x, int y) => new() { Positions = [new() { Id = 1, CanvasX = x, CanvasY = y, Verified = true }] };
        var original = Position(100, 100);
        service.Save(original, FilePath);
        service.SaveEdits(original, Position(110, 100), FilePath);
        var position = Assert.Single(service.SaveEdits(original, Position(100, 120), FilePath).Positions);
        Assert.Equal((110, 120, true), (position.CanvasX, position.CanvasY, position.Verified));
    }

    [Fact]
    public void ApProfileSelectionAndIndependentSaves_KeepBothNamedPlans()
    {
        var service = new ApConsumptionProfileService(new TestLog());
        var first = service.Edit(FilePath, data => service.Upsert(data, new ApConsumptionProfile { Name = "A" }));
        service.Edit(FilePath, data => service.Upsert(data, new ApConsumptionProfile { Name = "B" }));
        var result = service.Edit(FilePath, data => data.LastSelectedProfile = first.LastSelectedProfile);
        Assert.Equal(new[] { "A", "B" }, result.Profiles.Select(profile => profile.Name));
        Assert.Equal("A", result.LastSelectedProfile);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
