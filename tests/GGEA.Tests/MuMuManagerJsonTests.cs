using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>管理器非标准文本不能破坏设备识别，也不能放宽身份与损坏响应的保护。</summary>
public sealed class MuMuManagerJsonTests
{
    /// <summary>按反馈响应的末尾 name 结构回放，覆盖离线实例与在线实例的身份信息保留。</summary>
    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    public void TerminalNameMissingQuote_PreservesInstanceIdentity(string newline, bool online)
    {
        var json = "{\n  \"1\": {\n    \"is_android_started\": " + (online ? "true" : "false") +
            ",\n    \"adb_host_ip\": \"127.0.0.1\",\n    \"adb_port\": 16416,\n    \"name\": \"测试名称?\n  }\n}";
        json = json.Replace("\n", newline);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => { using var original = System.Text.Json.JsonDocument.Parse(json); });
        using var document = MuMuManagerJson.Parse(json);
        Assert.Equal("测试名称?", document.RootElement.GetProperty("1").GetProperty("name").GetString());
        Assert.Equal(online ? 1 : 0, MuMuDeviceIdentity.ParseRunningSerials(json).Count);
        Assert.Equal(online ? 1 : 0, MuMuScreenshotService.ParseInstances(json).Count);
    }

    /// <summary>不能修补身份字段、非末尾名称、非法转义或缺失的对象结束符。</summary>
    [Theory]
    [InlineData("{\n\"adb_host_ip\": \"127.0.0.1\n}")]
    [InlineData("{\n\"name\": \"bad\n,\"is_android_started\":true\n}")]
    [InlineData("{\n\"name\": \"bad\\\n}")]
    [InlineData("{\n\"name\": \"bad\n")]
    public void MissingQuoteOutsideNarrowBoundary_IsRejected(string json)
        => Assert.Throws<AdbException>(() => MuMuManagerJson.Parse(json));

    /// <summary>完整名称包含转义引号及反斜杠时保持值不变，不误补终止符。</summary>
    [Fact]
    public void CompleteTerminalName_IsUnchanged()
    {
        const string json = "{\n\"name\": \"quoted \\\"name\\\" and \\\\\"\n}";
        using var expected = System.Text.Json.JsonDocument.Parse(json);
        using var actual = MuMuManagerJson.Parse(json);
        Assert.Equal(expected.RootElement.GetProperty("name").GetString(), actual.RootElement.GetProperty("name").GetString());
    }

    /// <summary>同一响应经过品牌、截图和启动三个入口时均能保留目标实例。</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    public void BareWhitespace_AllManagerConsumersAccept(string whitespace)
    {
        var instance = "{\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16416,\"name\":\"first" + whitespace + "second\"}";
        var json = "{\"1\":" + instance + "}";
        Assert.Contains("127.0.0.1:16416", MuMuDeviceIdentity.ParseRunningSerials(json));
        Assert.Equal(1, Assert.Single(MuMuScreenshotService.ParseInstances(json)).Index);
        Assert.True(MuMuEmulatorAdapter.ParseRunningState(instance));
        using var document = MuMuManagerJson.Parse(instance);
        Assert.Equal("first" + whitespace + "second", document.RootElement.GetProperty("name").GetString());
    }

    /// <summary>合法转义、反斜杠和引号保持原义，结构换行不改变内容。</summary>
    [Fact]
    public void ValidEscapes_ArePreserved()
    {
        const string value = "quoted \"name\" C:\\folder\\\nline\tend";
        var json = System.Text.Json.JsonSerializer.Serialize(new { name = value });
        using var document = MuMuManagerJson.Parse("\n" + json + "\r\n");
        Assert.Equal(value, document.RootElement.GetProperty("name").GetString());
    }

    /// <summary>缺引号、非法转义、其他控制字符或混入日志都不能变成成功。</summary>
    [Theory]
    [InlineData("{\"name\":\"unfinished\n}")]
    [InlineData("{\"name\":\"bad\\\ntext\"}")]
    [InlineData("{\"name\":\"bad\u0001text\"}")]
    [InlineData("log\n{}")]
    public void StructuralDamage_IsRejectedWithoutResponseLeak(string json)
    {
        var error = Assert.Throws<AdbException>(() => MuMuManagerJson.Parse(json));
        Assert.Contains("MuMu 管理器实例信息 JSON 无效", error.Message);
        Assert.DoesNotContain(json, error.Message);
    }

    /// <summary>兼容另一个实例的文本后，仍只接受管理器明确列出的在线端点。</summary>
    [Theory]
    [InlineData("127.0.0.1:16416", true)]
    [InlineData("127.0.0.1:16448", false)]
    public void RecoveredResponse_StillRequiresIdentity(string serial, bool expected)
    {
        const string json = "{\"1\":{\"is_android_started\":true,\"adb_host_ip\":\"127.0.0.1\",\"adb_port\":16416},\"2\":{\"is_android_started\":false,\"name\":\"other\ninstance\"}}";
        Assert.Equal(expected, MuMuDeviceIdentity.ParseRunningSerials(json).Contains(serial));
    }

    /// <summary>管理器响应损坏仍不能提供原生截图的实例依据；标准 ADB 截图不依赖此解析。</summary>
    [Fact]
    public void InvalidManager_CannotSupplyNativeInstances()
    {
        var error = Assert.Throws<AdbException>(() => MuMuScreenshotService.ParseInstances("broken"));
        Assert.Contains("MuMu 管理器实例信息 JSON 无效", error.Message);
    }
}
