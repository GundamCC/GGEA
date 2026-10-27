using GGEA.App.Localization;
using System.Runtime.InteropServices;

namespace GGEA.App.UI;

/// <summary>通过 Windows 通知区域发送系统通知，无需安装器注册或额外运行依赖。由主窗口在 UI 线程持有并释放。</summary>
internal sealed class WindowsNotificationService : IDisposable
{
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifIcon = 2, NifTip = 4, NifInfo = 16;
    private NotifyIconData _data;
    private bool _added;

    /// <summary>保留通知图标直到窗口关闭，避免发送后立即删除导致系统通知被撤回。</summary>
    public bool Show(
        nint windowHandle,
        string message,
        string? title = null,
        bool isWarning = false)
    {
        if (!_added)
        {
            _data = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                Window = windowHandle,
                Id = 1,
                Flags = NifIcon | NifTip,
                // LoadIcon 返回系统共享图标，不由本服务销毁。
                Icon = LoadIconW(0, 32512),
                Tip = "GGEA",
                Info = "",
                InfoTitle = "",
            };
            _added = Shell_NotifyIconW(NimAdd, ref _data);
            if (!_added) return false;
        }

        _data.Flags = NifInfo;
        _data.InfoTitle = FitText(title ?? L.Text("GGEA · 任务队列执行完毕"), 63);
        _data.Info = FitText(message, 255);
        _data.InfoFlags = isWarning ? 2u : 1u; // NIIF_WARNING / NIIF_INFO。
        if (Shell_NotifyIconW(NimModify, ref _data)) return true;

        // Explorer 重启后图标可能丢失，下次通知重新注册。
        Dispose();
        return false;
    }

    /// <summary>主窗口关闭时移除本服务拥有的通知图标，可重复调用。</summary>
    public void Dispose()
    {
        if (_added) Shell_NotifyIconW(NimDelete, ref _data);
        _added = false;
    }

    /// <summary>预留 Unicode 定长字段的终止符；长状态或异常正文用省略号截断，避免通知发送失败。</summary>
    internal static string FitText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var length = maxLength - 1;
        // 不在 UTF-16 代理项之间截断，保留完整字符。
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }

    /// <summary>与 Windows SDK 的 NOTIFYICONDATAW 完整布局一致，使用 Unicode 定长字符串。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    /// <summary>新增、更新或删除 Windows 通知区域图标。</summary>
    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    /// <summary>加载共享的系统应用图标。</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadIconW(nint instance, nint iconName);
}
