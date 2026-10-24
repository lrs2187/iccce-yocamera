# 驿欧无线视频展台（YOCameraDesktop）通信协议逆向规格书

> 目标：脱离 `YOCameraDesktop.exe`，用纯 C#（.NET Framework 4.x / .NET 8 均可）自行完成
> **发现设备 → 建立控制通道 → 拉 RTSP 流 → 接收设备拍照文件 → 控制待机/WiFi** 全链路。
>
> 分析对象：`C:\Users\lrs2187\Desktop\data\teach\YiouBoard2.0\YOCameraDesktop.exe`
> 版本 **1.0.10.19**（.NET Framework 4.5.2，WPF，无混淆/无 IL2CPP）
> 反编译产物：`_research/YOCameraDesktop-decompiled/YOCameraDesktop/`（dnSpy 生成）
>
> 证据标注约定：
> - 🟩 **静态确证** = 反编译代码/PE 表中直接读到，行号可查；
> - 🟨 **强推断** = 由多条代码证据交叉推出，尚未在设备上观测；
> - 🟥 **待实机验证** = 只有真机抓包/联调才能定论（本机当前无该设备，见 §12）。

---

## 0. 结论速览（TL;DR）

| 项目 | 结论 |
|---|---|
| 发现 | UDP 广播 `255.255.255.255:12811`，载荷 `CMD_GETIP` + 8 位配对码，**每 1 秒发一次** 🟩 |
| 发现响应 | UDP 回包 ASCII 文本 `<本机IP>ACK_GETIP`，设备 IP 取 **UDP 源地址**（不是文本内容）🟩 |
| 控制通道 | TCP `{设备IP}:8888`，**无握手、无长度前缀、无分隔符**，纯 ASCII/GBK 文本命令流 🟩 |
| 心跳 | 连上后 **每 ≈1 秒** 发 `CMD_DATA_SYNC`，设备回含 `DATA_SYNC` 的包；连续 5 次没回则断开重连 🟩 |
| 开流 | TCP 8888 上发 `CMD_RTSP_TRANS_START`，等回包含 `ACK_RTSP_TRANS_START`，**之后** RTSP 才有流 🟩 |
| 取流 | `rtsp://{设备IP}:554/stream0`，**URL 不带任何凭据**，RTP over UDP（ffmpeg 默认），H.264 1080p（🟨 编码/分辨率待实机确认）|
| 拍照回传 | 设备→PC：TCP **11111**（PC 监听），23 字节文本头（`START_TRANSPHOTO` + 7 位 ASCII 长度）+ JPEG 原始字节 + `END_TRANSPHOTO` 🟩（头内容 🟨）|
| PC 主动拍照 | **不支持**。`SendGetImgCmd()` 直接抛 `NotImplementedException`（MainWindow.cs:3938-3941）；厂商「拍照」按钮只是本地抓预览帧 🟩 |
| 图像参数控制 | **没有任何** 白平衡/对焦/Zoom/亮度命令（整个程序集字符串表里不存在）🟩 |
| 待机/休眠 | `CMD_LIVE_STOP{分钟数}`（0=立刻，255=不休眠），回 `ACK_LIVE_STOP` 🟩；厂商的"休眠时间"设置项实际是死代码 🟩 |
| WiFi 配置 | `CMD_SETWIFIWIFINAME:{ssid}PASSWD:{psw}`（让设备加入指定 AP）／`CMD_WIFI_STA`（回普通 STA 模式），回 `ACK_SETWIFI` / `ACK_WIFI_STA` 🟩 |
| 二维码配对 | 内容 = `"{SSID} {密码} {8位配对码}"`，UTF-8、QR BYTE 模式、纠错 M、scale 4 🟩 |
| 另一套 SDK | `JCamera\JCameraW.dll` **不是 USB/有线采集**，而是 **Jetion WifiCamera SDK（MFC/COM，x86，WiFi + JPEG 帧）**，服务于**上一代**展台（`JCamera.exe`），与 RTSP 链路互斥（`Config.xml:<NewCamera>` 开关）🟩 |

---

## 1. 系统组成与两条并行链路

厂商同一个白板套件里其实有 **两代视频展台**，代码里用 `Config.xml` 的 `<NewCamera>` 选择把哪个窗口拉起来：

- `WriteBoard\WinCmd.cs:2528-2553`（`BtnVideoDesk_Click`）：
  - `NewCamera == true` → 启动 `YOCameraDesktop.exe`（窗口标题 `YODesktopCamera`，**本文件分析的"无线视频展台"**）
  - `NewCamera == false` → 启动 `JCamera\JCamera.exe`（窗口标题 `YiouJCamera`，走 `JCameraW.dll`）
- `C:\Users\lrs2187\Desktop\data\teach\YiouBoard2.0\Config.xml:34` 里当前是 `<NewCamera>true</NewCamera>`，即本机安装的是 **新设备（RTSP 链路）**。

```
                        ┌──────────────────────────────────────┐
   新设备（本任务目标）   │ YOCameraDesktop.exe (1.0.10.19)      │
   「驿欧无线视频展台」   │  ├ UDP 12811  发现                    │
                        │  ├ TCP 8888   控制 + 心跳              │
                        │  ├ TCP 11111  ← 设备回传拍照文件        │
                        │  └ RTSP 554   取流（FFmpeg.AutoGen）   │
                        └──────────────────────────────────────┘
   旧设备               ┌──────────────────────────────────────┐
   「JCamera 快拍仪」    │ JCamera.exe (2.0.10.8) → JCameraW.dll │
                        │  WiFi 扫 AP(Wlanapi) + socket + JPEG  │
                        └──────────────────────────────────────┘
```

> **插件只应实现新设备这条链路。** 若现场遇到老设备，属于另一套 SDK（见 §11）。

### 1.1 关键源码位置索引（本文件所有引用的基准）

所有行号均指 `_research/YOCameraDesktop-decompiled/YOCameraDesktop/MainWindow.cs`（4168 行），除特别注明。

| 功能 | 位置 |
|---|---|
| `DeskTopNet` 嵌套类（协议主体） | 3074–4158 |
| 端口常量 `DC_SEARCH_PORT/DC_CONNECT_PORT/DC_FILE_PORT` | 4064 / 4067 / 4070 |
| 心跳包字节 `CMD_DATA_SYNC` | 4157 |
| `Restart()` 初始化所有 socket/后台线程 | 3104–3179 |
| `DCUDPBackGround_DoWork`（1 秒轮询 + 心跳 + 搜索） | 3202–3229 |
| `DCFileBackGround_DoWork`（TCP 11111 收文件） | 3231–3375 |
| `TCPBackGround_doWork`（TCP 8888 收包） | 3377–3436 |
| `DCMsgAnalysis`（**唯一的收包解析函数**，UDP/TCP 共用） | 3438–3551 |
| `DCSearchBackGround_DoWork`（UDP 收包） | 3581–3599 |
| `SendSearchCmd`（发现请求） | 3610–3625 |
| `Connect`（TCP 8888 建链） | 3644–3689 |
| `SendRTSPStartCmd`（开流命令） | 3691–3705 |
| `SetAPMode`（WiFi 下发） | 3707–3758 |
| `GetQRCode` ×2（二维码内容） | 3785–3810 / 3812–3837 |
| `GetRTSPAddr`（取流地址） | 3839–3854 |
| `IsConnected`（心跳/掉线判定） | 3856–3907 |
| `SendDormancy`（待机） | 3909–3935 |
| `WakeUpDC`（唤醒） | 3943–3967 |
| `SetNormalMode`（回普通模式） | 4010–4049 |
| `RTSPFFmpegFun`（FFmpeg 拉流/解码） | 1494–1830 |
| `interupt`（AVIO 中断回调，10 秒） | 1478–1492 |
| 消息回调分发 `msgCallBackOperation` | 684–807 |

---

## 2. 端口与地址总表

| 用途 | 传输 | 端口 | 方向 | 绑定方式 | 出处 |
|---|---|---|---|---|---|
| 设备发现 | UDP | **12811** | PC → 广播 / 设备 → PC | `UdpClient(new IPEndPoint(LocalIPAddress, 0))`，目标 `IPAddress.Broadcast` | 3113-3114, 3614-3616 |
| 控制 + 心跳 + 拍照通知 | TCP | **8888** | PC 主动 connect 设备 | `Socket.Bind(LocalIPAddress,0)`（若指定网卡） | 3127-3135, 3677 |
| **设备→PC 拍照文件** | TCP | **11111** | **设备主动 connect PC** | `Listen` 在 `LocalIPAddress:11111`（默认 `IPAddress.Any`） | 3234-3243, 4070 |
| RTSP 取流 | RTSP/RTP | **554** | PC → 设备 | 由 FFmpeg 建，`localaddr`=配置网卡 IP | 3846, 1523-1527 |
| 集控 AP 信息（本地 IPC） | UDP | **34213** | PC 内部进程间 | `127.0.0.1` | 50-51, 378-392 |

`DC_FILE_PORT = 11111`（4070 行，🟩确证）。
`FileMaxBufSize = 2097152`（2 MiB，4112 行）——只用于分配 `FileBuf`，实际收文件走 `new byte[num2]` 动态分配，**所以 2 MiB 不是文件上限**（4115、3316 行）。

---

## 3. 阶段一：设备发现（UDP 12811）

### 3.1 报文逐字段

**PC → 设备（广播）**

```
偏移  长度  字段        取值 / 说明
0    10   ASCII 命令字  "CMD_GETIP"
10    8   ASCII 配对码  8 位十进制数字字符串，全局唯一持久化（见 §9.3）
──────────────────────────────────────────
合计 18 字节，无结尾符、无换行、无长度前缀
```

代码（🟩 MainWindow.cs:3610-3625）：

```csharp
byte[] bytes = Encoding.Default.GetBytes("CMD_GETIP" + this.Pincode);
this.SechIpendpoint.Address = IPAddress.Parse("255.255.255.255");
this.SearchSocket.Send(bytes, bytes.Length, this.SechIpendpoint);
```

- **编码**：`Encoding.Default` = 中文 Windows 的 **GBK(CP936)**；因为内容全 ASCII，字节与 ASCII 完全一致。
- **目标**：`255.255.255.255:12811`（`SechIpendpoint` 初值见 3114 行 `IPEndPoint(IPAddress.Broadcast, DC_SEARCH_PORT)`；每次发送前又强制改回 `255.255.255.255`）。
- **节拍**：由 `DCUDPBackGround_DoWork` 每 **1 秒**调用一次（3227 行 `Thread.Sleep(1000)`），**只在"当前未连接"时才发**（3218-3226：`if (!IsConnected()) … SendSearchCmd()`）。
- **本机源端口**：随机（`UdpClient` 绑 `LocalIPAddress:0`）。

**设备 → PC（单播 UDP，回到源地址/源端口）**

```
偏移  长度    字段              取值 / 说明
0     n     ASCII 文本        设备看到的 PC 的 IP（点分十进制），如 "192.168.0.2"
n    9     ASCII 命令字        "ACK_GETIP"
──────────────────────────────────────────
设备 IP 不在此文本中！取自 UDP 包的源 IP。
```

解析代码（🟩 3450-3460）：

```csharp
if (@string.Contains("ACK_GETIP")) {
    string[] array = @string.Split(new string[] { "ACK_GETIP" }, StringSplitOptions.RemoveEmptyEntries);
    this.DClientIP   = this.SechIpendpoint.Address.ToString();  // ← 设备 IP = UDP 源地址
    this.DCWindowsIP = array[0];                               // ← 文本前缀 = PC 自己的 IP
    this.IsDCFinded  = true;
    this.SendMsg("ACK_GETIP", Encoding.UTF8.GetBytes(this.DCWindowsIP), null);
    return true;
}
```

要点 / 坑：
1. **设备 IP 必须用 UDP 源地址**（`Receive(ref endpoint)` 覆盖后的 `endpoint.Address`），不能从文本解析。🟩
2. `Split(..., RemoveEmptyEntries)` → 若设备只回 `"ACK_GETIP"`（无前缀），`array` 长度 0 → `array[0]` 抛 `IndexOutOfRangeException`，被外层 `catch` 吞掉（3462-3467）。说明**设备一定会带前缀**。🟨
3. `DCWindowsIP` 只是"设备眼里 PC 的 IP"，厂商程序仅把它回送 UI（`msgCallBackOperation` 的 `ACK_GETIP` 分支只做日志，748 行附近）。**我们的实现可以直接忽略该文本**，但建议记录用于诊断。
4. 收到 `ACK_GETIP` 后厂商立刻 `Connect()`（TCP 8888）+ 起 RTSP 线程（744-749）。
5. 发现阶段**没有超时机制**——只要没连上就无限 1 秒广播，直到用户退出。🟩

### 3.2 文字时序图（发现 → 建链）

```
 插件/PC                                                     展台设备
   │                                                            │
   │  UDP → 255.255.255.255:12811                               │
   │  "CMD_GETIP92271885"                                       │
   ├───────────────────────────────────────────────────────────►│ (设备仅在自己存
   │                                                            │  的配对码==92271885
   │  UDP ← 源 IP:随机端口                                       │  时才应答？🟨)
   │  "192.168.0.2ACK_GETIP"                                    │
   │◄───────────────────────────────────────────────────────────┤
   │  ⇒ 设备IP = 源IP (e.g. 192.168.0.50)                        │
   │  ⇒ 每 1s 重发上述广播，直到 TCP 8888 连通                    │
   │                                                            │
   │  TCP connect 192.168.0.50:8888                             │
   ├───────────────────────────────────────────────────────────►│
   │◄────────────────────── (无握手、无问候包，连接即可用) ────────┤
   │                                                            │
   │  TCP "CMD_RTSP_TRANS_START"                                │
   ├───────────────────────────────────────────────────────────►│
   │  TCP "ACK_RTSP_TRANS_START"                                │
   │◄───────────────────────────────────────────────────────────┤
   │  ⇒ 此时 rtsp://192.168.0.50:554/stream0 才真正有流           │
   │                                                            │
```

### 3.3 可直接抄的实现（发现）

```csharp
public sealed class YocamDiscovery : IDisposable
{
    public const int SearchPort = 12811;
    private readonly string _pin;            // 8 位配对码，见 §9.3
    private readonly IPAddress _localAddr;   // 指定网卡 IP；多网卡/VPN 环境必须指定

    public async Task<YocamDevice> WaitForDeviceAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(_localAddr, 0));
        udp.EnableBroadcast = true;                 // 必须！否则 Send 到 255.255.255.255 抛异常
        var payload = Encoding.GetEncoding(936).GetBytes("CMD_GETIP" + _pin); // GBK 兼容，ASCII 时等价
        var dst = new IPEndPoint(IPAddress.Broadcast, SearchPort);

        var deadline = DateTime.UtcNow + timeout;
        var lastSend = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            if ((DateTime.UtcNow - lastSend).TotalMilliseconds >= 1000)   // 与厂商一致的 1s 节拍
            {
                try { await udp.SendAsync(payload, payload.Length, dst); } catch (SocketException) { }
                lastSend = DateTime.UtcNow;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(200);
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.GetEncoding(936).GetString(r.Buffer);
                if (text.Contains("ACK_GETIP"))
                {
                    // 重点：设备 IP 来自 UDP 源地址，不是文本
                    return new YocamDevice(r.RemoteEndPoint.Address, text, _pin);
                }
            }
            catch (OperationCanceledException) { /* 这一轮没包，下一轮 */ }
        }
        return null;
    }
}
```

> ⚠️ 广播在不同网卡上的行为：`UdpClient.Send` 到 `255.255.255.255` 走的是**路由表选出的默认接口**。
> 本机装了代理软件（`FlClash`，`198.18.0.0/30` + 默认路由抢占）时，直接发 `255.255.255.255` 很可能从错误网卡出去。
> 建议：绑定具体网卡地址发送（如上），必要时补发子网定向广播 `x.y.z.255`（本次实测两种都试过，见 §12）。

---

## 4. 阶段二：TCP 8888 控制通道

### 4.1 帧格式（这是本协议最容易被误解的地方）

| 特性 | 结论 | 依据 |
|---|---|---|
| 长度前缀 | **没有** | 全文件只有 `Send(byte[])`/`Receive(byte[1024])`，无任何长度字段解析 |
| 分隔符（`\r\n`/`\0`/STX…） | **没有** | 命令字面量里不含任何控制字符；接收端靠 `Contains("XXX")` 匹配 |
| 编码 | `Encoding.Default`（GBK），命令全 ASCII | 3461 行；含中文 SSID/密码时才是 GBK |
| 单包大小 | 命令 < 64 字节；接收缓冲 **1024** 字节 | 3382 `new byte[1024]` |
| 分帧策略 | **靠 TCP 边界 + 短包**，收到多少就整段当文本匹配 | 3401-3405 `Receive(array)` → `DCMsgAnalysis(array)` |
| 握手 | **没有**：connect 后可直接发命令 | 3644-3689 无任何收发 |
| 大小写/换行 | 无要求 | 匹配用 `Contains`，不带换行发也带换行收都能过 |

**实证（🟩）** 发送侧都是一次 `Send`：

```csharp
this.tcpClient.Send(Encoding.Default.GetBytes("CMD_RTSP_TRANS_START"), SocketFlags.None);  // 3695
this.tcpClient.Send(this.beatheart, SocketFlags.None);                                     // 3895, = "CMD_DATA_SYNC"
this.tcpClient.Send(Encoding.Default.GetBytes("CMD_LIVE_STOP" + time), SocketFlags.None);   // 3919
```

**实证（🟩）** 接收侧把一次 `Receive` 的结果**整段**转字符串再 `Contains`（3441、3450-3545）：

```csharp
string @string = Encoding.Default.GetString(RecvMsgByte);
if (@string.Contains("DATA_SYNC")) { ... }
```

⇒ 粘包（两条 ACK 落在同一次 `Receive`）时，`Contains` 链是 `if/else` 结构，**只有第一个命中的分支会生效**，其余被丢弃；拆包（一条 ACK 被切成两次 `Receive`）时则完全匹配不到。
**这是厂商实现的固有缺陷，我们的实现必须做缓冲累积 + 全量扫描**（见 §4.4 伪代码）。

### 4.2 命令表（全量，已核对程序集字符串表 🟩）

> 依据：`YOCameraDesktop.exe` 的 #US（UTF-16 用户字符串）堆中与协议相关的字面量共 22 条，
> 命令/回包全在下表；**不存在其它命令**（无 Zoom/Focus/白平衡/拍照/分辨率切换等）。

| # | 方向 | 报文（ASCII，原样发送，无换行） | 参数 | 期望回包（`Contains` 匹配） | 语义 | 出处 |
|---|---|---|---|---|---|---|
| 1 | PC→D | `CMD_GETIP{pin}` | pin=8 位数字 | `<pcIP>ACK_GETIP`（UDP） | 发现 | 3614 |
| 2 | PC→D | `CMD_DATA_SYNC` | — | `DATA_SYNC` | 心跳，~1s/次 | 4157, 3895 |
| 3 | PC→D | `CMD_RTSP_TRANS_START` | — | `ACK_RTSP_TRANS_START` | 开流（休眠唤醒同用此命令） | 3695, 3954, 3564 |
| 4 | PC→D | `CMD_LIVE_STOP{time}` | time=十进制字符串，单位分钟 | `ACK_LIVE_STOP` | 待机/停播延时 | 3919, 3477 |
| 5 | PC→D | `CMD_WIFI_STA` | — | `ACK_WIFI_STA` | 回普通 STA/正常模式 | 3748, 4019 |
| 6 | PC→D | `CMD_SETWIFIWIFINAME:{ssid}PASSWD:{psw}` | ssid ≤33 字节（超出截断），GBK | `ACK_SETWIFI`，失败时回包含 `FAIL` 的文本 | 让设备加入指定 AP | 3721-3744, 3532-3544 |
| 7 | D→PC | `START_TRANSPHOTO` | — | （PC 无需回） | 通知：马上有照片从 TCP 11111 发来 | 3495-3509 |
| 8 | D→PC | `END_TRANSPHOTO` | — | （PC 无需回） | 通知：照片发完 | 3510-3524, 3348-3352 |

补充：
- 第 6 条的拼装细节（🟩 3718-3745）：先发 `CMD_SETWIFIWIFINAME:`，紧跟 SSID 原始 GBK 字节（**超过 33 字节则截断到 33**），再跟 `PASSWD:` + 密码原始字节。**SSID 与 `PASSWD:` 之间没有任何分隔符**，设备端只能靠固定 33 字节字段或 `PASSWD:` 关键字反推——因此 **SSID 里不能出现字符串 `PASSWD:`**，且若 SSID 长度 < 33 设备必须有"找到 PASSWD:"的能力（🟨）。
- `CMD_WIFI_STA` 有两种用途：`SetNormalMode()` 用（4019）、`SetAPMode("", "")` 用（3748）。**回包都是 `ACK_WIFI_STA`**。
- 回包匹配是 `Contains`，所以 `ACK_SETWIFI...FAIL` 也命中 `ACK_SETWIFI` 分支，代码再单独判 `Contains("FAIL")`（3532-3544）。

### 4.3 连接生命周期（含心跳/掉线判定）

```
  应用启动
     │
     ├─► DeskTopNet.Restart()  ← 首次构造也会调用 (3182-3199)
     │     ├ 建 UDP 搜索 socket（若 !APMode）
     │     ├ 建 TCP socket + Bind(本机IP)
     │     ├ 起 DCSearchBackGround  (阻塞收 UDP)
     │     ├ 起 TCPBackGround       (每 10ms 轮询 Receive)
     │     ├ 起 DCFileBackGround    (Listen TCP 11111)
     │     └ 起 DCUDPBackGround     (每 1000ms：IsConnected() → 心跳/搜索)
     │
     ▼
  ┌──────────────── 每 1000ms ────────────────┐
  │ IsConnected():                             │
  │   if (!tcp.Connected) return false          │  ← TCP 层未连接：直接 false
  │   if (!GetHeartBeatBack) UnRecHBCount++     │  ← 上一轮的心跳没回音
  │   if (UnRecHBCount == 5) DisConnect()       │  ← 连续约 5 秒没回包 ⇒ 主动断开
  │   else tcp.Send("CMD_DATA_SYNC")            │  ← 每轮都发心跳
  │        GetHeartBeatBack = false             │
  │   return true                               │
  └────────────────────────────────────────────┘
     │
     ├ 返回 false 且"上一轮还是 true" → 回调 TCPServer_Miss（UI：已与设备断开）
     ├ 返回 false                    → SendSearchCmd()（重新 UDP 发现）
     └ 返回 true  且"上一轮是 false" → 回调 TCPServer_OnLine（UI：连接到设备）
                                        ⇒ 发现后立刻 Connect() + 起 RTSP 线程
```

关键数字（🟩）：

- 心跳周期：**≈1000 ms**（由 `DCUDPBackGround` 的 `Thread.Sleep(1000)` 驱动，3202-3228）。
- 掉线判定：`UnRecHBCount` 达到 **5** 即断开（3882），实测语义约等于"**5–6 秒无心跳应答**"。
- 心跳回包判据：收到任意 `Contains("DATA_SYNC")` 的文本即置 `GetHeartBeatBack = true` 并清零计数（3469-3474）。
- 断开后 `IsDCFinded = false`（3774），回到 UDP 广播发现阶段。

> ⚠️ 厂商实现有微妙顺序问题：先判断 `!GetHeartBeatBack` 再发送，且发送后立即置 `false`。
> 我们的实现应采用更清晰的"发送 + 超时计时器"模型（见 §4.4）。

### 4.4 可直接抄的实现（控制通道 + 心跳 + 解析）

```csharp
public sealed class YocamControl : IDisposable
{
    private readonly Socket _tcp;
    private readonly StringBuilder _rx = new StringBuilder();
    private readonly object _lock = new object();
    public event Action<string> PacketReceived;   // 已识别出"一条"命令/回包（尽力分帧）
    public bool Online { get; private set; }

    public void Connect(IPAddress devIp, IPAddress localIp, int port = 8888)
    {
        _tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (!localIp.Equals(IPAddress.Any)) _tcp.Bind(new IPEndPoint(localIp, 0)); // 多网卡必做
        _tcp.NoDelay = true;                    // 命令都很短，关 Nagle
        _tcp.ReceiveTimeout = 0;
        _tcp.Connect(new IPEndPoint(devIp, port));   // 注意：无握手，连上就能发
        Online = true;
        new Thread(ReceiveLoop) { IsBackground = true }.Start();
    }

    private void ReceiveLoop()
    {
        var buf = new byte[1024];
        while (Online)
        {
            int n;
            try { n = _tcp.Receive(buf); }
            catch { Online = false; break; }
            if (n <= 0) { Online = false; break; }

            lock (_lock)
            {
                _rx.Append(Encoding.GetEncoding(936).GetString(buf, 0, n)); // GBK
                // 尽力分帧：已知回包关键字，扫描到就"消费"掉
                foreach (var kw in KnownReplies)          // "DATA_SYNC","ACK_RTSP_TRANS_START",...
                {
                    int idx = _rx.ToString().IndexOf(kw, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        _rx.Remove(0, idx + kw.Length);   // 已消费
                        PacketReceived?.Invoke(kw);
                        break;                            // 一次只处理一条，外层 while 继续
                    }
                }
                if (_rx.Length > 4096) _rx.Clear();       // 防御：垃圾数据
            }
        }
    }

    private static readonly string[] KnownReplies =
    {
        "ACK_GETIP", "DATA_SYNC", "ACK_RTSP_TRANS_START", "ACK_LIVE_STOP",
        "ACK_WIFI_STA", "ACK_SETWIFI", "START_TRANSPHOTO", "END_TRANSPHOTO"
    };

    public void Send(string ascii)                  // 发送一律不带换行
    {
        var b = Encoding.GetEncoding(936).GetBytes(ascii);
        _tcp.Send(b, SocketFlags.None);
    }

    // 带超时的"发命令 + 等回包"，替代厂商的忙等
    public async Task<bool> SendAndWaitAsync(string cmd, string expect,
                                             TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(string kw) { if (kw == expect) tcs.TrySetResult(true); }
        PacketReceived += H;
        try
        {
            Send(cmd);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var _ = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally { PacketReceived -= H; }
    }
}
```

**心跳线程（替代 `DCUDPBackGround` 的 1 秒轮询）：**

```csharp
// 每 1s 发 CMD_DATA_SYNC；超过 5s 没收到任何 DATA_SYNC ⇒ 判定掉线
var hb = new Timer(_ =>
{
    if (!ctrl.Online) return;
    lastBeat = DateTime.UtcNow;
    if ((DateTime.UtcNow - lastBeatAck).TotalSeconds >= 5) { ctrl.Online = false; /* 触发重连 */ return; }
    try { ctrl.Send("CMD_DATA_SYNC"); } catch { ctrl.Online = false; }
}, null, 1000, 1000);

// PacketReceived 里遇到 DATA_SYNC 就 lastBeatAck = DateTime.UtcNow;
```

---

## 5. 阶段三：RTSP 取流

### 5.1 地址与参数（🟩 除注明外）

| 项目 | 值 | 出处 |
|---|---|---|
| URL | `rtsp://{DClientIP}:554/stream0` | 3846 |
| 端口 | 554（硬编码） | 3846 |
| 凭据/鉴权 | **URL 不含 userinfo**；FFmpeg 未设置任何 `user/password` `AVOption` | 3846, 1519-1535 |
| 传输方式 | 信令 RTSP/TCP；**媒体 RTP over UDP**（未设 `rtsp_transport=tcp`，FFmpeg RTSP 默认 UDP 优先） | 1519-1535 |
| 本地绑定 | `localaddr = CurConfig.IP`（用户在设置里选的网卡 IP） | 1523-1527 |
| 缓冲/超时 | `buffer_size=1655360`、`stimeout=5000000`(µs→5s)、`max_delay=1000000`(µs→1s) | 1520-1522 |
| 分析时长 | `max_analyze_duration = 5000000`(µs→5s) | 1554 |
| 阻塞 I/O 上限 | `AVIOInterruptCB` 回调：**超过 10 秒**就返回 -1 中断 | 1478-1492 |
| 视频编码 | **H.264**（🟨）：代码里唯一特殊处理是 `pix_fmt == AV_PIX_FMT_NONE && codec_id == AV_CODEC_ID_H264 → 按 YUV420P 处理` | 1598-1603 |
| 分辨率 | **1920×1080**（🟨强）：解码宽高为 0 时兜底 1920×1080；D3D 渲染面固定 1920×1080 YV12；对比模式 `ImgOriW/H = 1920/1080` | 1604-1609, 338, 822-823 |
| 帧率 | 代码完全不使用（不设、不读）→ 未知 | — |
| 其它流 | 只取**第一条视频流**；对音频流只判断存在、不做任何事；全程序只有 `/stream0` 一个路径字面量 | 1568-1584 |
| 硬件加速 | 只有 `dxva2`，找不到就退回软解 | 1515-1517 |
| 渲染 | 优先 D3D9 + `D3DImage`(YV12)，失败退回 `WriteBitMap`(BGR24 + `sws_scale`)，再退回 GDI | 332-355, 1598-1610 |

> **对插件的含义**：能直接拿 `rtsp://<ip>:554/stream0` 喂给 FFmpeg/LibVLC，**不需要任何鉴权协商、不需要先发 DESCRIBE 探路**（但必须先发 §4 的 `CMD_RTSP_TRANS_START`）。

### 5.2 开流的完整时序（含重试）

```
 发现成功（收到 ACK_GETIP）
   │
   ├─► TCP connect 8888                                   (MainWindow.cs:744-749)
   └─► StartFFmpegThread() → FFmpegFun() → RTSPFFmpegFun(GetRTSPAddr())
            │
            │  for i in 0..59:                             (1500-1508)
            │      SendRTSPStartCmd():
            │          TCP "CMD_RTSP_TRANS_START"   ────────────────►  设备
            │          RTSPStartSucess = false
            │          最多等 100 × 10ms = 1s，期间收到
            │          Contains("ACK_RTSP_TRANS_START") ⇒ 置位  ◄────  "ACK_RTSP_TRANS_START"
            │      失败 → Thread.Sleep(1000) 重试
            │  60 次全失败 ⇒ 放弃本次（不再重试，等下轮 TCPServer_Miss/OnLine 事件）
            ▼
        avformat_open_input("rtsp://ip:554/stream0", 带 §5.1 的 AVDictionary)
            │  失败(-1) ⇒ throw "No RTSP"（可能真是流没起来，也可能 IP/网络错）
            ▼
        avformat_find_stream_info
            ▼
        avcodec_open2(codec_id)
            ▼
        while(run) av_read_frame → avcodec_send_packet → avcodec_receive_frame → 渲染
            │  读错误 ⇒ throw "RTSPConnectFail" ⇒ UI「视频流连接中断」+ 重起 RTSP 线程
            │  10 秒无数据 ⇒ AVIO 中断 ⇒ "Connection Timed Out" ⇒ UI「连接超时」
            ▼
```

`RTSPStartSucess` 的置位事件（🟩 3480-3487）：`DCMsgAnalysis` 收到含 **`ACK_RTSP_TRANS_START`** 的 TCP 文本时置 `true`，**没有其它任何地方置位**。

### 5.3 可直接抄的实现（FFmpeg）

**快速验证（命令行，无需写代码）：**

```powershell
# 1) 先确认控制通道 + 开流命令；否则 554 上没有流
#    用 §3.3 的脚本拿到 IP 后：
# 2) 探测
ffprobe -rtsp_transport udp -timeout 5000000 "rtsp://192.168.0.50:554/stream0"
# 3) 播放/录一段验证帧率与编码
ffplay -fflags nobuffer -flags low_delay "rtsp://192.168.0.50:554/stream0"
ffmpeg -rtsp_transport udp -i "rtsp://192.168.0.50:554/stream0" -c copy -t 10 out.mkv
```

**C#（FFmpeg.AutoGen，与厂商同路线，便于复用其经验）关键参数：**

```csharp
AVDictionary* opt = null;
ffmpeg.av_dict_set(&opt, "buffer_size", "1655360", 0);   // 与厂商一致
ffmpeg.av_dict_set(&opt, "stimeout",    "5000000", 0);   // 5s socket 超时
ffmpeg.av_dict_set(&opt, "max_delay",   "1000000", 0);
ffmpeg.av_dict_set(&opt, "localaddr",   localIp.ToString(), 0);  // 多网卡必做
// 建议我们的插件额外加：rtsp_transport=tcp 作为 UDP 失败时的回退（厂商没做）

var fmt = ffmpeg.avformat_alloc_context();
// 强烈建议：设置 interrupt_callback（厂商用 10s），否则网络假死会把线程钉住
fmt->interrupt_callback.callback = (opaque) =>
    (DateTime.UtcNow - lastIoActivity).TotalSeconds > 10 ? 1 : 0;
```

**若用 LibVLCSharp（更省事，无需自建解码环）：**

```csharp
var libvlc = new LibVLC("--rtsp-udp", "--network-caching=300", "--clock-jitter=0");
var media = new Media(libvlc, new Uri("rtsp://192.168.0.50:554/stream0"),
                      FromType.FromLocation);
media.AddOption(":rtsp-tcp");   // 需要 TCP 时用；默认 UDP 与厂商一致
```

> 🟥 待实机确认：DUT 是否需要 Digest 鉴权、是否支持 RTP over TCP（若 UDP 丢包严重，插件的 TCP 回退路径能否生效）。

---

## 6. 阶段四：设备 → PC 拍照文件回传（TCP 11111）

### 6.1 谁触发

- **只有设备端能触发**（设备上的"拍照/抓拍"按键）。PC 侧**没有任何**触发命令：`SendGetImgCmd()` 是 `throw new NotImplementedException()`（3938-3941），全程序集也不存在"拍照命令"字面量 🟩。
- 厂商 UI 上的"拍照"按钮（`BtnTakePhoto_Click`，1167-1247）是 **本地对预览做 `RenderTargetBitmap` 截图**，与设备无关 🟩。
- 所以：**若 ICCCE 插件要"拍照"功能，请直接对解码后的帧做本地抓帧**，不要期待设备命令。

配套的 TCP 8888 通知（🟩 3495-3524）：设备在发文件前后，会在**控制通道**上发 `START_TRANSPHOTO` / `END_TRANSPHOTO` 文本；厂商只是回调 UI（`SendMsg("START_TRANSPHOTO", null, DCFileSize.ToString())`，而 `DCFileSize` 恒为 0，且该回调无人处理）。**通知与文件本身是两条独立 TCP 连接**。

### 6.2 文件传输协议（设备→PC，TCP 11111，设备为客户端）

PC 侧先 `Listen`（🟩 3232-3243）：绑定 `LocalIPAddress:11111`（`LocalIPAddress` 默认 `IPAddress.Any`，即所有网卡），`backlog = 20`，每个连接起一个后台线程处理。

**逐字节帧格式**（🟩 除头内容外）：

```
┌────────────────────────────────────────────────────────────────────┐
│ 23 字节 ASCII 文本头                                                │
│   偏移  长度   内容                                                  │
│   0     16    "START_TRANSPHOTO"（🟨 强推断，见下）                   │
│   16     7    文件长度，ASCII 十进制，如 "0209408"                    │
│               末尾不足 7 位时以 0xFF 为终止/填充（源码按 0xFF 截断）    │
├────────────────────────────────────────────────────────────────────┤
│ 文件数据：[文件长度] 字节，JPEG（.jpg）原始字节流，无任何转义/压缩       │
├────────────────────────────────────────────────────────────────────┤
│ 尾标记：ASCII "END_TRANSPHOTO"（14 字节）                             │
│   PC 端在读完数据后，会再收最多 100 字节并 Contains("END_TRANSPHOTO")  │
│   命中后把落盘文件交给 UI 生成缩略图                                   │
└────────────────────────────────────────────────────────────────────┘
```

**证据链（为什么头是 16+7）：**

1. 🟩 头部固定 23 字节：`byte[] array = new byte[23]; socket2.Receive(array);`（3268-3269）。
2. 🟩 长度字段在 **16..22**：`for (int k = 16; k < 23; k++) { if (array[k] == 0xFF) break; array2[k-16] = array[k]; }`，随后 `int.Parse(temStr.Trim())`（3286-3299）。
3. 🟩 整段头被当文本打印：`Console.WriteLine(Encoding.Default.GetString(array, 0, 23));`（3278）⇒ 前部也是文本。
4. 🟨 16 + 7 = 23，且程序集里恰好存在 16 字符的 `START_TRANSPHOTO` 与 14 字符的 `END_TRANSPHOTO` 字面量，控制通道还用它们做"照片传输开始/结束"的成对通知 ⇒ 头 16 字节极可能就是 `START_TRANSPHOTO`。
5. 🟨 长度必须是 7 位数字才能被厂商的 `int.Parse` 正确解析（见下面的坑），因此设备很可能用 `%07d` 零填充。

> **实现建议：不要依赖头部字节内容**。固定读 23 字节 → 取 `[16..23)` → 从第一个非数字字节处截断 → `int.Parse` →
> **精确读满 N 字节** → 落盘 → 再读到 `END_TRANSPHOTO` 或对端关闭。
> 同时把**前 23 字节的原始 hex 打进日志**，实机时一眼就能确认头内容（这是本协议唯一还未 100% 确定的地方）。

### 6.3 厂商实现的 4 个坑（我们必须绕开）

| # | 位置 | 问题 | 我们的做法 |
|---|---|---|---|
| 1 | 3269 `socket2.Receive(array)` | 不保证一次读满 23 字节，可能只读到部分 → 头错位 | `ReadExact(socket, 23)` 循环读满 |
| 2 | 3286-3299 | 长度字段是 7 位 ASCII；若设备用 **0xFF 填充** 而非零填充，`array2` 里会是数字+`\0`，`Trim()` 不删 `\0` ⇒ `int.Parse` 抛异常 ⇒ **整张照片丢弃** | 自己按"数字前缀"解析，兼容 `\0`/空格/0xFF 填充 |
| 3 | 3315-3341 | 若 `array[0] != 0xFF`，就"补一个 0xFF + 丢弃最后一个字节"（JPEG 重同步 hack）——若真触发会丢 EOI 甚至错位 | **原样写入**，不做任何"修正"；用 `array[0]==0xD8 && array[1]==0xFF` 之类的检查只做告警 |
| 4 | 3345-3348 | 用 100 字节缓冲一次性 `Receive` 找 `END_TRANSPHOTO`，可能读不全 | 累积缓冲直到匹配到 `END_TRANSPHOTO` 或对端关闭（或直接忽略尾标记，以"读满 N 字节 + EOF"为准） |

另外：
- 🟩 绑定失败时后台线程**直接 `return`**（3239-3242），照片接收功能静默失效、无任何提示 ⇒ 端口 11111 被占（同名软件多开、其它服务）时"照片永远收不到"。
- 🟩 落盘路径：`%USERPROFILE%\Documents\YOFiles\DesktopDocument\<ticks>.jpg`（3257、2859）。
- 🟩 文件名来自 `DateTime.Now.Ticks`，与设备无关。
- 🟨 设备连接 PC 11111 用的 IP = 它收到的 UDP 广播包的**源 IP**（因为发现包里不带 PC IP），所以 **PC 必须用与发现相同的网卡接收**；多网卡时建议监听 `IPAddress.Any` 并在防火墙放行。

### 6.4 可直接抄的实现（文件接收）

```csharp
public sealed class YocamPhotoReceiver : IDisposable
{
    public const int FilePort = 11111;
    private readonly TcpListener _listener;
    public event Action<string /*jpgPath*/> PhotoSaved;

    public YocamPhotoReceiver(IPAddress bind = null)
    {
        _listener = new TcpListener(bind ?? IPAddress.Any, FilePort);
        _listener.Start(20);                       // 厂商 Listen(20)
        new Thread(AcceptLoop) { IsBackground = true }.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            new Thread(() => Handle(c)) { IsBackground = true }.Start();
        }
    }

    private void Handle(TcpClient c)
    {
        using (c)
        using (var ns = c.GetStream())
        {
            var head = ReadExact(ns, 23);                       // 坑 1
            if (head == null) return;

            // 坑 2：长度 = bytes[16..23] 的 ASCII 数字前缀
            int size = ParseAsciiIntPrefix(head, 16, 7);
            if (size <= 0 || size > 64 * 1024 * 1024) return;   // 防御

            var data = ReadExact(ns, size);                     // 坑 3：原样，不修改
            if (data == null) return;

            var path = Path.Combine(PhotoDir, DateTime.Now.Ticks + ".jpg");
            File.WriteAllBytes(path, data);

            // 坑 4：尾标记尽力读，读不到也不影响文件
            TryReadUntil(ns, "END_TRANSPHOTO", 4096, 300);
            PhotoSaved?.Invoke(path);
        }
    }

    private static int ParseAsciiIntPrefix(byte[] buf, int off, int len)
    {
        int v = 0, i = off;
        for (; i < off + len; i++)
        {
            byte b = buf[i];
            if (b < (byte)'0' || b > (byte)'9') break;          // 兼容 0xFF/0x00/0x20 填充
            v = v * 10 + (b - '0');
        }
        return i == off ? -1 : v;
    }

    private static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n]; int got = 0;
        while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return null; got += r; }
        return b;
    }
}
```

---

## 7. 休眠 / 唤醒 / 待机

### 7.1 命令与语义

| 操作 | 命令 | 参数 | 回包 | 出处 |
|---|---|---|---|---|
| 停止直播 / 进入待机 | `CMD_LIVE_STOP{time}` | `time` = 十进制分钟数；**0 = 立刻待机**，1/3/5 = 延时，**255 = 不休眠** | `ACK_LIVE_STOP` | 3909-3935；3929-3932 存 `DormancyTime` |
| 唤醒 / 恢复直播 | `CMD_RTSP_TRANS_START` | — | `ACK_RTSP_TRANS_START` | 3943-3967（`WakeUpDC()`） |
| 回普通模式 | `CMD_WIFI_STA` | — | `ACK_WIFI_STA` | 4010-4049 |

`time` 的取值集合来自设置窗口的下拉框（🟩）：

- `YOCamDeskSet` 的 `CBTime` 绑定 5 个资源键：`Immediately`(立刻) / `Str1min` / `Str3min` / `Str5min` + 1 项，
  保存时映射为 `SleepTimeIndex`：`0→0`、`1→1`、`2→3`、`3→5`、`4→255`（🟩 2424-2447，
  `Language/zh-CN.xaml:29,32,33,34` 给出 5分钟/3分钟/1分钟/立刻）。
- 默认值 `SleepTimeIndex = 1`（`CDConfig.cs:10`）。

### 7.2 厂商实际怎么用（关键结论）

- 🟩 **`SleepTimeIndex` 只被保存，从未参与任何协议命令**——`SendDormancy` 只有两处调用：
  - 应用退出：`SendDormancy(1)`（1337 行）
  - `DisConnect()`：`SendDormancy(0)`（3770 行）
- 🟩 **`DormancyTimer`（`System.Timers.Timer`）从未创建/启动**，`DormancyTimerWork` 是死代码（3554-3578、4094）。
- 🟩 因此"休眠时间"这个设置项在本版本**不生效**。

⇒ 我们的实现建议：
1. 插件**不要**在正常使用中发 `CMD_LIVE_STOP`（会让设备进入待机/停流）。
2. 若插件关闭、且希望省电，可发 `CMD_LIVE_STOP0`（厂商断开时行为）或 `CMD_LIVE_STOP255`（明确要求不休眠，行为最安全可预期，🟥 待实机确认真实含义）。
3. 若设备已被别人（厂商软件/上一会话）置为待机，用 `CMD_RTSP_TRANS_START` 唤醒即可（与开流同一条命令）。

### 7.3 心跳保活与断流的关系（Lead 关心的第 4 点）

静态分析能给出的边界：

- 🟩 只要 TCP 8888 连接在，`DCUDPBackGround` 就**每 1 秒**发 `CMD_DATA_SYNC`——**与 RTSP 是否在播无关**，从连上到断开全程都在发。
- 🟩 RTSP 侧的保活完全交给 FFmpeg 的标准 RTSP 保活（`SET_PARAMETER`/`OPTIONS`），厂商代码里没有任何自定义 RTSP 保活。
- 🟥 **无法静态确定**：设备固件是否会在"N 秒收不到 `CMD_DATA_SYNC`"时主动断流/待机。厂商的掉线判定（5 次无回包）是 **PC 侧**逻辑，不代表设备侧。
- 结论（保守实现）：**插件必须同时维持「1s 的 `CMD_DATA_SYNC` 心跳」与「标准 RTSP 会话」**；不要用"只拉 RTSP 不开控制通道"的方案——控制通道还承担 8888 上的拍照通知。

---

## 8. AP/WiFi 配置与二维码配对

### 8.1 配对流程图

```
   ┌──────────── 教室集控/白板 ────────────┐
   │ eCControl (外部进程)  UDP 127.0.0.1:34213
   │     ▲  "YOCameraDesktop_AP_Get"
   │     └─ "eCControl_AP_<SSID> <密码>"       ← 校园网/黑板 AP 的账号
   └───────────────┬──────────────────────┘
                   │ YOCameraDesktop 启动时问一次（UseAP=true 时，MainWindow.cs:372-417）
                   ▼
        CurConfig.WifiName / WifiPsw
                   │
                   ▼
   二维码内容 = "{SSID} {密码} {8位配对码}"     ← QRCode.StartEnCode，UTF-8 / BYTE / EC=M / scale=4
                   │  （全屏窗口 YOCamDeskConnect，提示"请将快拍仪底座对准上方椭圆区域"）
                   ▼
             展台摄像头扫码（自身摄像头）
                   │  ① 在自己内部保存 SSID / 密码 / 配对码
                   │  ② 以 STA 身份加入该 WiFi
                   ▼
            与 PC 同一网段后：PC 广播 CMD_GETIP{配对码} → 设备回 ACK_GETIP
```

### 8.2 二维码内容格式（🟩）

```csharp
// MainWindow.cs:3786-3810 / 3813-3837
text = string.Concat(this.WinAPName, " ", this.WinAPPass, " ", this.Pincode);
```

即：**`SSID` + 单个空格 + `密码` + 单个空格 + `8 位配对码`**，例如：

```
Classroom-AP 12345678 92271885
```

编码参数（🟩 `QRCode.cs:15-40`）：

| 参数 | 值 |
|---|---|
| 内容编码 | `Encoding.UTF8` |
| 模式 | `QRCodeEncoder.ENCODE_MODE.BYTE` |
| 纠错级别 | `ERROR_CORRECTION.M` |
| 缩放 | `QRCodeScale = 4` |
| 版本 | `0`（自动） |
| 显示条件 | 内容长度 `> 16` 才显示（`YOCamDeskConnect.cs:34-38`） |

注意事项：
- 🟩 若 `WifiName` 或 `WifiPsw` 为空，`GetQRCode(name,psw)` 返回 `""`（3815-3821）⇒ 不显示二维码，只能用设置框手工填。
- 🟩 无参 `GetQRCode()` 会把名字/密码**硬编码成 `"iPhone"` / `"20181111"`**（3791-3792、4124-4127）——测试遗留，不要模仿。
- 🟨 设备扫码后是否还要求"配对码长度/字符集"限制未知；配对码固定 8 位数字（10,000,000–99,999,999）。
- 🟨 SSID/密码里若含中文，二维码内容是 UTF-8，而 `CMD_SETWIFIWIFINAME` 用的是 GBK 字节——两者编码不同，**中文 SSID 可能不兼容**，插件应优先支持 ASCII SSID。

### 8.3 让设备加入指定 AP / 回普通模式（🟩 3707-3758）

```
PC → 设备（TCP 8888）：
  SSID 非空:  "CMD_SETWIFIWIFINAME:" + <SSID 的 GBK 字节，最多 33 字节> + "PASSWD:" + <密码 GBK 字节>
  期望:       "ACK_SETWIFI"（失败时回包里含 "FAIL"）
  SSID 为空:  "CMD_WIFI_STA"
  期望:       "ACK_WIFI_STA"
```

厂商 UI 里的调用（🟩 518-550）：

- `CBNetMode.SelectedIndex == 1`（"视频展台wifi名称"）→ `SetAPMode(TbSSID.Text, "12345678")`
  ⇒ **密码被硬编码为 `12345678`**（532 行）。这是厂商实现的偷懒/遗留，实际部署若 AP 密码不同则需要我们自己发命令。
- 其它索引 → `SetAPMode("", "")` → 发 `CMD_WIFI_STA`。

⚠️ **`SetNormalMode()` 的死循环 bug**（🟩 4010-4049）：等待 `ACK_WIFI_STA` 的循环是

```csharp
int num = 0;
while (!this.SetWIFIStaSucess && num < 100) { Thread.Sleep(10); }   // num 从未自增！
```

若设备不回 `ACK_WIFI_STA`，**UI 线程会永久卡死**。我们的实现务必用超时。

### 8.4 "AP 模式"复选框的坑（重要，别照抄）

🟩 `CurConfig.APMode == true` 时，`Restart()` **根本不创建 UDP 搜索 socket**（3105-3119）：

```csharp
bool flag = !MainWindow.CurConfig.APMode;
if (flag) { this.Run = true; this.SearchSocket = new UdpClient(...); ... }
// APMode 下：没有 SearchSocket，DCSearchBackGround 不起
```

而 `DClientIP` **只在收到 `ACK_GETIP` 时被赋值**（3456），全文件没有其它赋值点 ⇒
**勾选"AP 模式"后本版本永远无法发现设备、也就永远连不上**。

⇒ 插件结论：**不要实现/继承这个开关**，始终走 UDP 广播发现。
（若现场确实需要"PC 直连设备热点"的 AP 模式，应自行确定设备热点下的固定 IP，绕过发现，直接 TCP 8888 —— 🟥 待实机确认设备热点 IP 段/网关。）

### 8.5 配对码（Pincode）生命周期

- 🟩 生成：`Random.Next(10000000, 99999999).ToString()`（3602-3607 / 177-181），首次运行生成一次。
- 🟩 持久化：`%USERPROFILE%\Documents\YOFiles\YOCamSet.xml` 的 `<PinCode>`（`CDConfig.cs:22`）。
  **本机实测该文件存在且 `PinCode = 92271885`**（见 §12），说明"配对码跨会话稳定"。
- 🟩 下发路径：只出现在二维码内容中 + 每次 `CMD_GETIP` 广播中。
- 🟨 设备端极可能**用配对码过滤应答**（否则多台展台/多个 PC 无法配对，且 QR 里放配对码没有其它用途）；未匹配时应答为"忽略"，无错误回包。
- 插件可用的三种拿到配对码的办法（优先级从高到低）：
  1. **读 `YOCamSet.xml` 的 `<PinCode>`**（厂商软件装过的机器直接可用，最省事）；
  2. 让用户在插件里输入 8 位配对码（新设备首次配对时，仍建议用厂商软件/我们自绘二维码完成配对）；
  3. 兜底：以空/任意码尝试 `CMD_GETIP`（🟥 待验证设备是否宽容）。

---

## 9. 消息接收函数全景（`DCMsgAnalysis`，UDP/TCP 共用）

参考实现时必须知道：**同一个函数同时处理 UDP(12811) 和 TCP(8888) 的包**（3595 与 3405 都调它），
判定完全靠 `Contains` 字符串，且是 **`if/else if` 链**（先命中先返回）：

| 优先级 | 匹配 | 处理 |
|---|---|---|
| 1 | `ACK_GETIP` | 取设备 IP + `DCWindowsIP`，`IsDCFinded=true`，回调（→ Connect + RTSP） |
| 2 | `DATA_SYNC` | `GetHeartBeatBack=true`，`UnRecHBCount=0` |
| 3 | `ACK_LIVE_STOP` | 什么都不做（空分支，3477-3479） |
| 4 | `ACK_RTSP_TRANS_START` | `RTSPStartSucess=true` |
| 5 | `START_TRANSPHOTO` | 回调（参数是恒为 0 的 `DCFileSize`，无人处理） |
| 6 | `END_TRANSPHOTO` | 回调（同上） |
| 7 | `ACK_WIFI_STA` | `SetWIFIStaSucess=true` |
| 8 | `ACK_SETWIFI` | 含 `FAIL` → `SetWIFIADSucess="fail"`，否则 `"succ"`（该字段无人读取） |

⚠️ 因为 `START_TRANSPHOTO` 同时是"TCP 通知"和"TCP 11111 文件头"的一部分，如果我们在 8888 的接收缓冲里做子串扫描，要注意**文件头不会出现在 8888 上**（两条不同连接）。

---

## 10. 报文逐字段汇总表（可直接照着抓包核对）

| 阶段 | 层 | 原始字节（示例，ASCII） | 长度 | 说明 |
|---|---|---|---|---|
| 发现请求 | UDP→广播:12811 | `43 4D 44 5F 47 45 54 49 50` + `39 32 32 37 31 38 38 35` = `CMD_GETIP92271885` | 18 | 无终止符 |
| 发现应答 | UDP→PC | `31 39 32 2E 31 36 38 2E 30 2E 32` + `ACK_GETIP` = `192.168.0.2ACK_GETIP` | 22 | 前缀=PC IP，设备IP取源地址 |
| 心跳 | TCP:8888 | `CMD_DATA_SYNC` | 13 | 1s/次 |
| 心跳回包 | TCP:8888 | `DATA_SYNC` | 9 | `Contains` 即可 |
| 开流 | TCP:8888 | `CMD_RTSP_TRANS_START` | 20 | — |
| 开流应答 | TCP:8888 | `ACK_RTSP_TRANS_START` | 20 | 置位后才去 open RTSP |
| 待机 | TCP:8888 | `CMD_LIVE_STOP0`（立刻）/ `...1` / `...255` | 14~16 | 分钟数拼在末尾，无分隔符 |
| 待机应答 | TCP:8888 | `ACK_LIVE_STOP` | 13 | 厂商忽略 |
| 普通模式 | TCP:8888 | `CMD_WIFI_STA` | 12 | — |
| 普通模式应答 | TCP:8888 | `ACK_WIFI_STA` | 12 | — |
| 设 WiFi | TCP:8888 | `CMD_SETWIFIWIFINAME:` + SSID + `PASSWD:` + Pwd | 变长 | SSID 截断 33 字节 |
| 设 WiFi 应答 | TCP:8888 | `ACK_SETWIFI` / `...FAIL` | 11+ | — |
| 拍照通知 | TCP:8888（设备→PC） | `START_TRANSPHOTO` / `END_TRANSPHOTO` | 16 / 14 | 与文件本体分离 |
| 文件头 | TCP:11111（设备→PC） | `START_TRANSPHOTO` + `0209408` | 23 | 🟨 头前 16B 待实机确认 |
| 文件体 | 同上 | JPEG 原始字节 | 变量 | 无转义 |
| 文件尾 | 同上 | `END_TRANSPHOTO` | 14 | 可忽略 |

---

## 11. `JCamera\JCameraW.dll` 分析结论（回答"是不是另一套 USB/有线采集 SDK"）

### 11.1 PE 事实（🟩 直接解析 PE 头部、导入/导出表得到）

| 项目 | 结果 |
|---|---|
| 架构 | **PE32 / x86**（Machine=0x14C），有 `.reloc`，无 CLR |
| 导出表 | 8 个 cdecl 导出（`JCameraW.lib` 亦印证）：`JCameraInit`、`OpenCamera`、`CloseCamera`、`GetFrame`、`SetCallBackFun`、`GetCameraDPIList`、`SetCameraDPI`、`GetWifiAPList` |
| 关键导入 | **`Wlanapi.dll`**：`WlanOpenHandle` / `WlanEnumInterfaces` / `WlanGetAvailableNetworkList` / `WlanFreeMemory` / `WlanCloseHandle`（**只有"枚举 AP"，没有 `WlanConnect`**）<br>**`WS2_32.dll`**：按序号导入 15 个 socket 函数；另有 `gdiplus.dll`、`WINSPOOL.DRV`（`OpenPrinterW/DocumentPropertiesW/ClosePrinter`）、`USER32/GDI32/OLEAUT32` |
| 运行时痕迹 | MFC 8.0（`AfxControlBar80su` 等）、`ICameraDataHandler` / `CCamera` RTTI、路径串 `e:\jetion\SDK\WifiCamera`、`dWifiCameraExExit`、格式化串 `%d*%d` |
| **没有**的导入 | 无 `strmiids/ddraw/ole32`（DirectShow）、无 `setupapi/hid/usbmon`（USB）、无 `ksuser/avrt` ⇒ **不是 UVC/DirectShow 采集** |

### 11.2 托管侧用法（🟩 `JCamera.exe` 反编译，已另存于 `_research/tmp-yocam/jcam/`）

```csharp
[DllImport("JCameraW.dll", CallingConvention = CallingConvention.Cdecl)]
static extern void     JCameraInit();
[DllImport("JCameraW.dll", CallingConvention = CallingConvention.Cdecl)]
static extern void     OpenCamera();
[DllImport("JCameraW.dll", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr   GetFrame();            // → FRAME*
[DllImport("JCameraW.dll", CallingConvention = CallingConvention.Cdecl)]
static extern void     SetCameraDPI(int w, int h);
[DllImport("JCameraW.dll", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr   GetCameraDPIList();    // → CAMERADPIS*
[DllImport("JCameraW.dll", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr   GetWifiAPList(int index);

struct FRAME   { byte frame_data[4194304]; int frame_len; }       // 4 MiB 固定缓冲 + 实际长度
struct CAMERADPI{ int width; int height; }
struct CAMERADPIS{ int count; CAMERADPI item[16]; }
struct WIFIAP  { byte ssid[64]; }
struct WIFIAPLIST{ WIFIAP item[20]; int list_count; }
```

- 帧数据是 **JPEG 字节流**（`JCamera.exe` 直接把 `frame_data` 塞进 `MemoryStream` → `BitmapImage`，每 20ms 取一帧）。
- 分辨率列表 9 档：`360*240 / 640*480 / 800*600 / 1280*720 / 1600*1200 / 1920*1080 / 2048*1536 / 2592*1944 / 3264*2488`；
  当前安装的 `Config.xml` 里 `<JCameraResolution>3</JCameraResolution>`（= 1280×720）。
- 与白板的集成：`WriteBoard\NativeMethods.cs:83` P/Invoke `JCameraInit`；`WriteBoard` 用窗口标题 `YiouJCamera` 找已打开的窗口，否则启动 `JCamera\JCamera.exe`（`WinCmd.cs:2544-2550`）。

### 11.3 判定

> **`JCameraW.dll` 不是 USB/有线采集 SDK，而是第三方（`e:\jetion\SDK\WifiCamera`，Jetion = 厂商 SDK 名）
> 的"WiFi 相机"SDK：WiFi 扫 AP + 原始 socket 通信 + GDI+ 解码 JPEG 帧 + 固定回调/轮询取帧。**
>
> 它服务于**上一代展台**（`JCamera.exe` 2.0.10.8 窗口名 `YiouJCamera`），
> 与我们目标设备（`YOCameraDesktop.exe` 1.0.10.19，RTSP/H.264）是**两条互斥链路**，
> 由 `Config.xml` 的 `<NewCamera>` 二选一。
>
> 对 ICCCE 插件的意义：
> 1. 不需要（也不应该）去调用 `JCameraW.dll`——它 x86-only、需要设备处于老协议、且与 RTSP 链路无关；
> 2. 它证明现场可能存在两种设备：**若抓到 554 没有 RTSP 流，先确认设备代数**（老设备只出 JPEG，不出 RTSP）；
> 3. `GetWifiAPList` 的存在说明"让设备加入指定 AP"的产品逻辑在上一代就已存在，与本代 `CMD_SETWIFIWIFINAME` 对应。

---

## 12. 实机联调状态（本机，2026-10-24）

### 12.1 环境与探测结果

| 检查项 | 结果 |
|---|---|
| 网卡 | 以太网 `192.168.0.2/24`，网关 `192.168.0.1`；另有代理软件虚拟网卡 `FlClash 198.18.0.1/30`（**会抢默认路由**） |
| 邻居表 | 仅 `192.168.0.1`（网关）、`192.168.0.114`（`FC-A3-86-E4-6D-CB`，疑似普通 PC） |
| UDP 探测 | 已向 `255.255.255.255:12811` 与 `192.168.0.255:12811` 发送 `CMD_GETIP92271885` 和 `CMD_GETIP` 各一次，监听 5 秒：**零回包** |
| TCP 探测 | `192.168.0.114:554/8888/11111`、`192.168.0.1:554/8888` 均 **closed** |
| 本地监听 | `34213/12811/11111` 无进程监听；`YOCameraDesktop/eCControl/WriteBoard/JCamera` 均未运行 |
| 历史痕迹 | `%USERPROFILE%\Documents\YOFiles\`：<br>• `YOCamSet.xml`：`PinCode=92271885`、`UseAP=true`、`APMode=false`、`SleepTimeIndex=1`<br>• `YiouCameraLog\2026-10-23.txt`：`以太网 192.168.0.2` / `开始接受udp:255.255.255.255:12811` / **`无法搜索到集控`** / `rtsp线程已退出`（**没有任何 `udp消息：` 记录 ⇒ 从未收到过设备回包**） |
| 结论 | **本机无该展台设备，也无集控进程，无法实机联调；本文件全部结论均为静态分析 + 代码交叉验证。** |

> 记录（不算"可联调设备"）：`192.168.0.114` 是一台普通主机（554/8888/11111 全 closed），不具备展台特征。

### 12.2 待实机验证清单（按优先级排序）

| # | 待验证项 | 现状 / 依据 | 验证方法 | 影响 |
|---|---|---|---|---|
| V1 | **配对码是否被设备校验** | 🟨 QR 里放配对码 + 广播里带配对码，推测设备按码过滤 | 用正确码/错误码/空码各广播 10 次，看是否回 `ACK_GETIP` | 决定插件能否自行发现（配错码可能永远发现不了） |
| V2 | **23 字节文件头的真实内容** | 🟨 16B(`START_TRANSPHOTO`) + 7B ASCII 长度 | 设备按一次拍照，抓 TCP 11111 前 23 字节 hexdump | 决定文件解析正确性 |
| V3 | 长度字段填充方式（`%07d` / 空格 / 0xFF） | 🟨 | 同上 hexdump | 决定是否要兼容填充 |
| V4 | RTSP 是否要求 Digest 鉴权 | 🟩 URL 无凭据、无 AVDictionary 凭据 | `ffprobe` 看是否 401；抓 DESCRIBE 应答 | 鉴权则需实现/索取账号 |
| V5 | 实际编码/分辨率/帧率 | 🟨 H.264 / 1920×1080 / 未知 fps | `ffprobe rtsp://...` 看 `codec_name,width,height,avg_frame_rate` | 解码器选择、虚拟摄像头能力声明 |
| V6 | 是否存在 `stream0` 之外的流（第二路/子码流） | 🟩 代码只有 stream0 | 试 `stream1/stream2/h264/ch0` 等常见路径 | 若存在可用于低带宽预览 |
| V7 | RTP over UDP 还是 TCP | 🟩 ffmpeg 默认 UDP | 抓包看 RTP 端口；`-rtsp_transport tcp` 对比 | 网络差/丢包时的回退策略 |
| V8 | **停止 `CMD_DATA_SYNC` 心跳是否导致断流/待机** | 🟩 无法静态确定 | 停止心跳，观察 RTSP 是否在 N 秒后断 | 决定插件是否必须常驻心跳线程 |
| V9 | `CMD_LIVE_STOP` 的 `0/1/3/5/255` 精确语义 | 🟩 代码只用了 0 和 1 | 逐个发送后观察设备是否关机/停流/恢复 | 插件退出时是否该发、发哪个 |
| V10 | 设备能否被唤醒（`CMD_RTSP_TRANS_START` 是否等于唤醒） | 🟩 代码用它做 WakeUpDC | 待机后发该命令，看流是否恢复 | 长时间挂载的恢复策略 |
| V11 | `CMD_SETWIFIWIFINAME` 中 SSID 的 33 字节字段与 `PASSWD:` 的边界 | 🟨 源码拼装方式 | 抓包 + 用 32/33/34 字节 SSID 测试 | 只在需要改设备网络时影响 |
| V12 | 二维码内容是否有额外校验（版本/长度/分隔符个数） | 🟩 `"ssid psw pin"` | 用自绘二维码让设备扫，看是否入网 | 插件自建配对 UI 的可行性 |
| V13 | 设备热点模式的固定 IP / 网关 | 🟩 APMode 分支不可用 | 让设备进 AP 模式，PC 连其热点后扫网段 | 有线/无集控场景的兜底连接 |
| V14 | TCP 8888 是否接受**多客户端**（厂商软件与插件同时连） | 🟩 无依据 | 同时开两份客户端观察 | 是否要与厂商软件共存 |
| V15 | 设备是否会在 TCP 8888 上主动发别的通知 | 🟩 代码只识别 8 种 | 长时间抓包 | 保持向前兼容 |

---

## 13. 插件实现建议（与协议相关的部分）

1. **线程模型**：一条 UDP 发现循环（1s）、一条 TCP 接收线程、一条心跳定时器、一条 RTSP 解码线程、一个 TCP 11111 监听线程。全部 `IsBackground = true`，用 `CancellationToken` 而非 `Thread.Abort()`（厂商大量用 `Abort`，是崩溃源）。
2. **多网卡/VPN 是头号风险**：本机就存在 `FlClash` 默认路由抢占。必须：
   - 让用户选择网卡（厂商的做法，`CGetNetWorks` 枚举 Ethernet/Wireless80211，`MainWindow.cs:184-217`）；
   - UDP 绑选定网卡发送；TCP 绑选定网卡；RTSP 设 `localaddr`；11111 监听 `IPAddress.Any` 但记录来源 IP 校验。
3. **不要发 `CMD_LIVE_STOP`**（除非明确要做"关闭即待机"），**不要碰 APMode**。
4. **RTSP 失败重试策略**：厂商是"收到 `TCPServer_Miss`→断开→重新发现→收到 `TCPServer_OnLine`→重新 `CMD_RTSP_TRANS_START` + 重开流"。插件应实现同样的状态机（建议 1s/3s/5s 退避，最多 60 次，与 `RTSPFFmpegFun` 的 60 次上限对齐）。
5. **解码**：可直接 `FFmpeg.AutoGen`（复用厂商参数：`buffer_size/stimeout/max_delay/localaddr`）+ `dxva2` 硬解；注意 FFmpeg LGPL/GPL 合规。
6. **拍照**：本地抓帧（`RenderTargetBitmap`/解码帧 `BitmapSource`），不要等设备；同时**保留 11111 监听**以接收设备端按键拍照（这是设备端唯一主动推给 PC 的数据）。
7. **诊断日志**：把每个收发报文（含 hexdump）落到插件日志，用于现场排查（厂商日志在 `%USERPROFILE%\Documents\YOFiles\YiouCameraLog\yyyy-MM-dd.txt`，可对照）。
8. **防火墙**：需要放行入站 TCP **11111**（设备回传照片）；出站 UDP 12811/任意、TCP 8888/554。

---

## 14. 可复制的最小验证脚本（PowerShell，实机到手即可跑）

```powershell
# 0) 选网卡 IP（把 192.168.0.2 换成实际）
$localIp = "192.168.0.2"; $pin = "92271885"

# 1) UDP 发现
$u = New-Object System.Net.Sockets.UdpClient([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($localIp),0))
$u.EnableBroadcast = $true; $u.Client.ReceiveTimeout = 1500
$payload = [Text.Encoding]::Default.GetBytes("CMD_GETIP$pin")
$dst = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Broadcast,12811)
[void]$u.Send($payload,$payload.Length,$dst)
$ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any,0)
try { $r = $u.Receive([ref]$ep); "ACK from $($ep.Address): $([Text.Encoding]::ASCII.GetString($r))"; $devIp=$ep.Address.ToString() }
catch { "no reply (设备不在线/配对码不匹配/广播被拦)"; exit }
$u.Close()

# 2) TCP 8888：心跳 + 开流
$t = [System.Net.Sockets.TcpClient]::new(); $t.Connect($devIp,8888); $s=$t.GetStream()
function Send($x){ $b=[Text.Encoding]::ASCII.GetBytes($x); $s.Write($b,0,$b.Length); $s.Flush() }
function Recv([int]$ms=1500){ $t.ReceiveTimeout=$ms; $b=New-Object byte[] 1024
  try { $n=$s.Read($b,0,1024); if($n -gt 0){ return [Text.Encoding]::Default.GetString($b,0,$n) } } catch {} return "" }
Send "CMD_DATA_SYNC";      "hb  -> " + (Recv)
Send "CMD_RTSP_TRANS_START"; "rtsp-> " + (Recv)

# 3) RTSP 探测（需已装 FFmpeg）
ffprobe -v error -rtsp_transport udp -show_entries stream=codec_name,width,height,avg_frame_rate `
        -of default=nw=1 "rtsp://${devIp}:554/stream0"
```

---

## 15. 证据索引（文件 + 行号）

反编译根目录：`_research/YOCameraDesktop-decompiled/YOCameraDesktop/`

| 文件 | 行号 | 内容 |
|---|---|---|
| `MainWindow.cs` | 50-51 | eCControl UDP 端点 `127.0.0.1:34213` |
| `MainWindow.cs` | 177-181 / 3602-3607 | 配对码生成（8 位随机） |
| `MainWindow.cs` | 184-217 | 枚举 Ethernet/WiFi 网卡（多网卡选择依据） |
| `MainWindow.cs` | 332-355 | 渲染路径选择（D3D/YV12 1920×1080 → WriteBitMap → GDI） |
| `MainWindow.cs` | 366-417 | DeskTopNet 初始化 + eCControl AP 请求（`YOCameraDesktop_AP_Get` / `eCControl_AP_`） |
| `MainWindow.cs` | 518-550 | `SetAPMode(TbSSID.Text, "12345678")`（密码硬编码） |
| `MainWindow.cs` | 684-807 | `msgCallBackOperation`：ACK_GETIP→Connect+RTSP；TCPServer_OnLine/Miss；END_TRANSPHOTO→生成页面 |
| `MainWindow.cs` | 1167-1247 | `BtnTakePhoto_Click` = 本地 RenderTargetBitmap 抓帧（非设备命令） |
| `MainWindow.cs` | 1262-1317 | 二维码窗口与内容刷新 |
| `MainWindow.cs` | 1337 | 退出时 `SendDormancy(1)` |
| `MainWindow.cs` | 1478-1492 | AVIO 中断回调（10s） |
| `MainWindow.cs` | 1494-1535 | FFmpeg 打开 RTSP 的 AVDictionary（buffer_size/stimeout/max_delay/localaddr） |
| `MainWindow.cs` | 1568-1612 | 选视频流 / 兜底 H264+YUV420P / 兜底 1920×1080 |
| `MainWindow.cs` | 2424-2447 | `SleepTimeIndex` 映射 0/1/3/5/255 |
| `MainWindow.cs` | 3074-4158 | `DeskTopNet` 全类（协议主体） |
| `MainWindow.cs` | 3104-3179 | `Restart()`：APMode 时跳过 UDP 搜索 |
| `MainWindow.cs` | 3202-3229 | 1s 循环：心跳 / 重新搜索 / TCPServer_Miss/OnLine |
| `MainWindow.cs` | 3231-3375 | TCP 11111 收文件（23 字节头/长度解析/端点/尾标记） |
| `MainWindow.cs` | 3377-3436 / 3438-3551 | TCP 收包 + `DCMsgAnalysis` 全部分支 |
| `MainWindow.cs` | 3581-3599 | UDP 收包（`Receive(ref ep)` → 设备 IP） |
| `MainWindow.cs` | 3610-3625 | `SendSearchCmd` |
| `MainWindow.cs` | 3644-3689 | `Connect()`（无握手） |
| `MainWindow.cs` | 3691-3705 | `SendRTSPStartCmd`（100×10ms 超时） |
| `MainWindow.cs` | 3707-3758 | `SetAPMode`（WiFi 下发两种形态） |
| `MainWindow.cs` | 3785-3837 | `GetQRCode` ×2（`"ssid psw pin"`） |
| `MainWindow.cs` | 3839-3854 | `GetRTSPAddr` |
| `MainWindow.cs` | 3856-3907 | `IsConnected`（心跳/5 次掉线） |
| `MainWindow.cs` | 3909-3935 / 3943-3967 | `SendDormancy` / `WakeUpDC` |
| `MainWindow.cs` | 3938-3941 | `SendGetImgCmd` = `NotImplementedException`（**PC 无法触发拍照**） |
| `MainWindow.cs` | 4010-4049 | `SetNormalMode`（含死循环 bug） |
| `MainWindow.cs` | 4051-4158 | 字段：端口/缓冲区/心跳字节/标志位 |
| `QRCode.cs` | 15-40 | 二维码编码参数 |
| `YOCamDeskConnect.cs` | 34-38 | 二维码显示条件 `Length > 16` |
| `CDConfig.cs` | 6-38 | 持久化字段（PinCode/UseAP/APMode/WifiName/WifiPsw/SSID/SleepTimeIndex） |
| `LogHelper.cs` | 11-49, 91 | 日志路径 `%USERPROFILE%\Documents\YOFiles\YiouCameraLog\<date>.txt` |
| `app.config` / `YOCameraDesktop.csproj` | — | `TargetFrameworkVersion=v4.5.2`、`PlatformTarget=AnyCPU`（PE32，未设 `Prefer32Bit=false` ⇒ 通常以 **32 位进程** 运行，与其 `FFmpeg\bin\x86` 一致）、`AllowUnsafeBlocks=true`、版本 1.0.10.19 |

`JCamera.exe` 反编译（本任务另存）：`_research/tmp-yocam/jcam/JCamera/MainWindow.cs`（P/Invoke 40-65、FRAME 810-818、DPI 列表 720、帧解码 104-146）。
`WriteBoard.exe` 反编译（本任务另存）：`_research/tmp-yocam/writeboard/WriteBoard/`（`WinCmd.cs:2528-2553` NewCamera 分支、`WinCmd.cs:323-371` eCControl AP 查询、`NativeMethods.cs:83` JCameraInit、`AppConfigHelper.cs:148,211`）。

---

## 16. 附录：术语与产品线索

| 术语 | 含义 |
|---|---|
| DC / DClient / DCWindows | Device Camera / 设备端 / 设备眼中的 Windows 端（厂商命名） |
| 快拍仪 | 厂商对该展台的称呼（`Language/zh-CN.xaml:227` `YOCamera=快拍仪`） |
| `TCPServer_OnLine` / `_Miss` | 本地 UI 文案键：连接到设备 / 已与设备断开连接（`:194-195`） |
| eCControl | 教室集中控制软件进程名，通过 `127.0.0.1:34213` 提供黑板 AP 的 SSID/密码 |
| Jetion | `JCameraW.dll` 第三方 WiFi 相机 SDK 的厂商标识（`e:\jetion\SDK\WifiCamera`） |
| 相关进程/信号量 | `YOCameraDesktop.exe`（窗口名 `YODesktopCamera`）、`JCamera.exe`（`YiouJCamera`）、信号量 `YOPPTControl_PPTShow`、`YOCamSendPic`（照片送去白板打印，非协议） |

---

### 变更记录

- 2026-10-24 初版：完成发现/控制/RTSP/文件回传/待机/WiFi/二维码全链路静态逆向，完成 `JCameraW.dll` PE 分析，完成本机可联调性探测（结论：无设备）。
- 明确标注：**只有 §6.1 的 23 字节头内容（V2/V3）与配对码校验（V1）是没有代码直接证据的强推断**，其余结论均有文件+行号依据。
