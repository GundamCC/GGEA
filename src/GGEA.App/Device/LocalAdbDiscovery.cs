using System.Net;
using System.Net.NetworkInformation;

namespace GGEA.App.Device;

/// <summary>发现被精确回环监听遮蔽的本机 ADB 转发；不修改模拟器端口或终止共享服务。</summary>
internal static class LocalAdbDiscovery
{
    /// <summary>补充管理器遗漏的真实监听端口，不逐个扫描未监听端口。</summary>
    internal static IReadOnlyList<int> ReadMuMuPorts()
        => FindMuMuPorts(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners());

    /// <summary>端口只是发现候选，不证明品牌或实例编号；限制数量及地址，避免连接外部设备。</summary>
    internal static IReadOnlyList<int> FindMuMuPorts(IEnumerable<IPEndPoint> listeners)
        => listeners.Where(endpoint => endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Address.Equals(IPAddress.Any))
            .Select(endpoint => endpoint.Port)
            .Where(port => port >= 16384 && port <= 32768 && (port - 16384) % 32 == 0)
            // 连接数量由调用方在排除在线端点后限制，避免前十六个在线实例遮蔽后续候选。
            .Distinct().Order().ToArray();

    /// <summary>读取真实监听端口；调用方处理查询失败，不把未知网络状态当作可连接设备。</summary>
    internal static IReadOnlyList<int> ReadShadowedPorts(int maxInstances)
        => FindShadowedPorts(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), maxInstances);

    /// <summary>仅检查有限的标准模拟器端口，同时存在通配与 127.0.0.1 监听才使用备用回环地址。</summary>
    internal static IReadOnlyList<int> FindShadowedPorts(IEnumerable<IPEndPoint> listeners, int maxInstances)
    {
        var endpoints = listeners.ToHashSet();
        // 限制上限，避免异常配置扩大为全端口扫描；端口仅用于发现，品牌仍由设备属性确认。
        return Enumerable.Range(0, Math.Clamp(maxInstances, 0, 16))
            .Select(index => 5555 + index * 2)
            .Where(port => endpoints.Contains(new IPEndPoint(IPAddress.Any, port)) &&
                           endpoints.Contains(new IPEndPoint(IPAddress.Loopback, port)))
            .ToArray();
    }
}
