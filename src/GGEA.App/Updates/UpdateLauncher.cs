using GGEA.App.Localization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GGEA.Updater;

namespace GGEA.App.Updates;

/// <summary>检查当前安装是否适合更新，并把安装所有权交给独立的自包含进程。</summary>
public static class UpdateLauncher
{
    public static Version CurrentVersion => typeof(App).Assembly.GetName().Version!;

    /// <summary>开发目录允许检查，但不允许在线覆盖源码构建产物。</summary>
    public static string? InstallationUnavailableReason(string root)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return L.Text("第一版在线安装仅支持 Windows x64。");
        if ((!File.Exists(Path.Combine(root, "build-info.json")) && !File.Exists(Path.Combine(root, "local-build-info.json"))) ||
            File.Exists(Path.Combine(root, "development-info.json")))
            return L.Text("当前为开发或本地构建版本，可检查更新；请从 GitHub 安装正式包后使用在线安装。");
        try
        {
            // 普通包自带同一更新器；没有正式清单时校验本地版本，下载包仍执行完整摘要校验。
            if (!File.Exists(Path.Combine(root, "build-info.json")))
            {
                var local = UpdateManifest.ReadInstallation(root);
                if (local.Version != CurrentVersion.ToString(4)) return L.Text("当前程序与发布清单不一致，请重新下载正式完整包。");
                return File.Exists(UpdateFiles.SafePath(root, "GGEA.Updater.exe")) ? null
                    : L.Text("更新器缺失或校验失败，请重新下载正式完整包。");
            }
            var manifest = UpdateManifest.Read(root);
            var commit = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(a => a.Key == "GgeaReleaseCommit")?.Value;
            if (manifest.Version != CurrentVersion.ToString(4) || manifest.Commit != commit)
                return L.Text("当前程序与发布清单不一致，请重新下载正式完整包。");
            if (!manifest.Files.TryGetValue("GGEA.Updater.exe", out var hash) ||
                UpdateFiles.Hash(UpdateFiles.SafePath(root, "GGEA.Updater.exe")) != hash.ToLowerInvariant())
                return L.Text("更新器缺失或校验失败，请重新下载正式完整包。");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { return L.Text("当前发布文件不完整，无法在线安装；请从 GitHub 下载完整包。"); }
    }

    /// <summary>从固定位置复制已验证更新器；恢复模式保留之前的独立副本以避开半更新文件。</summary>
    public static async Task StartAsync(string root, string? package, string? version, bool recover, CancellationToken cancellationToken)
    {
        var runner = UpdateFiles.SafePath(root, UpdateFiles.RunnerName);
        if (!recover)
        {
            var reason = InstallationUnavailableReason(root);
            if (reason is not null) throw new InvalidOperationException(reason);
            Directory.CreateDirectory(Path.GetDirectoryName(runner)!);
            File.Copy(UpdateFiles.SafePath(root, "GGEA.Updater.exe"), runner, true);
        }
        if (!File.Exists(runner)) throw new IOException(L.Text("独立恢复程序缺失，请保留 .ggea-update 中的备份并重新下载正式完整包。"));
        var request = new UpdateRequest(Path.GetFullPath(root), package, version, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, recover);
        var requestPath = UpdateFiles.SafePath(root, ".ggea-update/request.json");
        var ready = UpdateFiles.SafePath(root, ".ggea-update/ready");
        File.Delete(ready);
        UpdateFiles.WriteJson(requestPath, request);
        var start = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(runner)! };
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new IOException(L.Text("无法启动更新器。"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(ready))
            {
                if (process.HasExited) throw new IOException(L.Text("更新器未能启动，当前安装未修改。"));
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            if (File.ReadAllText(ready) != process.Id.ToString()) throw new IOException(L.Text("更新器交接标记不一致。"));
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            // 交接失败不能让后台进程在稍后的普通退出时意外安装。
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            throw;
        }
    }
}
