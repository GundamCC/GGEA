namespace GGEA.App.Vision;

/// <summary>只判断指定画布是否仍在移动；不能证明页面身份、按钮可点击或网络请求完成。</summary>
internal sealed class SceneMotionGate(Roi region)
{
    private byte[]? _anchor;
    private int _frames;
    private (int Width, int Height) _size;

    /// <summary>与本轮首帧比较三帧采样，避免逐帧微移被误当成稳定；尺寸变化会撤销证据。</summary>
    internal bool Observe(ScreenImage screen)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X + region.Width > screen.Width || region.Y + region.Height > screen.Height)
            throw new InvalidOperationException("画布稳定检测区域超出截图，停止导航。");
        var samples = new List<byte>();
        for (var y = region.Y; y < region.Y + region.Height; y += 8)
        for (var x = region.X; x < region.X + region.Width; x += 8)
        {
            var offset = (y * screen.Width + x) * 4;
            samples.Add(screen.Bgra[offset]); samples.Add(screen.Bgra[offset + 1]); samples.Add(screen.Bgra[offset + 2]);
        }
        var current = samples.ToArray();
        var changed = 0;
        if (_anchor is not null && _anchor.Length == current.Length)
            for (var i = 0; i < current.Length; i += 3)
                if (Math.Abs(current[i] - _anchor[i]) + Math.Abs(current[i + 1] - _anchor[i + 1]) +
                    Math.Abs(current[i + 2] - _anchor[i + 2]) > 45) changed++;
        if (_anchor is null || _size != (screen.Width, screen.Height) || _anchor.Length != current.Length || changed > current.Length / 3 * .02)
        {
            _anchor = current; _size = (screen.Width, screen.Height); _frames = 1; return false;
        }
        return ++_frames >= 3;
    }
}
