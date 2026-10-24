# YiouCamera.Core

驿欧无线视频展台（YOCameraDesktop 1.0.10.19）的**采集内核**：纯 .NET（`net10.0`）实现
「UDP 发现 → TCP 8888 控制/心跳 → RTSP 拉流 → H.264 解码 → 共享内存帧」全链路。
无 WPF、无 ICCCE SDK、无第三方 NuGet 依赖，供 `YiouCamera.Plugin` 调用。

- 接口契约：`docs/CONTRACTS.md` §3（冻结）
- 协议依据：`_research/yocam-protocol.md`（1065 行，逐条对齐）
- 设备事实基线：`docs/PLAN.md` §3.1
- 共享内存布局：`src/YiouCamera.Vcam/vcam_shm.h`（唯一事实来源，本模块只读）

```
src/YiouCamera.Core/
├─ Contracts.cs                  Frame / IFrameSource / IDeviceDiscovery / DeviceInfo / IDeviceControl
├─ YocamProtocol.cs              端口、命令字面量、发现报文构造/解析（ASCII/Latin1）
├─ YocamPinCode.cs               配对码：YOCamSet.xml → 配置 → 随机
├─ LocalNetwork.cs               网卡枚举/自动挑选/子网广播
├─ UdpDeviceDiscovery.cs         UDP 12811 发现（1s 节拍，设备 IP=源地址）
├─ TcpDeviceControl.cs           TCP 8888 控制（缓冲累积+全量扫描、心跳、开流 ACK 重试）
├─ FfmpegProcessFrameSource.cs   外挂 ffmpeg 解码（bgr24 rawvideo，UDP→TCP 回退）
├─ VcamShm.cs                    共享内存头布局常量/快照解析（对照 vcam_shm.h）
├─ SharedMemoryFrameSink.cs      唯一 SHM 写入者（seqlock、4 槽、占位帧、回读校验）
└─ YiouBoothSession.cs           编排 + 断线重连（1s/3s/5s 退避）
tests/Simulator/                 设备端仿真器 + 无真机 selftest
```

---

## 1. 构建

```powershell
dotnet build src\YiouCamera.Core\YiouCamera.Core.csproj -c Release
dotnet build tests\Simulator\YiouCamera.Simulator.csproj -c Release
```

环境：.NET SDK 10.0.402（TFM `net10.0`，`AllowUnsafeBlocks` 仅用于共享内存指针写入）。
ffmpeg：**生产由安装脚本释放到插件目录**；开发机自动解析顺序见
`FfmpegProcessFrameSource.ResolveFfmpegPath`（显式配置 → `YIOU_FFMPEG_PATH` → 程序目录 →
`PATH` → 开发机兜底 `C:\Program Files\Kdenlive\bin\ffmpeg.exe`）。
本次验证使用 ffmpeg 8.1.1（需 FFmpeg ≥ 5，`-fps_mode`/`-timeout` 参数）。

构建/自测日志在 `tests/Simulator/artifacts/`：
`build-core.log`、`build-simulator.log`、`selftest-final.log`、`shm-dump-verify.log`。

---

## 2. 快速开始

### 2.1 一行启动（插件用）

```csharp
using YiouCamera.Core;

var session = new YiouBoothSession(new YiouBoothSessionOptions
{
    PinCode = settings.PinCode,              // null = 自动读 %USERPROFILE%\Documents\YOFiles\YOCamSet.xml
    LocalBindAddress = settings.BindIp,      // null = 自动挑非虚拟网卡；多网卡/VPN 建议显式指定
    Width = 1280, Height = 720,              // 可切 1920×1080
    FfmpegPath = settings.FfmpegPath,        // "" = 自动解析
    // DeviceIpOverride = IPAddress.Parse("192.168.0.50"), // 已知设备 IP 时可跳过发现
});
session.StateChanged += s => host.Log($"booth -> {s}");
session.Faulted += msg => host.LogError(msg, null);
await session.StartAsync(ct);                // 立即返回；后台：发现→连接→开流→解码→写 SHM
// 插件手动截图：
// var frame = session.LatestFrame;          // BGR24/top-down；未出帧时 null
await session.StopAsync();                   // 或 await using
```

`YiouBoothSession` 启动即创建 `Local\YoCameraVcamFrames` 并每 200ms 写占位帧，
保证 vcam `MediaOpened` 后立刻有画面（契约要求 “先出占位帧”）。

### 2.2 分步使用

```csharp
// 1) 发现（UDP 12811；设备 IP 取 UDP 源地址）
var discovery = new UdpDeviceDiscovery(pinCode: "92271885", localBind: IPAddress.Parse("192.168.0.2"));
DeviceInfo? dev = await discovery.DiscoverAsync(TimeSpan.FromSeconds(15), ct);

// 2) 控制（TCP 8888，无握手）
await using var control = new TcpDeviceControl(new TcpControlOptions
{
    LocalBindAddress = IPAddress.Parse("192.168.0.2"),
});
if (await control.ConnectAsync(dev!, ct))
{
    // 心跳自动 1s 一次；连续 5 次无 DATA_SYNC ⇒ IsConnected=false + Faulted
    bool ready = await control.StartRtspStreamAsync(TimeSpan.FromSeconds(10), ct); // CMD_RTSP_TRANS_START + ACK
    await control.StopLiveAsync(minutes: 255);  // 0=立刻待机，255=不休眠（谨慎使用，协议 §7）
}

// 3) 解码帧源（外挂 ffmpeg）
await using var source = new FfmpegProcessFrameSource(dev!.RtspUrl, new FfmpegOptions
{
    Width = 1280, Height = 720,
    LocalBindAddress = IPAddress.Parse("192.168.0.2"),
});
source.FrameReady += frame => { /* 后台线程；BGR24/top-down/stride=(w*3+3)&~3 */ };
await source.StartAsync(ct);   // 等到第一帧或超时（默认 10s）

// 4) 写共享内存（唯一写入者；插件通常不需要手动创建，Session 已代劳）
using var sink = new SharedMemoryFrameSink();               // Local\YoCameraVcamFrames
sink.PublishFrame(frame);
sink.PublishPlaceholder(1280, 720);                         // 无信号占位帧
```

### 2.3 数据类型（契约 §3）

| 类型 | 关键成员 |
|---|---|
| `Frame` | 字段 `Width/Height/Stride/Buffer/FrameIndex/TimestampMs`；**BGR24、top-down、stride=(w*3+3)&~3、Buffer.Length=stride*h** |
| `IFrameSource` | `State`、`event FrameReady`、`StartAsync/StopAsync/DisposeAsync` |
| `IDeviceDiscovery` | `DiscoverAsync(timeout, ct)` |
| `IDeviceControl` | `ConnectAsync`、`StartRtspStreamAsync`、`StopLiveAsync`、`IsConnected`、`HeartbeatAlive` |
| `DeviceInfo` | `DeviceIp`、`PcIp`、`RtspUrl => rtsp://{ip}:554/stream0` |

附加（非契约冻结项，仅做加法）：`YiouBoothSessionOptions`、`BoothSessionState`、
`TcpControlOptions`、`UdpDiscoveryOptions`、`FfmpegOptions`、`VcamShmLayout`、
`SharedMemoryFrameSink.TryReadHeader/TryReadLatestFrame`、`UdpDeviceDiscovery.PinCode` 等。

---

## 3. 协议实现要点（对照 `_research/yocam-protocol.md`）

### 3.1 发现（§3，UDP 12811）

- 载荷 `"CMD_GETIP" + 8 位配对码`（18 字节 ASCII；GBK 对纯 ASCII 等价），**1 秒一次**；
- 目标：`255.255.255.255:12811` + 子网定向广播（`x.y.z.255`，可选）；
- 应答 `<pcIp>ACK_GETIP`：**设备 IP 必须取 UDP 源地址**（`UdpReceiveResult.RemoteEndPoint`），
  `PcIp` 仅作文本前缀记录；
- 绑定 `IPAddress? localBind`，多网卡/VPN（如 FlClash）必须显式指定；未指定时自动挑
  「有网关、非虚拟」的以太网/无线网卡；自测/仿真绑 loopback 时额外单播 `127.0.0.1:12811`。

### 3.2 控制通道（§4，TCP 8888）

- **无握手、无长度前缀、无分隔符**，纯 ASCII 文本流；
- 厂商缺陷：单次 `Receive` 直接 `Contains`（粘包只处理第一条、拆包匹配不到）→
  本实现用 `StringBuilder` 累积 + **按出现位置最早匹配消费**，粘包/拆包都能正确识别
  （仿真器故意把 `ACK_RTSP_TRANS_START` 拆成两个 TCP 段验证）；
- 心跳：连上即每 1s 发 `CMD_DATA_SYNC`；连续 **5 次**无 `DATA_SYNC` 应答 ⇒
  `IsConnected=false`、触发 `Faulted`/`Disconnected`（等价厂商 `UnRecHBCount==5`）；
- 开流：每次尝试发 `CMD_RTSP_TRANS_START`，等 `ACK_RTSP_TRANS_START` 窗口 1s
  （等价厂商 100×10ms 忙等）；失败间隔 1s 重试，最多 60 次或 `timeout` 预算耗尽；
- `CMD_LIVE_STOP{0..255}`、`CMD_WIFI_STA`、通用 `SendCommandAsync` 已实现；
  `CMD_SETWIFIWIFINAME` 的 SSID 字节拼接（GBK/33 字节截断）未在 v1 暴露（协议 §12 V11 待真机）。

### 3.3 RTSP（§5）

- URL `rtsp://{ip}:554/stream0`，无凭据；
- ffmpeg 参数按厂商 + 建议：
  `-rtsp_transport udp -fflags nobuffer -flags low_delay -max_delay 1000000 -buffer_size 1655360
  -timeout 5000000 -i <url> -an -sn -dn -vf scale=W:H -fps_mode passthrough -f rawvideo -pix_fmt bgr24 pipe:1`；
- 10s 无数据由看门狗杀进程重启（厂商 AVIO 中断 10s）；
- UDP 失败/断流后按 **UDP→TCP 回退**（`-rtsp_transport tcp`，协议 §5.3 建议，厂商未做）；
- stdout 异步读 + stderr 异步消费（避免管道阻塞），所有等待可取消，`Kill(entireProcessTree)` 清理。

> ⚠️ **ffmpeg 8.x 的 RTSP demuxer 已移除 `-localaddr` AVOption**（厂商旧版 FFmpeg 有）。
> 本实现启动时探测（`ffmpeg -h demuxer=rtsp`），只有支持时才传 `-localaddr`；不支持时
> RTSP 走系统默认路由，控制通道仍绑定指定网卡。多网卡/VPN 现场建议用路由表（`route add`）
> 把设备网段指到正确接口，或把该网卡设为默认路由优先级更高。

### 3.4 共享内存（`src/YiouCamera.Vcam/vcam_shm.h`）

布局：`[ 256B header ][ slot0 ][ slot1 ][ slot2 ][ slot3 ]`，槽按 **1920×1080×3** 分配
（`stride=5760，slotBytes=6,220,800`，总 23.7 MB）；720p 只写前 `stride*h` 字节，头里带实际
`width/height/stride`。seqlock：

```
producer: writeSeq++(奇) → 写头字段/槽数据 → writeSeq++(偶)
consumer: seq1=writeSeq; 奇数跳过; slot=(seq1/2-1)%4; 拷贝; seq2=writeSeq; seq2!=seq1 ⇒ 撕裂丢弃
```

| offset | 字段 | 本实现写入值 |
|---|---|---|
| 0 | magic | `0x4D414356`（'VCAM'） |
| 4 | version | `1` |
| 8 | headerBytes | `256` |
| 12 | slotCount | `4` |
| 16 | slotBytes | `6,220,800`（1920×1080×3） |
| 20/24/28 | width/height/stride | 实际分辨率与 `(w*3+3)&~3` |
| 32 | format | `0`（RGB24，内存中 BGR 字节序） |
| 36/40 | fpsNum/fpsDen | 如 30/1 |
| 44/48 | writeSeq/readSeq | seqlock / 消费端进度（本实现不写 readSeq） |
| 52 | timestamp100ns | 生产者单调时间戳（`Frame.TimestampMs*10000`） |
| 60 | frameIndex | 单调递增帧号（从 1 开始） |
| 68 | flags | bit0 = producer alive |

校验方式：`dotnet run --project tests/Simulator -- --shm-dump [--shm-name ...]`
逐字段按 `vcam_shm.h` 偏移硬编码校验（不依赖 Core 解析器），并回读最新帧统计像素；
selftest 还会用 FNV-1a 比对「最后发布帧」与「回读槽字节」完全一致。

> ⚠️ **行方向契约冲突（需要 vcam 侧确认）**：`vcam_shm.h` 里 `VCAM_FMT_RGB24` 的注释写
> “bottom-up rows”，而 `docs/CONTRACTS.md` §1 冻结为 **top-down**。本 Producer **按契约写
> top-down**。当前 `src/YiouCamera.Vcam/vcam_filter.cpp` 的 `CopyShmSlot` 逐行 `memcpy` 到
> `biHeight>0`（bottom-up）的 DirectShow 缓冲，未做翻转 ⇒ 若消费端不改，预览会上下颠倒。
> 已同步给 vcam-dev：二者取其一即可（消费端按 top-down 翻转 / 或契约改成 bottom-up）。

---

## 4. 设备仿真器 `tests/Simulator`

无真机闭环验证用。仿真器提供：

- **UDP 12811**：收到 `CMD_GETIP{pin}`（默认严格匹配配对码）→ 回 `<源IP>ACK_GETIP`；
- **TCP 8888**：`CMD_DATA_SYNC→DATA_SYNC`、`CMD_RTSP_TRANS_START→ACK_RTSP_TRANS_START`
  （**故意拆成 2 个 TCP 段**，验证拆包处理）、`CMD_LIVE_STOP{n}→ACK_LIVE_STOP`、
  `CMD_WIFI_STA→ACK_WIFI_STA`；支持多个控制客户端与强制断开（重连测试）；
- **RTSP 554**：最小 RTSP 服务（MJPEG，RFC 2435/RTP），支持 **RTP over UDP** 和
  **RTP over TCP interleaved**（验证 ffmpeg TCP 回退）；测试图由本机 ffmpeg 生成
  `testsrc2` 并缓存在 `%TEMP%\YiouCameraSimulator\mjpeg_1280x720_30`；
- 仿真器只模拟**传输/时序**，不模拟真机的 H.264 编码与待机行为。

### 4.1 命令

```powershell
# 无真机全链路自测（发现→心跳→开流→解码→SHM→占位帧→TCP 回退→Session→掉线重连）
dotnet run --project tests\Simulator -c Release -- --selftest

# 常驻设备仿真器（供手工/插件联调；--with-core 让 Core 会话写 SHM，可给 vcam filter 喂帧）
dotnet run --project tests\Simulator -c Release -- --serve --with-core

# 只起 RTSP（调试用）
dotnet run --project tests\Simulator -c Release -- --rtsp-only

# 读取/校验共享内存头（生产者运行中）
dotnet run --project tests\Simulator -c Release -- --shm-dump

# 其它：--pin 92271885 --width 1920 --height 1080 --ffmpeg <path>
#       --shm-name "Local\Other" --duration 30 --verbose --accept-any-pin
```

`--selftest` 默认使用**独立共享内存名** `Local\YiouCameraSelftest-<pid>`，避免与同时运行的
vcam Harness/插件生产者互相踩 seqlock；布局校验与契约名完全一致。要用契约名：
`--selftest --shm-name "Local\YoCameraVcamFrames"`（确认没有其它生产者后再用）。

### 4.2 selftest 覆盖点（49 项断言）

| 阶段 | 覆盖 |
|---|---|
| 0 | SDK/ffmpeg 环境、12811/8888/554 端口可用、配对码格式 |
| 1 | 仿真设备启动（UDP+TCP+RTSP） |
| 2 | UDP 发现、设备 IP=源地址、PcIp=文本前缀、RtspUrl 拼接 |
| 3 | TCP 无握手连接、1s 心跳、拆包 ACK 重组、`CMD_WIFI_STA`、`CMD_LIVE_STOP255` |
| 4 | ffmpeg 出帧 ≥20/3s、DESCRIBE/PLAY、seqlock 并发读、256B 头逐字段（偏移/magic/slotBytes/stride/writeSeq/frameIndex/flags）、槽像素 FNV 与发布帧一致、占位帧灰底+文字 |
| 4b | RTSP **TCP 回退**（interleaved RTP）可解码 |
| 5 | `YiouBoothSession` 进入 Running、持续出帧、`LatestFrame` 可取、写入 SHM、强制断开 8888+RTSP 后**自动重连恢复出帧**、Stop→Stopped |

### 4.3 本次验证结果（日志见 `tests/Simulator/artifacts/selftest-final.log`）

```
=== 结果：49 passed, 0 failed ===
```

串行流程耗时约 14s；无真机、真实 ffmpeg 解码、真实共享内存读写。

---

## 5. 已知限制

1. **SHM 行方向契约冲突**（见 §3.4）：本模块按冻结契约写 top-down；vcam 消费端需配合。
2. **ffmpeg 8.x 无 RTSP `-localaddr`**：RTSP 无法在 ffmpeg 层绑定指定网卡（控制通道可以）；
   多网卡/VPN 现场需靠路由表或默认网卡选择。旧版 FFmpeg（支持该选项）会自动传。
3. **单生产者假设**：`SharedMemoryFrameSink` 是唯一写入者；若同时运行厂商软件/另一个插件实例/
   vcam Harness 生产者，两个写者会破坏 seqlock（selftest 默认用独立名字规避）。
4. **拍照回传 TCP 11111 未实现**：协议 §6 设备→PC 照片接收不在 P1 范围；插件“拍照”走
   `LatestFrame` 本地抓帧（与厂商按钮行为一致）。V2/V3（23 字节头）因此未测。
5. **待机命令不主动发送**：协议 §7 建议正常使用不要发 `CMD_LIVE_STOP`；`StopLiveAsync` 仅供
   显式调用。会话 Stop 不改变设备休眠状态。
6. **Win32 依赖**：命名共享内存 `Local\...` 仅 Windows（产品本身 Windows-only，TFM 按契约保持
   net10.0；已抑制 CA1416）。`-localaddr` 探测会短暂启动一次 `ffmpeg -h`。
7. **帧分配**：ffmpeg 帧源每帧分配 `stride*h` 字节（720p ≈2.7MB×30fps）；后续可换
   `ArrayPool`/FFmpeg.AutoGen 降低 GC 压力。`FrameReady` 处理器必须快速返回。
8. **分辨率切换**：会话运行中不要改分辨率（会改 SHM 头 width/height/stride）；切换应
   Stop→改配置→Start。1920×1080 输出会同时受 ffmpeg scale 与 SHM 槽上限约束。
9. **仿真器只模拟传输**：RTSP 测试源是 MJPEG/720p（真机是 H.264/1080p），不模拟设备待机、
   拍照回传、配对失败重试等设备行为。
10. **`CMD_SETWIFIWIFINAME` 未暴露**：需要 GBK 编码与 SSID 33 字节边界（V11），v1 不做。
11. **未实现硬件解码**：v1 只验证外挂 ffmpeg 软解；接口层可换 FFmpeg.AutoGen。

---

## 6. 待真机验证清单（`_research/yocam-protocol.md` §12.2，V1–V15）

> 文档表格实际为 15 项（任务描述称 16 项，以协议文档为准）。
> 本代码全部结论来自静态分析 + 仿真器闭环；标 ✅ 的是仿真可覆盖的“实现正确性”，
> 标 🟥 的是**只有真机才能确认的协议行为**，标 🟨 为强推断。

| # | 项 | 本项目状态 |
|---|---|---|
| V1 | 配对码是否被设备校验 | 实现按码过滤（仿真器模拟严格匹配）；🟥 真机确认错码/空码是否忽略。`--accept-any-pin` 可做对照。 |
| V2 | 23 字节文件头真实内容 | ❌ 未实现 TCP 11111 照片接收（P1 之外）；🟥 |
| V3 | 长度字段填充方式 | ❌ 同上；🟥 |
| V4 | RTSP 是否要 Digest 鉴权 | 代码不加凭据；🟥 真机 `ffprobe` 看 401 |
| V5 | 实际编码/分辨率/帧率 | 解码后端不假设编码（ffmpeg 自适应）；强制 scale 到配置分辨率；🟥 真机 ffprobe 确认 H.264/1080p/fps |
| V6 | stream0 之外的子码流 | 只用 `/stream0`（与厂商一致）；🟥 |
| V7 | RTP over UDP or TCP | 默认 UDP，已实现 TCP 回退（仿真器 interleaved 已验证）；🟥 真机确认设备支持 TCP |
| V8 | 停心跳是否导致设备断流 | 会话全程保持 1s 心跳（保守策略）；🟥 真机验证不发的后果 |
| V9 | CMD_LIVE_STOP 0/1/3/5/255 精确语义 | 命令/ACK 已实现，仿真器仅回 ACK；🟥 真机逐条观察 |
| V10 | CMD_RTSP_TRANS_START 是否等于唤醒 | 唤醒复用开流命令（协议 §7）；🟥 |
| V11 | SSID 33 字节字段与 PASSWD: 边界 | ❌ v1 未暴露 WiFi 配置；🟥 |
| V12 | 二维码内容额外校验 | Core 不涉及（插件 UI 范畴）；配对码来源已支持 YOCamSet.xml |
| V13 | 设备 AP 模式固定 IP/网关 | 未实现 AP 模式（协议 §8.4 建议不要）；🟥 |
| V14 | TCP 8888 是否接受多客户端 | 仿真器接受多客户端；🟥 真机与厂商软件并存测试 |
| V15 | 设备是否主动发其它通知 | 接收端只识别 8 种已知回包，未知文本会被累积丢弃（不影响已知关键字）；🟥 长时间抓包 |

真机到手当天：先跑 `_research/yocam-protocol.md` §14 PowerShell 脚本确认发现/控制/RTSP，
再用本 README 的 `--serve --with-core` + 插件做端到端联调，逐条关闭上表。

---

## 7. 故障排查

| 现象 | 排查 |
|---|---|
| 发现超时（UDP 无回包） | 配对码（YOCamSet.xml）、设备是否上电/同网段、选对网卡、防火墙出站、厂商软件是否占用 |
| TCP 8888 连不上 | 设备 IP 是否正确（源地址）、设备是否已被其它客户端独占（V14）、防火墙 |
| 开流 ACK 超时 | 设备是否待机（发 `CMD_RTSP_TRANS_START` 唤醒）、命令是否到达（日志 TX） |
| ffmpeg 不出帧 | `ffmpeg -v info -rtsp_transport udp -i rtsp://ip:554/stream0` 手工验证；看 `LastStderrTail`；确认先收到开流 ACK；UDP 丢包可试 TCP 回退 |
| 共享内存读不到 | 生产者是否运行、名字是否 `Local\YoCameraVcamFrames`（`--shm-dump`）、是否有两个生产者 |
| 预览上下颠倒 | §3.4 行方向契约冲突，vcam 消费端需按 top-down 处理 |
| ffmpeg 提示 `Option localaddr not found` | 已自动探测规避；不要手动加 `-localaddr`（FFmpeg 8.x） |

---

## 8. 变更记录

- 2026-10-24 v1：实现发现/控制/心跳/开流/ffmpeg 解码/共享内存/会话编排 + 设备仿真器；
  selftest 49 项全通过；ffmpeg 8.x localaddr 探测、RTSP TCP 回退、占位帧、重连已验证。
