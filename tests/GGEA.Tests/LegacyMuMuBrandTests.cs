using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>旧版厂商标记仅用于列表展示，不再作为截图和输入的准入依据。</summary>
public sealed class LegacyMuMuBrandTests
{
    /// <summary>没有管理器或 MuMu 字样时仍能从精确厂商字段确认品牌。</summary>
    [Theory]
    [InlineData("[ro.product.manufacturer]: [NetEase]")]
    [InlineData("[ro.product.manufacturer]: [netease]\r\n[ro.product.model]: [phone]")]
    [InlineData("[ro.product.vendor.manufacturer]: [NETEASE]")]
    public async Task ManufacturerMarker_IdentifiesList(string properties)
    {
        var adb = new FakeAdbService([], ["legacy"]);
        adb.DeviceProperties["legacy"] = properties;
        var adapter = new GenericEmulatorAdapter(EmulatorTypes.GetProfile(EmulatorTypes.Custom), adb, new TestLog(), (_, _) => { });
        Assert.Equal("MuMu", Assert.Single(await adapter.DiscoverDevicesAsync()).Brand);
    }

    /// <summary>游戏包名、通用硬件、手机型号和相似厂商名均不能证明 MuMu 身份。</summary>
    [Theory]
    [InlineData("[debug.foreground.package]: [com.netease.game]")]
    [InlineData("[ro.product.model]: [NetEase]")]
    [InlineData("[ro.product.manufacturer]: [NotNetEase]")]
    [InlineData("[ro.hardware]: [ranchu]\n[ro.product.manufacturer]: [vivo]")]
    public void UnrelatedMarkers_RemainUnknown(string properties)
        => Assert.Equal(EmulatorTypes.Custom, GenericEmulatorAdapter.DetectDeviceType(properties));
}
