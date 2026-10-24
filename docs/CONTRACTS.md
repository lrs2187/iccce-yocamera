# 模块间契约（冻结版 v1）

> 所有模块以此为准，改动必须同步更新本文件并在群里说明。
> 目录与写作用域：
> - `src/YiouCamera.Vcam/`      → vcam-dev
> - `src/YiouCamera.Core/`      → core-dev
> - `src/YiouCamera.Plugin/`    → plugin-dev
> - `tools/`、`tests/Harness/`  → lead
> - `tests/Simulator/`          → core-dev

---

## 1. 命名与常量

| 项 | 值 |
|---|---|
| 插件 Id | `com.yiou.boothcam` |
| 插件目录 | `<ICCCE>\Plugins\com.yiou.boothcam\` |
| 入口程序集 | `YiouCamera.Plugin.dll` |
| 设备显示名（FriendlyName） | `驿欧无线展台` ← 同时是 `canvas.videoPresenterLastCameraName` 的值 |
| 虚拟摄像头 CLSID | `{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}`（沿用 PoC，已注册） |
| 类别 Instance 键 | `HKCU\Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}` |
| **DevicePath（必须写）** | `yocam:yiou-wireless-dc` |
| 共享内存名 | `Local\YoCameraVcamFrames` |
| 帧像素格式 | **RGB24（BGR 字节序）**，**top-down（第 0 行 = 图像顶部）**，`stride = (w*3+3) & ~3` |
| 行序翻转责任 | **滤波器侧**：DirectShow RGB24 正 biHeight 是 bottom-up，`vcam_filter` 从 SHM 拷到 sample 时逐行倒序拷贝（生产者不做翻转） |
| 默认分辨率 | 1280×720@30（可切 1920×1080@30） |
| 设备协议常量 | UDP 12811 / TCP 8888 / TCP 11111 / RTSP 554 |

> **DevicePath 是硬要求**：不写会让 WPFMediaKit 的 `AddFilterByDevicePath(null)` 串到别的软件摄像头（详见 docs/PLAN.md §13.1）。

---

## 2. 帧管线（Producer → Filter）

- **Producer** = Core 的 SHM 写入端（`SharedMemoryFrameSink`）。
- **Consumer** = `vcam_filter.dll`（DirectShow 源滤波器）。
- 布局以 `src/YiouCamera.Vcam/vcam_shm.h` 为唯一事实来源（沿用 PoC，seqlock + 256B 头 + 4 槽）。
- 槽大小按 **1920×1080×3** 计算，720p 只写前 `stride*h` 字节；头里带 `width/height/stride`，滤波器按头取用。
- 帧率：生产者按设备实际帧率写；滤波器按 `AvgTimePerFrame` 打时间戳，缺帧时重复最后一帧（不阻塞）。
- 断开/无信号：生产者写「无信号」占位帧（灰底 + 文字），**不允许停止更新**（否则宿主预览冻结）。

---

## 3. Core 公开接口（供 Plugin 使用）

```csharp
namespace YiouCamera.Core;

public sealed class Frame {
    public int Width, Height, Stride;
    public byte[] Buffer;      // RGB24, BGR 序, top-down
    public long FrameIndex;
    public long TimestampMs;
}

public interface IFrameSource : IAsyncDisposable {
    FrameSourceState State { get; }          // Idle/Starting/Running/Faulted
    event Action<Frame> FrameReady;          // 后台线程触发
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

public interface IDeviceDiscovery {
    Task<DeviceInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed class DeviceInfo {
    public string DeviceIp;      // 来自 UDP 源地址
    public string PcIp;          // 报文文本前缀
    public string RtspUrl => $"rtsp://{DeviceIp}:554/stream0";
}

public interface IDeviceControl : IAsyncDisposable {
    Task<bool> ConnectAsync(DeviceInfo dev, CancellationToken ct);
    Task<bool> StartRtspStreamAsync(TimeSpan timeout, CancellationToken ct); // CMD_RTSP_TRANS_START + 等 ACK
    Task StopLiveAsync(int minutes);  // CMD_LIVE_STOP
    bool IsConnected { get; }
    bool HeartbeatAlive { get; }
}

// v1 实现类名（保持可替换）
public sealed class FfmpegProcessFrameSource : IFrameSource;  // 外挂 ffmpeg.exe
public sealed class UdpDeviceDiscovery : IDeviceDiscovery;
public sealed class TcpDeviceControl : IDeviceControl;
public sealed class SharedMemoryFrameSink;                    // 唯一 SHM 写入者
public sealed class YiouBoothSession;                          // 编排：发现→控制→开流→解码→SHM
```

**约定**
- Core 不引用任何 ICCCE SDK 程序集；纯 .NET（`net10.0`，无 WPF）。
- Core 所有网络操作接受 `IPAddress? localBind`（多网卡/代理虚拟网卡必绑）。
- 任何异常都不抛给 UI 线程，走 `State` + `event Action<string> Faulted`。
- **v1 解码后端 = 外挂 `ffmpeg.exe`**（进程 stdin 控制、stdout 读 `-f rawvideo -pix_fmt bgr24`）；接口层不暴露进程概念，后续可换 FFmpeg.AutoGen。

---

## 4. Plugin 职责（不碰帧数据内部）

1. `Initialize`：注册工具栏按钮、读配置、按需启动 `YiouBoothSession`。
2. 虚拟摄像头生命周期：从嵌入资源释放 `vcam_filter.dll` 到 `%LOCALAPPDATA%\ICC-CE\YiouCamera\vcam\<ver>\` → `LoadLibrary`+`DllInstall` 注册（写 DevicePath）→ 校验枚举 → 记录版本。
3. 「打开视频展台」：`ISettingsService.Set("Canvas.VideoPresenterLastCameraName", "驿欧无线展台")` → `IVideoBoothService.Toggle()`。
4. 截图/手动拍照：从 `IFrameSource` 最近帧取图 → `ICanvasInkService` 插入画布。
5. 卸载：注销 COM、停会话、删 DLL（宿主占用时标记延迟删除）。

---

## 5. vcam 必须满足（摘自 iccce-integration.md C1–C20，摘要）

x64 in-proc、不假设 STA、Capture pin + `PinCategory.Capture`、**必须另有 `PinCategory.Preview` 输出 pin**、`IAMStreamConfig` 完整、`FormatType.VideoInfo`、RGB24、`SetFormat` 支持未连接调用、push source、反复 Stop→重建、**`MediaOpened` < 1.5s**（先出占位帧）、`FriendlyName` 稳定、**`DevicePath` 非空**、不实现 `IAMVideoProcAmp/IAMCameraControl`。

> ⚠️ **Preview pin 是硬要求（实测确证，2026-10-24）**：
> WPFMediaKit `VideoCapturePlayer.SetupGraph()` 第 252 行调用
> `captureGraphBuilder.RenderStream(PinCategory.Preview, MediaType.Video, source, null, vmr9)`。
> 只有 Capture pin 的源会让 CaptureGraphBuilder2 报 `VFW_E_CANNOT_CONNECT`（"未找到可用于建立连接的介质筛选器组合"），
> 表现为视频展台预览黑屏/MediaFailed。Capture 与 Preview 两个 pin 必须共享同一帧源（不要各自读 SHM）。

---

## 6. 构建与安装

```
tools\build-all.ps1    # vcam + core + plugin → build\out\
tools\install.ps1      # 释放到 <ICCCE>\Plugins\com.yiou.boothcam\ + 注册 vcam(含 DevicePath)
tools\uninstall.ps1    # 反注册 + 删目录
tools\verify.ps1       # 枚举校验（DevicePath 非空且唯一）+ 插件加载日志检查
```
默认 `<ICCCE>` = `C:\Users\lrs2187\Applications\InkCanvasForClass.CE.1.8.0.11-x64`，可参数覆盖。
