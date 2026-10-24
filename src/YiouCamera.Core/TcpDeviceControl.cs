using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YiouCamera.Core;

/// <summary>TCP 8888 控制通道参数。</summary>
public sealed class TcpControlOptions
{
    /// <summary>本机绑定网卡 IP（协议 §13.2 多网卡必绑）；null = 自动选择。</summary>
    public IPAddress? LocalBindAddress { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>连续多少次心跳无应答判掉线（厂商 UnRecHBCount==5，协议 §4.3）。</summary>
    public int HeartbeatMissLimit { get; set; } = 5;

    /// <summary>单次“发命令等 ACK”窗口（厂商是 100×10ms=1s 忙等，这里事件驱动等价超时）。</summary>
    public TimeSpan RtspStartAttemptTimeout { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>两次开流尝试间隔（厂商失败后 Sleep(1000) 重试）。</summary>
    public TimeSpan RtspStartRetryInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>开流最多尝试次数（厂商 60 次）。</summary>
    public int RtspStartMaxAttempts { get; set; } = 60;

    /// <summary>ACK 全量扫描缓冲上限，超过后只保留尾部（防御垃圾数据）。</summary>
    public int MaxReceiveBacklog { get; set; } = 8192;

    public Action<string>? Log { get; set; }
}

/// <summary>
/// TCP 8888 控制通道（协议 §4）。
/// 关键点：**无握手、无长度前缀、无分隔符**。厂商用 Contains 直接匹配单次 Receive，
/// 有粘包/拆包缺陷；本实现做「累积缓冲 + 按出现位置全量扫描消费」，两条 ACK 粘在一起或
/// 一条 ACK 被拆成两次 TCP 段都能正确识别。
/// </summary>
public sealed class TcpDeviceControl : IDeviceControl
{
    private readonly TcpControlOptions _options;
    private readonly Action<string>? _log;
    private readonly object _waiterGate = new();
    private readonly List<PendingWait> _waiters = new();
    private readonly StringBuilder _rx = new(4096);

    private Socket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private int _connected;      // 0/1
    private int _disconnectOnce; // 0/1
    private int _disposed;
    private int _heartbeatAck;   // 0/1（仅诊断用）
    private long _heartbeatAckCount; // 收到的 DATA_SYNC 累计数（心跳循环用它判定本周期是否有应答）
    private long _lastAckTick;   // Environment.TickCount64
    private DeviceInfo? _device;
    private string? _closeReason;

    public TcpDeviceControl(TcpControlOptions? options = null, Action<string>? log = null)
    {
        _options = options ?? new TcpControlOptions();
        _log = log ?? _options.Log;
    }

    /// <summary>收到一条已识别关键字（回包/通知）。在接收线程/线程池触发。</summary>
    public event Action<string>? PacketReceived;

    /// <summary>链路故障（异常/心跳超时/接收线程结束）。</summary>
    public event Action<string>? Faulted;

    /// <summary>断开（含主动 Close 与故障）。</summary>
    public event Action? Disconnected;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public bool HeartbeatAlive
    {
        get
        {
            if (!IsConnected) return false;
            long limitMs = (long)(_options.HeartbeatInterval.TotalMilliseconds * _options.HeartbeatMissLimit);
            return Environment.TickCount64 - Interlocked.Read(ref _lastAckTick) <= limitMs;
        }
    }

    public DeviceInfo? Device => _device;

    public string? CloseReason => _closeReason;

    public async Task<bool> ConnectAsync(DeviceInfo dev, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dev);
        if (string.IsNullOrWhiteSpace(dev.DeviceIp))
        {
            Fail("设备 IP 为空，无法建立控制通道");
            return false;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await CloseAsync(raiseFaulted: false).ConfigureAwait(false);

        IPAddress localBind = _options.LocalBindAddress ?? LocalNetwork.PickPreferredLocalAddress(_log);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true, // 命令都很短，关 Nagle
        };

        try
        {
            if (!localBind.Equals(IPAddress.Any))
                socket.Bind(new IPEndPoint(localBind, 0));

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            _log?.Invoke($"[control] 连接 {dev.DeviceIp}:{YocamProtocol.ControlPort}（bind={localBind}）…");
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(dev.DeviceIp), YocamProtocol.ControlPort), connectCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            Fail($"连接 {dev.DeviceIp}:{YocamProtocol.ControlPort} 超时（{_options.ConnectTimeout.TotalSeconds:0.#}s）");
            return false;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            Fail($"连接 {dev.DeviceIp}:{YocamProtocol.ControlPort} 失败：{ex.Message}");
            return false;
        }

        _socket = socket;
        _device = dev;
        _closeReason = null;
        _rx.Clear();
        Interlocked.Exchange(ref _disconnectOnce, 0);
        Interlocked.Exchange(ref _heartbeatAck, 0);
        Interlocked.Exchange(ref _heartbeatAckCount, 0L);
        Interlocked.Exchange(ref _lastAckTick, Environment.TickCount64);
        Volatile.Write(ref _connected, 1);
        _loopCts = new CancellationTokenSource();

        // 协议 §4.3：连上即开始 1s 心跳；没有握手包
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_loopCts.Token));
        _log?.Invoke($"[control] 已连接（无握手，直接可用）");
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 每次尝试：发送 CMD_RTSP_TRANS_START → 等 ACK_RTSP_TRANS_START（窗口 1s，等价厂商 100×10ms）；
    /// 失败后间隔 1s 重试，最多 60 次或直到 <paramref name="timeout"/> 预算耗尽。
    /// </remarks>
    public async Task<bool> StartRtspStreamAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected)
        {
            _log?.Invoke("[control] StartRtspStreamAsync 失败：控制通道未连接");
            return false;
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        int attempt = 0;
        while (IsConnected && attempt < _options.RtspStartMaxAttempts && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            Task<bool> waitTask = WaitForReplyAsync(YocamProtocol.RtspStartAck, _options.RtspStartAttemptTimeout, ct);
            if (!Send(YocamProtocol.RtspStartCommand))
                return false;

            if (await waitTask.ConfigureAwait(false))
            {
                _log?.Invoke($"[control] 收到 {YocamProtocol.RtspStartAck}（第 {attempt} 次尝试），RTSP 流应已就绪");
                return true;
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            TimeSpan delay = remaining < _options.RtspStartRetryInterval ? remaining : _options.RtspStartRetryInterval;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                break;
            }
        }

        _log?.Invoke($"[control] 开流 ACK 超时/失败（尝试 {attempt} 次，预算 {timeout.TotalSeconds:0.#}s）");
        return false;
    }

    public async Task StopLiveAsync(int minutes)
    {
        if (!IsConnected) return;
        string command = YocamProtocol.BuildLiveStopCommand(minutes);
        Task<bool> waitTask = WaitForReplyAsync(YocamProtocol.LiveStopAck, TimeSpan.FromMilliseconds(1000), CancellationToken.None);
        if (!Send(command)) return;
        bool ok = await waitTask.ConfigureAwait(false);
        _log?.Invoke(ok
            ? $"[control] {command} → {YocamProtocol.LiveStopAck}"
            : $"[control] {command} 已发送（未等到 ACK，厂商实现同样忽略该回包）");
    }

    /// <summary>回普通 STA 模式（协议 §4.2 #5）。</summary>
    public async Task<bool> SendWifiStaAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected) return false;
        Task<bool> waitTask = WaitForReplyAsync(YocamProtocol.WifiStaAck, timeout, ct);
        if (!Send(YocamProtocol.WifiStaCommand)) return false;
        return await waitTask.ConfigureAwait(false);
    }

    /// <summary>通用“发 ASCII 命令 + 等包含某关键字回包”接口（如 CMD_SETWIFI…，v1 不暴露 SSID 编码）。</summary>
    public async Task<bool> SendCommandAsync(string command, string expectKeyword, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected) return false;
        Task<bool> waitTask = WaitForReplyAsync(expectKeyword, timeout, ct);
        if (!Send(command)) return false;
        return await waitTask.ConfigureAwait(false);
    }

    /// <summary>发送一条 ASCII 命令（不带换行/长度前缀）。返回是否成功写入 socket。</summary>
    public bool Send(string command)
    {
        var socket = _socket;
        if (socket == null || !IsConnected || string.IsNullOrEmpty(command)) return false;
        try
        {
            byte[] bytes = YocamProtocol.WireEncoding.GetBytes(command);
            socket.Send(bytes, SocketFlags.None);
            _log?.Invoke($"[control] TX({bytes.Length}) {Printable(command)}");
            return true;
        }
        catch (Exception ex)
        {
            Fail($"发送 {Printable(command)} 失败：{ex.Message}");
            return false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024]; // 与厂商一致的接收缓冲
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket = _socket!;
                int n = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    RequestClose("对端关闭连接（Receive=0）", raiseFaulted: true);
                    return;
                }

                string text = YocamProtocol.WireEncoding.GetString(buffer, 0, n);
                _log?.Invoke($"[control] RX({n}) {Printable(text)}");

                lock (_rx)
                {
                    _rx.Append(text);
                    if (_rx.Length > _options.MaxReceiveBacklog)
                        _rx.Remove(0, _rx.Length - 64);

                    while (TryConsumeReply(out string keyword))
                    {
                        if (keyword == YocamProtocol.HeartbeatAck)
                        {
                            Interlocked.Exchange(ref _heartbeatAck, 1);
                            Interlocked.Increment(ref _heartbeatAckCount);
                            Interlocked.Exchange(ref _lastAckTick, Environment.TickCount64);
                        }
                        DispatchReply(keyword);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            if (IsConnected) RequestClose($"接收线程异常：{ex.Message}", raiseFaulted: true);
        }
    }

    /// <summary>
    /// 从累积缓冲中消费「最早出现」的一条已知回包（而不是厂商的 if/else 首命中）。
    /// 返回 false 表示当前缓冲还拼不出完整关键字（拆包）→ 保留等待后续字节。
    /// </summary>
    private bool TryConsumeReply(out string keyword)
    {
        keyword = string.Empty;
        string s = _rx.ToString();
        if (s.Length < 8) return false; // 最短关键字 DATA_SYNC = 9，留一点余量

        int bestIdx = -1;
        string? best = null;
        foreach (string kw in YocamProtocol.KnownReplies)
        {
            int idx = s.IndexOf(kw, StringComparison.Ordinal);
            if (idx < 0) continue;
            if (bestIdx < 0 || idx < bestIdx || (idx == bestIdx && kw.Length > best!.Length))
            {
                bestIdx = idx;
                best = kw;
            }
        }
        if (best == null) return false;

        _rx.Remove(0, bestIdx + best.Length);
        keyword = best;
        return true;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        int missed = 0;
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                long acksBefore = Interlocked.Read(ref _heartbeatAckCount);
                if (!Send(YocamProtocol.HeartbeatCommand))
                    return;

                await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);

                // 用“本周期内 DATA_SYNC 计数是否增长”判定应答，避免同 TickCount 粒度导致的误判
                if (Interlocked.Read(ref _heartbeatAckCount) > acksBefore)
                {
                    missed = 0;
                }
                else
                {
                    missed++;
                    _log?.Invoke($"[control] 心跳无应答 {missed}/{_options.HeartbeatMissLimit}");
                    if (missed >= _options.HeartbeatMissLimit)
                    {
                        RequestClose($"心跳连续 {missed} 次无应答，判定掉线", raiseFaulted: true);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsConnected) RequestClose($"心跳线程异常：{ex.Message}", raiseFaulted: true);
        }
    }

    private Task<bool> WaitForReplyAsync(string keyword, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new PendingWait(keyword, tcs);
        lock (_waiterGate) _waiters.Add(wait);
        return AwaitAsync();

        async Task<bool> AwaitAsync()
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                using var reg = cts.Token.Register(static state =>
                    ((TaskCompletionSource<bool>)state!).TrySetResult(false), tcs);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_waiterGate) _waiters.Remove(wait);
            }
        }
    }

    private void DispatchReply(string keyword)
    {
        // 先完成等待者，再触发外部事件，避免处理器重入造成的时序问题
        PendingWait[] matching;
        lock (_waiterGate)
        {
            int count = 0;
            for (int i = 0; i < _waiters.Count; i++)
                if (_waiters[i].Keyword == keyword) count++;
            matching = new PendingWait[count];
            int j = 0;
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Keyword != keyword) continue;
                matching[j++] = _waiters[i];
                _waiters.RemoveAt(i);
            }
        }
        foreach (var w in matching) w.Tcs.TrySetResult(true);

        try { PacketReceived?.Invoke(keyword); }
        catch (Exception ex) { _log?.Invoke($"[control] PacketReceived 处理器异常：{ex}"); }
    }

    private void RequestClose(string reason, bool raiseFaulted)
    {
        if (Interlocked.Exchange(ref _disconnectOnce, 1) == 1) return;
        _closeReason = reason;
        bool wasConnected = Volatile.Read(ref _connected) == 1;
        Volatile.Write(ref _connected, 0);

        try { _loopCts?.Cancel(); } catch { }
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket?.Close(); } catch { }

        if (wasConnected) _log?.Invoke($"[control] 断开：{reason}");
        FaultPendingWaiters();

        if (!raiseFaulted) return;
        try { Faulted?.Invoke(reason); }
        catch (Exception ex) { _log?.Invoke($"[control] Faulted 处理器异常：{ex}"); }
        try { Disconnected?.Invoke(); }
        catch (Exception ex) { _log?.Invoke($"[control] Disconnected 处理器异常：{ex}"); }
    }

    private void Fail(string reason) => RequestClose(reason, raiseFaulted: true);

    private void FaultPendingWaiters()
    {
        PendingWait[] all;
        lock (_waiterGate)
        {
            all = _waiters.ToArray();
            _waiters.Clear();
        }
        foreach (var w in all) w.Tcs.TrySetResult(false);
    }

    /// <summary>主动断开（不触发 Faulted）。Stop/重连前调用。</summary>
    private async Task CloseAsync(bool raiseFaulted)
    {
        RequestClose("主动断开", raiseFaulted);
        Task? rx = _receiveTask, hb = _heartbeatTask;
        _receiveTask = _heartbeatTask = null;

        foreach (Task? t in new[] { rx, hb })
        {
            if (t == null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { _log?.Invoke("[control] 等待后台线程退出超时（忽略）"); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        _socket?.Dispose();
        _socket = null;
        _loopCts?.Dispose();
        _loopCts = null;
        _rx.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await CloseAsync(raiseFaulted: false).ConfigureAwait(false);
        _device = null;
    }

    private static string Printable(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is >= ' ' and <= '~' ? c : '.');
        return sb.ToString();
    }

    private readonly record struct PendingWait(string Keyword, TaskCompletionSource<bool> Tcs);
}
