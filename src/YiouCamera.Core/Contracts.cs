namespace YiouCamera.Core;

/// <summary>
/// 帧源状态。契约见 docs/CONTRACTS.md §3。
/// </summary>
public enum FrameSourceState
{
    Idle,
    Starting,
    Running,
    Faulted,
}

/// <summary>
/// 一帧解码后的图像。像素格式固定为 RGB24（<b>内存中 BGR 字节序</b>，top-down），
/// <see cref="Stride"/> = (Width*3+3) &amp; ~3，<see cref="Buffer"/> 长度 = Stride*Height。
/// 契约见 docs/CONTRACTS.md §1/§3。
/// </summary>
public sealed class Frame
{
    public int Width;
    public int Height;
    public int Stride;
    public byte[] Buffer = Array.Empty<byte>();
    public long FrameIndex;
    public long TimestampMs;
}

/// <summary>解码帧源抽象（v1 实现 = FfmpegProcessFrameSource，后续可换 FFmpeg.AutoGen）。</summary>
public interface IFrameSource : IAsyncDisposable
{
    FrameSourceState State { get; }

    /// <summary>后台线程触发。处理器必须尽快返回（通常只做一次 SHM 拷贝）。</summary>
    event Action<Frame>? FrameReady;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

/// <summary>设备发现抽象。</summary>
public interface IDeviceDiscovery
{
    Task<DeviceInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// 发现到的设备信息。设备 IP 必须来自 UDP 源地址（协议 §3.1），不是报文文本。
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>展台设备 IP（UDP 回包的源地址）。</summary>
    public string DeviceIp = string.Empty;

    /// <summary>设备眼里的 PC IP（回包文本前缀，仅用于诊断）。</summary>
    public string PcIp = string.Empty;

    /// <summary>RTSP 取流地址，协议 §5.1 固定端口 554 + /stream0，无凭据。</summary>
    public string RtspUrl => $"rtsp://{DeviceIp}:554/stream0";

    public override string ToString() => $"DeviceIp={DeviceIp}, PcIp={PcIp}, RtspUrl={RtspUrl}";
}

/// <summary>TCP 8888 控制通道抽象（无握手、无分帧，实现内部负责缓冲累积+全量扫描）。</summary>
public interface IDeviceControl : IAsyncDisposable
{
    Task<bool> ConnectAsync(DeviceInfo dev, CancellationToken ct);

    /// <summary>发送 CMD_RTSP_TRANS_START 并等待 ACK_RTSP_TRANS_START（协议 §5.2 的 100×10ms / 60×1s 重试）。</summary>
    Task<bool> StartRtspStreamAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>发送 CMD_LIVE_STOP{minutes}（0=立刻待机，255=不休眠）。</summary>
    Task StopLiveAsync(int minutes);

    bool IsConnected { get; }

    bool HeartbeatAlive { get; }
}
