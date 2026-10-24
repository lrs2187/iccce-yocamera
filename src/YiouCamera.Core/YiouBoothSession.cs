using System.Net;

namespace YiouCamera.Core;

/// <summary>展台会话状态。</summary>
public enum BoothSessionState
{
    Idle,
    Discovering,
    Connecting,
    StartingStream,
    Running,
    Reconnecting,
    Faulted,
    Stopped,
}

/// <summary>会话配置（插件设置页可直接绑定这些属性）。</summary>
public sealed class YiouBoothSessionOptions
{
    /// <summary>8 位配对码；null 时自动读 YOCamSet.xml，读不到随机（见 <see cref="YocamPinCode.Resolve"/>）。</summary>
    public string? PinCode { get; set; }

    /// <summary>本机网卡 bind IP（多网卡/VPN 必设）；null = 自动挑选非虚拟网卡。</summary>
    public IPAddress? LocalBindAddress { get; set; }

    /// <summary>已知设备 IP 时跳过 UDP 发现直接连（默认 null = 走发现）。</summary>
    public IPAddress? DeviceIpOverride { get; set; }

    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int TargetFps { get; set; } = 30;

    /// <summary>ffmpeg.exe 路径；空 = 自动解析（见 <see cref="FfmpegProcessFrameSource.ResolveFfmpegPath"/>）。</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>RTSP UDP 失败/断流后回退 TCP。</summary>
    public bool EnableTcpFallback { get; set; } = true;

    /// <summary>是否由会话创建并写入 <see cref="SharedMemoryName"/>（插件不需要自己碰 SHM）。</summary>
    public bool UseSharedMemory { get; set; } = true;

    public string SharedMemoryName { get; set; } = VcamShmLayout.DefaultName;

    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ControlConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RtspStartTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>无视频帧时占位帧刷新周期（契约 §2：不允许停止更新）。</summary>
    public TimeSpan PlaceholderInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>会话层“多久没有解码帧就认为链路坏了、重连”。</summary>
    public TimeSpan FrameStarvationTimeout { get; set; } = TimeSpan.FromSeconds(25);

    public Action<string>? Log { get; set; }
}

/// <summary>
/// 展台会话编排：UDP 发现 → TCP 8888 心跳 → CMD_RTSP_TRANS_START/ACK → ffmpeg 拉流解码 →
/// 写共享内存；控制通道掉线/长时间无帧自动重连（1s/3s/5s 退避）。
/// 所有异常都走 <see cref="State"/> + <see cref="Faulted"/> 事件，不抛给 UI 线程。
/// </summary>
public sealed class YiouBoothSession : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5) };

    private readonly YiouBoothSessionOptions _options;
    private readonly Action<string>? _log;
    private readonly string _pinCode;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private Task? _placeholderLoop;
    private SharedMemoryFrameSink? _sink;
    private TaskCompletionSource<bool>? _runningSignal;

    private int _state = (int)BoothSessionState.Idle;
    private Frame? _latestFrame;
    private long _lastVideoTicks;
    private long _framesDecoded;
    private int _reconnects;
    private string _lastError = string.Empty;
    private DeviceInfo? _device;
    private int _started;
    private int _disposed;

    public YiouBoothSession(YiouBoothSessionOptions? options = null, Action<string>? log = null)
    {
        _options = options ?? new YiouBoothSessionOptions();
        _log = log ?? _options.Log;
        _pinCode = YocamPinCode.Resolve(_options.PinCode, log: _log);
        if (_options.Width < 16 || _options.Height < 16 ||
            _options.Width > VcamShmLayout.MaxWidth || _options.Height > VcamShmLayout.MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(options), $"分辨率必须在 16..{VcamShmLayout.MaxWidth}x{VcamShmLayout.MaxHeight}");
    }

    public event Action<BoothSessionState>? StateChanged;

    /// <summary>故障通知（异常/掉线/重连原因）。契约 §3：任何异常不抛给 UI 线程，走这里。</summary>
    public event Action<string>? Faulted;

    /// <summary>每解码出一帧触发（已写入 SHM 之后）；处理器须尽快返回。</summary>
    public event Action<Frame>? FrameReady;

    public BoothSessionState State => (BoothSessionState)Volatile.Read(ref _state);
    public DeviceInfo? Device => Volatile.Read(ref _device);
    public string PinCode => _pinCode;
    public string? LastError => Volatile.Read(ref _lastError);
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    public int ReconnectCount => Volatile.Read(ref _reconnects);
    public int Width => _options.Width;
    public int Height => _options.Height;

    /// <summary>最近一帧（BGR24/top-down）。插件手动截图直接取它；未出帧时为 null。</summary>
    public Frame? LatestFrame => Volatile.Read(ref _latestFrame);

    public bool TryGetLatestFrame(out Frame frame)
    {
        Frame? f = LatestFrame;
        frame = f!;
        return f != null;
    }

    /// <summary>启动后台编排，立即返回（不阻塞 UI）。重复调用无副作用。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (_started == 1) return Task.CompletedTask;
            _started = 1;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runningSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_options.UseSharedMemory)
            {
                _sink = new SharedMemoryFrameSink(
                    _options.SharedMemoryName,
                    maxWidth: VcamShmLayout.MaxWidth,
                    maxHeight: VcamShmLayout.MaxHeight,
                    fpsNum: Math.Max(1, _options.TargetFps),
                    fpsDen: 1,
                    log: _log);
            }

            _placeholderLoop = Task.Run(() => PlaceholderLoopAsync(_cts.Token));
            _supervisor = Task.Run(() => SupervisorLoopAsync(_cts.Token));
        }

        SetState(BoothSessionState.Discovering);
        _log?.Invoke($"[session] 启动：pin={_pinCode}, {_options.Width}x{_options.Height}, " +
                     $"shm={(_sink != null ? _options.SharedMemoryName : "<disabled>")}");
        return Task.CompletedTask;
    }

    /// <summary>等待进入 Running（首帧已出）；超时返回 false。插件“打开展台后等待就绪”用。</summary>
    public async Task<bool> WaitForRunningAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (State == BoothSessionState.Running) return true;
        TaskCompletionSource<bool>? signal = _runningSignal;
        if (signal == null) return false;
        try
        {
            return await signal.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? supervisor;
        Task? placeholder;
        lock (_gate)
        {
            cts = _cts;
            supervisor = _supervisor;
            placeholder = _placeholderLoop;
            _cts = null;
            _supervisor = null;
            _placeholderLoop = null;
        }

        try { cts?.Cancel(); } catch { }

        if (supervisor != null)
        {
            try { await supervisor.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { _log?.Invoke("[session] 等待编排线程退出超时（忽略）"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log?.Invoke($"[session] 编排线程退出异常：{ex.Message}"); }
        }
        if (placeholder != null)
        {
            try { await placeholder.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
        }

        lock (_gate)
        {
            _sink?.Dispose();
            _sink = null;
        }
        cts?.Dispose();
        SetState(BoothSessionState.Stopped);
        _log?.Invoke("[session] 已停止");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            LinkOutcome outcome = await RunLinkOnceAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            if (outcome.Ran) failures = 1;   // 已出过流再掉线：1s 后重连
            else failures++;                 // 连续失败：1s/3s/5s 退避

            SetState(BoothSessionState.Reconnecting);
            Interlocked.Increment(ref _reconnects);
            TimeSpan delay = Backoff[Math.Min(failures - 1, Backoff.Length - 1)];
            _log?.Invoke($"[session] {delay.TotalSeconds:0}s 后重连（第 {ReconnectCount} 次）" +
                         (string.IsNullOrEmpty(outcome.FailureMessage) ? string.Empty : $"；上次原因：{outcome.FailureMessage}"));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        SetState(ct.IsCancellationRequested ? BoothSessionState.Stopped : BoothSessionState.Faulted);
    }

    /// <summary>跑一次完整链路：发现→连接→开流→解码；返回时链路已断（或失败）。</summary>
    private async Task<LinkOutcome> RunLinkOnceAsync(CancellationToken ct)
    {
        TcpDeviceControl? control = null;
        FfmpegProcessFrameSource? source = null;
        var linkLost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            SetState(BoothSessionState.Discovering);
            DeviceInfo? dev = await DiscoverAsync(ct).ConfigureAwait(false);
            if (dev == null)
            {
                const string msg = "UDP 12811 未收到 ACK_GETIP（设备未上电/配对码不匹配/网卡选错/防火墙）";
                RaiseFault(msg);
                return LinkOutcome.Failure(msg);
            }

            Volatile.Write(ref _device, dev);
            _log?.Invoke($"[session] 发现设备：{dev}");

            SetState(BoothSessionState.Connecting);
            control = new TcpDeviceControl(new TcpControlOptions
            {
                LocalBindAddress = _options.LocalBindAddress,
                ConnectTimeout = _options.ControlConnectTimeout,
            }, _log);
            control.Faulted += msg =>
            {
                RecordError(msg);
                linkLost.TrySetResult(true);
            };
            control.Disconnected += () => linkLost.TrySetResult(true);

            if (!await control.ConnectAsync(dev, ct).ConfigureAwait(false))
            {
                const string msg = "TCP 8888 控制通道连接失败";
                RaiseFault(msg);
                return LinkOutcome.Failure(msg);
            }

            SetState(BoothSessionState.StartingStream);
            if (!await control.StartRtspStreamAsync(_options.RtspStartTimeout, ct).ConfigureAwait(false))
            {
                string msg = $"CMD_RTSP_TRANS_START 未在 {_options.RtspStartTimeout.TotalSeconds:0.#}s 内收到 ACK_RTSP_TRANS_START";
                RaiseFault(msg);
                return LinkOutcome.Failure(msg);
            }

            source = new FfmpegProcessFrameSource(dev.RtspUrl, new FfmpegOptions
            {
                FfmpegPath = _options.FfmpegPath,
                LocalBindAddress = _options.LocalBindAddress,
                Width = _options.Width,
                Height = _options.Height,
                TargetFps = _options.TargetFps,
                EnableTcpFallback = _options.EnableTcpFallback,
                StallTimeout = _options.StallTimeout,
            }, _log);
            source.FrameReady += OnFrameReady;
            source.Faulted += RecordError;

            await source.StartAsync(ct).ConfigureAwait(false);

            Interlocked.Exchange(ref _lastVideoTicks, DateTime.UtcNow.Ticks);
            SetState(BoothSessionState.Running);
            _log?.Invoke($"[session] Running：{dev.RtspUrl} → {_options.Width}x{_options.Height}");
            _runningSignal?.TrySetResult(true);

            await MonitorAsync(linkLost.Task, control, ct).ConfigureAwait(false);

            string reason = control.CloseReason ?? "链路结束";
            _log?.Invoke($"[session] 链路断开：{reason}");
            return LinkOutcome.RanThenLost(reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new LinkOutcome(false, false, "已取消");
        }
        catch (Exception ex)
        {
            RaiseFault($"链路异常：{ex.Message}");
            return LinkOutcome.Failure(ex.Message);
        }
        finally
        {
            if (source != null)
            {
                source.FrameReady -= OnFrameReady;
                source.Faulted -= RecordError;
                try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (control != null)
            {
                try { await control.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    private async Task<DeviceInfo?> DiscoverAsync(CancellationToken ct)
    {
        if (_options.DeviceIpOverride != null)
        {
            string ip = _options.DeviceIpOverride.ToString();
            _log?.Invoke($"[session] 使用指定设备 IP：{ip}（跳过 UDP 发现）");
            return new DeviceInfo { DeviceIp = ip, PcIp = string.Empty };
        }

        var discovery = new UdpDeviceDiscovery(new UdpDiscoveryOptions
        {
            PinCode = _pinCode,
            LocalBindAddress = _options.LocalBindAddress,
            Log = _log,
        });
        return await discovery.DiscoverAsync(_options.DiscoveryTimeout, ct).ConfigureAwait(false);
    }

    private async Task MonitorAsync(Task linkLost, TcpDeviceControl control, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Task completed = await Task.WhenAny(linkLost, Task.Delay(TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            if (completed == linkLost) return;

            if (!control.IsConnected)
            {
                RecordError("控制通道已断开");
                return;
            }

            double noFrameSeconds = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastVideoTicks), DateTimeKind.Utc)).TotalSeconds;
            if (noFrameSeconds > _options.FrameStarvationTimeout.TotalSeconds)
            {
                RaiseFault($"超过 {_options.FrameStarvationTimeout.TotalSeconds:0}s 未解码出新帧，重建链路");
                return;
            }
        }
    }

    private async Task PlaceholderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SharedMemoryFrameSink? sink = _sink;
                if (sink != null)
                {
                    double sinceVideo = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastVideoTicks), DateTimeKind.Utc)).TotalSeconds;
                    if (sinceVideo > 0.5)
                        sink.PublishPlaceholder(_options.Width, _options.Height);
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _log?.Invoke($"[session] 占位帧写入异常：{ex.Message}");
            }

            try { await Task.Delay(_options.PlaceholderInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnFrameReady(Frame frame)
    {
        Interlocked.Exchange(ref _lastVideoTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _framesDecoded);
        Volatile.Write(ref _latestFrame, frame);

        try
        {
            _sink?.PublishFrame(frame);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[session] 写共享内存失败：{ex.Message}");
        }

        try { FrameReady?.Invoke(frame); }
        catch (Exception ex) { _log?.Invoke($"[session] FrameReady 处理器异常：{ex.Message}"); }
    }

    private void RecordError(string message)
    {
        Volatile.Write(ref _lastError, message);
        _log?.Invoke($"[session] ERROR: {message}");
    }

    private void RaiseFault(string message)
    {
        RecordError(message);
        SetState(BoothSessionState.Faulted);
        try { Faulted?.Invoke(message); }
        catch (Exception ex) { _log?.Invoke($"[session] Faulted 处理器异常：{ex.Message}"); }
    }

    private void SetState(BoothSessionState state)
    {
        int old = Interlocked.Exchange(ref _state, (int)state);
        if ((BoothSessionState)old == state) return;
        _log?.Invoke($"[session] state: {(BoothSessionState)old} → {state}");
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { _log?.Invoke($"[session] StateChanged 处理器异常：{ex.Message}"); }
    }

    private readonly record struct LinkOutcome(bool Ran, bool Failed, string? FailureMessage)
    {
        public static LinkOutcome Failure(string message) => new(false, true, message);
        public static LinkOutcome RanThenLost(string reason) => new(true, false, reason);
    }
}
