namespace GGEA.App.IO;

/// <summary>路径身份按宿主平台比较；非 Windows 保留大小写，避免合并不同文件或越过资源根目录。</summary>
internal static class FileSystemPaths
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
