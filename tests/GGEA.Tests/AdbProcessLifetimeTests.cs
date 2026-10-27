using System.Diagnostics;
using System.IO;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>用真实长运行子进程验证取消、超时和输出管道回收，不依赖 FakeAdb。</summary>
public sealed class AdbProcessLifetimeTests
{
    /// <summary>监听端口必须属于预期进程，不能因为收到 ADB 样式回复就接受其他进程。</summary>
    [Fact]
    public void ServerPort_RequiresExpectedOwner()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(BlueStacksAdbServer.OwnsListener(port, Environment.ProcessId));
            Assert.False(BlueStacksAdbServer.OwnsListener(port, -1));
        }
        finally { listener.Stop(); }
    }

    /// <summary>释放专属服务的所有权会结束真实子进程，避免应用退出后遗留 ADB 服务。</summary>
    [Fact]
    public async Task OwnedServer_StopsWhenLifetimeHandleCloses()
    {
        using var child = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
            UseShellExecute = false, CreateNoWindow = true
        })!;
        try
        {
            using (BlueStacksAdbServer.CreateLifetimeJob(child)) Assert.False(child.HasExited);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    /// <summary>非交互命令不继承宿主输入；文本与二进制命令都必须收到明确 EOF。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonInteractiveCommand_ReceivesClosedStandardInput(bool binary)
    {
        var service = new AdbService("powershell.exe", new TestLog());
        string[] args = ["-NoProfile", "-NonInteractive", "-Command",
            "if (-not [Console]::IsInputRedirected) { exit 9 }; $inputText=[Console]::In.ReadToEnd(); if ($inputText.Length -ne 0) { exit 8 }; [Console]::Out.Write('eof')"];
        if (binary)
            Assert.Equal("eof"u8.ToArray(), await service.RunBytesAsync(args, 5000, default));
        else
            Assert.Equal("eof", await service.RunTextAsync(args, 5000, default));
    }

    /// <summary>文本命令及二进制截图必须在取消返回时已退出，旧 token 不能再启动点击。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Command_CancelOrTimeout_ReapsProcess(bool binary, bool timeout)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"ggea-process-{Guid.NewGuid():N}.txt");
        var service = new AdbService("powershell.exe", new TestLog());
        using var cts = new CancellationTokenSource();
        Process? child = null;
        try
        {
            var script = $"[IO.File]::WriteAllText('{pidFile}', [string]$PID); [Console]::Out.WriteLine('ready'); [Console]::Error.WriteLine('ready'); Start-Sleep -Seconds 60";
            string[] args = ["-NoProfile", "-NonInteractive", "-Command", script];
            Task command = binary
                ? service.RunBytesAsync(args, timeout ? 3000 : 30000, cts.Token)
                : service.RunTextAsync(args, timeout ? 3000 : 30000, cts.Token);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile)) await Task.Delay(20, ready.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
            Assert.False(child.HasExited);
            if (timeout)
                await Assert.ThrowsAsync<AdbException>(() => command.WaitAsync(TimeSpan.FromSeconds(10)));
            else
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.True(child.HasExited);
            // 停止完成后新运行能正常工作；旧 token 仍保持失效。
            var restarted = await service.RunTextAsync(
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('new-session')"],
                10000, CancellationToken.None);
            Assert.Equal("new-session", restarted);
            cts.Cancel();
            // 若错误地启动了下一条进程，无效可执行文件将产生 AdbException 而非取消异常。
            var next = new AdbService("does-not-exist.exe", new TestLog());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.TapAsync("device", 1, 1, cts.Token));
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                child.Dispose();
            }
            File.Delete(pidFile);
        }
    }

    /// <summary>已取消的运行不得启动截图或滑动客户端。</summary>
    [Fact]
    public async Task AlreadyCanceled_DoesNotStartProcess()
    {
        var service = new AdbService("does-not-exist.exe", new TestLog());
        var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScreenshotAsync("device", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SwipeAsync("device", 0, 0, 1, 1, 1000, ct));
    }
}
