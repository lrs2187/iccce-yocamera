using System.Text;

namespace YiouCamera.Core;

/// <summary>
/// 驿欧无线视频展台协议常量（依据 _research/yocam-protocol.md）。
/// 所有命令字面量均为 ASCII；发送不带换行/分隔符/长度前缀。
/// </summary>
public static class YocamProtocol
{
    // ---- 端口（协议 §2）----
    public const int DiscoveryPort = 12811;   // UDP 发现
    public const int ControlPort = 8888;      // TCP 控制 + 心跳 + 拍照通知
    public const int PhotoPort = 11111;       // TCP 设备→PC 拍照文件（PC 监听）
    public const int RtspPort = 554;          // RTSP

    // ---- 命令 / 回包（协议 §4.2、§10）----
    public const string DiscoveryCommand = "CMD_GETIP";
    public const string DiscoveryAck = "ACK_GETIP";
    public const string HeartbeatCommand = "CMD_DATA_SYNC";
    public const string HeartbeatAck = "DATA_SYNC";
    public const string RtspStartCommand = "CMD_RTSP_TRANS_START";
    public const string RtspStartAck = "ACK_RTSP_TRANS_START";
    public const string LiveStopCommand = "CMD_LIVE_STOP";
    public const string LiveStopAck = "ACK_LIVE_STOP";
    public const string WifiStaCommand = "CMD_WIFI_STA";
    public const string WifiStaAck = "ACK_WIFI_STA";
    public const string SetWifiCommand = "CMD_SETWIFIWIFINAME:";
    public const string SetWifiAck = "ACK_SETWIFI";
    public const string PhotoStartNotify = "START_TRANSPHOTO";
    public const string PhotoEndNotify = "END_TRANSPHOTO";

    /// <summary>
    /// 线路编码：命令/回包全部是 ASCII，配对码是 8 位数字。
    /// 用 Latin1 做“字节→字符”1:1 映射，便于在原始字节流里按 ASCII 关键字全量扫描；
    /// 只有 CMD_SETWIFIWIFINAME 的 SSID 可能含中文（GBK），v1 未暴露该命令。
    /// </summary>
    public static Encoding WireEncoding { get; } = Encoding.Latin1;

    /// <summary>已知的设备回包/通知关键字（用于 TCP 8888 接收缓冲的全量扫描）。</summary>
    public static IReadOnlyList<string> KnownReplies { get; } = new[]
    {
        // 长关键字在前，避免短关键字先命中（扫描时仍按“最早出现位置”消费）
        PhotoStartNotify,
        PhotoEndNotify,
        RtspStartAck,
        LiveStopAck,
        WifiStaAck,
        SetWifiAck,
        HeartbeatAck,
        DiscoveryAck,
    };

    /// <summary>构造发现报文 "CMD_GETIP" + 8 位配对码（18 字节 ASCII）。</summary>
    public static byte[] BuildDiscoveryPayload(string pinCode)
    {
        if (!YocamPinCode.IsValid(pinCode))
            throw new ArgumentException("配对码必须是 8 位十进制数字字符串", nameof(pinCode));
        return WireEncoding.GetBytes(DiscoveryCommand + pinCode);
    }

    /// <summary>构造发现报文（严格按厂商：允许空/异常配对码时只发命令，便于实机做 V1 验证）。</summary>
    public static byte[] BuildDiscoveryPayloadAllowAnyPin(string? pinCode)
        => WireEncoding.GetBytes(DiscoveryCommand + (pinCode ?? string.Empty));

    /// <summary>
    /// 解析发现应答 &lt;pcIp&gt;ACK_GETIP；pcIp 是文本前缀，设备 IP 必须另外取 UDP 源地址。
    /// </summary>
    public static bool TryParseDiscoveryAck(string payload, out string pcIp)
    {
        pcIp = string.Empty;
        if (string.IsNullOrEmpty(payload)) return false;
        int idx = payload.IndexOf(DiscoveryAck, StringComparison.Ordinal);
        if (idx < 0) return false;
        pcIp = payload[..idx].Trim();
        return true;
    }

    /// <summary>把分钟数编码为 CMD_LIVE_STOP{minutes}（0=立刻，255=不休眠）。</summary>
    public static string BuildLiveStopCommand(int minutes)
    {
        if (minutes is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(minutes), "待机分钟数必须在 0..255");
        return LiveStopCommand + minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
