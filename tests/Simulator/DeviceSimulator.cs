using System.Net;
using System.Net.Sockets;
using System.Text;
using YiouCamera.Core;

namespace YiouCamera.Simulator;

/// <summary>设备端仿真参数。</summary>
public sealed class DeviceSimulatorOptions
{
    public string PinCode { get; set; } = "92271885";
    public int DiscoveryPort { get; set; } = YocamProtocol.DiscoveryPort;
    public int ControlPort { get; set; } = YocamProtocol.ControlPort;
    public int RtspPort { get; set; } = YocamProtocol.RtspPort;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public string? FfmpegPath { get; set; }

    /// <summary>是否忽略配对码校验（协议 §12 V1 待验证项；默认严格按码过滤）。</summary>
    public bool AcceptAnyPin { get; set; }

    /// <summary>是否把 ACK_RTSP_TRANS_START 拆成两个 TCP 段发送（验证内核拆包处理）。</summary>
    public bool FragmentedRtspAck { get; set; } = true;

    public bool Verbose { get; set; }
    public Action<string>? Log { get; set; }
}

/// <summary>
/// 设备端仿真器：
/// - UDP 12811：收到 <c>CMD_GETIP{pin}</c>（严格匹配配对码）→ 回 <c>&lt;源IP&gt;ACK_GETIP</c>；
/// - TCP 8888：心跳 CMD_DATA_SYNC / 开流 CMD_RTSP_TRANS_START / CMD_LIVE_STOP{n} / CMD_WIFI_STA /
///   CMD_SETWIFI…，按协议回对应 ACK；开流 ACK 可故意拆包；
/// - RTSP 554：本地最小 MJPEG/RTP-UDP 服务（无真机时的画面来源）。
/// </summary>
public sealed class DeviceSimulator : IAsyncDisposable
{
    private readonly DeviceSimulatorOptions _options;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _controlClients = new();
    private readonly object _clientGate = new();

    private UdpClient? _udp;
    private TcpListener? _tcp;
    private Task? _udpTask;
    private Task? _tcpTask;
    private int _discoveryRequests;
    private int _heartbeats;
    private int _rtspStarts;
    private int _liveStops;
    private int _wifiSta;
    private int _controlConnects;

    public DeviceSimulator(DeviceSimulatorOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? options.Log;
        Frames = new JpegFrameProvider(options.Width, options.Height, options.Fps, options.FfmpegPath, _log);
        RtspServer = new MiniRtspServer(Frames, options.RtspPort, _log);
    }

    public JpegFrameProvider Frames { get; }
    public MiniRtspServer RtspServer { get; }

    public int DiscoveryRequests => Volatile.Read(ref _discoveryRequests);
    public int Heartbeats => Volatile.Read(ref _heartbeats);
    public int RtspStartRequests => Volatile.Read(ref _rtspStarts);
    public int LiveStopRequests => Volatile.Read(ref _liveStops);
    public int WifiStaRequests => Volatile.Read(ref _wifiSta);
    public int ControlConnects => Volatile.Read(ref _controlConnects);

    public async Task StartAsync()
    {
        // RTSP 先起来（真机也是 554 常驻，开流命令后才真正有流）
        await RtspServer.StartAsync().ConfigureAwait(false);

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _options.DiscoveryPort));
        _udpTask = Task.Run(() => UdpLoopAsync(_cts.Token));

        _tcp = new TcpListener(IPAddress.Any, _options.ControlPort);
        _tcp.Start(16);
        _tcpTask = Task.Run(() => TcpAcceptLoopAsync(_cts.Token));

        _log?.Invoke($"[sim] 设备仿真器已启动：UDP {_options.DiscoveryPort} / TCP {_options.ControlPort} / " +
                     $"RTSP {_options.RtspPort}（pin={_options.PinCode}, {_options.Width}x{_options.Height}@{_options.Fps}）");
    }

    private async Task UdpLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                string text = Encoding.ASCII.GetString(result.Buffer);
                Interlocked.Increment(ref _discoveryRequests);
                _log?.Invoke($"[sim-udp] RX '{Printable(text)}' from {result.RemoteEndPoint}");

                if (!text.Contains(YocamProtocol.DiscoveryCommand, StringComparison.Ordinal))
                    continue;

                if (!_options.AcceptAnyPin &&
                    !text.Contains(_options.PinCode, StringComparison.Ordinal))
                {
                    _log?.Invoke($"[sim-udp] 配对码不匹配（期望 {_options.PinCode}），忽略");
                    continue;
                }

                string pcIp = result.RemoteEndPoint.Address.ToString();
                byte[] reply = Encoding.ASCII.GetBytes(pcIp + YocamProtocol.DiscoveryAck);
                await _udp.SendAsync(reply, result.RemoteEndPoint, ct).ConfigureAwait(false);
                _log?.Invoke($"[sim-udp] TX '{Printable(Encoding.ASCII.GetString(reply))}' → {result.RemoteEndPoint}");
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // 客户端 socket 已关闭时回包会收到 ICMP port unreachable（ConnectionReset），忽略
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { return; }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[sim-udp] 异常：{ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _log?.Invoke($"[sim-tcp] accept 失败：{ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            client.NoDelay = true;
            Interlocked.Increment(ref _controlConnects);
            lock (_clientGate) _controlClients.Add(client);
            _log?.Invoke($"[sim-tcp] 控制客户端接入：{client.Client.RemoteEndPoint}");
            _ = Task.Run(() => ControlClientLoopAsync(client, ct));
        }
    }

    private async Task ControlClientLoopAsync(TcpClient client, CancellationToken ct)
    {
        var rx = new StringBuilder();
        var buffer = new byte[1024];
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (n <= 0) break;

                    string text = Encoding.Latin1.GetString(buffer, 0, n);
                    _log?.Invoke($"[sim-tcp] RX({n}) '{Printable(text)}'");
                    rx.Append(text);

                    while (TryConsumeCommand(rx, out string command, out string argument))
                    {
                        switch (command)
                        {
                            case YocamProtocol.HeartbeatCommand:
                                Interlocked.Increment(ref _heartbeats);
                                await SendAsync(stream, YocamProtocol.HeartbeatAck, ct).ConfigureAwait(false);
                                break;

                            case YocamProtocol.RtspStartCommand:
                                Interlocked.Increment(ref _rtspStarts);
                                if (_options.FragmentedRtspAck)
                                {
                                    // 故意拆包：验证内核“累积缓冲 + 全量扫描”能识别跨 TCP 段的关键字
                                    await SendAsync(stream, "ACK_RTSP_TRAN", ct).ConfigureAwait(false);
                                    await Task.Delay(20, ct).ConfigureAwait(false);
                                    await SendAsync(stream, "S_START", ct).ConfigureAwait(false);
                                    _log?.Invoke("[sim-tcp] TX 'ACK_RTSP_TRANS_START'（拆成 2 个 TCP 段）");
                                }
                                else
                                {
                                    await SendAsync(stream, YocamProtocol.RtspStartAck, ct).ConfigureAwait(false);
                                }
                                break;

                            case YocamProtocol.LiveStopCommand:
                                Interlocked.Increment(ref _liveStops);
                                _log?.Invoke($"[sim-tcp] CMD_LIVE_STOP 参数 = {argument}（仿真器不真正待机）");
                                await SendAsync(stream, YocamProtocol.LiveStopAck, ct).ConfigureAwait(false);
                                break;

                            case YocamProtocol.WifiStaCommand:
                                Interlocked.Increment(ref _wifiSta);
                                await SendAsync(stream, YocamProtocol.WifiStaAck, ct).ConfigureAwait(false);
                                break;

                            case YocamProtocol.SetWifiCommand:
                                await SendAsync(stream, YocamProtocol.SetWifiAck, ct).ConfigureAwait(false);
                                break;

                            default:
                                _log?.Invoke($"[sim-tcp] 未知命令：{command}");
                                break;
                        }
                    }

                    if (rx.Length > 8192) rx.Remove(0, rx.Length - 64);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
        catch (Exception ex)
        {
            _log?.Invoke($"[sim-tcp] 客户端异常：{ex.Message}");
        }
        finally
        {
            lock (_clientGate) _controlClients.Remove(client);
            _log?.Invoke("[sim-tcp] 控制客户端断开");
        }
    }

    /// <summary>设备端视角的命令扫描（客户端命令都是已知命令字面量）。</summary>
    private static bool TryConsumeCommand(StringBuilder rx, out string command, out string argument)
    {
        command = string.Empty;
        argument = string.Empty;
        string s = rx.ToString();
        if (s.Length == 0) return false;

        string[] commands =
        {
            YocamProtocol.RtspStartCommand,
            YocamProtocol.HeartbeatCommand,
            YocamProtocol.LiveStopCommand,
            YocamProtocol.WifiStaCommand,
            YocamProtocol.SetWifiCommand,
        };

        int bestIdx = -1;
        string? best = null;
        foreach (string c in commands)
        {
            int idx = s.IndexOf(c, StringComparison.Ordinal);
            if (idx < 0) continue;
            if (bestIdx < 0 || idx < bestIdx || (idx == bestIdx && c.Length > best!.Length))
            {
                bestIdx = idx;
                best = c;
            }
        }
        if (best == null) return false;

        int end = bestIdx + best.Length;
        if (best == YocamProtocol.LiveStopCommand)
        {
            int p = end;
            while (p < s.Length && s[p] is >= '0' and <= '9') p++;
            argument = s[end..p];
            end = p;
        }
        else if (best == YocamProtocol.SetWifiCommand)
        {
            argument = s[end..];
            end = s.Length;
        }

        rx.Remove(0, end);
        command = best;
        return true;
    }

    private async Task SendAsync(NetworkStream stream, string ascii, CancellationToken ct)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(ascii);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        _log?.Invoke($"[sim-tcp] TX '{Printable(ascii)}'");
    }

    /// <summary>模拟设备侧掉线：断开所有 TCP 8888 控制连接（重连测试用）。</summary>
    public void DropControlClients()
    {
        TcpClient[] clients;
        lock (_clientGate)
        {
            clients = _controlClients.ToArray();
            _controlClients.Clear();
        }
        foreach (var c in clients)
        {
            try { c.Client.Close(); } catch { }
            try { c.Close(); } catch { }
        }
        _log?.Invoke($"[sim-tcp] 已强制断开 {clients.Length} 个控制连接");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Stop(); } catch { }
        DropControlClients();

        foreach (Task? t in new[] { _udpTask, _tcpTask })
        {
            if (t == null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        await RtspServer.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private static string Printable(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is >= ' ' and <= '~' ? c : '.');
        return sb.ToString();
    }
}
