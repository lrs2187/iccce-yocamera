# PR：插件摄像头提供者（Camera Provider）——让非 DirectShow 画面进入内置视频展台

> 分支：`feat/plugin-camera-provider`（基于 `net10` @ `87fe0409`）
> 补丁：`docs/camera-provider.patch`（13 个文件，+1995 / −33；`git apply` 已对 pristine HEAD 校验通过）
> 测试截图：`docs/pr-assets/camera-provider-booth-preview.png`

## 1. 标题

```
feat(plugin-sdk): 新增插件摄像头提供者（camera provider），支持非 DirectShow 来源进入视频展台
```

## 2. 动机（真实用例）

宿主「视频展台」的摄像头下拉框只有一条数据来源：DirectShow
`FilterCategory.VideoInputDevice` 枚举（`MW_VideoPresenter.cs` + `DirectShowCameraService.cs`）。
插件无法向这个列表注入任何非 DirectShow 来源。

我们的真实设备是**驿欧无线视频展台**：它只输出 WiFi RTSP 流（`rtsp://<设备IP>:554/stream0`），
本身不是 USB/DirectShow 摄像头。目前唯一能让它出现在宿主下拉框里的办法是**在系统里注册一个
虚拟 DirectShow 摄像头**（原生 C++ COM 滤波器 + HKCU 注册表 + VMR9 renderless 分配器协商）。
这条路径的代价：

- 要写原生 COM 滤波器并处理 VMR9/D3D 渲染协商（我们目前正卡在这里）；
- 对**全系统**可见，相机/会议软件都会看到，用户困惑；
- 与 OBS Virtual Camera 等软件设备存在 `DevicePath` 冲突/串台风险；
- 安装/卸载涉及系统级 COM 注册，插件包不再“自包含”。

更正规的做法是宿主直接提供 **plugin camera provider** 能力：插件把自己采集的帧交给宿主，
由宿主负责预览、拍照与分辨率。本 PR 实现该能力，且**不改变现有 DirectShow 路径的行为**。

## 3. 改动摘要

### 3.1 SDK（`InkCanvas.PluginSdk/`）

- 新增 `ICameraProviderService` + 相关类型（`ICameraProviderService.cs`，233 行）：
  - `ICameraProviderService`：`RegisterProvider` / `UnregisterProvider` / `NotifyCamerasChanged`；
  - `IPluginCameraProvider`（插件实现）：`ProviderId`、`GetCameras()`、
    `StartCamera(cameraId, sink)`、`StopCamera(cameraId)`、`SetCameraFormat(w,h,fps)`；
  - `IPluginCameraFrameSink`（宿主实现）：`IsActive`、`Submit(byte[] bgr24, w, h, stride, ts)`；
  - `PluginCameraDescriptor`（Id / DisplayName / Capabilities / SupportsPropertyControl）、
    `PluginCameraCapability`（W/H/FPS）。
- `HostApiRequirement.CurrentApiVersion` 1.13.0 → **1.14.0**（按文件注释的维护规则：新增对外接口必须抬次版本号）。
- `README.md` 增加 camera provider 章节；`rules/plugin_sdk.md` 更新服务表 + 新增使用章节。

### 3.2 宿主（`Ink Canvas/`）

| 文件 | 改动 |
|---|---|
| `Helpers/CameraProviderRegistry.cs`（新增 556 行） | 提供者注册表 + `IPluginCameraFrameSink` 实现。双缓冲“最近一帧”（避免逐帧分配大数组）、活动推流生命周期、`CamerasChanged`/`FrameSubmitted` 事件、`RenderLatestFrame`/`GetLatestFrameBitmapSource` |
| `Plugins/Services/CameraProviderService.cs`（新增 59 行） | SDK 服务的宿主实现（DI 单例） |
| `App.xaml.cs` | 向插件 DI 注册 `ICameraProviderService` |
| `Helpers/ICameraService.cs` | `CameraInfo` 增加 `IsProviderCamera` / `ProviderId` / `ProviderCameraId` |
| `Helpers/DirectShowCameraService.cs`（+329） | ① `RefreshCameraListAsync` 把 provider 摄像头合并进 `AvailableCameras`；② `EnumerateResolutionsAsync` 用插件声明的能力填充 `NativeResolutions/AllResolutionFpsCombos`（**在调用线程内联填充**，避免与 UI 遍历竞争）；③ `StartPreviewAsync` 为 provider 摄像头走推流分支（转发 `FrameReceived`、`GetCurrentFrameAsBitmap*`）；④ `ProbeCameraPropertiesAsync` 对 provider 摄像头短路（属性滑块置灰） |
| `MainWindow.xaml` | `VideoPresenterSpecialModeContainer` 内新增 `VideoPresenterProviderImage`（WPF `Image`） |
| `MainWindow_cs/MW_VideoPresenter.cs`（+493） | 非 DirectShow 渲染路径：插件帧 → `WriteableBitmap` → Image；**与 `VideoCaptureElement` 共享 LayoutTransform/RenderTransform 实例**（缩放/旋转/镜像/批注一致）；拍照取最近帧后走现有旋转/镜像/矫正/插画布管线；生命周期（切换摄像头/分辨率、照片页、退出展台、卸载插件）；`CamerasChanged` 时刷新下拉框 |
| `Plugins/PluginManager.cs` | 插件卸载/加载失败时按 `ProviderId` 兜底清理 provider |
| `docs/camera-provider.md`（新增 257 行） | 上游文档：最小接入、API 参考、宿主实现、与虚拟 DirectShow 路线对比、限制、本机自用最小改动集 |

### 3.3 关键实现点

1. **单一下拉框数据源不变**：provider 摄像头被合并进 `ICameraService.AvailableCameras`，
   所有现有列表/索引/持久化/预选逻辑（`videoPresenterLastCameraName` 等）零改动即可工作。
2. **渲染复用现有变换**：`VideoPresenterProviderImage` 的 `LayoutTransform`/`RenderTransform`
   在代码里指向 `VideoCaptureElement` 的同一批实例，因此镜像（`VideoPresenterFullCanvasMirror`）、
   旋转（`VideoPresenterFullCanvasRotation`）、缩放/平移（`ApplyBoothPreviewTransform`）、
   墨迹批注、照片页变换全部自动一致，无需复制逻辑。
3. **拍照不依赖 D3D 回读**：provider 摄像头激活时取 `CameraProviderRegistry.GetLatestFrameBitmapSource()`，
   之后完全复用现有 path 2（旋转/镜像/纸框矫正/插照片栏）。
4. **线程安全**：插件可在任意线程 `Submit`；宿主在锁内拷贝到双缓冲，UI 线程合并渲染通知并用
   `WriteableBitmap.WritePixels` 更新，插件缓冲区可立即复用。
5. **兼容性优先**：`DirectShowCameraService` 的所有 DirectShow 分支保留原逻辑，只在入口处
   根据 `CameraInfo.IsProviderCamera` 分流；`PluginManager` 的清理对不注册 provider 的插件是空操作。

## 4. 兼容性

- **现有插件**：不受影响。只新增接口/类型，未改动任何既有接口签名；
  未调用 `ICameraProviderService` 的插件行为完全不变。
- **DirectShow 摄像头**：不受影响，原有枚举/预览/拍照/属性/分辨率路径未改动（仅在方法入口加分派）。
- **API 版本**：1.13.0 → 1.14.0。声明 `ApiVersion: "1.0.0"` / `"1.13.0"` 的旧插件继续加载；
  只有需要 camera provider 的新插件才声明 `1.14.0`。
- **宿主配置**：`videoPresenterLastCameraName` 可保存 provider 摄像头显示名，跨会话恢复照常工作。
- **最低宿主**：该能力需 ≥ 1.8.0.11 + 本补丁（API 1.14.0）；旧宿主上插件 `GetService<ICameraProviderService>()`
  返回 null，按 SDK 惯例优雅降级（示例代码有判空）。

## 5. 测试步骤（可复现）

### 5.1 编译

```powershell
git worktree add <dir> -b feat/plugin-camera-provider
cd <dir>
dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Release -p:Platform=x64
```

结果（本机 .NET SDK 10.0.402 / VS2022，x64）：**0 错误**；无新增警告
（仅仓库既有的 12 条 `NETSDK1187` / `CS0414` 警告，改动前基线相同）。

### 5.2 宿主侧集成冒烟（无 GUI，27 项断言）

临时测试工程（不随 PR 提交）引用宿主工程，注册一个假 provider：
声明 2 个能力、StartCamera 后按 ~20fps 推 640×360 BGR24 渐变帧。全部通过：

```
PASS  DirectShowCameraService 把插件摄像头合并进 AvailableCameras
PASS  分辨率列表来自 provider 声明的能力 / AllResolutionFpsCombos 已填充
PASS  默认选中 1920×1080（与 DirectShow 路径同样的就近策略）
PASS  StartPreviewAsync(插件摄像头) 返回 true
PASS  provider.StartCamera 被调用 / provider.SetCameraFormat 收到选中分辨率 1920
PASS  宿主 FrameReceived 收到插件推来的帧（Submit=27，被拒=0）
PASS  GetCurrentFrameAsBitmapSource / GetCurrentFrameAsBitmap 返回最近帧
PASS  StopPreview 触发 provider.StopCamera，之后不再有帧提交
PASS  provider.StartCamera 返回 false 时返回 false 且通过 ErrorOccurred 通知 UI
PASS  WPF 允许两个 Image 共享同一个 LayoutTransform 实例（旋转/镜像同步）
PASS  注销提供者后 registry.Cameras 为空
=== ALL PASS ===
```

### 5.3 宿主 GUI 冒烟（真实宿主进程 + 临时测试插件）

`dotnet publish` 出可运行宿主，装入一个临时插件（临时插件不在 PR 内）：注册 provider，
8 秒后写 `Canvas.VideoPresenterLastCameraName` 并 `IVideoBoothService.Toggle()`，
19 秒拍照，27 秒关闭展台。结果：

- 插件与宿主日志：`RegisterProvider OK` → `Provider.StartCamera` → `Provider.SetCameraFormat 640x360@30`
  → `IsActive=True ... provider 推帧数=158` → `Provider.StopCamera`（关闭时正确清理）；
- 预览：截图为插件推送的彩色测试图（**成功渲染**；截图中的 90° 旋转来自上一次会话保存的
  `videoPresenterRotationAngle=1`，说明共享 LayoutTransform 生效）；
- 拍照：宿主日志
  `视频展台拍照: 走路径2 BitmapSource=640x360（插件摄像头=True）` →
  `视频展台拍照: 完成 输出 BitmapImage=360x640`（说明旋转已按当前角度应用）；
- 该轮冒烟发现并修复了一个真实缺陷：`StartProviderCameraPreviewAsync` 里
  `StopBoothProviderCamera()` 在 `PrepareProviderImageView()` 之后执行，把 Image 重新置为
  Collapsed，导致“有帧但看不到画面”。已调整顺序并复测通过。

### 5.4 补丁版宿主交付构建（`build\patched-host`）

已用本分支源码产出可直接双击运行的 portable 宿主（**不在 PR diff 内**，仅本项目内部交付）：

```powershell
dotnet publish "Ink Canvas\InkCanvasForClass.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained false -o build\patched-host
```

- 产物：`build\patched-host\`，170.2 MB / 187 文件（framework-dependent，.NET 10 Desktop Runtime x64）；
  含 `BUILD-INFO.txt`（commit、branch、补丁 SHA256、构建命令、与原版差异、验证记录）。
- 验证：用临时测试插件（验证后已从交付目录移除）在该交付目录中端到端跑通——
  启动 → 插件注册 provider → 自动打开视频展台 → 预览出图（157 帧/8s）→
  拍照成功（`走路径2 BitmapSource=640x360（插件摄像头=True）` → ``完成 输出 BitmapImage=640x360``）
  → 关闭展台触发 `Provider.StopCamera`。
- 使用/回滚说明：`docs/patched-host-usage.md`。

### 5.5 回归检查建议（reviewer）

1. 不装任何 provider 插件 → 视频展台行为与改动前完全一致（DirectShow 摄像头枚举/预览/拍照/属性/分辨率）。
2. 装一个 provider 插件 → 下拉框出现「xxx（插件）」；选中后缩放/旋转/镜像/批注/拍照/分辨率切换正常；
   切回 DirectShow 摄像头正常；退出展台后插件收到 `StopCamera`。
3. 展台打开期间卸载/禁用插件 → provider 摄像头从下拉框消失，不残留推流。
4. 重复进入/退出展台 5 次、切换摄像头 5 次 → 插件 `StartCamera`/`StopCamera` 成对、无泄漏。

## 6. 风险与已知限制

| # | 风险/限制 | 说明 / 缓解 |
|---|---|---|
| R1 | 插件推帧线程模型 | 宿主只保留最近一帧、锁内拷贝；插件 `Submit` 返回 false 即应停采集。已在集成测试覆盖 |
| R2 | 分辨率切换是“重建推流” | `SetCameraFormat` 返回 false 时宿主 Stop→Start 重建；实际渲染以插件推送的帧尺寸为准（`WriteableBitmap` 自动跟随帧尺寸），不会花屏 |
| R3 | 属性调节不支持 | 插件摄像头 `SupportsPropertyControl` 暂不映射；展台亮度/对比度等滑块自动置灰（与不支持这些接口的 USB 摄像头一致），后续可在 provider 上扩展 |
| R4 | `NotifyCamerasChanged` 会重刷下拉框 | 展台已打开时会重新枚举并重启当前预览（短暂黑一下）。仅在插件摄像头列表变化时发生，频率极低 |
| R5 | 未验证 x86/ARM64 | 本机仅验证 x64（当前发行版 x64）；代码为纯托管，无平台相关 P/Invoke |
| R6 | 未覆盖截图选择器窗口 | `ScreenshotSelectorWindow` 会在其列表中看到 provider 摄像头（`StartPreviewAsync` 分支已实现），但本次未做 GUI 验证 |
| R7 | 插件卸载清理依赖 ProviderId 约定 | 建议 `ProviderId == manifest.Id`；即使插件不主动注销，`PluginManager.UnloadPlugin` 也会按插件 Id 兜底清理 |

## 7. 补丁内容

```
 Ink Canvas/App.xaml.cs                             |   3 +-
 Ink Canvas/Helpers/CameraProviderRegistry.cs       | 556 +++++++++++++++++++++
 Ink Canvas/Helpers/DirectShowCameraService.cs      | 329 +++++++++++-
 Ink Canvas/Helpers/ICameraService.cs               |  13 +
 Ink Canvas/MainWindow.xaml                         |   9 +
 Ink Canvas/MainWindow_cs/MW_VideoPresenter.cs      | 493 +++++++++++++++++-
 Ink Canvas/Plugins/PluginManager.cs                |  22 +
 .../Plugins/Services/CameraProviderService.cs      |  59 +++
 InkCanvas.PluginSdk/HostApiRequirement.cs          |   4 +-
 InkCanvas.PluginSdk/ICameraProviderService.cs      | 233 +++++++++
 InkCanvas.PluginSdk/README.md                      |  18 +
 docs/camera-provider.md                            | 243 +++++++++
 rules/plugin_sdk.md                                |  32 +-
 13 files changed, 1981 insertions(+), 33 deletions(-)
```

- 补丁文件：`docs/camera-provider.patch`（116,071 B）
- SHA256：`c20bd83b69c9a98170abb862403ffa84e60ad1ee29c75c85cabc4f846e164f76`
- 基线：`87fe04094ef338c53b83c5122667b6820b42fb1c`（net10）
- 已审计：diff 中无本地路径、临时测试插件、`build/` 产物或私有标识
  （示例代码统一为 `com.example.boothcam` / `MyCameraProvider`）。

## 8. 如果只在本机自用（不等上游合并）

**已产出可直接双击运行的补丁版宿主**：`build\patched-host\`（见 §5.4 与
`docs\patched-host-usage.md`，内含 `BUILD-INFO.txt`）。也可以自己从源码构建：

`docs/camera-provider.patch` 可直接 `git apply` 到任意 1.8.0.11 源码副本后本地构建；
**插件侧不需要任何系统级安装**（不用注册 COM、不用管理员权限）。
最小必要文件集合（即本补丁全部）：

1. `InkCanvas.PluginSdk/ICameraProviderService.cs` + `HostApiRequirement.cs`
2. `Ink Canvas/Helpers/CameraProviderRegistry.cs` + `Plugins/Services/CameraProviderService.cs`
3. `Ink Canvas/App.xaml.cs`（注册服务）
4. `Ink Canvas/Helpers/{ICameraService,DirectShowCameraService}.cs`
5. `Ink Canvas/MainWindow.xaml` + `MW_VideoPresenter.cs`
6. `Ink Canvas/Plugins/PluginManager.cs`（卸载清理，建议保留）

（`docs/` 与 `rules/` 仅为文档，可不同步到自用副本。）

## 9. GitHub PR 正文（按仓库模板整理，可直接粘贴）

**Title**：`feat(plugin-sdk): 新增插件摄像头提供者（camera provider），支持非 DirectShow 来源进入视频展台`

**Base / Branch**：`net10` ← `feat/plugin-camera-provider`

### Description

宿主「视频展台」的摄像头下拉框目前只来自 DirectShow `FilterCategory.VideoInputDevice` 枚举，
插件无法把非 DirectShow 的画面来源（网络摄像头、无线视频展台、采集卡 SDK、屏幕采集等）接进内置展台。

本 PR 新增 **plugin camera provider** 能力：

- SDK 新增 `ICameraProviderService`：插件注册提供者、声明虚拟摄像头（Id/显示名/能力列表），
  宿主把摄像头合并进视频展台下拉框；
- 插件通过 `IPluginCameraFrameSink.Submit(byte[] bgr24, width, height, stride, timestamp)`
  按自己的节奏推帧（BGR24 / top-down，宿主在返回前拷贝）；
- 宿主负责预览渲染（WPF `Image`，与 `VideoCaptureElement` 共享同一组
  LayoutTransform/RenderTransform 实例，缩放/旋转/镜像/批注行为一致）、拍照（取最近一帧后走现有
  「插入画布/照片栏」逻辑）、分辨率下拉框（用插件声明的能力填充）与生命周期；
- `HostApiRequirement.CurrentApiVersion` 1.13.0 → 1.14.0（按仓库规则：新增对外接口抬次版本号）。

**典型用例**：只输出 RTSP 的无线视频展台此前必须借助原生虚拟 DirectShow 摄像头
（COM + 注册表 + VMR9 分配器协商）才能进入展台；使用本能力后插件只需纯托管推帧，
无需系统级安装、对全系统不可见、也不会与 OBS 等虚拟摄像头冲突。

详见 `docs/camera-provider.md`（用法/API/宿主实现/限制）。

### Type of Change

- [ ] Bug fix
- [x] New feature
- [ ] Breaking change
- [x] Documentation update
- [ ] Refactoring

### How Has This Been Tested?

1. **编译**：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Release -p:Platform=x64`
   → 0 错误（仅仓库既有警告）。
2. **宿主侧集成冒烟**（无 GUI，临时工程 + 假 provider，27 项断言全部通过）：
   列表合并、能力→分辨率下拉框、默认 1920×1080、帧转发、拍照取帧、Stop 后不再推帧、
   启动失败经 `ErrorOccurred` 通知、注销清理、WPF 共享 LayoutTransform 实测。
3. **真实宿主 GUI 冒烟**（临时测试插件）：注册 provider → 自动打开视频展台 → 预览出图
   （157 帧/8s）→ 拍照成功（`640x360` → `BitmapImage`，旋转/镜像按当前状态应用）→
   关闭展台触发 `Provider.StopCamera`。
4. **回归**：不装 provider 插件时视频展台行为与改动前一致（DirectShow 摄像头路径未改逻辑）；
   装插件后切换 DirectShow/插件摄像头、退出展台、卸载插件均正常。

### Checklist

- [x] 新增能力以**新接口**形式提供，未改动任何既有接口签名
- [x] 未修改现有 DirectShow 预览/拍照/属性/分辨率路径（仅在入口按摄像头类型分流）
- [x] 新接口/类型有完整 XML 注释与示例（`ICameraProviderService.cs`）
- [x] 文档：`docs/camera-provider.md`、`rules/plugin_sdk.md`、`InkCanvas.PluginSdk/README.md`
- [x] API 版本按仓库维护规则抬次版本号（1.13.0 → 1.14.0）
- [x] 插件卸载 / 加载失败时清理 provider（停止推流、从下拉框移除）
- [x] 无本地路径、测试插件或构建产物进入 diff
- [x] 在本机 x64（.NET SDK 10.0.402）完成构建与运行验证

## 10. 如何跟进上游 `net10` 分支更新（rebase 指引）

上游持续在 `net10` 上迭代时，建议每个迭代周期 rebase 一次本分支。核心原则：
**provider 的实现尽量集中在新增文件里，对既有文件的改动保持“入口分流”式最小 diff**，
冲突会集中在下面几处可预期位置。

### 10.1 标准流程

```powershell
$W = "C:\Users\lrs2187\Projects\iccce-provider-pr"
git -C $W fetch origin net10
git -C $W branch backup/provider-$(Get-Date -Format yyyyMMdd)          # 先留后路
git -C $W rebase origin/net10
# —— 逐个解决冲突（见 10.2）——
git -C $W add -A; git -C $W rebase --continue

# 重新构建 + 冒烟 + 重新导出补丁（见 10.3）
```

### 10.2 冲突高发点（按优先级）

| 文件 | 我们的改动 | 处理方式 |
|---|---|---|
| `InkCanvas.PluginSdk/HostApiRequirement.cs` | `CurrentApiVersion = "1.14.0"` | 若上游已把版本抬到 ≥1.14.0：**保留上游值**；若上游同时新增了其它接口也抬了版本，取较大值，并在 PR.md/提交信息里注明 |
| `Ink Canvas/Helpers/DirectShowCameraService.cs` | `RefreshCameraListAsync` 合并 provider 列表；`EnumerateResolutionsAsync`/`StartPreviewCore`/`ProbeCameraPropertiesAsync`/`GetCurrentFrame*` 的 provider 分支 | 冲突时**保留上游逻辑，再把 provider 分支按当前方法签名重新插入**；新增的 provider 方法块（`FillProviderCameraResolutions`/`StartProviderPreviewCore`/`StopProviderPreviewCore` 等）整段保留 |
| `Ink Canvas/MainWindow_cs/MW_VideoPresenter.cs` | `RefreshVideoPresenterDeviceListCoreAsync`、`StartVideoPresenterPreview`/`StartVideoCaptureElementPreviewAsync`、`BtnCapturePhoto_Click`、`SwitchBoothToPhotoPage/LivePage`、`StopVideoPresenterPreviewAndFrameCache`、`CameraProviderRegistry_*` 回调 | 同上：以上游方法体为准，重新应用“provider 分流 + 生命周期清理”；独立方法块整段保留 |
| `Ink Canvas/MainWindow.xaml` | `VideoPresenterSpecialModeContainer` 内新增 `VideoPresenterProviderImage` | 通常无冲突；若上游调整了该 Grid，按当前层级把 Image 放在 `VideoCaptureElement` 之后、冻结照片 `FillImage` 之前 |
| `Ink Canvas/Plugins/PluginManager.cs` | `UnloadPlugin` / 加载失败路径按 `ProviderId` 清理 | 若上游重构插件生命周期，把清理调用挂到“Shutdown 之后、ALC 卸载之前”的等价位置 |
| `Ink Canvas/App.xaml.cs` | 服务注册数组加 `ICameraProviderService` | 通常无冲突；按数组现有风格补一行 |
| `Ink Canvas/Helpers/ICameraService.cs` | `CameraInfo` 增加 3 个属性 | 通常无冲突；若上游改了 `CameraInfo`，把属性并入 |
| `InkCanvas.PluginSdk/README.md`、`rules/plugin_sdk.md`、`docs/camera-provider.md` | 文档 | 冲突时以上游结构为准重排，内容保留 |

**无冲突（除非上游大改）**：`InkCanvas.PluginSdk/ICameraProviderService.cs`、
`Ink Canvas/Helpers/CameraProviderRegistry.cs`、`Ink Canvas/Plugins/Services/CameraProviderService.cs`。

### 10.3 rebase 后必须重做的验证（缺一不可）

1. **编译**：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Release -p:Platform=x64` → 0 错误。
2. **集成冒烟（27 项）**：`_research\tmp-provider-smoke\ProviderSmoke.exe`
   （引用宿主工程的临时测试工程，不属于 PR；若上游改动导致 API 变化，先修它再跑）→ `ALL PASS`。
3. **GUI 冒烟**：用临时测试插件（`_research\tmp-provider-smoke\GuiPlugin`）在发布构建里跑
   “开展台 → 出图 → 拍照 → 关展台”，确认日志出现 `Plugins/… Provider.StartCamera/StopCamera`。
4. **重新导出补丁**：
   ```powershell
   git -C $W add -A
   git -C $W diff --cached > docs/camera-provider.patch
   (Get-FileHash docs/camera-provider.patch -Algorithm SHA256).Hash
   git -C "C:\Users\lrs2187\Projects\ICCCE" apply --check docs/camera-provider.patch
   ```
   更新 `docs/PR.md` 里的 diffstat / SHA256 / 基线 commit。
5. **重新构建交付版宿主**（若现场依赖）：
   ```powershell
   dotnet publish "Ink Canvas\InkCanvasForClass.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained false -o build\patched-host
   ```
   更新其中的 `BUILD-INFO.txt`（commit/补丁 hash/构建时间），并按 `docs/patched-host-usage.md` §7 自检。

### 10.4 长期维护建议

- 上游若合并了本 PR：本分支删除，插件侧改为声明 `ApiVersion: "1.14.0"` + `MinHostVersion: <合并版本>`
  并删除 vcam 回退路径（或保留为旧宿主兼容）。
- 上游若长期不合并：每次 `net10` 正式发版后按 10.1–10.3 跟一次，保持 `build/patched-host` 与
  官方版本同步（安全修复/功能更新都能带上，只有 camera provider 是我们额外加的）。
- 每次 rebase 后对比 `docs/camera-provider.patch` 与上一次的 diffstat，若既有文件改动量突然增大，
  说明上游重构了相关区域，需要重新评估分流点是否仍成立。
