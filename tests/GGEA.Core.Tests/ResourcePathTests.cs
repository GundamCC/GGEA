using GGEA.App.Config;
using GGEA.App.IO;

namespace GGEA.Core.Tests;

public sealed class ResourcePathTests
{
    [Fact]
    public void PathIdentity_UsesPlatformSemantics()
    {
        Assert.Equal(OperatingSystem.IsWindows(), FileSystemPaths.Comparer.Equals("/root/A.png", "/root/a.png"));
        Assert.Equal(OperatingSystem.IsWindows(), string.Equals("/root/A.png", "/root/a.png", FileSystemPaths.Comparison));
    }

    [Fact]
    public void LegacySeparators_ResolveAndEnumerateTheSameResource()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggea-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "resources", "ja", "templates");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "sample.png");
            File.WriteAllText(file, "resource");
            var resolver = new ResourceResolver(root, "ja");
            Assert.Equal(file, resolver.Resolve(@"resources\templates\sample.png"));
            Assert.Equal(file, resolver.ResolveWritePath(@"resources\templates\sample.png"));
            Assert.Equal(new[] { file }, resolver.GetFiles(@"resources\templates", "*.png"));
            Assert.True(resolver.DirectoryExists(@"resources\templates"));
            // 相邻资源目录不是资源覆盖层，不能因为目录前缀相似而重定向。
            var external = Path.Combine(root, "resources-extra", "sample.png");
            Assert.Equal(external, resolver.Resolve(external));
            if (!OperatingSystem.IsWindows())
            {
                var distinctRoot = Path.Combine(root, "Resources", "sample.png");
                Assert.Equal(distinctRoot, resolver.Resolve(distinctRoot));
                var other = Path.Combine(directory, "Sample.png");
                File.WriteAllText(other, "other");
                // 仅在实际支持区分大小写的卷上核对两个文件；其余断言仍验证路径解析边界。
                if (Directory.GetFiles(directory).Length == 2)
                    Assert.Equal(2, resolver.GetFiles("resources/templates", "*.png").Length);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
