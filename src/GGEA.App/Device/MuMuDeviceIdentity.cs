using System.IO;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>从宿主管理器读取运行实例的真实 ADB 地址，不依赖可被伪装的 Android 品牌属性。</summary>
internal static class MuMuDeviceIdentity
{
    /// <summary>管理器明确提供的端点；不根据实例编号推算端口，兼容桥接及端口迁移。</summary>
    internal sealed record Endpoint(string Host, int Port)
    {
        /// <summary>ADB 序列号格式；IPv6 使用方括号避免与端口分隔符混淆。</summary>
        public string Serial => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    }

    /// <summary>读取单实例响应中的在线端点；启动中的实例暂时没有地址时允许继续等待。</summary>
    internal static Endpoint? ParseEndpoint(string json)
    {
        // 状态响应损坏必须失败，不能把查询失败当作尚未启动。
        if (!MuMuEmulatorAdapter.ParseRunningState(json)) return null;
        using var document = MuMuManagerJson.Parse(json);
        return ReadEndpoint(document.RootElement);
    }

    /// <summary>只接受合法 IP 和端口；该值将传给 ADB，不接受任意命令或空白地址。</summary>
    internal static Endpoint? ReadEndpoint(JsonElement data)
    {
        if (!data.TryGetProperty("adb_port", out var port) || port.ValueKind != JsonValueKind.Number ||
            !port.TryGetInt32(out var number) || number is < 1 or > 65535 ||
            !data.TryGetProperty("adb_host_ip", out var host) || host.ValueKind != JsonValueKind.String) return null;
        // 旧版接受 localhost；仅兼容这一明确回环名称，不做任意 DNS 解析或端口猜测。
        var hostText = host.GetString();
        if (string.Equals(hostText, "localhost", StringComparison.OrdinalIgnoreCase)) hostText = "127.0.0.1";
        if (!System.Net.IPAddress.TryParse(hostText, out var address) ||
            address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any)) return null;
        return new Endpoint(address.ToString(), number);
    }

    /// <summary>只查询实例信息；有界等待避免管理器无响应时阻塞设备刷新。</summary>
    internal static async Task<IReadOnlySet<string>> ReadRunningSerialsAsync(string? managerPath, IGgeaLog log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(managerPath) || !File.Exists(managerPath))
            return new HashSet<string>();
        // 与原生截图共用原始字节解码及进程回收逻辑，避免旧版 ANSI 输出产生不同解析结果。
        var manager = new AdbService(managerPath, log);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return ParseRunningSerials(await QueryAllAsync(
                (args, token) => manager.RunTextAsync(args, 5000, token), timeout.Token));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AdbException("MuMu 实例信息查询超时，未取得管理器身份依据", ex);
        }
    }

    /// <summary>只读查询自动兼容长短参数；两次均须严格解析，不输出原始响应或猜测实例。</summary>
    internal static async Task<string> QueryAllAsync(
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> query, CancellationToken ct)
    {
        string? emptyResponse = null;
        foreach (var option in new[] { "-v", "--vmindex" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var output = await query(["info", option, "all"], ct);
                ct.ThrowIfCancellationRequested();
                using var document = MuMuManagerJson.Parse(output);
                // 枚举迫使身份结构和管理器错误码校验执行，不能把错误 JSON 当成在线信息。
                _ = MuMuManagerJson.EnumerateInstances(document.RootElement).ToArray();
                // 合法 JSON 不代表查到了可用实例；空结果继续尝试另一种参数，但不猜测实例身份。
                if (ParseRunningSerials(output).Count > 0) return output;
                emptyResponse = output;
            }
            catch (AdbException) when (option == "-v") { /* 短参数响应不可用时安全重试长参数，不假定模拟器版本。 */ }
            catch (AdbException ex)
            {
                if (emptyResponse is not null) return emptyResponse;
                throw new AdbException("MuMu 管理器长短参数查询均未返回有效实例信息，将尝试本机监听发现；原生实例身份仍不可猜测", ex);
            }
        }
        return emptyResponse ?? throw new InvalidOperationException("MuMu 实例查询未执行");
    }

    /// <summary>只接受管理器确认已启动的实例及其显式地址；不按端口范围或实例索引猜测设备。</summary>
    internal static IReadOnlySet<string> ParseRunningSerials(string json)
    {
        using var document = MuMuManagerJson.Parse(json);
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, data) in MuMuManagerJson.EnumerateInstances(document.RootElement))
        {
            if (!data.TryGetProperty("is_android_started", out var started) || started.ValueKind != JsonValueKind.True ||
                ReadEndpoint(data) is not { } endpoint) continue;
            serials.Add(endpoint.Serial);
        }
        return serials;
    }
}
