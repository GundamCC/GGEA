using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>验证截图结构诊断能够区分故障且不泄露未知输出内容。</summary>
public sealed class AdbScreenshotDiagnosticsTests
{
    /// <summary>完整签名保持原样；后四字节损坏不能再误判为 PNG。</summary>
    [Fact]
    public void Signature_RequiresAllEightBytes()
    {
        AdbService.ValidateScreenshot([137, 80, 78, 71, 13, 10, 26, 10]);
        Assert.Throws<AdbException>(() => AdbService.ValidateScreenshot([137, 80, 78, 71, 0, 0, 0, 0]));
    }

    /// <summary>典型异常输出产生可区分的诊断，而不是统一的长度错误。</summary>
    [Theory]
    [InlineData("", "空输出")]
    [InlineData("0102", "输出不足")]
    [InlineData("010289504E470D0A1A0A", "PNG 前存在额外输出")]
    [InlineData("89504E470D0D0A1A0D0A", "疑似发生换行转换")]
    [InlineData("0000000000000000", "未知格式")]
    public void InvalidOutput_IsClassified(string hex, string expected)
    {
        var error = Assert.Throws<AdbException>(() => AdbService.ValidateScreenshot(Convert.FromHexString(hex)));
        Assert.Contains(expected, error.Message);
    }

    /// <summary>诊断扫描有界，且不会把未知文本当作日志内容回传。</summary>
    [Fact]
    public void UnknownOutput_IsNotDisclosedAndScanIsBounded()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("private-output-example" + new string('x', 5000));
        bytes = [.. bytes, 137, 80, 78, 71, 13, 10, 26, 10];
        var error = Assert.Throws<AdbException>(() => AdbService.ValidateScreenshot(bytes));
        Assert.DoesNotContain("private-output-example", error.Message);
        Assert.Contains("PNG偏移=-1", error.Message);
    }

    /// <summary>真实启动失败也必须保留所选目标和失败阶段，不依赖品牌名称。</summary>
    [Fact]
    public async Task StartFailure_ContainsScreenshotContext()
    {
        var service = new AdbService("ggea-missing-adb-for-test.exe", new TestLog());
        var error = await Assert.ThrowsAsync<AdbException>(() => service.ScreenshotAsync("emulator-5558"));
        Assert.Contains("serial=emulator-5558", error.Message);
        Assert.Contains("stage=执行命令", error.Message);
        Assert.Contains("command=exec-out screencap -p", error.Message);
        Assert.Contains("elapsedMs=", error.Message);
    }
}
