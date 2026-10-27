using System.Globalization;
using System.Text.RegularExpressions;

namespace GGEA.App.Device;

/// <summary>将 MuMu 的输入路由到目标游戏显示屏，避免多标签页模式下默认点击安卓桌面。</summary>
internal sealed class MuMuInputService
{
    private readonly string _package;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string>> _run;

    /// <summary>共用 ADB 的进程取消与超时机制，命令边界可替换以验证误触保护。</summary>
    internal MuMuInputService(string package,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> run)
    { _package = package; _run = run; }

    /// <summary>普通 ADB 不依赖 dumpsys 格式；只有明确发现独立显示屏才切换通道，多目标歧义仍拒绝操作。</summary>
    internal async Task<int?> TryResolveDisplayAsync(string serial, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string output;
        try { output = await _run(["-s", serial, "shell", "dumpsys", "activity", "activities"], ct); }
        catch (AdbException) { ct.ThrowIfCancellationRequested(); return null; }
        ct.ThrowIfCancellationRequested();
        return TryParseDisplay(output, _package);
    }

    /// <summary>每次输入前重查所选设备，不缓存可能随游戏重启或标签切换变化的显示编号。</summary>
    internal async Task<int> ResolveDisplayAsync(string serial, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var output = await _run(["-s", serial, "shell", "dumpsys", "activity", "activities"], ct);
        ct.ThrowIfCancellationRequested();
        return ParseDisplay(output, _package);
    }

    /// <summary>仅在唯一确认游戏正在目标显示屏运行后发送输入，失败绝不回退默认显示屏。</summary>
    internal async Task ExecuteAsync(string serial, IReadOnlyList<string> input, CancellationToken ct)
    {
        var display = await ResolveDisplayAsync(serial, ct);
        ct.ThrowIfCancellationRequested();
        // 常规动作由 AdbService 统一记录，显示屏路由不重复刷屏；失败仍通过异常交由调用方处理。
        var result = await _run(["-s", serial, "shell", "input", "-d",
            display.ToString(CultureInfo.InvariantCulture), .. input], ct);
        // 部分 Android input 会以零退出码报告参数错误，不能将不支持 -d 的版本视为成功。
        if (Regex.IsMatch(result, @"(?im)^\s*(Error:|Usage:|Unknown command|Invalid arguments)"))
            throw new AdbException("MuMu 未能执行指定显示屏输入，请确认模拟器支持 input -d；未回退默认显示屏");
    }

    /// <summary>只读每个 Display 段内的已恢复 Activity，忽略历史任务和全局焦点，防止选中桌面或同名前缀应用。</summary>
    internal static int ParseDisplay(string output, string package)
        => TryParseDisplay(output, package)
            ?? throw new AdbException("无法确认目标游戏的显示屏，请在所选设备打开游戏；本次未发送输入");

    /// <summary>缺少显示信息返回未知，让普通设备继续标准 ADB；多个已恢复游戏显示屏不可任意择一。</summary>
    internal static int? TryParseDisplay(string output, string package)
    {
        if (string.IsNullOrWhiteSpace(package)) throw new AdbException("游戏包名为空，已停止 MuMu 输入");
        var displays = new HashSet<int>();
        int? current = null;
        var topSeen = false;
        var topIsGame = false;
        var resumedIsGame = false;
        var activityPattern = @"^\s*(?:topResumedActivity|mResumedActivity)\s*[:=]\s*ActivityRecord\{[^\r\n]*\bu\d+\s+"
            + Regex.Escape(package) + @"/[^\s}]+";
        // 同一显示屏以顶层声明为准；任务内残留的 resumed 不能覆盖当前前台应用。
        void CompleteDisplay()
        {
            if (current is not null && (topSeen ? topIsGame : resumedIsGame)) displays.Add(current.Value);
            current = null;
            topSeen = topIsGame = resumedIsGame = false;
        }
        foreach (var line in output.Split('\n'))
        {
            var header = Regex.Match(line, @"^Display #(?<id>\d+) \(activities from top to bottom\):\s*$");
            if (header.Success)
            {
                CompleteDisplay();
                current = int.TryParse(header.Groups["id"].Value, out var id) ? id : null;
            }
            // 非缩进的后续摘要不属于前一个 Display，不能借用其编号。
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                CompleteDisplay();
            else if (current is not null)
            {
                var isGame = Regex.IsMatch(line, activityPattern);
                if (Regex.IsMatch(line, @"^\s*topResumedActivity\s*[:=]"))
                {
                    // 一个 Display 重复出现矛盾的顶层声明属于损坏响应，不按最后一项猜测。
                    if (topSeen && topIsGame != isGame)
                        throw new AdbException($"游戏显示屏顶层状态冲突（display={current}）；本次未发送输入");
                    topSeen = true;
                    topIsGame = isGame;
                }
                else if (isGame) resumedIsGame = true;
            }
        }
        CompleteDisplay();
        if (displays.Count > 1)
            throw new AdbException($"无法唯一确认目标游戏的 MuMu 显示屏（候选={string.Join(',', displays.Order())}），请在所选实例关闭游戏的重复窗口或后台保活/分屏后重试；已停止输入，未回退安卓桌面");
        return displays.Count == 1 ? displays.Single() : null;
    }
}
