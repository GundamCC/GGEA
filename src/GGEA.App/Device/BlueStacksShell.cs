using System.Globalization;
using System.Text.RegularExpressions;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>蓝叠 shell 兼容路由：只读探测后选择通道，业务命令只执行一次。</summary>
internal sealed class BlueStacksShell
{
    private readonly Func<IReadOnlyList<string>, int, CancellationToken, Task<string>> _run;
    private readonly IGgeaLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _modes = new(StringComparer.Ordinal);

    /// <summary>复用 ADB 进程的超时、取消和回收机制；委托用于验证协议失败场景。</summary>
    internal BlueStacksShell(Func<IReadOnlyList<string>, int, CancellationToken, Task<string>> run, IGgeaLog log)
    { _run = run; _log = log; }

    /// <summary>按设备隔离选择结果，失败后清除选择但绝不重发可能已生效的点击或购买。</summary>
    internal async Task<string> ExecuteAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var serial = args[1];
        try
        {
            if (!_modes.TryGetValue(serial, out var mode))
            {
                foreach (var candidate in new[] { "shell", "shell -x", "exec-out" })
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var probe = await ExecuteOnceAsync(serial, candidate, "printf GGEA_PROBE", Math.Min(timeoutMs, 5000), ct);
                        if (probe != "GGEA_PROBE") throw new AdbException("探测返回内容不匹配");
                        mode = candidate;
                        _modes[serial] = mode;
                        _log.Info($"BlueStacks 命令通道已确认: serial={serial}, mode={mode}（只读探测）");
                        break;
                    }
                    catch (AdbException ex)
                    {
                        _log.Warn($"BlueStacks 通道探测失败: serial={serial}, mode={candidate}: {ex.Message}");
                    }
                }
                if (mode is null)
                    throw new AdbException($"BlueStacks 命令通道均不可用（设备 {serial}）；已停止，未执行游戏操作。请反馈本次日志");
            }
            ct.ThrowIfCancellationRequested();
            // 保持现有 adb shell 的参数拼接语义，兼容调用方已经引用的 sh -c 脚本。
            return await ExecuteOnceAsync(serial, mode, string.Join(" ", args.Skip(3)), timeoutMs, ct);
        }
        catch
        {
            _modes.Remove(serial);
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>旧 shell 和 exec-out 不可靠地传递远程退出码，因此所有通道都验证独立结束标记。</summary>
    private async Task<string> ExecuteOnceAsync(string serial, string mode, string command, int timeoutMs, CancellationToken ct)
    {
        var marker = "GGEA_EXIT_" + Guid.NewGuid().ToString("N");
        var script = BuildScript(command, marker);
        string[] prefix = mode == "shell -x" ? ["shell", "-x"] : [mode];
        // HD-Adb 1.0.36 的 exec-out 分参数转义会破坏脚本中的引号/反斜杠。
        // 传入完整远程命令，避免将 printf 的换行或原命令的引用改写。
        string[] commandArgs = mode == "exec-out" ? ["sh -c " + Quote(script)] : ["sh", "-c", Quote(script)];
        var output = await _run(["-s", serial, .. prefix, .. commandArgs], timeoutMs, ct);
        ct.ThrowIfCancellationRequested();
        return ParseOutput(output, marker);
    }

    /// <summary>子 shell 隔离 exit；收集远程 stderr，避免 exec-out 丢失错误，标记仅在命令结束后打印。</summary>
    internal static string BuildScript(string command, string marker)
        => $"( {command}\n) 2>&1; ggea_rc=$?; printf '\\n{marker}:%s\\n' \"$ggea_rc\"";

    /// <summary>引用整个远程脚本，保护原命令内的单引号及变量免于被外层 shell 提前解释。</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>必须收到完整的末尾标记且远程退出码为零；连接中断或客户端伪成功均不能放行。</summary>
    internal static string ParseOutput(string output, string marker)
    {
        var match = Regex.Match(output, @"\r?\n" + Regex.Escape(marker) + @":([0-9]{1,3})\r?\n\z");
        if (!match.Success)
            throw new AdbException("BlueStacks 命令未返回完整结束标记，执行结果未知；未自动重试");
        var code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (code != 0)
            throw new AdbException($"BlueStacks 远程命令失败（exit {code}）；未自动重试");
        return output[..match.Index];
    }
}
