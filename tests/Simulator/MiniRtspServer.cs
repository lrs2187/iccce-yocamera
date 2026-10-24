using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace YiouCamera.Simulator;

/// <summary>
/// 最小 RTSP 服务端（仅用于无真机闭环验证）：OPTIONS/DESCRIBE/SETUP/PLAY/TEARDOWN，
/// 视频为 MJPEG，RTP over UDP（payload type 26，RFC 2435 8 字节 JPEG 头 + 数据）。
///
/// 注意：真机是 H.264/1080p，本服务只是为了验证「控制通道开流 ACK → RTSP 拉流 → ffmpeg 解码」
/// 这条代码路径，不模拟真实编码格式。
/// </summary>
public sealed class MiniRtspServer : IAsyncDisposable
{
    private const int RtpPayloadType = 26; // JPEG
    private const int MaxRtpPayload = 1300;
    private const int Fps = 30;
    private const int RtpClock = 90_000;

    private readonly JpegFrameProvider _frames;
    private readonly int _port;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<RtspSession> _sessions = new();
    private readonly object _sessionGate = new();

    private TcpListener? _listener;
    private Task? _acceptTask;
    private int _playCount;
    private int _describeCount;

    public MiniRtspServer(JpegFrameProvider frames, int port, Action<string>? log)
    {
        _frames = frames;
        _port = port;
        _log = log;
    }

    public int Port => _port;
    public int DescribeCount => Volatile.Read(ref _describeCount);
    public int PlayCount => Volatile.Read(ref _playCount);

    public Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(8);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log?.Invoke($"[sim-rtsp] 监听 rtsp://0.0.0.0:{_port}/stream0（MJPEG/RTP-UDP，测试源）");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _log?.Invoke($"[sim-rtsp] accept 失败：{ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            client.NoDelay = true;
            var session = new RtspSession(this, client);
            lock (_sessionGate) _sessions.Add(session);
            _ = Task.Run(() => session.RunAsync(ct));
        }
    }

    internal void RemoveSession(RtspSession session)
    {
        lock (_sessionGate) _sessions.Remove(session);
    }

    internal JpegFrameProvider Frames => _frames;
    internal Action<string>? Log => _log;

    internal void OnDescribe() => Interlocked.Increment(ref _describeCount);
    internal void OnPlay() => Interlocked.Increment(ref _playCount);

    /// <summary>模拟设备侧断流：关闭所有 RTSP 会话（重连测试用）。</summary>
    public void DropSessions()
    {
        RtspSession[] sessions;
        lock (_sessionGate) sessions = _sessions.ToArray();
        foreach (var s in sessions)
        {
            try { s.Dispose(); } catch { }
        }
        _log?.Invoke($"[sim-rtsp] 已强制断开 {sessions.Length} 个 RTSP 会话");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        RtspSession[] sessions;
        lock (_sessionGate) sessions = _sessions.ToArray();
        foreach (var s in sessions)
        {
            try { s.Dispose(); } catch { }
        }
        if (_acceptTask != null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }

    // ------------------------------------------------------------------
    // 单个 RTSP 连接
    // ------------------------------------------------------------------
    internal sealed class RtspSession : IDisposable
    {
        private readonly MiniRtspServer _server;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationTokenSource _sessionCts = new();
        private readonly string _sessionId = Random.Shared.Next(10000000, 99999999).ToString();
        private readonly string _contentBase;
        private readonly IPAddress _localIp;

        private UdpClient? _rtpSocket;
        private UdpClient? _rtcpSocket;
        private IPEndPoint? _clientRtp;
        private int _serverRtpPort;
        private int _serverRtcpPort;
        private uint _ssrc;
        private int _seq;
        private uint _rtpTimestamp;
        private int _frameIndex;
        private Task? _sender;
        private int _disposed;

        // RTP over TCP（interleaved）：ffmpeg 的 TCP 回退路径
        private bool _interleaved;
        private int _rtpChannel;
        private int _rtcpChannel;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        private readonly byte[] _readBuffer = new byte[8192];
        private int _readLen;

        public RtspSession(MiniRtspServer server, TcpClient client)
        {
            _server = server;
            _client = client;
            _stream = client.GetStream();
            var local = (IPEndPoint)client.Client.LocalEndPoint!;
            _localIp = local.Address;
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            _contentBase = $"rtsp://{_localIp}:{server.Port}/stream0/";
            _ssrc = (uint)Random.Shared.Next();
            _seq = Random.Shared.Next(0, 65535);
            _log($"[sim-rtsp] 新连接 {remote}");
        }

        private void _log(string msg) => _server.Log?.Invoke(msg);

        public async Task RunAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    string? request = await ReadRequestAsync(linked.Token).ConfigureAwait(false);
                    if (request == null) break;

                    string[] lines = request.Split("\r\n", StringSplitOptions.None);
                    string[] first = lines[0].Split(' ', 3);
                    if (first.Length < 3) continue;
                    string method = first[0].ToUpperInvariant();
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon <= 0) continue;
                        headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                    }
                    int cseq = headers.TryGetValue("CSeq", out var cseqText) && int.TryParse(cseqText, out int v) ? v : 0;

                    switch (method)
                    {
                        case "OPTIONS":
                            await WriteResponseAsync(cseq, "200 OK", new()
                            {
                                ["Public"] = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER",
                            }, null, linked.Token).ConfigureAwait(false);
                            break;

                        case "DESCRIBE":
                            _server.OnDescribe();
                            _log($"[sim-rtsp] DESCRIBE {first[1]}");
                            byte[] sdp = Encoding.ASCII.GetBytes(BuildSdp());
                            await WriteResponseAsync(cseq, "200 OK", new()
                            {
                                ["Content-Type"] = "application/sdp",
                                ["Content-Base"] = _contentBase,
                            }, sdp, linked.Token).ConfigureAwait(false);
                            break;

                        case "SETUP":
                            await HandleSetupAsync(cseq, headers, linked.Token).ConfigureAwait(false);
                            break;

                        case "PLAY":
                            _server.OnPlay();
                            _log($"[sim-rtsp] PLAY {first[1]}");
                            await WriteResponseAsync(cseq, "200 OK", new()
                            {
                                ["Session"] = $"{_sessionId};timeout=60",
                                ["Range"] = "npt=0.000-",
                                ["RTP-Info"] = $"url={_contentBase}trackID=0;seq={_seq};rtptime={_rtpTimestamp}",
                            }, null, linked.Token).ConfigureAwait(false);
                            StartSender();
                            break;

                        case "PAUSE":
                            await WriteResponseAsync(cseq, "200 OK", new() { ["Session"] = _sessionId }, null, linked.Token).ConfigureAwait(false);
                            break;

                        case "TEARDOWN":
                            await WriteResponseAsync(cseq, "200 OK", new() { ["Session"] = _sessionId }, null, linked.Token).ConfigureAwait(false);
                            return;

                        case "GET_PARAMETER":
                        case "SET_PARAMETER":
                            await WriteResponseAsync(cseq, "200 OK", new() { ["Session"] = _sessionId }, null, linked.Token).ConfigureAwait(false);
                            break;

                        default:
                            await WriteResponseAsync(cseq, "501 Not Implemented", null, null, linked.Token).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                _log($"[sim-rtsp] 会话异常：{ex.Message}");
            }
            finally
            {
                Dispose();
            }
        }

        private async Task HandleSetupAsync(int cseq, Dictionary<string, string> headers, CancellationToken ct)
        {
            string transport = headers.TryGetValue("Transport", out var t) ? t : string.Empty;

            // RTP over TCP（interleaved）：与真机一致，ffmpeg -rtsp_transport tcp 的 SETUP 形态
            Match il = Regex.Match(transport, @"interleaved=(\d+)(?:-(\d+))?");
            if (il.Success)
            {
                _interleaved = true;
                _rtpChannel = int.Parse(il.Groups[1].Value);
                _rtcpChannel = il.Groups[2].Success ? int.Parse(il.Groups[2].Value) : _rtpChannel + 1;
                _log($"[sim-rtsp] SETUP interleaved={_rtpChannel}-{_rtcpChannel}（RTP over TCP）");
                await WriteResponseAsync(cseq, "200 OK", new()
                {
                    ["Transport"] = $"RTP/AVP/TCP;unicast;interleaved={_rtpChannel}-{_rtcpChannel};ssrc={_ssrc:X8}",
                    ["Session"] = $"{_sessionId};timeout=60",
                }, null, ct).ConfigureAwait(false);
                return;
            }

            Match m = Regex.Match(transport, @"client_port=(\d+)(?:-(\d+))?");
            if (!m.Success)
            {
                _log($"[sim-rtsp] SETUP 不支持的 transport：{transport}");
                await WriteResponseAsync(cseq, "461 Unsupported Transport", new() { ["Session"] = _sessionId }, null, ct).ConfigureAwait(false);
                return;
            }

            int clientRtp = int.Parse(m.Groups[1].Value);
            int clientRtcp = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : clientRtp + 1;
            IPAddress remote = ((IPEndPoint)_client.Client.RemoteEndPoint!).Address;
            _clientRtp = new IPEndPoint(remote, clientRtp);

            (_rtpSocket, _serverRtpPort) = BindUdp();
            (_rtcpSocket, _serverRtcpPort) = BindUdp(_serverRtpPort + 1);
            _ = DrainRtcpAsync();

            _log($"[sim-rtsp] SETUP client_port={clientRtp}-{clientRtcp} server_port={_serverRtpPort}-{_serverRtcpPort} ssrc={_ssrc:X8}");
            await WriteResponseAsync(cseq, "200 OK", new()
            {
                ["Transport"] = $"RTP/AVP;unicast;client_port={clientRtp}-{clientRtcp};server_port={_serverRtpPort}-{_serverRtcpPort};ssrc={_ssrc:X8}",
                ["Session"] = $"{_sessionId};timeout=60",
            }, null, ct).ConfigureAwait(false);
        }

        private static (UdpClient Socket, int Port) BindUdp(int? preferredPort = null)
        {
            if (preferredPort is > 0)
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(IPAddress.Any, preferredPort.Value));
                    return (socket, preferredPort.Value);
                }
                catch (SocketException)
                {
                    // 端口被占，退回随机端口
                }
            }
            var any = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            return (any, ((IPEndPoint)any.Client.LocalEndPoint!).Port);
        }

        private async Task DrainRtcpAsync()
        {
            try
            {
                while (!_sessionCts.IsCancellationRequested)
                {
                    if (_rtcpSocket == null) return;
                    await _rtcpSocket.ReceiveAsync(_sessionCts.Token).ConfigureAwait(false);
                }
            }
            catch { /* RTCP 可忽略 */ }
        }

        private void StartSender()
        {
            if (_sender != null) return;
            if (!_interleaved && (_clientRtp == null || _rtpSocket == null)) return;
            _sender = Task.Run(() => SenderLoopAsync(_sessionCts.Token));
        }

        private async Task SenderLoopAsync(CancellationToken ct)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    double targetMs = _frameIndex * (1000.0 / Fps);
                    double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsedMs < targetMs)
                        await Task.Delay(TimeSpan.FromMilliseconds(targetMs - elapsedMs), ct).ConfigureAwait(false);

                    MjpegFrame frame = _server.Frames[_frameIndex];
                    await SendJpegFrameAsync(frame, ct).ConfigureAwait(false);
                    _frameIndex++;
                    _rtpTimestamp += (uint)(RtpClock / Fps);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log($"[sim-rtsp] RTP 发送异常：{ex.Message}");
            }
        }

        private async Task SendJpegFrameAsync(MjpegFrame frame, CancellationToken ct)
        {
            if (!_interleaved && (_clientRtp == null || _rtpSocket == null)) return;
            byte[] scan = frame.ScanData;
            byte[] qtables = frame.QTables;
            int widthBlocks = _server.Frames.Width / 8;
            int heightBlocks = _server.Frames.Height / 8;
            int scanOffset = 0;
            bool first = true;

            do
            {
                int extraHeader = first ? 4 + qtables.Length : 0;
                int maxChunk = 1400 - 12 - 8 - extraHeader; // 保证 UDP 包 < 1400B
                int chunk = Math.Min(Math.Min(MaxRtpPayload, maxChunk), Math.Max(0, scan.Length - scanOffset));
                bool last = scanOffset + chunk >= scan.Length;
                int payloadLen = 8 + extraHeader + chunk;
                byte[] packet = new byte[12 + payloadLen];

                packet[0] = 0x80; // V=2
                packet[1] = (byte)((last ? 0x80 : 0) | RtpPayloadType);
                packet[2] = (byte)(_seq >> 8);
                packet[3] = (byte)_seq;
                packet[4] = (byte)(_rtpTimestamp >> 24);
                packet[5] = (byte)(_rtpTimestamp >> 16);
                packet[6] = (byte)(_rtpTimestamp >> 8);
                packet[7] = (byte)_rtpTimestamp;
                packet[8] = (byte)(_ssrc >> 24);
                packet[9] = (byte)(_ssrc >> 16);
                packet[10] = (byte)(_ssrc >> 8);
                packet[11] = (byte)_ssrc;

                // RFC 2435 主头：type-specific + 24-bit 扫描数据偏移 + type + Q + w/8 + h/8
                packet[12] = 0;
                packet[13] = (byte)(scanOffset >> 16);
                packet[14] = (byte)(scanOffset >> 8);
                packet[15] = (byte)scanOffset;
                packet[16] = (byte)frame.Type;
                packet[17] = 255; // 显式量化表
                packet[18] = (byte)widthBlocks;
                packet[19] = (byte)heightBlocks;

                int p = 20;
                if (first)
                {
                    // 量化表子头：reserved + precision(0=8bit) + 16bit length + tables
                    packet[p++] = 0;
                    packet[p++] = 0;
                    packet[p++] = (byte)(qtables.Length >> 8);
                    packet[p++] = (byte)qtables.Length;
                    Buffer.BlockCopy(qtables, 0, packet, p, qtables.Length);
                    p += qtables.Length;
                }
                if (chunk > 0)
                    Buffer.BlockCopy(scan, scanOffset, packet, p, chunk);

                try
                {
                    if (_interleaved)
                        await WriteInterleavedAsync(_rtpChannel, packet, ct).ConfigureAwait(false);
                    else
                        await _rtpSocket!.SendAsync(packet, _clientRtp!, ct).ConfigureAwait(false);
                }
                catch (SocketException) { return; }
                catch (OperationCanceledException) { return; }
                catch (IOException) { return; }

                scanOffset += chunk;
                first = false;
                _seq = (_seq + 1) & 0xFFFF;

                if (last) break;
            } while (scanOffset < scan.Length);
        }

        /// <summary>RTP over TCP：RTSP 标准 `$` 交织帧（0x24 + channel + 16bit length + payload）。</summary>
        private async Task WriteInterleavedAsync(int channel, byte[] payload, CancellationToken ct)
        {
            var framed = new byte[4 + payload.Length];
            framed[0] = 0x24;
            framed[1] = (byte)channel;
            framed[2] = (byte)(payload.Length >> 8);
            framed[3] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, framed, 4, payload.Length);

            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(framed, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private string BuildSdp()
        {
            return string.Join("\r\n", new[]
            {
                "v=0",
                $"o=- 0 0 IN IP4 {_localIp}",
                "s=YiouCamera Simulator",
                $"c=IN IP4 {_localIp}",
                "t=0 0",
                "a=control:*",
                $"m=video 0 RTP/AVP {RtpPayloadType}",
                $"a=rtpmap:{RtpPayloadType} JPEG/90000",
                $"a=framerate:{Fps}",
                "a=control:trackID=0",
                "",
            });
        }

        private async Task WriteResponseAsync(int cseq, string status, Dictionary<string, string>? headers, byte[]? body, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append("RTSP/1.0 ").Append(status).Append("\r\n");
            sb.Append("CSeq: ").Append(cseq).Append("\r\n");
            sb.Append("Server: YiouCameraSimulator/1.0\r\n");
            if (headers != null)
                foreach (var kv in headers)
                    sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (body != null)
                sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(head, ct).ConfigureAwait(false);
                if (body != null)
                    await _stream.WriteAsync(body, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task<string?> ReadRequestAsync(CancellationToken ct)
        {
            while (true)
            {
                int end = FindHeaderEnd(_readBuffer, _readLen);
                if (end >= 0)
                {
                    string head = Encoding.Latin1.GetString(_readBuffer, 0, end + 4);
                    int contentLength = ParseContentLength(head);
                    int total = end + 4 + contentLength;
                    while (_readLen < total)
                    {
                        if (_readLen == _readBuffer.Length) break; // 请求体过大：不处理
                        int more = await _stream.ReadAsync(_readBuffer.AsMemory(_readLen), ct).ConfigureAwait(false);
                        if (more <= 0) return null;
                        _readLen += more;
                    }
                    if (_readLen < total) return null;
                    Buffer.BlockCopy(_readBuffer, total, _readBuffer, 0, _readLen - total);
                    _readLen -= total;
                    return head;
                }

                if (_readLen == _readBuffer.Length) return null;
                int n = await _stream.ReadAsync(_readBuffer.AsMemory(_readLen), ct).ConfigureAwait(false);
                if (n <= 0) return null;
                _readLen += n;
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i;
            return -1;
        }

        private static int ParseContentLength(string head)
        {
            foreach (string line in head.Split("\r\n"))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                string value = line["Content-Length:".Length..].Trim();
                return int.TryParse(value, out int n) ? n : 0;
            }
            return 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _sessionCts.Cancel(); } catch { }
            try { _rtpSocket?.Dispose(); } catch { }
            try { _rtcpSocket?.Dispose(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
            _server.RemoveSession(this);
        }
    }
}
