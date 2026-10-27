using System.IO;

namespace GGEA.App.Vision;

/// <summary>串行执行会话拥有的模板像素缓存；文件变化、删除或损坏时不继续使用旧证据。</summary>
internal sealed class TemplateImageCache
{
    private readonly Dictionary<string, (long Length, DateTime Modified, ScreenImage Image)> _images
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>只读复用未变化的解码像素；调用方不得修改返回数组，加载异常仍交给原错误处理路径。</summary>
    internal ScreenImage Load(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists)
        {
            _images.Remove(file.FullName);
            throw new FileNotFoundException("识别模板不存在。", file.FullName);
        }
        var length = file.Length;
        var modified = file.LastWriteTimeUtc;
        if (_images.TryGetValue(file.FullName, out var cached) &&
            cached.Length == length && cached.Modified == modified) return cached.Image;
        _images.Remove(file.FullName);
        var image = ImageCodec.DecodePng(File.ReadAllBytes(file.FullName));
        _images[file.FullName] = (length, modified, image);
        return image;
    }
}
