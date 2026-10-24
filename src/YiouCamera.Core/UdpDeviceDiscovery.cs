using System.Net;
using System.Net.Sockets;

namespace YiouCamera.Core;

/// <summary>UDP 发现参数。</summary>
public sealed class UdpDiscoveryOptions
{
    /// <summary>8 位配对码；null/非法时自动按 <see cref="YocamPinCode.Resolve"/> 解析。</summary>
    public string? PinCode { get; set; }

    /// <summary>本机绑定网卡 IP（多网卡/VPN 环境必设）；null = 自动挑选非虚拟网卡。</summary>
    public IPAddress? LocalBindAddress { get; set; }

    /// <summary>厂商节拍：每 1 秒广播一次（协议 §3.1）。</summary>
    public TimeSpan SendInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>单次接收轮询上限（影响取消/超时响应速度）。</summary>
    public TimeSpan ReceivePollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 额外探测端点（可选）。仿真器/自测时可加 <c>127.0.0.1:12811</c>；
    /// 真机环境通常留空，仅发 255.255.255.255 + 子网定向广播。
    /// </summary>
    public IList<IPEndPoint> AdditionalProbeEndpoints { get; } = new List<IPEndPoint>();

    /// <summary>除 255.255.255.255 外，是否再发一份子网定向广播（如 192.168.0.255）。协议 §3.3 建议。</summary>
    public bool SendSubnetBroadcast { get; set; } = true;

    public Action<string>? Log { get; set; }
}

/// <summary>
/// UDP 12811 设备发现（协议 §3）。
/// - 载荷 <c>"CMD_GETIP"+8位配对码</c>（18 字节 ASCII/GBK 等价）；
/// - 每 1 秒广播一次，直到超时/取消；
/// - 应答 <c>&lt;pcIp&gt;ACK_GETIP</c>，<b>设备 IP 取 UDP 源地址</b>，不解析文本。
/// </summary>
public sealed class UdpDeviceDiscovery : IDeviceDiscovery
{
    private readonly UdpDiscoveryOptions _options;
    private readonly string _pinCode;
    private readonly Action<string>? _log;

    public UdpDeviceDiscovery(string pinCode, IPAddress? localBind = null, Action<string>? log = null)
        : this(new UdpDiscoveryOptions { PinCode = pinCode, LocalBindAddress = localBind, Log = log })
    {
    }

    public UdpDeviceDiscovery(UdpDiscoveryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log;
        _pinCode = YocamPinCode.Resolve(options.PinCode, log: _log);
    }

    public string PinCode => _pinCode;

    public async Task<DeviceInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        IPAddress localBind = _options.LocalBindAddress ?? LocalNetwork.PickPreferredLocalAddress(_log);
        _log?.Invoke($"[discovery] 开始发现：pin={_pinCode}, bind={localBind}, timeout={timeout.TotalSeconds:0.#}s");

        using var udp = new UdpClient(new IPEndPoint(localBind, 0));
        udp.EnableBroadcast = true;   // 必须；否则 Send 到 255.255.255.255 抛异常

        byte[] payload = YocamProtocol.BuildDiscoveryPayloadAllowAnyPin(_pinCode);
        List<IPEndPoint> targets = BuildTargets(localBind);
        _log?.Invoke($"[discovery] 广播目标：{string.Join(", ", targets.Select(t => t.ToString()))}");

        DateTime deadline = DateTime.UtcNow + timeout;
        DateTime nextSend = DateTime.MinValue;
        int sent = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow >= nextSend)
            {
                foreach (var target in targets)
                {
                    try
                    {
                        await udp.SendAsync(payload, target, ct).ConfigureAwait(false);
                        sent++;
                    }
                    catch (SocketException ex)
                    {
                        _log?.Invoke($"[discovery] 发送到 {target} 失败：{ex.SocketErrorCode}（忽略，继续其它目标）");
                    }
                }
                nextSend = DateTime.UtcNow + _options.SendInterval;
                if (sent == targets.Count)
                    _log?.Invoke($"[discovery] 已发送第 {sent / Math.Max(1, targets.Count)} 轮 CMD_GETIP");
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            TimeSpan poll = remaining < _options.ReceivePollInterval ? remaining : _options.ReceivePollInterval;
            if (poll <= TimeSpan.Zero) break;

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pollCts.CancelAfter(poll);
            try
            {
                UdpReceiveResult result = await udp.ReceiveAsync(pollCts.Token).ConfigureAwait(false);
                string text = YocamProtocol.WireEncoding.GetString(result.Buffer);
                if (!YocamProtocol.TryParseDiscoveryAck(text, out string pcIp))
                {
                    _log?.Invoke($"[discovery] 忽略非 ACK 回包（{result.Buffer.Length}B, from {result.RemoteEndPoint}）：{text}");
                    continue;
                }

                IPAddress deviceIp = result.RemoteEndPoint.Address;
                if (deviceIp.IsIPv4MappedToIPv6) deviceIp = deviceIp.MapToIPv4();

                var info = new DeviceInfo { DeviceIp = deviceIp.ToString(), PcIp = pcIp };
                _log?.Invoke($"[discovery] 发现设备：{info}（源地址优先于文本）");
                return info;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 本轮接收窗口到期，继续发下一轮广播
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ICMP 端口不可达等：忽略，继续轮询
            }
        }

        _log?.Invoke($"[discovery] 超时未收到 ACK_GETIP（共发 {sent} 包；检查配对码/网卡/防火墙/设备是否在线）");
        return null;
    }

    private List<IPEndPoint> BuildTargets(IPAddress localBind)
    {
        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, YocamProtocol.DiscoveryPort),
        };

        IPAddress? mask = null;
        if (!localBind.Equals(IPAddress.Any))
            mask = LocalNetwork.GetMaskForLocalAddress(localBind);

        if (_options.SendSubnetBroadcast && mask != null && !IPAddress.IsLoopback(localBind))
        {
            IPAddress? directed = LocalNetwork.GetSubnetBroadcast(localBind, mask);
            if (directed != null && !targets.Any(t => t.Address.Equals(directed)))
                targets.Add(new IPEndPoint(directed, YocamProtocol.DiscoveryPort));
        }

        // 回环/仿真环境：广播可能出不了 loopback，补一发单播（真机环境不会命中）
        if (IPAddress.IsLoopback(localBind) || localBind.Equals(IPAddress.Any))
        {
            var loop = new IPEndPoint(IPAddress.Loopback, YocamProtocol.DiscoveryPort);
            if (!targets.Any(t => t.Equals(loop))) targets.Add(loop);
        }

        foreach (var extra in _options.AdditionalProbeEndpoints)
            if (!targets.Any(t => t.Equals(extra)))
                targets.Add(extra);

        return targets;
    }
}
