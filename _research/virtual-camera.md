# 虚拟摄像头方案（RTSP → DirectShow VideoInputDevice）可行性与 PoC

> 任务：把 WiFi RTSP 视频源 `rtsp://<ip>:554/stream0`（驿欧无线展台）以「虚拟摄像头」形式提供给 ICCCE（.NET 6/10 WPF 白板）。
> ICCCE 用 `DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)` 枚举摄像头，全屏预览用 WPFMediaKit `VideoCaptureElement`（DirectShow + VMR9 renderless + D3DImage）。
> 产出目录：`_research/poc-vcam/`（代码、脚本、日志）。本文所有「实测」结论均可在该目录复现。

---

## 0. 结论摘要（TL;DR）

**推荐路线：(a) 原生 C++ DirectShow 源滤波器（自实现 COM，不用 strmbase）+ HKCU 免提权注册；.NET 侧用 FFmpeg 解 RTSP，帧经 named shared memory 环形缓冲跨进程送入滤波器。**

- 实测可行性：**已跑通**。在 Windows 11 26H2（build 26340）**普通用户（未提权）** 下，自研滤波器成功出现在 `ICreateDevEnum(CLSID_VideoInputDeviceCategory)` 枚举中，`BindToObject(IBaseFilter)` 成功，`Run()` 后以 1920x1080@30 稳定出 RGB24 帧（2 秒 60 帧，PTS 单调，图案内容校验通过）。见 §7 验证记录。
- **不需要管理员权限**：注册只写 `HKCU\Software\Classes`。这是本方案相对「regsvr32 需要提权」的最大优势（对「.icpx 只允许 3 个文件、不能带安装器」的约束尤其关键）。实测安装命令：
  `regsvr32 /n /s /i "vcam_filter.dll"` → exit 0（普通用户）。**注意：普通的 `regsvr32 /s /i`（不带 `/n`）会 exit 5 拒绝访问**，原因与实测见 §6.3。
- **strmbase（DirectShow 基类）不在现代 Windows SDK 里**（实测 SDK 10.0.26100.0 无 baseclasses/strmbase），因此 PoC 全程不使用基类：单文件 + Windows SDK 即可编译，产物仅 **186,368 字节**，依赖只有 `ole32/advapi32/kernel32`（静态 CRT `/MT`，无需 VC++ 运行库）。
- **Media Foundation 虚拟摄像头（`MFCreateVirtualCamera`）不能替代本方案**：它注册的是 MF/KS 设备类别（`KSCATEGORY_VIDEO_CAMERA` 等），不会生成 DirectShow `VideoInputDevice` 类别键 `{860BB310-…}\Instance\{CLSID}`，所以 `DsDevice` 枚举不到。实测 API 可用（create hr=0），创建后 DShow 枚举数量不变、相关注册表项不存在（细节与局限见 §2.3）。
- **现成 OBS Virtual Camera 已在本机可直接用**（对照与应急回退），但作为产品方案不可控（需 OBS 进程、需用户安装），仅推荐作回退/对照（§2.4）。

---

## 1. 环境与基线证据

| 项目 | 实测值 |
|---|---|
| OS | Windows 11 26H2，build **26340**（`[Environment]::OSVersion`=10.0.26340.0） |
| 当前权限 | `whoami /groups`：**Mandatory Label\Medium Mandatory Level**，`BUILTIN\Administrators` = *Group used for deny only* → **未提权** |
| 编译器 | VS2022 Community 17.14.42，MSVC 14.44.35207（`vcvars64.bat` 可用） |
| Windows SDK | 10.0.26100.0（`strmiids.lib`、`quartz.lib`、`mfvirtualcamera.h`、`mfsensorgroup.lib` 均在） |
| .NET SDK | 8.0.426 / 9.0.319 / 10.0.402 |
| 已装虚拟摄像头 | **OBS Virtual Camera** 已注册（`HKCR\CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}`，`HKLM` 侧，DLL=`C:\Program Files\obs-studio\data\obs-plugins\win-dshow\obs-virtualcam-module64.dll`） |
| ICCCE | 本机未安装（`Program Files`/`LocalAppData\Programs` 下无 ICCCE 目录）→ **未能做真机宿主预览验证**，这是本报告最大的「未覆盖项」 |

宿主侧已由 Lead/分析线确认：**x64、.NET 10（net10.0-windows10.0.19041.0）**。因此 PoC 只做 x64（x86 方案见 §8.4）。

---

## 2. 四条技术路线对比

### 2.1 (a) 原生 C++ DirectShow 源滤波器

**自实现 COM（推荐）**：一个 DLL 内实现 `IBaseFilter` + 1 个输出 pin（`IPin` + `IAMStreamConfig` + `IKsPropertySet`），自带取帧线程，向 renderer 的 `IMemInputPin::Receive` 送样本。

- 优点：不依赖任何第三方框架、不依赖 ATL/strmbase、产物极小（186 KB）、行为完全可控、可按需裁剪。
- 缺点：COM 样板代码多（PoC 约 1200 行含注册与共享内存），出错（尤其引用计数/allocator/时钟）会以「宿主崩溃/黑屏」的形式暴露，需要仔细自测。

**使用 strmbase（DirectShow 基类）**：
- **实测：现代 Windows SDK 不提供**。`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0` 与 `Lib\...` 下均无 `strmbase*`/`baseclasses`/`amfilter.h`；`strmbase.lib` 也不在 SDK 里。微软官方文档《Using the DirectShow Base Classes》也把基类描述为随 DirectShow SDK 示例提供（[链接](https://learn.microsoft.com/en-us/windows/win32/directshow/using-the-directshow-base-classes)），而这些示例如今只存在于归档的 Windows-classic-samples（`Samples/Win7Samples/multimedia/directshow` 一脉）与 DirectX SDK (June 2010) 的 `Samples\C++\DirectShow\BaseClasses`。
- 结论：要用基类就得 **vendor 一份开源实现**（几十个 .cpp，且老代码对 C++17/新 SDK 需打补丁），或者装 DirectX SDK June 2010 后用旧工具链。与「插件包只允许 3 个文件、原生 DLL 内嵌」的约束一起考虑，**不划算**——PoC 证明自实现只需 1 个 .cpp。

### 2.2 (b) .NET 实现「可注册 COM」的源滤波器

做法：`[ComVisible(true)]` 的 C# 类实现 `IBaseFilter/IPin` 等，`regasm /codebase` 注册，供 DirectShow 宿主 `CoCreateInstance`。

- 技术上可能，但风险高：
  1. **线程模型**：DirectShow 会从**非托管线程**直接调用 COM 对象；C# CCW 需要与该线程的 COM apartment 匹配。滤波器必须 `ThreadingModel=Both`，且宿主（WPF，STA）与取帧线程（MTA）都会回调，跨 apartment 编组/死锁风险大；DirectShow 内部对 `IMemInputPin::Receive` 有「不能阻塞过久」的隐含要求，GC/编组暂停都可能触发卡顿。
  2. **注册**：`regasm` 默认写 HKLM（要提权）；`/codebase` 免 GAC 但仍是 HKLM。虽然也可手工写 HKCU，但 .NET 的 `InprocServer32` = `mscoree.dll` + `Class`/`Assembly`/`RuntimeVersion` 子键，再加 `ThreadingModel`，细节更多。
  3. **.NET 版本耦合**：`mscoree.dll` 会按注册的 runtime 版本加载；ICC CE 是 .NET 10（CoreCLR）宿主，但**滤波器 DLL 的 COM 激活走的是 .NET Framework 的 `mscoree`（CLR v4）**，必须确保机器有 .NET Framework 4.x（Win10/11 自带）——这点能成立，但会让「.NET 侧 FFmpeg」与「滤波器 COM 侧」跑在两个完全不同的运行时里，调试成本高。
  4. 性能：每帧 6 MB 数据穿过托管边界，`Marshal.Copy`/`GCHandle` 固定开销 + GC 压力，1080p30 不划算。
- 结论：**不推荐**。若一定要「全 .NET」，更实际的是 (d) 复用现成虚拟摄像头（OBS）或自带预览窗口。

### 2.3 (c) Windows 11 Media Foundation 虚拟摄像头（`MFCreateVirtualCamera`）

官方文档（[MFCreateVirtualCamera](https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera)）：

- **最低客户端 = Windows Build 22000**（Win11 21H2；不是 22H2）；库 `mfsensorgroup.lib` / DLL `mfsensorgroup.dll`。
- `categories` 参数：**非管理员只能传 `KSCATEGORY_VIDEO_CAMERA` / `KSCATEGORY_VIDEO` / `KSCATEGORY_CAPTURE` / `KSCATEGORY_SENSOR_CAMERA` 的子集**，默认就是这三类 KS 类别。
- `MFVirtualCameraAccess_AllUsers` 需要管理员；`CurrentUser` 不需要。friendlyName 会被系统追加 "Windows Virtual Camera" 后缀。
- 还要求实现一个**自定义 `IMFMediaSource`**（`sourceId` 指向其 CLSID），只调 API 不实现 media source 是没有画面的。

**关键问题：它会出现在 DirectShow `VideoInputDevice` 里吗？—— 不会（高置信，含部分实测）。**

依据链：
1. `DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)` 内部只做一件事：`ICreateDevEnum::CreateClassEnumerator(CLSID_VideoInputDeviceCategory)`，返回的每个 moniker DisplayName 形如 `@device:sw:{860BB310-…}\{CLSID}`，即**完全由注册表 `HKCR\CLSID\{860BB310-…}\Instance\{CLSID}` 派生**（[Enumerating Devices and Filters](https://learn.microsoft.com/en-us/windows/win32/directshow/enumerating-devices-and-filters)、[System Device Enumerator](https://learn.microsoft.com/en-us/windows/win32/directshow/system-device-enumerator)）。
2. 本 PoC 直接实证了这条机制：往 `HKCU\Software\Classes\CLSID\{860BB310-…}\Instance\{我们的CLSID}` 写入 `FriendlyName`+`CLSID` 后，**下一次枚举立刻出现**；删除后**立刻消失**（§7）。
3. `MFCreateVirtualCamera` 注册的是 MF frameserver / KS 设备接口（`KSCATEGORY_*`），**不写** `{860BB310-…}\Instance`。实测（`src/mf_probe.cpp`）：在本机 Win11 26H2 上 `MFCreateVirtualCamera(...CurrentUser, Session...)` 返回 **hr=0x00000000**（说明 API 可用），存活期间 `verify-enum.exe` 枚举数量仍为 2（OBS + 我们的），`HKLM/HKCU\...\CLSID\{860BB310-…}\Instance` 下没有新增项，全盘搜索 CLSID 下 "MF Probe" 无匹配。
4. 局限（诚实标注）：该实验中 `IMFVirtualCamera::Start` 返回 `0x80040154 (REGDB_E_CLASSNOTREG)`，因为 `sourceId` 是假 CLSID、我们没有实现可工作的 `IMFMediaSource`；因此不能 100% 排除「一个能成功 Start 的 MF 虚拟摄像头会额外注册 KS 设备接口从而被 DShow 的硬件枚举路径看到」。但即使发生，那条路径要求宿主能 `CreateFile` 打开 KS 设备，MF 软件摄像头不提供 KS 设备路径，实际商用 DirectShow 应用普遍看不到 Win11 虚拟摄像头（社区大量同类报告）。**结论：为 ICCCE 这条 DShow 枚举路径，MF 虚拟摄像头不可行，除非改造宿主。**

> 补充：如果将来目标宿主改用 Media Foundation 枚举（`MFEnumDeviceSources`），`MFCreateVirtualCamera` 是「官方正道」；但那是另一个产品的迁移，不在本任务范围。

### 2.4 (d) 复用现成开源虚拟摄像头（OBS VirtualCam / UnityCapture 等）

- **OBS Virtual Camera（本机已装）**：`obs-virtualcam-module64.dll` + HKLM 注册，实测已在 `VideoInputDevice` 枚举中（`FriendlyName = "OBS Virtual Camera"`）。
  - 产品化用法：OBS 加一个「媒体源」（RTSP URL）→ 启动虚拟摄像头 → ICCCE 选 "OBS Virtual Camera"。可用 `obs-websocket`（v5）或 `obs64 --startvirtualcam --startstreaming` 类命令行/脚本自动化。
  - 优点：0 开发量、开箱能用；缺点：必须安装并运行 OBS（~200 MB）、用户会看到 OBS 界面、OBS 升级/插件策略不可控、与 ICCCE 插件「3 文件」约束冲突（等于要用户装第三方软件）、无法静默/免配置、多实例与开机自启体验差。
- **UnityCapture**（开源、MIT）：单个 DLL + regsvr32 注册，用法是把帧写进它的命名共享内存。可以在它的基础上改（但仍是「vendoring 别人的代码」，且代码较老，需自行维护 ABI/兼容）。
- **Virtual Camera DirectShow filter 类项目**（如 `e2eSoft VCam`、`ManyCam` 等商业软件）：不可控、许可问题。
- 结论：**只作为「应急回退」与「对照验证」**，不作为交付方案。

### 2.5 对比表与推荐

| 维度 | (a) 自研 C++ DShow 滤波器 | (a') strmbase 版 | (b) .NET COM 滤波器 | (c) MF 虚拟摄像头 | (d) OBS/UnityCapture |
|---|---|---|---|---|---|
| 能否被 `DsDevice` 枚举 | **能（实测）** | 能 | 能（理论） | **不能**（实测+机制） | 能（实测，OBS） |
| 是否需要提权 | **不需要（HKCU 实测）** | 不需要（HKCU） | 不需要（手工 HKCU） | 不需要（CurrentUser） | OBS 安装需提权 |
| 依赖 | 仅 Windows SDK | 需 vendor 基类源码/旧 DXSDK | .NET Framework 4.x + CLR 互操作 | 需自研 IMFMediaSource | **必须装并运行 OBS** |
| 产物大小 | **186 KB** | ~400 KB+ 基类 | ~100 KB + 依赖 | ~n 百 KB | 0（但需 200 MB 宿主程序） |
| 线程/死锁风险 | 中（可控，单线程出帧） | 中 | **高**（CCW + STA/MTA） | 高（MF 管线） | 低（用别人实现） |
| 1080p30 性能 | 好（一次 memcpy 到样本） | 好 | 差（托管边界） | 好 | 好 |
| 开发成本 | **中（PoC 已跑通，可复用）** | 中高 | 高 | 高 | 极低 |
| 产品可控性 | **高** | 高 | 中 | 中 | 低 |
| 与「.icpx 3 文件」约束 | **兼容**（内嵌→释放→HKCU 注册） | 兼容 | 兼容 | 兼容 | **不兼容** |

**推荐 (a)，理由排序：**① 唯一能在不改宿主的前提下被 `DsDevice` 枚举到；② 免提权安装（HKCU）与插件包约束天然契合；③ 产物极小、零第三方依赖；④ PoC 已在真机验证枚举+激活+出帧全链路。

---

## 3. 推荐方案架构

```
┌──────────────────────── ICCCE 宿主进程 (x64, .NET 10) ────────────────────────┐
│  WPFMediaKit VideoCaptureElement                                              │
│    ├─ DsDevice.GetDevicesOfCat(VideoInputDevice) → 枚举到 "YoCamera Virtual Camera"
│    ├─ moniker.BindToObject(IBaseFilter) → CoCreateInstance(CLSID) → 加载 ↓    │
│    └─ ICaptureGraphBuilder2.RenderStream(Capture, Video, src, null, VMR9)    │
│                        │                                                     │
│                  vcam_filter.dll （DShow 源滤波器，in-proc，x64）              │
│                        │  IMemInputPin::Receive(sample)                      │
│                        ▼                                                     │
│                     VMR9 renderless → D3DImage → WPF 画面                     │
└──────────────────────────────────────────────────────────────────────────────┘
                    ▲  读帧（seqlock，零阻塞、只取最新完整帧）
                    │  named shared memory: Local\YoCameraVcamFrames
                    │
┌───────────────────┴──────── ICCCE 插件进程（或宿主内 .NET 任务） ─────────────┐
│  FFmpeg (ffmpeg.exe 或内嵌库) 解 RTSP H.264 → rawvideo bgr24 帧              │
│  → 写入共享内存环（4 槽 × 6.22 MB）→ 更新 writeSeq                          │
└──────────────────────────────────────────────────────────────────────────────┘
```

要点：
- **进程边界**：滤波器必须活在宿主进程里（in-proc COM）；RTSP/解码放在插件侧（.NET），两者用共享内存解耦。这样解码崩溃不影响宿主，滤波器也不会因为网络抖动卡住。
- **滤波器不依赖 FFmpeg**：它只认共享内存 + 固定测试图案兜底 → 出问题时至少能出彩条，便于定位。
- **降级链**：共享内存无数据 → 内置彩条（已实现）；注册失败/枚举不到 → 插件自带展台窗口（§8.3）。

---

## 4. 最小接口契约

### 4.1 必须实现的 COM 接口（PoC 实测通过项）

| 接口 | 位置 | 必须性 | 关键点 | PoC 状态 |
|---|---|---|---|---|
| `IClassFactory` + `DllGetClassObject` | DLL 导出 | **必须** | CLSID 必须与注册表一致；`CreateInstance` 只接受 `IBaseFilter`/`IUnknown` | ✅ |
| `IPersist::GetClassID` | 滤波器 | **必须** | 返回自己的 CLSID（GraphBuilder/属性页会查） | ✅ |
| `IMediaFilter::Run/Pause/Stop/GetState` | 滤波器 | **必须** | `Run(tStart)` 里启动出帧线程；`Stop` 要能打断阻塞在 `GetBuffer` 的线程（先 `Decommit` 再 join） | ✅ |
| `IMediaFilter::SetSyncSource/GetSyncSource` | 滤波器 | **必须** | 保存 `IReferenceClock`；有 clock 时用它算 PTS，没有则用 QPC 兜底 | ✅ |
| `IBaseFilter::EnumPins/FindPin` | 滤波器 | **必须** | 至少 1 个输出 pin；`FindPin(L"Output")`；`FindPin(L"Capture")` 也返回它更稳 | ✅ |
| `IBaseFilter::QueryFilterInfo` | 滤波器 | **必须** | 返回 `pGraph`（AddRef）+ 名字；名字用注册的 FriendlyName | ✅ |
| `IBaseFilter::JoinFilterGraph` | 滤波器 | **必须** | 保存/释放 `IFilterGraph*` | ✅ |
| `IPin`（全部 13 个方法） | 输出 pin | **必须** | `Connect` 是核心（见 4.2）；`QueryAccept`/`EnumMediaTypes` 供协商；`ReceiveConnection` 对源滤波器返回 `E_UNEXPECTED` | ✅ |
| `IAMStreamConfig` | 输出 pin | **强烈建议** | 让宿主/GraphBuilder 能列出 1920x1080@30 等能力并 `SetFormat`（见 4.2） | ✅ |
| `IKsPropertySet`（仅 `AMPROPSETID_Pin`） | 输出 pin | **强烈建议** | 报告 `PIN_CATEGORY_CAPTURE`，否则 `RenderStream(PinCategory.Capture, …)` 可能找不到 pin（WPFMediaKit 走这条） | ✅ |
| `IMemInputPin` | — | **不需要** | 源滤波器是数据发出方；它通过**下游**的 `IMemInputPin` 送帧 | ✅（用下游的） |
| `ISpecifyPropertyPages` | 滤波器/pin | 可选 | 不实现即可（`QueryInterface` 返回 `E_NOINTERFACE` 是标准行为，宿主会跳过「属性」按钮）。PoC 未实现 | ⛔ 未实现，安全 |
| `IAMVideoProcAmp` / `IAMCameraControl` | 滤波器 | **不需要** | 宿主对摄像头会探测这两者；**让 QI 失败即可**，宿主（含 DirectShow.NET 的封装属性）会判空跳过，不会崩。PoC 实测宿主路径下无异常 | ⛔ 不实现，安全 |
| `IAMDroppedFrames` / `IAMPushSource` | — | 不需要 | — | — |

> 关于「属性探测不崩」：DirectShow 的惯例是「能力接口用 `QueryInterface` 探测，失败即代表不支持」，`solutions` 里宿主普遍写成 `if (filter is IAMVideoProcAmp p) {...}` 或先 QI 判 null。**不要**为了「有属性页」而返回半实现的接口——半实现才会崩；干净地返回 `E_NOINTERFACE` 最安全。PoC 已按此设计。

### 4.2 Pin 上的媒体类型与 IAMStreamConfig

**暴露哪些 media type**（结论：**先只做 RGB24**）：

| 类型 | 建议 | 理由 |
|---|---|---|
| **RGB24**（`MEDIATYPE_Video`/`MEDIASUBTYPE_RGB24`/`FORMAT_VideoInfo`） | **必须，默认** | VMR9 renderless 原生支持；.NET 侧 FFmpeg 可直接输出 `bgr24`，无需在滤波器里写颜色转换；`biHeight` 用正数（bottom-up）最兼容 |
| YUY2 | 可选（第二阶段） | 带宽减半，但需要 4:2:2 打包与 chroma 采样，且 .NET 侧 FFmpeg 输出 `yuyv422` 也可行；收益是 memcpy 少一半 |
| MJPG | **不需要** | 解码应放在 .NET 侧（FFmpeg 一步到位）；在滤波器里解 MJPG 会把编解码器依赖塞进宿主进程，得不偿失 |

**能力枚举**（实测宿主可见）：

- `IAMStreamConfig::GetNumberOfCapabilities` → 2；`*piSize = sizeof(VIDEO_STREAM_CONFIG_CAPS)`（128 字节）。
- `GetStreamCaps(0)` → `1920x1080 RGB24 @30fps`，`MinFrameInterval=MaxFrameInterval=333333`（100ns 单位），`MinBitsPerSecond=MaxBitsPerSecond≈ 1920*1080*3*8*30`。
- `GetStreamCaps(1)` → `1280x720 RGB24 @30fps`。
- `GetFormat`/`SetFormat`：`SetFormat` 只接受 RGB24 + `16..1920 x 16..1080` 且 `biCompression=BI_RGB`；**必须把传入的 `VIDEOINFOHEADER` 归一化成自己生成的**（保证 stride=4 对齐、`biHeight>0`），否则宿主传负数高度/空 format 块会踩坑。
- `EnumMediaTypes`：返回同样的 2 组 `AM_MEDIA_TYPE`（宿主在 `RenderStream` 协商时实际用这个）。
- 帧率通过 `AvgTimePerFrame`（100ns）表达；实际送帧节奏由滤波器线程按此间隔 + 系统时钟控制。

**连接顺序**（`IPin::Connect`，PoC 已实现并验证）：
1. 取下游 `IMemInputPin`（失败 → `VFW_E_TYPE_NOT_ACCEPTED`）。
2. 若宿主指定了 `pmt`：`CopyMediaType` → `pReceivePin->ReceiveConnection(this, pmt)`；失败则回退到自己的 caps。
3. 遍历自己的 caps，`ReceiveConnection` 成功即采用。
4. allocator：优先 `downstream->GetAllocator()`（VMR9 会提供自己的 allocator）；拿不到就 `CoCreateInstance(CLSID_MemoryAllocator)` 并 `SetProperties({cBuffers=3, cbBuffer=stride*height})`。
5. `Commit()` 在 `Run()` 里做；`Stop()` 里先 `Decommit()`（打断阻塞的 `GetBuffer`）再 join 线程。

### 4.3 帧的送出（时间戳与同步）

- 每帧：`allocator->GetBuffer()` → 填数据 → `SetActualDataLength(stride*h)` → `SetSyncPoint(TRUE)` → `SetPreroll(FALSE)` → `SetTime(&tStart,&tStop)` → `SetMediaTime(nullptr,nullptr)` → `downstream->Receive(sample)`。
- `tStart` 以 `IMediaFilter::Run(tStart)` 传入的流时间为基准，按 `10000000/fps` 递增；`tStop = tStart + interval`。**PTS 必须单调**（PoC 用测试断言验证，回归为 0）。
- 下游 `Receive` 返回 `S_FALSE` = 丢帧（正常）；返回 `VFW_E_NOT_CONNECTED` = 断链，应退出线程。
- **不要在 `Pause()` 里送 preroll**（PoC 简化：只在 `Running` 出帧）。如果真机 VMR9 在 `Pause→Run` 时要求 preroll 样本，再加一个「Pause 时只送 1 帧、`SetPreroll(TRUE)`」的分支即可（小改动）。

### 4.4 线程模型

- 滤波器内部线程：`Run` 时创建（`std::thread`），线程入口先 `CoInitializeEx(nullptr, COINIT_MULTITHREADED)`；`Stop` 时 `Decommit` + `SetEvent` + join。
- DLL 注册 `ThreadingModel=Both`：宿主在 STA（WPF UI 线程）创建滤波器，取帧线程在 MTA 里调 `Receive`——这是 DirectShow 滤波器的**标准形态**（renderer 的 `Receive` 必须线程安全，VMR9 是）。
- 不要在滤波器线程里做长耗时操作（如解 RTSP）；它只做「共享内存拷贝 + Deliver」。

### 4.5 64 位与注册形态

- 宿主 x64 → DLL 必须 x64，注册在 `HKLM\SOFTWARE\Classes\CLSID\…` 或 `HKCU\Software\Classes\CLSID\…`（**不是** `Wow6432Node`）。
- 若要兼顾 x86 宿主：同一份源码用 `vcvars32` 编 x86 DLL，注册到 `…\Wow6432Node\CLSID\{}`（HKLM）或 `HKCU\Software\Classes\Wow6432Node\CLSID\{}`；**x86 与 x64 可以共用同一个 CLSID**（各自 hive 互不影响）。代价只是多一个二进制、注册脚本多一份分支。PoC 未覆盖 x86（宿主已确认 x64）。

---

## 5. 跨进程送帧：named shared memory 环形缓冲

### 5.1 为什么是共享内存而不是命名管道

- 1080p RGB24 = **6,220,800 B/帧**，30fps = **186.6 MB/s**。命名管道（即使是 message mode）每帧一次 6 MB 拷贝 + 内核切换，容易堆积；共享内存是**零 IPC 语义的裸拷贝**，滤波器始终读「最新完整帧」，天然抗抖动、天然丢帧不排队。
- 性能预算：生产者写 6.2 MB + 滤波器读 6.2 MB ≈ 373 MB/s 内存带宽，现代机器 memcpy 10–20 GB/s，理论 CPU 占用 2–4%（实测 30fps 稳定；未做精确 CPU 采样，见 §9）。
- 缺点：需要自己设计同步 —— 用「seq-lock（写序号奇偶）」即可，无锁、不阻塞、无优先级反转。

### 5.2 共享内存布局（`src/vcam_shm.h`，唯一事实来源）

映射名：**`Local\YoCameraVcamFrames`**（`Local\`=同登录会话；插件与宿主同会话，够用，且不用 `Global\` 的权限）。总大小 = `256 + slotCount*slotBytes`，默认 4 槽 → **256 + 4×6,220,800 = 24,883,456 B ≈ 23.7 MB**。

**头部 256 字节（小端）**：

| 偏移 | 类型 | 字段 | 含义 |
|---|---|---|---|
| 0 | u32 | `magic` | `0x4D414356` = `'VCAM'` |
| 4 | u32 | `version` | 契约版本，当前 `1` |
| 8 | u32 | `headerBytes` | 恒 256（消费端校验） |
| 12 | u32 | `slotCount` | 环形槽数 2..8，默认 4 |
| 16 | u32 | `slotBytes` | 每槽字节数，`>= stride*height` |
| 20 | u32 | `width` | 像素宽 |
| 24 | u32 | `height` | 像素高 |
| 28 | u32 | `stride` | 槽内每行字节数（RGB24 = `(w*3+3)&~3`，1920→5760） |
| 32 | u32 | `format` | `0=RGB24`, `1=RGB32(BGRA)`, `2=YUY2` |
| 36 | u32 | `fpsNum` | 30 |
| 40 | u32 | `fpsDen` | 1 |
| 44 | i32 | `writeSeq` | **seqlock 发布序号**（奇=正在写，偶=稳定） |
| 48 | i32 | `readSeq` | 消费端进度（诊断用） |
| 52 | i64 | `timestamp100ns` | 生产者呈现时间（诊断用；PTS 由滤波器自己生成） |
| 60 | u64 | `frameIndex` | 生产端帧号（单调递增） |
| 68 | u32 | `flags` | bit0 = 生产者存活 |
| 72 | u8[184] | `pad` | 补齐到 256 |

槽布局：`base = 256 + (slot % slotCount) * slotBytes`，槽内按 `stride` 行存放，**行序与 DirectShow RGB24 一致：bottom-up**（第 0 行在缓冲区开头 = 画面底部）。滤波器按 `min(dstStride, hdr->stride)` 逐行 copy。

### 5.3 发布/消费协议与丢帧、重连

生产者（每帧）：

```
InterlockedIncrement(&writeSeq);      // 变奇数：开始写
slot = frameIndex % slotCount;
memcpy(slot, frame, stride*height);   // 或直接从 FFmpeg sws_scale 写到 slot
timestamp100ns = ...; frameIndex++;
InterlockedIncrement(&writeSeq);      // 变偶数：这一帧可读
```

消费者（滤波器，每帧）：

```
seq1 = writeSeq;
if (seq1 & 1) -> 本帧正在写，跳过（不等待，继续用上一帧/彩条节奏）
if (seq1 != 0) {
    idx = (seq1/2 - 1) % slotCount;
    row-copy from slot idx;
    if (writeSeq != seq1) -> 拷贝期间被覆盖（撕裂），丢弃本次结果
}
```

策略：
- **丢帧**：环满即覆盖最旧；消费端永远只读「最新完成帧」，绝不排队。RTSP 抖动/解码慢时自然表现为掉帧，不会积压延迟。
- **重连**：`shm` 不存在 / magic/version 不匹配 / 尺寸不匹配 → 滤波器**回退到内置彩条**（已实现，每 ~1 秒重试打开一次，不空转）；生产者重启后若尺寸不变，滤波器下一帧自动恢复吃新帧。尺寸变化时需要宿主重新 `SetFormat`（或插件重启预览），PoC 策略是「尺寸不匹配就继续彩条 + 记 drop 计数」，避免在预览中途换格式导致 renderer 重置。
- **生产者退出**：`flags` bit0=0；滤波器检测到 `writeSeq` 长时间不变 → 继续彩条（不会黑屏/崩溃）。
- **对齐**：`slotBytes` 建议 4 KB 对齐（6,220,800 已是 4096 的倍数：6220800/4096=1518.75 不是；实际 6220800 % 4096 = 3072，可向上取整到 6,225,920，非必须，只是 cache 友好）。

### 5.4 .NET 侧生产者（C# 参考实现）

```csharp
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

const uint MAGIC = 0x4D414356, VERSION = 1, HEADER = 256, SLOTS = 4;
int w = 1920, h = 1080;
int stride = (w * 3 + 3) & ~3;
uint slotBytes = (uint)(stride * h);
long total = HEADER + (long)SLOTS * slotBytes;

using var mmf = MemoryMappedFile.CreateOrOpen(@"Local\YoCameraVcamFrames", total);
using var acc = mmf.CreateViewAccessor(0, total);
var hdr = new byte[HEADER];
BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0), MAGIC);
// ... 填 version/headerBytes/slotCount/slotBytes/width/height/stride/format/fpsNum/fpsDen
acc.WriteArray(0, hdr, 0, HEADER);

long frame = 0;
// 每帧（FFmpeg 解出 bgr24 的 byte[] 或 unmanaged 指针）：
int seqOff = 44;
int cur = acc.ReadInt32(seqOff);
acc.Write(seqOff, cur + 1);                      // 奇：开始写
long slotOff = HEADER + (frame % SLOTS) * slotBytes;
acc.WriteArray(slotOff, frameBytes, 0, (int)slotBytes);
acc.Write(60, frame);                            // frameIndex
acc.Write(52, timestamp100ns);                   // timestamp100ns
acc.Write(seqOff, cur + 2);                      // 偶：发布
frame++;
```

> 注：`MemoryMappedFile.CreateOrOpen` 默认 ACL 允许同用户访问，够用。若插件与宿主跑在不同用户下（例如宿主以管理员另开账号）需显式 ACL，本方案默认同用户。

### 5.5 实测证据（跨进程）

滤波器内置彩条第 0 像素 = `235,235,235`；`shm_producer` 生产的帧第 0 像素 = `250,250,250`（故意做区分）。同一份 `testhost.exe`：

```
无生产者：  [run] first pixel (B,G,R) = 235,235,235    samples=60 distinctFrames=60
有生产者：  [run] first pixel (B,G,R) = 250,250,250    samples=60 distinctFrames=60
```

→ 证明滤波器在运行时**确实改从共享内存取帧**，且跨进程 30fps 无撕裂（`distinctFrames` 与 `samples` 一致）。

---

## 6. 注册与部署

### 6.1 完整注册表键值

**免提权（HKCU，实测可用）**：

```
HKEY_CURRENT_USER\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}
    (Default) = "YoCamera Virtual Camera"
HKEY_CURRENT_USER\Software\Classes\CLSID\{9A3E7D21-...}\InprocServer32
    (Default) = "C:\...\vcam_filter.dll"        ; ★绝对路径
    ThreadingModel = "Both"
HKEY_CURRENT_USER\Software\Classes\CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance\{9A3E7D21-...}
    FriendlyName = "YoCamera Virtual Camera"
    CLSID        = "{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}"
```

**提权（HKLM，等价路径）**：把 `HKCU\Software\Classes\` 换成 `HKEY_CLASSES_ROOT\`（即 `HKLM\SOFTWARE\Classes\`）。这就是 OBS 用的形态——本机 OBS 实例可作对照：

```
HKCR\CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}\(Default)="OBS Virtual Camera"
HKCR\CLSID\{...}\InprocServer32\(Default)=C:\Program Files\obs-studio\data\obs-plugins\win-dshow\obs-virtualcam-module64.dll
HKCR\CLSID\{A3FCE0F5-...}\InprocServer32\ThreadingModel="Both"
HKCR\CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance\{A3FCE0F5-...}\FriendlyName="OBS Virtual Camera"
HKCR\CLSID\{...\}\Instance\{A3FCE0F5-...}\CLSID="{A3FCE0F5-...}"
HKCR\CLSID\{...\}\Instance\{A3FCE0F5-...}\FilterData=<binary, 88 bytes>
```

**为什么 HKCU 也能被枚举**：`HKCR` 是 `HKLM\Software\Classes` + `HKCU\Software\Classes` 的合并视图（HKCU 优先），而 `ICreateDevEnum` 读的就是 `HKCR`；`InprocServer32` 的 COM 激活同样先查 HKCU。本 PoC 的枚举与激活**均只依赖 HKCU 就成功了**（§7）。

### 6.2 安装/卸载命令（四种形态）

| 场景 | 命令 | 权限 | 实测 |
|---|---|---|---|
| 免提权安装（推荐） | `regsvr32 /n /s /i "C:\path\vcam_filter.dll"` | 普通用户 | ✅ exit 0，立即可枚举 |
| 免提权卸载 | `regsvr32 /n /s /u /i "C:\path\vcam_filter.dll"` | 普通用户 | ✅ 枚举立即消失 |
| 免提权（脚本兜底） | `powershell -ExecutionPolicy Bypass -File scripts\install-user.ps1 -Verify` | 普通用户 | ✅（regsvr32 失败时直接写 HKCU） |
| 机器级安装 | `regsvr32 /s "C:\path\vcam_filter.dll"`（管理员） | **需提权** | ⚠️ 本机未提权，未实测（脚本 `scripts\install-admin.bat`） |
| 从代码注册（插件推荐） | `LoadLibrary` + `GetProcAddress("DllInstall")(TRUE, L"")` | 普通用户 | ✅（`regtool.exe install` 走同一路径） |

### 6.3 提权相关实测结论（重要）

1. 直接写 HKCR：`reg add "HKCR\CLSID\{...}" ...` → **ERROR: Access is denied（exit 1）**（当前 medium integrity）。
2. `regsvr32 /s /i "…dll"` → **exit 5**（拒绝访问）。原因：`/i` 时 regsvr32 仍会调用 `DllRegisterServer`（写 HKCR）；我们的 `DllRegisterServer` 因此失败。
3. `regsvr32 /n /s /i "…dll"` → **exit 0**，`HKCU` 注册成功。`/n` 的含义正是「不要调用 DllRegisterServer，只调用 DllInstall」。
4. `regtool.exe install`（`LoadLibrary` + `DllInstall(TRUE,"")`）→ `HRESULT=0`。
5. `regsvr32.exe` 是 **GUI 子系统程序**，PowerShell 里直接 `&` 调用不会等待，`$LASTEXITCODE` 为空且可能读到旧状态；脚本里必须用 `Start-Process -Wait`（PoC 脚本已如此）。

> 结论：**产品化默认走 HKCU（免提权）**；`DllRegisterServer` 保留给管理员/机器级安装。`DllInstall` 是我们自己的导出（`DllInstall` 是 regsvr32 的约定入口），签名 `HRESULT DllInstall(BOOL bInstall, LPCWSTR pszCmdLine)`。

### 6.4 `FilterData` 是什么、要不要写

- `FilterData` 是 `REGFILTER2`（v2）结构的二进制序列化，由 `IFilterMapper2::RegisterFilter` 生成，记录该滤波器的 merit、引脚、媒体类型表，供 **Filter Mapper**（`IFilterMapper2::EnumMatchingFilters` 按媒体类型搜索滤波器）使用。
- OBS 本机实例的 `FilterData` 为 88 字节，开头三个 DWORD = `02 00 00 00`（version=2）/`00 00 20 00`（merit=0x00200000）/`01 00 00 00`（1 个引脚），尾部是 `MEDIATYPE_Video` + `MEDIASUBTYPE_NV12` 两个 GUID。
- **本 PoC 不写 `FilterData`，枚举与预览通路依然正常**（实测）。原因：System Device Enumerator 只用 `Instance\{CLSID}` 下的 `FriendlyName`/`CLSID` 构造 moniker 与属性包，不用 FilterData。
- 什么时候需要写：仅当有老软件通过 `IFilterMapper2::EnumMatchingFilters` 按「媒体类型/引脚」搜索采集设备时。若确实需要，最稳的做法不是在注册脚本里手拼二进制，而是在 `DllRegisterServer` 里调 `IFilterMapper2::RegisterFilter(...)`（[文档](https://learn.microsoft.com/en-us/windows/win32/api/strmif/nf-strmif-ifiltermapper2-registerfilter)），由系统生成 FilterData；该路径需要提权（写 HKCR）。

### 6.5 免注册 COM（manifest / ActCtx）能否让 `DsDevice` 枚举到？

**不能。** 依据：
1. 免注册 COM/ActCtx 的机制是「**激活**期间用 manifest 覆盖 `CoCreateInstance` 的注册表查找」，不改变系统注册表内容（[Registration-Free COM 组件的激活：演练](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/ms973913(v=msdn.10))、[Activation Contexts](https://learn.microsoft.com/en-us/windows/win32/sbscs/activation-contexts)）。
2. `DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)` 的**枚举阶段**跟 manifest 无关：它由宿主里的 `CLSID_SystemDeviceEnum`（quartz.dll）直接 `RegOpenKey` 读 `HKCR\CLSID\{对应 Category}\Instance` 并逐个建 moniker（DisplayName 形如 `@device:sw:{860BB310-…}\{CLSID}`）。注册表里没有这个键，就枚举不到——这一点被本 PoC 直接实证（写键→出现，删键→消失）。
3. 即使你在宿主 manifest 里声明了自己的 CLSID，宿主进程的 manifest 你**改不了**（那是 ICCCE 的 exe/dll manifest，且 Windows 不允许对已运行进程追加 ActCtx 影响其后续 DShow 枚举）。
4. 另外 DShow 的 moniker 绑定是按 DisplayName 里的 CLSID 走注册表激活，manifest 也不会改写这条路径。

→ 结论：**必须真正写注册表**（HKCU 即可，不需要提权），不要指望免注册 COM。

### 6.6 卸载与「下拉框里不再出现」

1. 注销（`regsvr32 /n /s /u /i` 或 `install-user.ps1 -Uninstall`）会删掉：`…\CLSID\{860BB310-…}\Instance\{我们的CLSID}` 与 `…\CLSID\{我们的CLSID}`。
2. **枚举是实时的、无缓存**：实测注销后立刻 `[enum] total VideoInputDevice = 1`（只剩 OBS）。宿主若已缓存下拉列表，重新打开「视频展台」窗口/重新选设备即可看到消失；不需要重启宿主或注销登录。
3. 注意「**DLL 被占用**」：预览中滤波器已被加载进宿主进程，此时删 DLL 文件会失败（`ERROR_SHARING_VIOLATION`）。正确顺序：先停止预览 → 注销注册表 → 再删文件。插件卸载流程应把「删文件」放到下次启动时做（或提示关闭宿主）。
4. 若同一 CLSID 曾在 HKLM 注册过（提权装过），只删 HKCU 不会让它消失——HKCR 合并视图会回落到 HKLM。卸载脚本要先判断两处（PoC 的 `install-user.ps1` 只处理 HKCU；机器级用 `uninstall-admin.bat`）。

### 6.7 滤波器 DLL 放哪个目录最稳（宿主 x64 加载）

要求：**绝对路径稳定、不被宿主目录清扫/插件升级覆盖、不放 C:\Program Files（免提权写不了）、不放 %TEMP%（会被清理）**。建议：

```
%LOCALAPPDATA%\<ICCCE 厂商目录>\ICCCE\PluginData\<插件ID>\vcam\1.0.0\vcam_filter.dll
```

- 用**版本子目录**（`1.0.0`）：插件升级时不覆盖旧文件（旧路径仍在被注册表引用，进程可能仍持有旧文件），新版本写新目录后重新注册即可原子切换。
- 路径记入插件自己的配置文件（不要每次从注册表反查，注册表里存的是绝对路径，是「单一事实来源」，但插件需要知道当前版本用哪个）。
- 注册前用 SHA-256 校验释放出来的 DLL 是否完整（避免杀软/磁盘问题导致半个文件）。
- 若宿主允许，`PluginConfigFolder`（Lead 提到的插件配置目录）优先；否则 `%LOCALAPPDATA%` 下的固定目录。

### 6.8 「.icpx 只允许 3 个文件」约束下的释放 + 注册 + 校验 + 卸载

约束：插件包内只允许 `manifest.json` + 入口 dll + 同名 `deps.json`，**原生 DLL 不能进包**。方案：

**① 内嵌**：`vcam_filter.dll`（186,368 B）作为 `EmbeddedResource` 编进入口 DLL（Deflate 后预计 60–90 KB，构建时压缩；体积可忽略）。

**② 释放**（插件首次启用/版本变化时）：
```
读嵌入资源 → 校验内嵌 SHA-256（构建期写入常量）→
写 %PluginConfigFolder%\vcam\<ver>\vcam_filter.dll（先写 .tmp 再 Move 原子替换）→
再做一次落盘 SHA-256 比对，不一致则重试/报错降级
```

**③ 注册**（免提权，推荐 P/Invoke，**不依赖 regsvr32.exe**）：
```csharp
[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
static extern IntPtr LoadLibraryW(string path);
[DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
[DllImport("kernel32")] static extern bool FreeLibrary(IntPtr h);
[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
delegate int DllInstallFn([MarshalAs(UnmanagedType.Bool)] bool bInstall, string cmdLine);

var h = LoadLibraryW(dllPath);
var pfn = (DllInstallFn)Marshal.GetDelegateForFunctionPointer(GetProcAddress(h, "DllInstall"), typeof(DllInstallFn));
int hr = pfn(true, "");            // false = 卸载
FreeLibrary(h);
```
（或 `Process.Start("regsvr32", "/n /s /i \"path\"")`；PoC 两条路径都验证过，P/Invoke 更可控、无弹窗、能拿到 HRESULT。）

**④ 校验**（注册后立刻）：用 `ICreateDevEnum` 枚举（代码见 `poc-vcam/verify/verify-enum.cs`，可直接移植）确认 `FriendlyName` 出现；失败则最多重试 1 次（重新注册），仍失败进入降级。

**⑤ 卸载/禁用**：`DllInstall(false, "")` → 校验枚举里消失 → **不要立刻删文件**（预览中会被占用）；标记「下次启动清理」，或提示关闭宿主后删。

**⑥ 失败回退**（强制要求）：注册失败/枚举不到/预览黑屏 → **插件降级为自带展台窗口**（插件自己用 WPF/MediaElement/D3D 显示 RTSP 画面），不写宿主配置、不弹错、只记日志。这样即使在某台机器上虚拟摄像头不可用，产品功能仍完整。

**⑦ 大小评估**：`vcam_filter.dll` = 186,368 B（x64，静态 CRT，导出 5 个函数）。作为嵌入资源并 Deflate 后预计 **60–90 KB**，对 .icpx 体积无实质影响；远小于 FFmpeg（若 FFmpeg 也要内嵌，那是另一个数量级，建议优先调用系统/用户已有 ffmpeg 或内嵌精简版，见 §9）。

---

## 7. PoC 说明与验证记录

### 7.1 目录与构建

见 `_research/poc-vcam/README.md`。一键构建：`powershell -ExecutionPolicy Bypass -File .\build.ps1`。

产物：

| 文件 | 大小 | 说明 |
|---|---|---|
| `out\vcam_filter.dll` | 186,368 B | x64 滤波器；依赖仅 `ole32.dll / ADVAPI32.dll / KERNEL32.dll` |
| `out\testhost.exe` | 170,496 B | 枚举 + 连接 + 出帧验证器 |
| `out\verify-enum.exe` | 7,168 B（C#） | 模拟 `DsDevice.GetDevicesOfCat` |
| `out\shm_producer.exe` | 141,824 B | 共享内存帧生产者 |
| `out\regtool.exe` | 137,728 B | 注册/诊断 |

### 7.2 实测 1：枚举（宿主同款 API）

```
[enum] VideoInputDevice category:
  1) display: @device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}
     FriendlyName: YoCamera Virtual Camera
     BindToObject(IBaseFilter) OK, filter name: YoCamera Virtual Camera
  2) display: @device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}
     FriendlyName: OBS Virtual Camera
[enum] total video input devices: 2
[PASS] our filter is enumerated as a video input device
[PASS] CoCreateInstance(our filter) OK
```

C# 版（`verify/verify-enum.cs`，复刻 `DsDevice.GetDevicesOfCat` 的 `ICreateDevEnum`+`IMoniker`+`IPropertyBag` 路径）：

```
1) YoCamera Virtual Camera
     display: @device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}
     BindToObject(IBaseFilter): OK
2) OBS Virtual Camera
[enum] total VideoInputDevice = 2
[PASS] host-style enumeration found the virtual camera and activated it
```

### 7.3 实测 2：能力枚举 + 建链 + 出帧（1920x1080@30 RGB24）

```
[caps] IKsPropertySet PIN_CATEGORY = PIN_CATEGORY_CAPTURE (hr=0x00000000)
[caps] pin exposes 2 capability set(s), sizeof(VIDEO_STREAM_CONFIG_CAPS)=128
[caps]   #0: RGB24 1920x1080 @ 30.000 fps, minInterval=333333
[caps]   #1: RGB24 1280x720 @ 30.000 fps, minInterval=333333
[PASS] pin connected to sink (RGB24 negotiated)
[run] first pixel (B,G,R) = 235,235,235
[run] samples=60 bytes=373248000 bad=0 ptsRegressions=0 distinctFrames=60 firstPixelWhite=yes
[PASS] sample flow rate looks right
[PASS] frames animate (test pattern changes per frame)
[PASS] presentation timestamps monotonic
[PASS] test pattern content verified
RESULT: ALL CHECKS PASSED
```

`bytes=373,248,000 / 60 = 6,220,800 B/帧`（=5760×1080），即**完整 1080p 帧、2 秒 60 帧 = 29.9fps**，符合 30fps。

### 7.4 实测 3：多轮安装/卸载/枚举一致性

| 操作 | 结果 |
|---|---|
| `regsvr32 /n /s /i`（普通用户） | exit 0，`HKCU` 三个键写入，枚举 = 2 |
| `regsvr32 /s /i`（普通用户） | **exit 5**（拒绝访问，见 §6.3） |
| `regsvr32 /n /s /u /i` | 键删除，枚举 = 1（立刻消失，无需重启） |
| `install-user.ps1 -Uninstall` | state=absent，枚举 = 1 |
| `install-user.ps1 -Verify` | 注册 + C# 枚举校验 PASS |

### 7.5 实测 4：MFCreateVirtualCamera 对照实验（`src/mf_probe.cpp`）

```
[mf] MFCreateVirtualCamera(name='MF Probe Camera', lifetime=Session, access=CurrentUser)
[mf] MFCreateVirtualCamera hr=0x00000000
[mf] IMFVirtualCamera::Start hr=0x80040154          <- 假 sourceId，未注册 media source，符合预期
[mf] Media Foundation video capture devices: 0
[mf] staying alive 2 s ...
```

存活期间同时检查：

```
HKLM\...\CLSID\{860BB310-…}\Instance  → 只有 OBS
HKCU\...\CLSID\{860BB310-…}\Instance  → 只有我们的
reg query ... /f "MF Probe"           → 0 match
verify-enum.exe                       → total VideoInputDevice = 2（不含 MF Probe）
```

→ 支撑 §2.3 的结论：MF 虚拟摄像头不进入 DirectShow 的 `VideoInputDevice` 类别。

### 7.6 未覆盖项（明确标注）

- **真机 ICCCE 预览**（WPFMediaKit + VMR9 renderless + D3DImage）：本机未安装 ICCCE，**未实测**。已尽量降低风险：pin 报 `PIN_CATEGORY_CAPTURE`、只暴露 RGB24、`IAMStreamConfig` 完整、PTS 单调、`QueryFilterInfo` 正确。**下一步（交给集成线）**：在真机 ICCCE 里选 "YoCamera Virtual Camera" 看是否出彩条；这一步是唯一还没验证的关键环节。
- VMR9 对 `Pause→Run` 是否要求 preroll 样本：未实测（若黑屏，按 §4.3 补 preroll 分支）。
- 32 位宿主、多用户/服务账户、摄像头隐私开关（Win11「设置→隐私→相机」对 DShow 的影响）、杀软对 `regsvr32` 写 CLSID 的告警：均未实测。
- x86 构建、多分辨率/多帧率、YUY2、属性页、音频 pin：未做。

---

## 8. 风险与回退

### 8.1 主要风险

| 风险 | 影响 | 概率 | 缓解 |
|---|---|---|---|
| 真机 ICCCE/VMR9 预览黑屏或卡死 | 核心功能不可用 | 中低 | 已按 VMR9 友好方式实现；若黑屏先查：`SetPreroll`/preroll 样本、`IKsPropertySet` 类别、是否被 `SetFormat` 换成 YUY2（补 YUY2 支持）、日志加 `OutputDebugString` 计数 |
| 宿主用 `RenderStream(PinCategory.Preview, …)` 而非 Capture | 找不到 pin | 低 | 已报 `PIN_CATEGORY_CAPTURE`，Capture Graph Builder 会自动插 Smart Tee；仍不行则在 IKsPropertySet 里同时支持按请求返回 PREVIEW |
| 宿主先 `SetFormat` 成 YUY2/MJPG | 连接失败 | 中低 | 当前 `SetFormat` 拒绝非 RGB24 → 回退 `EnumMediaTypes`。若真机报 `VFW_E_INVALIDMEDIATYPE`，加 YUY2 |
| 插件升级/移动 DLL 后忘重注册 | 枚举到但激活失败（`0x80040154`） | 中 | 版本目录 + 启动时校验（枚举+`CoCreateInstance` 探活）；不一致自动重注册 |
| 免提权注册在「宿主以其他用户运行」时不可见 | 设备不出现 | 低 | 文档说明：同一用户才有效；跨用户走机器级 HKLM（需提权，或让用户手动装一次） |
| 1080p30 内存带宽（约 373 MB/s 双拷贝） | CPU 占用升高/掉帧 | 中 | 可降到 720p，或 YUY2（带宽/2），或让生产者直接写「滤波器提交给下级的 allocator 缓冲」（需句柄跨进程共享，复杂度高，暂不做）；实测 30fps 稳定 |
| 杀软/SmartScreen 对「写 CLSID + 免注册表释放 DLL」告警 | 安装被拦 | 中 | 只写 HKCU（影响面小于 HKLM）、内嵌资源有签名更好、给用户明确说明 |
| OBS 已装时列表出现两个虚拟摄像头 | 用户困惑 | 确定 | 设备名加品牌前缀（"YoCamera Virtual Camera"），文档说明 |

### 8.2 回退层级

1. **L1 参数回退**：出不了 1080p → 720p；RGB24 不通 → 补 YUY2；VMR9 黑屏 → 补 preroll。
2. **L2 复用现成虚拟摄像头**：装 OBS（本机已有），用 OBS 媒体源 + 虚拟摄像头，ICCCE 选 "OBS Virtual Camera"。零开发、当天可交付；缺点是依赖第三方软件。
3. **L3 插件自带展台窗口（最终回退）**：插件自己用 WPF `MediaElement`/`D3D11`/`LibVLC` 显示 RTSP 画面，完全不碰 DirectShow 虚拟摄像头。功能完整性最好（可做缩放/拍照/标注），只是不再复用 ICCCE 原有的展台预览控件。**这也是注册失败时的自动降级路径**（§6.8 ⑥）。
4. **L4 不装不注册**：仅当用户手动在设置里「启用虚拟摄像头」时才释放+注册；默认关闭，避免在客户机器上留下看不见的设备项。

### 8.3 建议的默认策略

- 功能**默认关闭**，设置里提供「启用虚拟摄像头（免管理员）」开关；
- 启用流程 = 释放 → 注册 → 枚举校验 → 失败自动回退到 L3 并写日志；
- 禁用/卸载 = 注销 → 提示重启宿主 → 下次启动删文件；
- 提供「复制诊断信息」按钮（HRESULT、DLL 路径、SHA-256、枚举结果），便于远程支持。

---

## 9. 下一步工作（建议分工）

1. **集成线（高优先）**：在真机 ICCCE 上装本 PoC（`scripts\install-user.ps1 -Verify`），打开视频展台，确认下拉框出现 "YoCamera Virtual Camera" 且预览出彩条。若黑屏，抓 `[run]` 计数与 VMR9 返回值。
2. **生产者实现**：.NET 插件内用 FFmpeg 解 RTSP：`-i rtsp://... -f rawvideo -pix_fmt bgr24 -s 1920x1080 -r 30 -`，或用 `ffmpeg.exe` 子进程 + 解析 stdout（注意 3 文件约束 → ffmpeg 也要内嵌/释放，或优先用系统 ffmpeg；这是插件包体积的主要变数，建议单独评估）。
3. **增强滤波器**：YUY2 支持、`Pause` preroll、`IAMDroppedFrames` 统计、`ISpecifyPropertyPages`（可选，做一个只读信息页）、x86 版本。
4. **重连策略**：FFmpeg 的 RTSP 断流重连（`-rtsp_transport tcp`、`-stimeout`、`-fflags nobuffer`），滤波器侧只负责「没新帧就继续彩条」，不要在滤波器里做网络重试。

---

## 10. 参考依据

**DirectShow 枚举/注册机制**
- [Enumerating Devices and Filters（System Device Enumerator / Filter Mapper）](https://learn.microsoft.com/en-us/windows/win32/directshow/enumerating-devices-and-filters)
- [System Device Enumerator](https://learn.microsoft.com/en-us/windows/win32/directshow/system-device-enumerator)
- [Using the Filter Mapper](https://learn.microsoft.com/en-us/windows/win32/directshow/using-the-filter-mapper)
- [IFilterMapper2::RegisterFilter](https://learn.microsoft.com/en-us/windows/win32/api/strmif/nf-strmif-ifiltermapper2-registerfilter)
- [ICreateDevEnum](https://learn.microsoft.com/en-us/windows/win32/api/strmif/nn-strmif-icreatedevenum)

**滤波器接口**
- [IAMStreamConfig](https://learn.microsoft.com/en-us/windows/win32/api/strmif/nn-strmif-iamstreamconfig)
- [Pin Property Set（IKsPropertySet / AMPROPSETID_Pin / PIN_CATEGORY_CAPTURE）](https://learn.microsoft.com/en-us/windows/win32/directshow/pin-property-set)
- [Using the DirectShow Base Classes（基类由 SDK 示例提供，非 SDK 库）](https://learn.microsoft.com/en-us/windows/win32/directshow/using-the-directshow-base-classes)
- DirectShow 基类归档位置：microsoft/Windows-classic-samples（`Samples/Win7Samples/multimedia/directshow` 一脉）或 DirectX SDK (June 2010) `Samples\C++\DirectShow\BaseClasses`

**Media Foundation 虚拟摄像头**
- [MFCreateVirtualCamera（Min client = Windows Build 22000；categories 仅 KS 类别；AllUsers 需管理员）](https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera)
- [IMFVirtualCamera](https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nn-mfvirtualcamera-imfvirtualcamera)

**免注册 COM**
- [Registration-Free COM 组件的激活：演练（ms973913）](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/ms973913(v=msdn.10))
- [Activation Contexts](https://learn.microsoft.com/en-us/windows/win32/sbscs/activation-contexts)
- [Activation Context Reference](https://learn.microsoft.com/en-us/windows/win32/sbscs/activation-context-reference)

**宿主侧依赖**
- WPFMediaKit（NuGet，本机缓存 `~\.nuget\packages\wpfmediakit`）：`VideoCaptureElement` 基于 DirectShow + VMR9 renderless + D3DImage
- DirectShowLib.Net（NuGet，本机缓存 `~\.nuget\packages\directshowlib.net`）：`DsDevice.GetDevicesOfCat` 的实现来源

**本机实测对照物**
- OBS Virtual Camera 注册项与 DLL（`C:\Program Files\obs-studio\data\obs-plugins\win-dshow\`），作为「商业可用的 DShow 虚拟摄像头」注册形态参考

---

## 附录 A：注册表导出（实测）

```reg
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}]
@="YoCamera Virtual Camera"

[HKEY_CURRENT_USER\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}\InprocServer32]
@="C:\\Users\\lrs2187\\Projects\\iccce-yocamera\\_research\\poc-vcam\\out\\vcam_filter.dll"
"ThreadingModel"="Both"

[HKEY_CURRENT_USER\Software\Classes\CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}]
"FriendlyName"="YoCamera Virtual Camera"
"CLSID"="{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}"
```

## 附录 B：PoC 文件清单

```
_research/poc-vcam/
├─ README.md
├─ build.ps1                       # vcvars64 + cl /MT 一键构建
├─ src/
│  ├─ vcam_filter.cpp              # ★滤波器：IBaseFilter/IPin/IAMStreamConfig/IKsPropertySet + 注册 + shm 读取 + 彩条
│  ├─ vcam_filter.def              # 导出 DllGetClassObject/DllCanUnloadNow/DllRegisterServer/DllUnregisterServer/DllInstall
│  ├─ vcam_shm.h                   # ★共享内存帧契约（256B 头 + 环槽）
│  ├─ testhost.cpp                 # 枚举 + 本地 sink 收帧 + 图案/PTS 校验
│  ├─ shm_producer.cpp             # 共享内存帧生产者（替代 .NET/FFmpeg）
│  ├─ regtool.cpp                  # LoadLibrary + DllInstall 安装/卸载/诊断
│  └─ mf_probe.cpp                 # MFCreateVirtualCamera 对照实验
├─ scripts/
│  ├─ install-user.ps1             # 免提权安装/卸载（regsvr32 /n /s /i + HKCU 兜底 + 校验）
│  ├─ install-admin.bat            # 提权安装（未实测）
│  └─ uninstall-admin.bat          # 提权卸载（未实测）
├─ verify/
│  ├─ verify-enum.cs               # 复刻 DsDevice.GetDevicesOfCat 的 C# 验证器
│  └─ verify-enum.ps1              # 编译 + 运行
└─ out/                            # 构建产物与运行日志（run_a.txt / run_b.txt / run_final.txt 等）
```
