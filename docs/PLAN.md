# 驿欧无线视频展台 → ICCCE 视频展台摄像头选择 · 实施方案

> 状态：**规划稿（待你确认 3 个决策点后开工）**
> 日期：2026-10-24
> 目标宿主：ICCCE 1.8.0.11（x64 / .NET 10），安装于 `C:\Users\lrs2187\Applications\InkCanvasForClass.CE.1.8.0.11-x64`
> 目标设备：驿欧无线视频展台（YiouBoard 2.0 套件，厂商接收端 `YOCameraDesktop.exe` 1.0.10.19）

---

## 1. 验收标准

用户视角的最终效果：

1. 打开 ICCCE 内置「视频展台」→ **摄像头下拉框里能选到「驿欧无线展台」**；
2. 选中后全屏预览出实时画面（延迟可接受、连续不卡死）；
3. 展台原有的**拍照 → 插入画布/照片栏、放大缩小、旋转、镜像、分辨率切换**等按钮全部照常可用；
4. 不安装、不运行厂商 `YOCameraDesktop.exe` 也能工作（完全替代）；
5. 插件可一键安装/卸载，卸载后下拉框里干净消失，不留系统垃圾。

---

## 2. 结论先行

### 2.1 可行性已由四路证据确认

| 议题 | 结论 | 依据 |
|---|---|---|
| 设备视频是什么 | 标准 **RTSP**：`rtsp://{设备IP}:554/stream0`，URL 无凭据 | `_research/yocam-protocol.md` §5 |
| 能否脱离厂商 EXE | **能**。发现=UDP 12811，控制=TCP 8888，仅 8 条命令、无握手无分帧 | 同上 §3–§4 |
| 插件能否直接往宿主下拉框加源 | **不能**。宿主列表唯一来源是 DirectShow `VideoInputDevice` 枚举，SDK/IPC/DI/URI 均无注入口 | `_research/iccce-integration.md` §2 |
| 唯一完整路径 | **系统级虚拟 DirectShow 摄像头**（插件负责装/注册/喂帧） | 同上 §2 路径 I |
| 虚拟摄像头难不难 | **已跑通 PoC**：普通用户免提权注册 → 被 `ICreateDevEnum` 枚举 → `BindToObject` 成功 → 1920×1080@30 RGB24 稳定出帧 | `_research/virtual-camera.md` §7、`_research/poc-vcam/` |

### 2.2 架构一句话

> **插件把系统环境改造一下，让 ICCCE 原封不动地把「驿欧无线展台」当成一个普通 USB 摄像头。**

宿主源码零修改、不依赖未公开 API、不需要驱动。

---

## 3. 事实基线

### 3.1 设备侧（驿欧无线展台）

| 项 | 值 |
|---|---|
| 发现 | UDP 广播 `255.255.255.255:12811`，载荷 `"CMD_GETIP"+8位配对码`，1 秒一次 |
| 发现响应 | 设备单播回 `"<PC的IP>ACK_GETIP"`；**设备 IP 取 UDP 源地址**，不解析文本 |
| 控制通道 | TCP `{设备IP}:8888`，纯 ASCII/GBK 文本，**无握手 / 无长度前缀 / 无分隔符 / 无换行** |
| 心跳 | 每 ~1s `CMD_DATA_SYNC`，回包含 `DATA_SYNC`；连续 5 次无回应则断开重连 |
| 开流 | TCP 发 `CMD_RTSP_TRANS_START`，收到含 `ACK_RTSP_TRANS_START` 后 RTSP 才有流 |
| 取流 | `rtsp://{ip}:554/stream0`，RTP/UDP，H.264 1920×1080（编码/分辨率待真机 ffprobe） |
| 拍照回传 | 设备→PC：PC 监听 TCP **11111**，23 字节文本头 + JPEG + `END_TRANSPHOTO` |
| 主动拍照/画质控制 | **不支持**（`SendGetImgCmd` 抛 `NotImplementedException`；无 Zoom/对焦/白平衡命令） |
| 待机 | `CMD_LIVE_STOP{分钟}`；`CMD_WIFI_STA` 回普通模式 |
| 配对码 | 持久化于 `%USERPROFILE%\Documents\YOFiles\YOCamSet.xml`（本机实测 `PinCode=92271885`） |
| 其余命令 | 穷举仅 8 条，已全部列表化 |

**关键含义**：宿主展台的「分辨率切换」由我们的虚拟摄像头自己报能力，不依赖设备；「拍照按钮」是抓我们提供的帧，也不依赖设备；而宿主的**画质滑块**（亮度/对比度等）设备根本支持不了 → 我们虚拟摄像头**不实现** `IAMVideoProcAmp/IAMCameraControl`，让滑块自动禁用。

### 3.2 宿主侧（ICCCE 1.8.0.11）

| 项 | 值 |
|---|---|
| 平台 | .NET 10 WPF，TFM `net10.0-windows10.0.19041.0`，**x64**，框架依赖 |
| 插件 TFM | 与宿主一致（net10.0-windows10.0.19041.0），本机 dotnet SDK 10.0.402 可编译 |
| SDK API 版本 | `HostApiRequirement.CurrentApiVersion = 1.13.0` |
| 摄像头列表 | `DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)` 唯一来源 |
| 全屏预览 | WPFMediaKit `VideoCaptureElement`（DirectShow + VMR9 renderless + D3DImage） |
| 拍照 | 特殊模式从 D3DImage 回读 → 要求 VMR9 真出图，不能只靠 SampleGrabber |
| 展台窗体 | 主窗口内联，**非独立窗口** |
| 「上次选的摄像头」 | `Configs/Settings.json` → `canvas.videoPresenterLastCameraName`（即 `DsDevice.Name`） |
| 打开展台 | 插件可 `IVideoBoothService.Toggle()`；或 `icc://videopresenter`（需启用 URI 且注册 HKCR，需管理员） |
| 插件包 | `.icpx` 运行时**不校验**文件白名单（只查路径逃逸），额外 DLL 可装；3 文件是 CI/市场上架约定 |
| manifest | 字段必须 **PascalCase**；建议 `ApiVersion:"1.0.0"`、`MinHostVersion:"1.8.0.11"` |
| 设备可见时机 | `MultimediaUtil.VideoInputDevices` 是进程内静态缓存 → 设备须在宿主**首次打开展台前**已注册 |

### 3.3 虚拟摄像头 PoC 现状（已完成）

| 项 | 结果 |
|---|---|
| 产物 | `vcam_filter.dll` = 186,368 B，静态 CRT，仅依赖 ole32/advapi32/kernel32 |
| 实现方式 | 原生 C++ 自实现 COM（`strmbase` 已确认不在 SDK 10.0.26100.0 中） |
| 注册 | **HKCU 免提权**：`regsvr32 /n /s /i vcam_filter.dll` 或 `LoadLibrary + DllInstall`，**无需 UAC** |
| 已验证 | 枚举到 `YoCamera Virtual Camera` → `BindToObject` 成功 → 1920×1080@30 RGB24 出帧 60/60、PTS 单调、图案校验通过 |
| 跨进程 | 命名共享内存 `Local\YoCameraVcamFrames`（seqlock，256B 头 + 4 槽 ×6.22MB）喂帧通过 |
| 未验证 | **在 ICCCE 的 VMR9 renderless 预览链路内出图**（唯一关键缺口） |
| 现状 | 该虚拟摄像头**当前仍注册在本机**（CLSID `{9A3E7D21-...}`），便于下一步验证 |

---

## 4. 总体架构

```
┌───────────────────────── ICCCE 1.8.0.11 宿主进程 (x64, .NET 10) ─────────────────────────┐
│                                                                                          │
│  视频展台 UI  ──  摄像头下拉框（DirectShow 枚举）  ──  选中「驿欧无线展台」               │
│       │                                                                                  │
│       └─> WPFMediaKit VideoCaptureElement (VMR9 renderless)  ←── vcam_filter.dll (in-proc)│
│                                                                    ▲                     │
│  插件 YiouCamera.Plugin.dll  ── 注册/生命周期/UI/预选/日志          │ 命名共享内存         │
└────────────────────────────────────────────────────────────────────┼─────────────────────┘
                                                                     │ RGB24 帧 + 时间戳
                    ┌────────────────────────────────────────────────┴──────────────┐
                    │  采集内核（托管，host 进程内或独立辅助进程）                    │
                    │  发现(UDP 12811) → 控制(TCP 8888 心跳) → RTSP 客户端 → H.264 解码 │
                    └───────────────────────────────────────────────────────────────┘
                                          │
                                    WiFi / RTSP 554
                                          ▼
                                  驿欧无线视频展台
```

**分层职责**

| 层 | 语言/框架 | 职责 | 失败时 |
|---|---|---|---|
| 采集内核 `YiouCamera.Core` | C# / net10.0 | 发现、控制、心跳、RTSP、解码、帧源抽象、重连 | 重连；长时间失败则虚拟摄像头出「无信号」占位帧 |
| 虚拟摄像头 `vcam_filter.dll` | C++ x64 | 把共享内存帧包装成 DirectShow 视频输入设备 | 注册失败 → 降级自带展台窗口 |
| 插件 `YiouCamera.Plugin` | C# / net10-windows WPF | 安装/注册/启动/停止、设置页、工具栏按钮、预选 + Toggle、日志、降级 UI | — |

---

## 5. 关键技术决策（**已拍板**，2026-10-24）

> 锁定结论：
> - **D1 = 先 FFmpeg（`IFrameSource` 抽象隔离），后续再瘦身**
> - **D2 = 安装脚本 + 文件夹安装**（免 UAC，允许原生文件）
> - **D3 = 暂不做降级展台窗口**
> - **实施原则：尽量按厂家实现写**（发现 1s 节拍、心跳 1s/5 次掉线、FFmpeg 参数 `buffer_size=1655360`/`stimeout=5000000`/`max_delay=1000000`/`localaddr`、开流 60×1s 重试……），以降低周一现场联调失败返工风险。只在厂家实现**确有 bug** 处做最小修正（缓冲分帧、23 字节头读满）。
> - **生产环境无物理摄像头、无 OBS** → 我们的虚拟摄像头通常是系统里**唯一/第一个**视频输入设备，`DevicePath=null` 冲突风险（R2）大幅下降，但仍需一次性验证。

### D1 · RTSP 与 H.264 解码 ← 选定「先 FFmpeg，后续瘦身」

### D1 · RTSP 与 H.264 解码怎么做

| 方案 | 做法 | 优点 | 代价 |
|---|---|---|---|
| **F1（推荐）** | `FFmpeg.AutoGen` + x64 FFmpeg 共享库 | 厂商同路线、最稳、实现最快、兼容各种异常流 | 需带原生库（约 40–60 MB，运行时释放）；厂商只自带 **x86**，需另取 x64 构建 |
| F2 | 自写 RTSP/RTP/H.264 拆包 + Windows Media Foundation 解码器 MFT | 零第三方原生依赖，包极小 | 工作量 +1.5k 行，长时稳定性要自己扛 |
| F3 | `LibVLCSharp` + libvlc | 省事 | 体积更大、封装不可控 |

**推荐 F1**：先把「能用」做出来；F2 作为后续瘦身选项，接口上用 `IFrameSource` 抽象隔离，切换不影响其他层。

### D2 · 交付与安装形态

| 方案 | 说明 | 优点 | 缺点 |
|---|---|---|---|
| **G1（推荐）** | 安装脚本把「插件 dll + vcam_filter.dll + FFmpeg 库」释放到 `Plugins/com.yiou.boothcam/`，并自动 `DllInstall` 注册虚拟摄像头 | 简单可靠、允许原生文件、免 UAC | 非标准 `.icpx` 流程 |
| G2 | 单文件 `.icpx`：把 vcam dll（压缩后 ~60–90KB）与 FFmpeg 作为**嵌入资源**打进入口 dll，首启释放 | 符合 3 文件约定，可上市场 | 包体积大、首启释放/校验逻辑、本地安装仍需 UI 审批 |

**推荐 G1 为主 + G2 作为后续市场版**。宿主运行时接受额外文件（源码确证），G1 当天就能用。

### D3 · 是否做「降级：插件自带展台窗口」

- 需要：当虚拟摄像头注册失败（企业策略/杀软/枚举异常）时，仍能给学生用。
- 成本：中等（全屏窗口 + 拍照插画布 + 缩放旋转，接口走 `ICanvasInkService`）。
- **推荐做**，但排在 P4 最后，不阻塞主线。

### D4 · 设备显示名

推荐 `驿欧无线展台`（必要时加后缀 `(ICC-CE)`），写入 `canvas.videoPresenterLastCameraName` 时用它，大小写不敏感匹配。

### D5 · 配对码（Pincode）策略

1. 优先读 `%USERPROFILE%\Documents\YOFiles\YOCamSet.xml` 的 `PinCode`（厂商设备已被这个值配对过，最省事）；
2. 读不到则在插件设置页让用户填 8 位数字，或生成随机值并引导走二维码配对；
3. 待真机确认设备是否强校验配对码（验证项 V1）。

---

## 6. 工作区结构

```
iccce-yocamera/
├─ docs/PLAN.md                      ← 本文件
├─ _research/                        ← 已完成的调研与反编译产物（保留）
│  ├─ yocam-protocol.md              设备协议规格书（1065 行）
│  ├─ iccce-integration.md           宿主接入面 + 虚拟摄像头契约 C1–C20（552 行）
│  ├─ virtual-camera.md              虚拟摄像头方案与 PoC 记录（658 行）
│  └─ poc-vcam/                      可直接编译注册的原生 PoC
├─ src/
│  ├─ YiouCamera.Core/               net10.0 类库：Discovery / Control / Rtsp / Decode / FrameSource
│  ├─ YiouCamera.Plugin/             net10.0-windows WPF：ICCCE 插件入口 + UI
│  └─ YiouCamera.Vcam/               C++ x64（由 poc-vcam 演进，非重写）
├─ tools/
│  ├─ build-all.ps1
│  ├─ install.ps1 / uninstall.ps1   释放文件 + 注册/反注册虚拟摄像头
│  └─ verify-enum.ps1               枚举校验
└─ tests/
   ├─ YiouCamera.Simulator/          设备端仿真器（UDP 12811 / TCP 8888 / 本地 RTSP）
   └─ YiouCamera.Harness/            无宿主环境下建图/VMR9 验证
```

---

## 7. 阶段计划

| 阶段 | 目标 | 主要交付物 | 验证门（Gate） | 预估 |
|---|---|---|---|---|
| **P0** | 规划定稿 | 本文件 + 三份调研 | 你确认 D1/D2/D3 | 完成 |
| **P1** | 采集内核 + 设备仿真器 | `YiouCamera.Core`、`YiouCamera.Simulator` | 无真机下：仿真器应答发现/心跳/开流，内核成功拉到本地 RTSP 并解出帧 | 1–2 天 |
| **P2** | **虚拟摄像头在 ICCCE 内跑通**（最高风险，先做） | vcam 演进 + 测试宿主 | ICCCE 1.8.0.11 展台下拉框出现设备、全屏出图、拍照成功、切分辨率 3 次不黑屏 | 0.5–1 天 |
| **P3** | 端到端打通 | Core → 共享内存 → vcam → 宿主 | 仿真器提供画面时，ICCCE 展台内看到该画面；断流/重连可恢复 | 1–2 天 |
| **P4** | 插件产品化 | 工具栏按钮、设置页、安装/卸载、预选 + Toggle、日志、降级窗口 | 装/卸载干净；点按钮直接打开带我们的展台；异常有提示 | 2–3 天 |
| **P5** | 真机联调（需设备） | 联调记录 | 协议文档 §12 的 16 项待验证清单逐条关闭 | 0.5–1 天 |
| **P6** | 打包交付 | 安装包、README、排障手册 | 干净机器上一路装到出图 | 0.5 天 |

> **排序理由**：P2 是"能不能成"的唯一硬门槛，且不需要真机（设备已注册），所以提前到 P1 之后立刻做；真机只影响 P5。

---

## 8. 虚拟摄像头验收契约（摘自调研，实现以此为准）

**必须（缺了就不工作）**

- C1 注册进 `CLSID_VideoInputDeviceCategory`，`DsDevice.GetDevicesOfCat` 能枚举到
- C2 moniker 可 `BindToObject`；`AddSourceFilterForMoniker` 可用
- C3 x64 in-proc COM，**不可假设 STA**（宿主在 `Task.Run` 线程建图）
- C4 capture pin 必须带 `PinCategory.Capture`
- C6 capture pin 实现 `IAMStreamConfig`（`GetNumberOfCapabilities/GetStreamCaps/SetFormat/GetFormat`）
- C8 media type 必须是 **`FormatType.VideoInfo`**（不是 `VideoInfo2`），首选 **RGB24**、正 height、stride `=(w*3+3)&~3`
- C10 未连接时也要能 `SetFormat`
- C11 push source：worker 线程 `IMemInputPin.Receive(sample)`，Stop 立即退线程
- C13 反复 Stop → 重建图不得残留
- C14 图构建要 **< 1.5s** 成功（RTSP 可异步，先出占位帧），否则宿主重试 2 次就放弃
- C17 `FriendlyName` 稳定唯一

**高风险（P2 必须实测）**

- ~~**C18 `DevicePath`**~~ → **已在规划阶段解决，见 §13**。结论：必须在注册时写入 `DevicePath` 值，否则 WPFMediaKit `AddFilterByDevicePath(null)` 会把系统里**任意一个** `DevicePath=null` 的软件设备绑进来（实测本机 OBS Virtual Camera 与我们的设备同时为 null → 风险真实存在）。
- **VMR9 renderless**：`testhost` 只验证了普通建图，未验证 VMR9 无窗口渲染。若黑屏，按 `Pause() → 等 preroll → Run()`、必要时补 YUY2 输出。

**刻意不做**

- 不实现 `IAMVideoProcAmp/IAMCameraControl`：设备不支持画质控制，实现了反而要维护"常驻探测图 + 流图"多实例共享，风险大于收益。宿主会自动禁用滑块。

---

## 9. 无真机的验证策略

真机不一定随时在手，所以：

1. **设备端仿真器**（`tests/YiouCamera.Simulator`）：模拟 UDP 12811 应答 `ACK_GETIP`、TCP 8888 心跳/开流 ACK、并在本地起一个 RTSP 服务端推测试图。→ 采集内核可在无设备下完整回归。
2. **测试宿主**（`tests/YiouCamera.Harness`）：不启 ICCCE，用与宿主相同的调用序列（枚举 → `BindToObject` → `IAMStreamConfig` → SampleGrabber → **VMR9 renderless**）建图并抓帧，覆盖 C1–C18。
3. **宿主内冒烟**：ICCCE 1.8.0.11 上，装插件 → 打开展台 → 截图比对 → 连续开关 5 次、切分辨率 3 次、拍照 3 次，检查 `PluginLogs` 与内存。
4. **真机到手**：跑 `yocam-protocol.md` §14 的一键 PowerShell 脚本，先抓包确认协议，再关 16 项清单。

---

## 10. 风险与回退

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| R1 | **无真机**，协议细节（配对码校验、23 字节头、RTSP 鉴权）未实测 | 联调返工 | 仿真器 + 16 项验证清单前置；真机到手当天关清单 |
| R2 | `DevicePath=null` 与其它软件虚拟摄像头（如 OBS）冲突 | 预览绑错设备 | **已解决（实测）**：见 §13 |
| R3 | VMR9 renderless 出图兼容 | 预览黑屏 | `Pause/Preroll`、YUY2 兜底、`MediaOpened` 占位帧 |
| R4 | 虚拟摄像头对**全系统**可见（相机/会议软件也会看到） | 用户困惑 | 名字带标识、设置页可一键卸载、安装说明写清 |
| R5 | 宿主升级改内部实现 | 插件失效 | 我们只依赖 DirectShow + 公开 SDK 设置项，脆弱面小于反射型插件；锁 `MinHostVersion` |
| R6 | 1080p RGB24 双拷贝约 373 MB/s | CPU 占用 | 可选 720p 默认；后续可加 YUY2/共享纹理优化 |
| R7 | FFmpeg x64 依赖体积与来源 | 安装包大 | 运行时可下载 + 校验哈希；或改 F2 |
| R8 | 插件 SDK 为 GPL-3.0-only | 分发许可 | 自用无碍；对外发布前确认许可策略 |

**回退层级**：① 虚拟摄像头不可用 → 插件自带展台窗口（功能等价、不在下拉框）；② 再不行 → 引导用户把宿主展台模式改成"自定义"指向插件程序。

---

## 11. 需要你确认的 3 件事

1. **D1 解码**：走 FFmpeg（推荐，快且稳，但要带 x64 原生库）还是纯托管 MF 路线（包小、工作量大）？
2. **D2 交付**：安装脚本（推荐，允许原生文件、免 UAC）还是单文件 `.icpx`（合规、但包大）？
3. **D3 降级**：是否要「插件自带展台窗口」作为保底？会增加约 0.5–1 天。

另外两个信息确认：

4. **真机什么时候能到手**？如果短期内没有，我按 P1→P2→P3 用仿真器推进，真机只影响 P5。
5. 这台机器上 **OBS Virtual Camera 已安装**（与 R2 冲突相关），能否在 P2 期间临时禁用它？若不能，我优先做「规避方案」。

---

## 12. 附：调研证据索引

| 文档 | 内容 |
|---|---|
| `_research/yocam-protocol.md` | 设备协议全量规格（时序图、逐字段报文、可抄实现、16 项待验证清单、证据行号） |
| `_research/iccce-integration.md` | 宿主 12 条路径对比、虚拟摄像头契约 C1–C20、工程模板、R1–R13 风险 |
| `_research/virtual-camera.md` | 四条虚拟摄像头路线对比、接口清单、共享内存布局、注册键值、PoC 实测记录 |
| `_research/poc-vcam/` | 可编译可注册的原生 PoC（`vcam_filter.cpp` 等） |
| `_research/YOCameraDesktop-decompiled/` | dnSpy 反编译源码 |
| `_research/easiconnect-decompiled/` | 生态内既有插件参考（反射注入展台侧栏/照片栏） |

---

## 13. 规划阶段的关键技术验证（已做）

### 13.0 ⚠️ 主线变更（2026-10-24 15:55）：改为 camera provider

**结论：原生虚拟摄像头路线在目标机器上无法验收，主线切换为「给 ICCCE 打 camera provider 补丁 + 插件通过 provider 推帧」。**

证据链（都在本机实测）：

| 实验 | 结果 |
|---|---|
| vcam 自身 `testhost`（自建 sink，非 VMR9） | 52–53 项全 PASS（双 pin、4 组 caps、60/60 帧、多实例、Stop 重建、方向正立） |
| vcam `testhost --vmr9-renderless`（自建最小 surface allocator，等价 WPFMediaKit 契约） | D3D9Ex 设备创建 hr=0；`AdviseSurfaceAllocator`/`SetD3DDevice` hr=0；`RenderStream(Preview)` 成功；`IMediaControl::Run` 返回 S_OK —— **但 VMR9 从不调用 `InitializeDevice`（0 次）**，输入分配器 `Commit()` 对 5 组属性全部 `E_OUTOFMEMORY`，`GetBuffer=VFW_E_NOT_COMMITTED`，图永远停在 Paused |
| **真实 ICCCE 1.8.0.11 宿主**（`icc://videopresenter` 直接开展台） | 与上面完全一致：`Commit=E_OUTOFMEMORY`、`Run(tStart=)` 出现 0 次、黑屏 |
| **物理摄像头对照**（`HIK 1080P Camera`，WPFMediaKit 同路径） | `MediaOpened 530ms`、无失败、系统提示"正在使用摄像头" → **宿主渲染链路本身可用** |

⇒ 机器环境没问题（D3D9Ex 可用），卡点是我们这个软件滤波器的输入分配器与 VMR9 renderless 的 `InitializeDevice` 时机不兼容；本机含 `GameViewer Virtual Display Adapter`，但物理摄像头能跑通说明不是显示栈全废。
⇒ 继续调 VMR9 的投入产出比不划算（且没有第二台机器可交叉验证），**native 线冻结保留**。

替代方案（已实测跑通）：**camera provider** —— 宿主新增 SDK 接口，插件直接推帧，宿主用 WPF `Image` 渲染（共享现有 LayoutTransform/RenderTransform），完全绕开 DirectShow/VMR9。
真实宿主 GUI 截图证据：`docs/pr-assets/camera-provider-booth-preview.png`；补丁 `docs/camera-provider.patch`（13 文件，+1995/−33，编译 0 错误）。

**交付形态**：`build/patched-host/`（可双击运行的 ICCCE）+ 插件（运行时探测 provider，缺失则降级 vcam 兼容模式）+ `docs/PR.md`（可提交上游）。

### 13.1 R2 `DevicePath` 冲突 —— 已定位并解决（实测）

**问题链（源码 + 实测双向确认）**

1. ICCCE 以 `VideoCaptureDevice = device` 赋值（`MW_VideoPresenter.cs:2354`），不是按名字。
2. 因此 WPFMediaKit 走 `AddFilterByDevicePath(device.DevicePath)`（`VideoCapturePlayer.cs:201`）。
3. `AddFilterByDevicePath` 的实现是
   `GetDevicesOfCat(...).Where(d => d.DevicePath == devicePath).FirstOrDefault()`
   （`MediaPlayerBase.cs:762-774`）——**当我们的 `DevicePath` 为 null 时，它会匹配所有 null 设备，然后取第一个**。
4. 实测本机枚举结果：

   | 设备 | DevicePath |
   |---|---|
   | YoCamera Virtual Camera（我们的） | `<NULL>` |
   | OBS Virtual Camera | `<NULL>` |

   → 两个 null 设备，**R2 真实存在**，绑到谁取决于枚举顺序。

**解决办法（已验证有效）**

在设备类别 Instance 键下增加一个字符串值 `DevicePath`：

```
HKCU\Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\{<滤波器CLSID>}
    FriendlyName = 驿欧无线展台
    CLSID        = {<滤波器CLSID>}
    DevicePath   = yocam:yiou-wireless-dc      ← 新增，唯一值
```

DirectShow 的设备 moniker 属性包会把该值作为 `DevicePath` 暴露，`DsDevice.DevicePath` 随之非空。实测加完后枚举结果变为：

| 设备 | DevicePath |
|---|---|
| YoCamera Virtual Camera | `yocam:yiou-wireless-dc` |
| OBS Virtual Camera | `<NULL>` |

→ `AddFilterByDevicePath("yocam:yiou-wireless-dc")` **精确命中且唯一**，与 OBS、与任何其它软件设备都不再冲突。

**落地要求**：虚拟摄像头安装脚本/`DllInstall` 必须写这个 `DevicePath` 值；卸载时一并删除。验收项加一条：枚举后 `DsDevice.DevicePath` 必须非空且全局唯一。

> 注：现场生产环境本来就没有物理摄像头和 OBS，此风险天然不存在；但加上该值后，即使老师机器上装了 OBS/其它虚拟摄像头也不会串台，属于低成本高收益加固。

### 13.2 其余已验证结论

| 项 | 结论 |
|---|---|
| 宿主版本/位数 | 1.8.0.11 / x64 / .NET 10（`runtimeconfig.json` + PE 头实测） |
| 虚拟摄像头注册 | HKCU 免提权，`regsvr32 /n /s /i` 或 `DllInstall`，**无需 UAC** |
| 枚举/绑定/出帧 | `ICreateDevEnum` 枚举到 → `BindToObject` 成功 → 1920×1080@30 RGB24 正常 |
| MF 虚拟摄像头 | 对 ICCCE 无效（不进 DirectShow `VideoInputDevice`） |
| 协议是否需厂商 EXE | 不需要（RTSP + 8 条命令，规格书已完备） |

### 13.3 宿主内预览验证（P2）—— 工具已就绪，卡点已定位

已搭建 `tests/Harness`（WPF，引用**宿主内解包出来的 WPFMediaKit.dll** + DirectShowLib.Net 3.0.0），
完全照抄 ICCCE 的启动序列：
```
Stop() → VideoCaptureDevice=null → 250ms → BeginInit →
  VideoCaptureDevice=device / EnableSampleGrabbing=false / UseYuv=false /
  LoadedBehavior=Manual / DesiredPixelWidth=1280 / Height=720 / FPS=30 →
EndInit() → Play()
```
并以 SHM 头的 `readSeq` 增量判定「滤波器是否真的在消费帧」（比看窗口截图更可靠）。

**已定位的关键卡点（实测）**：WPFMediaKit `SetupGraph()` 第 252 行渲染的是
`RenderStream(PinCategory.Preview, ...)`，而 PoC 滤波器只暴露 **Capture** pin →
`VFW_E_CANNOT_CONNECT`（无可用中间筛选器）。**结论：Preview pin 是硬要求**，已写入 §8 与 CONTRACTS。

**已发现并规避的 WPFMediaKit 怪癖**：首次 `SetupGraph()` 一定走
`if (m_videoCaptureSourceChanged) AddFilterByName(VideoCaptureSource)`（此时是空串）而失败，
失败分支会清掉该标志，**第二次建图才走 DevicePath 分支**。ICCCE 的 1.5s 兜底重试正是为此
（`MW_VideoPresenter.cs:134-143`）。插件/回归脚本必须容忍「首次必败」。

一键回归：`powershell -ExecutionPolicy Bypass -File tests\Harness\run-harness.ps1`（判据：日志出现 `PASS(device-path)`）。


