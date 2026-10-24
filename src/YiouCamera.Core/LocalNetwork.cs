using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace YiouCamera.Core;

/// <summary>可用 IPv4 网卡快照（供插件设置页选择/诊断）。</summary>
public sealed class LocalNetworkInterface
{
    public string Name = string.Empty;
    public string Description = string.Empty;
    public IPAddress IpAddress = IPAddress.None;
    public IPAddress SubnetMask = IPAddress.None;
    public NetworkInterfaceType Type = NetworkInterfaceType.Unknown;
    public bool HasGateway;
    public bool IsVirtual;
    public override string ToString() => $"{IpAddress}/{MaskToPrefix(SubnetMask)} ({Name}, {Description}){(IsVirtual ? " [virtual]" : string.Empty)}";

    private static int MaskToPrefix(IPAddress mask)
    {
        uint m = ToUInt(mask);
        int bits = 0;
        while (m != 0) { bits += (int)(m & 1); m >>= 1; }
        return bits;
    }

    internal static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}

/// <summary>
/// 本机 IPv4 网卡选择。协议 §3.3/§13.2：多网卡 + 代理虚拟网卡（如 FlClash）会抢默认路由，
/// 必须能显式绑网卡；未指定时自动挑“有网关的非虚拟以太网/无线网卡”。
/// </summary>
public static class LocalNetwork
{
    private static readonly string[] VirtualHints =
    {
        "virtual", "vmware", "hyper-v", "vethernet", "tap-", "tap adapter", "tun", "wireguard",
        "clash", "flclash", "npcap", "loopback", "bluetooth", "vpn", "zerotier", "tailscale",
        "docker", "wsl", "pseudo", "wan miniport",
    };

    /// <summary>列出全部 Up 状态、带 IPv4 的网卡（含虚拟网卡，便于用户手动选择）。</summary>
    public static IReadOnlyList<LocalNetworkInterface> ListCandidates()
    {
        var list = new List<LocalNetworkInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }

            IPAddress? ip = null, mask = null;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                ip = ua.Address;
                mask = ua.IPv4Mask;
                break;
            }
            if (ip == null || mask == null) continue;

            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.None));

            list.Add(new LocalNetworkInterface
            {
                Name = ni.Name,
                Description = ni.Description,
                IpAddress = ip,
                SubnetMask = mask,
                Type = ni.NetworkInterfaceType,
                HasGateway = hasGateway,
                IsVirtual = IsVirtualNetworkInterface(ni),
            });
        }
        return list;
    }

    /// <summary>
    /// 自动挑选默认本机 IP：优先非虚拟 + 有网关（以太网优先于无线）；再退化到任意非虚拟；最后回环。
    /// </summary>
    public static IPAddress PickPreferredLocalAddress(Action<string>? log = null)
    {
        var candidates = ListCandidates();

        LocalNetworkInterface? best = candidates
            .Where(c => !c.IsVirtual && c.HasGateway)
            .OrderBy(c => c.Type == NetworkInterfaceType.Ethernet ? 0 : 1)
            .FirstOrDefault()
            ?? candidates.Where(c => !c.IsVirtual)
                .OrderBy(c => c.Type == NetworkInterfaceType.Ethernet ? 0 : 1)
                .FirstOrDefault();

        if (best != null)
        {
            log?.Invoke($"[net] 自动选择网卡：{best}");
            return best.IpAddress;
        }

        log?.Invoke("[net] 未找到可用物理网卡，回退到 loopback 127.0.0.1（仅自测/仿真环境可用）");
        return IPAddress.Loopback;
    }

    public static bool IsVirtualNetworkInterface(NetworkInterface ni)
    {
        string text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        return VirtualHints.Any(h => text.Contains(h, StringComparison.Ordinal));
    }

    /// <summary>计算子网定向广播地址（如 192.168.0.2/24 → 192.168.0.255）；失败返回 null。</summary>
    public static IPAddress? GetSubnetBroadcast(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            return null;

        uint ip = LocalNetworkInterface.ToUInt(address);
        uint m = LocalNetworkInterface.ToUInt(mask);
        uint broadcast = (ip & m) | ~m;
        var bytes = new byte[4];
        bytes[0] = (byte)(broadcast >> 24);
        bytes[1] = (byte)(broadcast >> 16);
        bytes[2] = (byte)(broadcast >> 8);
        bytes[3] = (byte)broadcast;
        return new IPAddress(bytes);
    }

    /// <summary>按本机 IP 反查其掩码（用于补发子网定向广播）；找不到返回 null。</summary>
    public static IPAddress? GetMaskForLocalAddress(IPAddress address)
    {
        foreach (var c in ListCandidates())
            if (c.IpAddress.Equals(address)) return c.SubnetMask;

        if (address.Equals(IPAddress.Loopback)) return IPAddress.Parse("255.0.0.0");
        return null;
    }

    /// <summary>是否是回环绑定（仿真/自测环境，发现会额外单播 127.0.0.1:12811）。</summary>
    public static bool IsLoopback(IPAddress? address)
        => address != null && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any));
}
