using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GGEA.Updater;

/// <summary>更新文件边界：Windows 路径、链接、用户数据和持久化记录共用同一套规则。</summary>
public static class UpdateFiles
{
    public const string StateDirectory = ".ggea-update";
    public const string JournalName = ".ggea-update/pending.json";
    public const string RunnerName = ".ggea-update/runner/GGEA.Updater.exe";
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>仅接受四段产品版本，不将预发布标记或字符串字典序当作正式版本。</summary>
    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+\.\d+$") || !Version.TryParse(value, out var version))
            throw new InvalidDataException("更新版本号无效。");
        return version;
    }

    /// <summary>所有相对路径均采用清单中的正斜线；拒绝设备名、ADS、尾空格和目录穿越。</summary>
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\'))
            throw new InvalidDataException("更新路径无效。");
        foreach (var part in relative.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("更新路径包含不允许的名称。");
        }
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        RejectLinks(path);
        return path;
    }

    /// <summary>检查整条祖先链，禁止通过 junction/symlink 写出用户选择的安装目录。</summary>
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新目录不能包含符号链接或目录联接。");
        }
    }

    /// <summary>这些随包提供的默认文件在运行后属于用户；存在时保留，缺失时使用新版默认值。</summary>
    public static bool IsUserFile(string relative) =>
        relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("resources/stage_work_positions.json", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("resources/common/stage_work_positions.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>维护目录、日志和开发标记不能由下载包声明为程序文件。</summary>
    public static void ValidateManagedPath(string relative)
    {
        _ = SafePath(Path.GetTempPath(), relative);
        var first = relative.Split('/')[0];
        if (first.StartsWith('.') || first.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("local-build-info.json", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("development-info.json", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("build-info.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发布清单包含非程序文件。");
    }

    /// <summary>流式摘要不把大安装包读入内存。</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>先刷盘再重命名，恢复记录必须先于任何目标文件修改落盘。</summary>
    [RequiresUnreferencedCode("反射序列化仅供未裁剪的主程序使用；独立更新器必须传入生成的类型元数据。")]
    [RequiresDynamicCode("反射序列化仅供未裁剪的主程序使用。")]
    public static void WriteJson<T>(string path, T value) =>
        WriteJson(path, value, (JsonTypeInfo<T>)new JsonSerializerOptions(JsonOptions)
            { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(T)));

    /// <summary>独立更新器显式传入编译期元数据，运行库裁剪后仍可读写交接与恢复记录。</summary>
    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        RejectLinks(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    /// <summary>同卷暂存并刷盘后替换单个文件，避免断电留下被截断的 EXE；整套版本仍由事务记录恢复。</summary>
    public static void CopyAtomically(string source, string destination, string installation)
    {
        RejectLinks(source);
        RejectLinks(destination);
        var temporary = SafePath(installation, StateDirectory + "/write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>主程序和更新器持有同一目录锁；禁止多实例对同一安装并发读写。</summary>
    public static FileStream AcquireLock(string root)
    {
        var path = SafePath(root, StateDirectory + "/installation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
