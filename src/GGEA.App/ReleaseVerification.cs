using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App;

/// <summary>解压包启动自检：加载实际原生库和资源，不创建主窗口、不访问模拟器。</summary>
internal static class ReleaseVerification
{
    /// <summary>用退出码报告失败；诊断写入标准错误，避免修改被校验的发布目录。</summary>
    public static int Run()
    {
        try
        {
            var root = AppContext.BaseDirectory;
            using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "build-info.json")));
            if (info.RootElement.GetProperty("version").GetString() != typeof(App).Assembly.GetName().Version?.ToString(4))
                throw new InvalidDataException("程序版本与 build-info 不一致。");
            var commit = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "GgeaReleaseCommit")?.Value;
            if (string.IsNullOrEmpty(commit) || commit != info.RootElement.GetProperty("commit").GetString())
                throw new InvalidDataException("程序构建提交与 build-info 不一致。");
            foreach (var directory in new[] { "config", "resources" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.json", SearchOption.AllDirectories))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                }
            }
            var images = Directory.GetFiles(Path.Combine(root, "resources"), "*.png", SearchOption.AllDirectories);
            if (images.Length == 0) throw new InvalidDataException("发布包没有模板。");
            foreach (var file in images)
            {
                // 与正式识图路径一致，由 .NET 读取文件，避免 OpenCV 的窄字符路径和长路径限制。
                using var image = Cv2.ImDecode(File.ReadAllBytes(file), ImreadModes.Unchanged);
                if (image.Empty()) throw new InvalidDataException($"无法解码模板：{Path.GetFileName(file)}");
                // 同时覆盖 WPF 模板加载路径，避免原生库自检成功却在实际识图时解码失败。
                _ = ImageCodec.DecodePng(File.ReadAllBytes(file));
            }
            // 验证随包 CPU 原生库和固定 OCR 模型确实能加载，不能只检查文件名存在。
            using var ocr = new OnnxTextRecognizer();
            ocr.Validate();
            return 0;
        }
        catch (Exception ex)
        {
            // WinExe 无控制台，不能设置 Console.OutputEncoding；只为重定向的错误流指定编码。
            using var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
            error.WriteLine($"Release self-test failed: {ex}");
            return 1;
        }
    }
}
