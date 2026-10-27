using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>回归用户管理器返回单实例对象的格式；发现和原生截图必须使用相同身份依据。</summary>
public sealed class MuMuInstanceFormatTests
{
    /// <summary>来自反馈的必要字段，省略名称、时间戳和进程信息等无关宿主数据。</summary>
    private const string SingleInstance = """
        {"index":"0","is_android_started":true,"is_process_started":true,
         "adb_port":16384,"adb_host_ip":"127.0.0.1","player_state":"start_finished"}
        """;

    /// <summary>单对象与编号集合均可发现目标，emulator 别名仍通过启动身份确认。</summary>
    [Theory]
    [InlineData(false, "127.0.0.1:16384")]
    [InlineData(false, "emulator-5554")]
    [InlineData(true, "emulator-5554")]
    public async Task RunningInstance_IsDiscoveredAndResolved(bool wrapped, string selected)
    {
        var json = wrapped ? "{\"0\":" + SingleInstance + "}" : SingleInstance;
        Assert.Equal("127.0.0.1:16384", Assert.Single(MuMuDeviceIdentity.ParseRunningSerials(json)));
        var instances = MuMuScreenshotService.ParseInstances(json);
        var boot = Guid.NewGuid().ToString();
        var instance = await MuMuScreenshotService.ResolveAsync(instances, selected, (_, _) => Task.FromResult(boot), default);
        Assert.Equal(0, instance.Index);
        Assert.Null(instance.AndroidVersion);
    }

    /// <summary>新格式不放宽在线状态、实例编号或端点要求，也不接受集合内互相矛盾的编号。</summary>
    [Theory]
    [InlineData("{\"index\":\"bad\",\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384}")]
    [InlineData("{\"index\":-1,\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384}")]
    [InlineData("{\"index\":0,\"is_android_started\":false,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384}")]
    [InlineData("{\"index\":0,\"is_android_started\":true,\"adb_port\":16384}")]
    [InlineData("{\"1\":{\"index\":0,\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384}}")]
    public void InvalidIdentity_IsNotUsable(string json)
    {
        Assert.Empty(MuMuDeviceIdentity.ParseRunningSerials(json));
        Assert.Empty(MuMuScreenshotService.ParseInstances(json));
    }

    /// <summary>单实例数字索引与旧集合无 index 字段均保留兼容。</summary>
    [Theory]
    [InlineData("{\"index\":0,\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384}")]
    [InlineData("{\"0\":{\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16384}}")]
    public void NumericAndLegacyIndices_AreSupported(string json)
    {
        Assert.Single(MuMuDeviceIdentity.ParseRunningSerials(json));
        Assert.Equal(0, Assert.Single(MuMuScreenshotService.ParseInstances(json)).Index);
    }
}
