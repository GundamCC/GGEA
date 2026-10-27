using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GGEA.Updater;

/// <summary>本地主程序与独立更新进程的交接记录；不包含任意命令、脚本或网络地址。</summary>
public sealed record UpdateRequest(string TargetDirectory, string? PackageDirectory, string? Version,
    int ParentProcessId, long ParentStartTimeUtcTicks, bool RecoverOnly);

/// <summary>自包含更新进程，从安装目录的 runner 副本运行，主程序退出后才取得写入所有权。</summary>
internal static class Program
{
    /// <summary>唯一入口：探测可启动性，或读取主程序生成的请求。失败以日志和提示明确报告。</summary>
    private static int Main(string[] args)
    {
        if (args is ["--probe"]) return 0;
        string? target = null;
        try
        {
            // 主 EXE 因外部故障无法启动时，用户仍可双击保留的 runner 恢复，不依赖半更新主程序。
            if (args.Length == 0)
            {
                var runnerDirectory = new DirectoryInfo(AppContext.BaseDirectory);
                var installation = runnerDirectory.Parent?.Parent?.FullName;
                if (installation is null || runnerDirectory.Name != "runner" || runnerDirectory.Parent?.Name != UpdateFiles.StateDirectory ||
                    !File.Exists(UpdateFiles.SafePath(installation, UpdateFiles.JournalName)))
                    throw new InvalidOperationException("请从 GGEA 设置页启动更新。中断恢复时可双击安装目录 .ggea-update/runner/GGEA.Updater.exe。");
                var recoveryPath = UpdateFiles.SafePath(installation, ".ggea-update/request.json");
                UpdateFiles.WriteJson(recoveryPath, new UpdateRequest(installation, null, null, 0, 0, true), UpdateJsonContext.Default.UpdateRequest);
                args = ["--request", recoveryPath];
            }
            if (args is not ["--request", var requestPath]) throw new InvalidDataException("请从 GGEA 设置页启动更新。");
            UpdateFiles.RejectLinks(requestPath);
            if (new FileInfo(requestPath).Length > 16384) throw new InvalidDataException("更新请求无效。");
            var request = JsonSerializer.Deserialize(File.ReadAllText(requestPath), UpdateJsonContext.Default.UpdateRequest)
                ?? throw new InvalidDataException("更新请求无效。");
            target = Path.GetFullPath(request.TargetDirectory);
            if (!Path.GetFullPath(requestPath).Equals(UpdateFiles.SafePath(target, ".ggea-update/request.json"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新请求必须来自目标安装。");
            if (!Path.GetFullPath(Environment.ProcessPath!).Equals(UpdateFiles.SafePath(target, UpdateFiles.RunnerName), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新器必须从独立 runner 目录启动。");
            if (!request.RecoverOnly && (request.PackageDirectory is null || request.Version is null ||
                !Path.GetFullPath(request.PackageDirectory).StartsWith(UpdateFiles.SafePath(target, ".ggea-update") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("更新暂存目录无效。");

            // 先通知主程序交接成功，再等待其正常关闭；从不强杀主程序或模拟器。
            using var parent = GetParent(request);
            UpdateFiles.WriteJson(UpdateFiles.SafePath(target, ".ggea-update/ready"), Environment.ProcessId, UpdateJsonContext.Default.Int32);
            if (parent is not null && !parent.WaitForExit(90000)) throw new IOException("GGEA 尚未完成退出，更新未开始。请关闭后重试。");
            using (UpdateFiles.AcquireLock(target))
            {
                if (request.RecoverOnly) UpdateInstaller.Recover(target);
                else UpdateInstaller.Install(target, request.PackageDirectory!, request.Version!, VerifyExecutable);
            }
            Log(target, request.RecoverOnly ? "旧版恢复完成。" : $"更新至 {request.Version} 完成。");
            // 成功后再启动主程序；重启失败不假报安装失败，也不重复覆盖已验证的新版。
            try { Process.Start(new ProcessStartInfo(UpdateFiles.SafePath(target, "GGEA.exe")) { WorkingDirectory = target, UseShellExecute = false }); }
            catch (Exception ex) { Show($"文件更新已完成，请手动启动 GGEA.exe。\n{ex.Message}"); }
            return 0;
        }
        catch (Exception ex)
        {
            if (target is not null) Log(target, ex.ToString());
            Show(ex.Message + "\n\n详细记录位于安装目录 .ggea-update/update.log。原文件备份会保留，恢复未完成时请勿手动覆盖。");
            return 1;
        }
    }

    /// <summary>PID 可能复用，必须同时匹配启动时间；没有原进程时视为已经退出。</summary>
    private static Process? GetParent(UpdateRequest request)
    {
        if (request.ParentProcessId == 0) return null;
        try
        {
            var process = Process.GetProcessById(request.ParentProcessId);
            if (process.StartTime.ToUniversalTime().Ticks == request.ParentStartTimeUtcTicks) return process;
            process.Dispose();
            return null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>复用程序既有离线发布自检，不启动窗口、ADB、游戏或任务；超时只终止自检子进程。</summary>
    private static void VerifyExecutable(string directory)
    {
        var start = new ProcessStartInfo(UpdateFiles.SafePath(directory, "GGEA.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--verify-release");
        using var process = Process.Start(start) ?? throw new IOException("无法启动新版自检。");
        if (!process.WaitForExit(120000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("新版自检超时。");
        }
        if (process.ExitCode != 0) throw new IOException("新版启动自检失败，更新不能继续。");
    }

    /// <summary>日志失败不能掩盖原安装错误。</summary>
    private static void Log(string target, string message)
    {
        try { File.AppendAllText(UpdateFiles.SafePath(target, ".ggea-update/update.log"), $"{DateTimeOffset.Now:u} {message}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>独立进程不依赖目标 WPF 运行库，仅在错误时使用系统提示框。</summary>
    private static void Show(string message) => MessageBox(IntPtr.Zero, message, "GGEA 在线更新", 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
