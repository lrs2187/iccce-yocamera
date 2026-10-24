# YiouCamera.Vcam — 虚拟 DirectShow 摄像头（驿欧无线展台）

把 `Local\YoCameraVcamFrames` 共享内存里的 RGB24 帧变成 ICCCE 宿主能枚举、能预览的
DirectShow 视频输入设备。x64 in-proc COM，静态 CRT，只依赖系统 DLL（不引入第三方库）。

对应契约：`docs/CONTRACTS.md` §1/§5、`docs/PLAN.md` §8（C1–C20）与 §13.1（DevicePath）。

## 目录

```
vcam_filter.cpp       DirectShow 源滤波器（自实现 COM，不用 strmbase）
vcam_shm.h            共享内存帧契约（256B 头 + 4 槽 seqlock；RGB24/BGR，top-down）
testhost.cpp          C1–C20 原生回归自测（双 pin / caps / 60 帧 / 重建 / 多实例 / 方向）
shm_producer.cpp      共享内存喂帧工具（测试图案；orient 模式用于方向探针）
regtool.cpp           DllInstall 注册/反注册助手（免 UAC）
verify/verify-enum.cs 宿主同款枚举 + DevicePath 非空/唯一校验（csc 编译）
build.ps1 build.cmd   构建脚本（产物写入仓库根 build\out）
```

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File .\src\YiouCamera.Vcam\build.ps1
# 或仓库根: powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物（仓库根 `build\out\`）：

| 文件 | 说明 |
|---|---|
| `vcam_filter.dll` | 交付给插件的虚拟摄像头（约 211 KB，x64，静态 CRT） |
| `testhost.exe` | 原生回归自测（退出码 0 = 全过） |
| `shm_producer.exe` | 共享内存喂帧工具，`shm_producer.exe [w h fps seconds]` / `orient [seconds]` |
| `regtool.exe` | `regtool install|uninstall|regserver|unregserver <dll> [friendlyName]` |
| `verify-enum.exe` | 宿主同款枚举 + DevicePath 校验 |

需要 VS2022 Community/BuildTools（x64 + Windows SDK 10.0.26100），脚本会自己找 `vcvars64.bat`。

## 注册（免 UAC，写 HKCU）

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\vcam-register.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\vcam-register.ps1 -Dll .\build\out\vcam_filter.dll
```

脚本会：`DllInstall` 注册 → 校验注册表（FriendlyName / CLSID / **DevicePath**）→
调用 `verify-enum.exe` 确认宿主枚举到的名字正确、**DevicePath 非空且全局唯一**、`BindToObject` 成功。

卸载：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\vcam-unregister.ps1
```

## 回归验证

```powershell
# 1) 无生产者：灰底占位帧持续推送（不允许停推）
.\build\out\testhost.exe

# 2) 有生产者：真帧内容 + 双 pin 同时出帧
Start-Process .\build\out\shm_producer.exe -ArgumentList 1280,720,30,30
.\build\out\testhost.exe --producer

# 3) 方向探针（SHM 顶部 100 行红、其余蓝 → 输出底部蓝、顶部红）
Start-Process .\build\out\shm_producer.exe -ArgumentList orient,30
.\build\out\testhost.exe --orient

# 4) 宿主同款枚举 / DevicePath
powershell -ExecutionPolicy Bypass -File .\src\YiouCamera.Vcam\verify\verify-enum.ps1
```

`testhost.exe` 覆盖（53 项）：枚举名称、DevicePath 非空/唯一、BindToObject、两个 pin 的
PinCategory（Capture/Preview）、`IAMStreamConfig` 四组 caps（YUY2/RGB24 × 720p/1080p，
`AvgTimePerFrame≠0`）、未连接 SetFormat（含 WPFMediaKit 的 0×0 调用）、Pause preroll 与
Run 首帧 <1.5s、60/60 帧、Stop→Run/重建/3 次快速重建、Capture+Preview 双 pin、双实例共享同一
SHM 帧源、无信号灰底占位、时间戳单调、方向正确。

## 已实测（本机 Windows 11 26H2 / x64 / VS2022）

- `build.ps1` 构建通过；`vcam_filter.dll` 约 211 KB，仅依赖系统 DLL（静态 CRT）。
- `DllInstall` 免 UAC 注册成功；注册表：
  `HKCU\Software\Classes\CLSID\{860BB310-…}\Instance\{9A3E7D21-…}` 下
  `FriendlyName=驿欧无线展台`、`CLSID={9A3E7D21-…}`、**`DevicePath=yocam:yiou-wireless-dc`**。
- 宿主同款枚举（`ICreateDevEnum` + `IPropertyBag`）：名称正确、DevicePath 非空且唯一（本机
  OBS Virtual Camera 仍为 `<NULL>`，不冲突）、`BindToObject(IBaseFilter)` 成功。
- 原生自测 `testhost.exe`：无生产者 53/53、有生产者 53/53（含双 pin、多实例、方向）。
- 反注册脚本：删除两处 HKCU 键（含 DevicePath），枚举确认设备消失；重新注册后恢复。

## 设计与实现要点

- **两个输出 pin**（真实采集卡的形态，WPFMediaKit 需要）：
  - `Capture` → `PIN_CATEGORY_CAPTURE` + `IAMStreamConfig`
  - `Preview` → `PIN_CATEGORY_PREVIEW`
  - 两者由**同一个 worker 线程、同一个帧源**驱动（Smart-Tee 式分发），不会各自读 SHM。
  - `RenderStream(PinCategory.Preview, …)` 是 WPFMediaKit `VideoCapturePlayer.SetupGraph()` 的
    真实调用；只有 Capture pin 会得到 `VFW_E_CANNOT_CONNECT`。
- **格式**：优先 **YUY2**（VMR9 renderless / X8R8G8B8 表面走 16bpp 4:2:2 最稳），同时提供
  **RGB24**（SampleGrabber / 宿主非特殊模式）。`GetFormat` 默认 YUY2 1280×720@30；SHM 为 RGB24，
  YUY2 由滤波器实时转换（BT.601，`Y0 U Y1 V`）。
- **行序**：SHM 是 **top-down**（第 0 行 = 图像顶部，`docs/CONTRACTS.md` §1）；DirectShow 采样缓冲
  为 positive biHeight 的 bottom-up，`CopyShmSlot()` 逐行倒序（每行一次 memcpy），画面正立。
- **C14**：`Pause()` 立即出 preroll 占位帧（失败重试最多 ~3s），`Run()` 首帧 <20ms；无信号时
  灰底占位帧按 30fps 持续推送，绝不停推。
- **C13**：allocator 跨 `Stop→Run` 保留（只在 `Disconnect`/析构释放），worker 线程可重启；
  3 次快速重建无残留。
- **C18 DevicePath**：由 `DllRegisterServer`/`DllInstall` 写入，`DllUnregisterServer`/`DllInstall(FALSE)` 删除。
- **不做** `IAMVideoProcAmp`/`IAMCameraControl`（设备无画质控制，PLAN §8 刻意不做）。
- 多实例共用同一个 `Local\` 共享内存命名区，各自只读帧槽，不写帧数据（仅写 `readSeq` 诊断计数）。

## 已知环境限制（本机实测，2026-10-25）

本机（ASUS 笔电，除 Intel UHD 630 外还装有一个 **GameViewer Virtual Display Adapter** 虚拟显示器）
上，**真实 ICCCE 1.8.0.11 宿主 + WPFMediaKit(VMR9 renderless) 预览黑屏**。滤波器侧日志：

```
NotifyAllocator(renderer, TRUE) hr=0x00000000        ← 渲染器接受用它的 allocator
Commit FAILED hr=0x8007000E (E_OUTOFMEMORY)          ← VMR9 输入 pin 分配器 Commit 失败
alloc variant 0..4 (cBuffers/cbBuffer 五种组合): set=0x80004005 commit=0x8007000E
Pause done   之后图一直停在 Paused，Run(tStart=…) 从未被调用
```

`testhost` 的对照实验（`--vmr9-renderless`，自带最小 `IVMRSurfaceAllocator9`/`IVMRImagePresenter9`）：
D3D9Ex 设备创建成功（hr=0）、`RenderStream(PinCategory.Preview)` 连接成功、`IMediaControl::Run` 返回 S_OK，
但 **VMR9 从未调用分配器的 `InitializeDevice`**（0 次），且滤波器 `Commit` 恒 E_OUTOFMEMORY。
`--vmr9-file <video>`（普通文件源 → 同样 VMR9 renderless）用于判断是否为环境问题（本机无对应
DirectShow 解码器，未得到有效对照）。

**结论**：VMR9 renderless 的 D3D 表面分配在该机器的显示栈（虚拟显示器/远程串流）上不可用，与滤波器
代码无关 —— 同一构建用 testhost 的宽容 sink 路径 **52–53/53 全过**，且 windowed VMR9 也停在 Paused
（与真实宿主表现一致）。native 方案代码就绪，需在**没有虚拟显示器的物理桌面机器**上复验，或改用
provider 方案绕开 VMR9 表面分配。

## 诊断日志（默认关闭）

设环境变量 `YIOU_VCAM_LOG=<文件路径>`（或 `VCAM_LOG`）后，滤波器把 Run/Pause/Stop、worker 起停、
pin 连接与 allocator 协商、`GetBuffer`/`Receive` 的 HRESULT 分类计数、SHM 打开情况等追加写入该文件。
生产环境不要开启。

## 已知限制 / 排障

- **移动 DLL 后必须重新注册**（注册表保存 DLL 绝对路径）。
- 卸载/覆盖 DLL 前先关闭宿主（DLL 被 in-proc 加载时文件被锁）。
- 32 位宿主未覆盖（本方案只出 x64；x86 需要 `Wow6432Node` 另行注册）。
- 若 WPFMediaKit 路径「图能开但黑屏」，先看 `YIOU_VCAM_LOG`：
  - `Commit FAILED hr=0x8007000E (E_OUTOFMEMORY)` 且 `GetBuffer … VFW_E_NOT_COMMITTED` 说明
    VMR9 renderless 的 D3D 表面分配失败 —— 常见于**无交互桌面/无 D3D 的会话**（远程/服务会话）；
    在有桌面的用户会话里同一二进制应正常。
  - `VFW_E_SAMPLE_REJECTED`（0x8004022B）表示 preroll 首帧被渲染器拒绝；滤波器会自动重试。
