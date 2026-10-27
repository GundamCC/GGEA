using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>语言优先与同帧补试的回归：验证点击中心、截图次数、区域与异常边界。</summary>
public sealed class TemplateFallbackTests
{
    /// <summary>独立资源树避免修改全局语言或仓库素材，可与其他测试并行。</summary>
    private sealed class Resources : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ggea-fallback-");
        public ResourceResolver Resolver { get; }
        public const string Logical = "resources/templates/target.png";
        public string Common { get; }
        public string Localized { get; }

        public Resources()
        {
            Resolver = new(_directory.FullName, "ja");
            Common = Add("common", 12, 10, 100);
            Localized = Add("ja", 8, 6, 220);
        }

        /// <summary>两种素材尺寸与灰度不同，命中错误候选会改变实际点击坐标。</summary>
        private string Add(string language, int width, int height, byte value)
        {
            var path = Path.Combine(_directory.FullName, "resources", language, "templates", "target.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, height, value), width, height));
            return path;
        }

        public void Dispose() => _directory.Delete(true);
    }

    /// <summary>存在语言目标时优先点击它；语言未命中或不存在时点击通用目标，整个过程只取一帧。</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Click_UsesPriorityAndActualDimensionsOnOneFrame(bool languageFile, bool languageVisible)
    {
        using var resources = new Resources();
        if (!languageFile) File.Delete(resources.Localized);
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(pixels, 1920, 12, 10, 60, 20, 100);
        if (languageVisible) TestImageFactory.PasteBlock(pixels, 1920, 8, 6, 20, 20, 220);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), resources: resources.Resolver);
        Assert.True(await automation.ClickIfExistsAsync("serial", Resources.Logical,
            new WaitOptions { Threshold = .999, Roi = new(0, 0, 100, 60) }));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Equal(new[] { languageVisible ? (24, 23) : (66, 25) }, adb.Taps);
    }

    /// <summary>同帧回退不能扩大搜索区域，也不能把仍存在的通用目标当成已经消失。</summary>
    [Fact]
    public async Task Match_RespectsRoiAndDisappearanceChecksBothVariants()
    {
        using var resources = new Resources();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(pixels, 1920, 12, 10, 60, 20, 100);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(png);
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), resources: resources.Resolver);
        Assert.Null(await automation.MatchImageAsync(png, Resources.Logical,
            new WaitOptions { Threshold = .999, Roi = new(0, 0, 40, 40) }));
        Assert.NotNull(await automation.MatchImageAsync(png, Resources.Logical,
            new WaitOptions { Threshold = .999, Roi = new(60, 20, 12, 10) }));
        var blank = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
        adb.EnqueueScreenshots(png, blank, blank);
        Assert.True(await automation.WaitImageDisappearAsync("serial", Resources.Logical,
            new WaitOptions { Threshold = .999, Roi = new(0, 0, 100, 60), TimeoutMs = 2000,
                PollIntervalMs = 1, AllowGlobalContinuation = false }));
        Assert.Equal(4, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>语言命中不读取损坏的次选；主素材损坏则在截图和动作前失败，不用通用素材掩盖异常。</summary>
    [Fact]
    public async Task DecodeFailure_IsNotAMissAndSuccessfulPrimaryDoesNotLoadFallback()
    {
        using var resources = new Resources();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(pixels, 1920, 8, 6, 20, 20, 220);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        File.WriteAllText(resources.Common, "invalid png");
        using (var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), resources: resources.Resolver))
            Assert.NotNull(await automation.FindImageAsync("serial", Resources.Logical,
                new WaitOptions { Threshold = .999, Roi = new(0, 0, 100, 60) }));
        Assert.Equal(1, adb.ScreenshotCount);
        File.WriteAllText(resources.Localized, "invalid png");
        using var fresh = new AutomationService(adb, new TemplateMatcher(), new TestLog(), resources: resources.Resolver);
        await Assert.ThrowsAnyAsync<Exception>(() => fresh.FindImageAsync("serial", Resources.Logical));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>同名通用素材只补试一次；命中即短路，损坏或取消不能被当成未命中。</summary>
    [Fact]
    public void CandidateAttempts_StopOnSuccessErrorOrCancellation()
    {
        using var resources = new Resources();
        var calls = new List<string>();
        Assert.Null(TemplateFallback.Match<string>(Resources.Logical, path => { calls.Add(path); return null; },
            resources: resources.Resolver));
        Assert.Equal(new[] { resources.Localized, resources.Common }, calls);
        calls.Clear();
        Assert.Equal("hit", TemplateFallback.Match(Resources.Logical, path => { calls.Add(path); return "hit"; },
            resources: resources.Resolver));
        Assert.Equal(new[] { resources.Localized }, calls);
        calls.Clear();
        Assert.Throws<InvalidDataException>(() => TemplateFallback.Match<string>(Resources.Logical,
            path => { calls.Add(path); throw new InvalidDataException(); }, resources: resources.Resolver));
        Assert.Single(calls);
        using var cancellation = new CancellationTokenSource();
        calls.Clear();
        Assert.Throws<OperationCanceledException>(() => TemplateFallback.Match<string>(Resources.Logical,
            path => { calls.Add(path); cancellation.Cancel(); return null; }, cancellation.Token, resources.Resolver));
        Assert.Single(calls);
    }

    /// <summary>解析过的物理路径同样有正确候选；通用素材的配套文件不重新回到语言目录。</summary>
    [Fact]
    public void Resolver_DeduplicatesCommonAndKeepsCompanionWithItsTemplate()
    {
        using var resources = new Resources();
        Assert.Equal(new[] { resources.Localized, resources.Common }, resources.Resolver.TemplateCandidates(resources.Localized));
        Assert.Equal(resources.Common, resources.Resolver.ResolveTemplateCompanion(resources.Common, resources.Localized));
        File.Delete(resources.Localized);
        Assert.Equal(new[] { resources.Common }, resources.Resolver.TemplateCandidates(resources.Common));
        var external = Path.Combine(Path.GetTempPath(), "external-template.png");
        Assert.Equal(new[] { external }, resources.Resolver.TemplateCandidates(external));
    }
}
