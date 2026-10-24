# ICCCE「驿欧无线视频展台」插件接入面与构建工具链分析（task-2 交付）

> 分析对象：InkCanvasForClass（ICC-CE）源码仓库 `C:\Users\lrs2187\Projects\ICCCE`，`version.json = 1.8.0.11`（源码 HEAD `87fe04094e`）。
> 事实来源优先级：**ICCCE 源码（唯一事实来源）** > 实机安装目录只读观察（现有 `1.8.0.11.dd312cc` 安装实例，仅作旁证）> 外部上游源码（WPFMediaKit 3.0.1）。
> 证据等级标注：`[源码确证]` = 有 ICCCE 源码文件+行号；`[实测]` = 本机可复现实验（编译/反序列化/PE 读取等，脚本产物在 `_research/tmp-iccce/`）；`[上游源码]` = WPFMediaKit/DirectShow 上游源码或微软文档；`[推测]` = 合理推断、未验证；`[待实机验证]` = 必须在「与源码一致的 1.8.0.11 安装宿主」上确认。
> 本文不修改 ICCCE 源码仓库；本机实验工程全部位于 `_research/tmp-iccce/`。

---

## 0. 结论摘要（先看这 12 条）

1. **宿主是 .NET 10 WPF，不是 .NET Framework**：`Ink Canvas\InkCanvasForClass.csproj:7` 的 `TargetFramework` 为 `net10.0-windows10.0.19041.0`，`UseWPF=true`、`UseWindowsForms=true`，框架依赖发布（安装目录 `InkCanvasForClass.runtimeconfig.json` 的 frameworks 为 `Microsoft.NETCore.App 10.0.0` / `Microsoft.WindowsDesktop.App 10.0.0`）。task-2 描述里的 net452/.NET Framework 说法全部作废。`[源码确证]` `[辅助实测]`
2. **SDK 包版本不是 1.13.0**：`1.13.0` 是 `HostApiRequirement.CurrentApiVersion`（`InkCanvas.PluginSdk\HostApiRequirement.cs:18`），不是 NuGet 包版本。与 1.8.0.11 对应的 NuGet 包是 `InkCanvas.PluginSdk 1.8.0.11-gcaf5b75f81`（本机实测可还原、可编译，SDK 程序集 FileVersion=1.8.0.11、CurrentApiVersion=1.13.0）。`[实测]`
3. **插件无法在不改宿主源码的前提下，向宿主「视频展台摄像头下拉框」注入非 DirectShow 源**。下拉框数据源只有一条链路：`_cameraService.RefreshCameraListAsync()` → `DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)`（`Ink Canvas\Helpers\DirectShowCameraService.cs:605-632`）；UI 只遍历 `_cameraService.AvailableCameras`（`Ink Canvas\MainWindow_cs\MW_VideoPresenter.cs:1605,1615-1644`）。SDK 没有摄像头提供者/注入接口（`InkCanvas.PluginSdk\IPluginHost.cs:14-95`）。`[源码确证]`
4. **SDK 里确实有 `ICameraService`，但它是给插件自己用的只读封装**（`InkCanvas.PluginSdk\ICameraService.cs:14-45`；宿主实现在 `Ink Canvas\Plugins\Services\CameraService.cs`），`AvailableCameras` 同样是 DirectShow 枚举结果，不能改宿主列表；`IVideoBoothService`（`InkCanvas.PluginSdk\IVideoBoothService.cs:12-52`）只能开关/拍照/缩放/旋转展台，**没有“选择某个摄像头”方法**。task-2 旧描述“SDK 只有 RegisterToolbarItem 等”不准确，但“没有摄像头注入点”的结论成立。`[源码确证]`
5. **DI 覆盖宿主服务对本需求无效**：宿主启动时把 `ICameraService`/`IVideoBoothService` 注册进 `PluginManager` 的 `ServiceCollection`（`Ink Canvas\App.xaml.cs:336-406`，其中 :375 `ICameraService`、:376 `IVideoBoothService`），但宿主自己的展台代码用的是 `CameraServiceFactory.Create()`（`Ink Canvas\MainWindow_cs\MW_VideoPresenter.cs:1147-1157`，工厂见 `Ink Canvas\Helpers\CameraServiceFactory.cs:12-15`），从不从插件 DI 容器取这两个服务；插件 `RegisterService` 只影响 `host.GetService<T>()` 的解析结果（`Ink Canvas\Plugins\PluginManager.cs:1937-1974`）。`[源码确证]`
6. **唯一“不改宿主源码且完整可用”的路径：系统层注册一个虚拟 DirectShow 视频输入设备**。只要它出现在 `VideoInputDevice` 分类、可被 moniker 绑定，宿主就会把它当成普通摄像头：进下拉框、走内置展台预览、走拍照、分辨率下拉框、摄像头属性滑块。插件该做的是：注册/维护这个设备、喂帧（RTSP→RGB24）、预写“上次摄像头”设置、调用 `IVideoBoothService.Toggle()`。`[源码确证+推荐]`
7. **虚拟摄像头契约的核心不是“能出图”，而是宿主的两条 DirectShow 图都会来查它**：
   - 非特殊模式/截图：`FilterGraph` + `SampleGrabber`（宿主强制 MediaType `RGB24`，`DirectShowCameraService.cs:677-697`），连接 capture pin（:706-714），从 `GetConnectedMediaType` 只接受 `FormatType.VideoInfo` 并**忽略 BiSizeImage、按 `stride=(w*3+3)&~3` 计算**（:1604-1639）；
   - 特殊模式全屏：WPFMediaKit `VideoCaptureElement`（`UseYuv=False`、`EnableSampleGrabbing=False`、VMR9 Renderless+D3DImage），用 `IAMStreamConfig.GetFormat/SetFormat` 改 W/H/FPS 后 `RenderStream(PinCategory.Preview,...)`（上游 `VideoCapturePlayer.cs` `SetupGraph()`/`SetVideoCaptureParameters()`）。
   两端都要求 **Capture 分类 pin + IAMStreamConfig + formatType=VideoInfo**；建议只宣传 RGB24（4 字节对齐 stride）能力。`[源码确证 + 上游源码]`
8. **必须支持“同一设备被创建多个 filter 实例”**：特殊模式下宿主先建一个**不 Run 的 `FilterGraphNoThread` + source filter** 做分辨率枚举（用完即释放，`DirectShowCameraService.cs:950-1053`），再做属性探测（若实现了 `IAMVideoProcAmp/IAMCameraControl` 且探测到支持项，探测图 `_propGraph` 会**常驻到展台退出**，`:278-283`），随后 WPFMediaKit 再建 source filter 流式预览（`MW_VideoPresenter.cs:2353-2387`）。是否发生“两实例同时存在”取决于属性接口是否实现，但**创建/释放/重建的反复调用是必然的**；虚拟摄像头不能假设自己只会被实例化一次，也不能每个实例各开一路独占 RTSP。`[源码确证]`
9. **“上次选的摄像头”设置**：`Configs\Settings.json` → `canvas.videoPresenterLastCameraName`（值为 `DsDevice.Name`，大小写不敏感匹配）；分辨率是 `canvas.videoPresenterLastResolutionKey`（格式 `WxH@FPS`）。插件可以用 `ISettingsService.Set("Canvas.VideoPresenterLastCameraName", 设备名)` 在打开展台前预选，再 `IVideoBoothService.Toggle()`，从而做到“点插件按钮 → 展台带着我们的虚拟摄像头打开”。`[源码确证]`（`Ink Canvas\Resources\Settings.cs:26-27,516-530`；`MW_VideoPresenter.cs:1646-1671,1709-1784,3141-3150`；`SettingsService.cs:25-39`；`MW_VideoPresenter.cs:4375-4444`）
10. **视频展台不是独立窗口**：全屏预览层是主窗口 `Main_Grid` 内的 `VideoPresenterSpecialModeContainer`（`Ink Canvas\MainWindow.xaml:145-190`），展台菜单是主窗口内的 WPF `Popup`（`MainWindow.xaml:856`，转发属性见 `MainWindow.xaml.cs:135-142`）。没有 `new Window`/独立进程；`Windows\ScreenshotSelectorWindow` 是另一个功能窗口，不是展台。`[源码确证]`
11. **宿主进程 x64**：安装版 exe PE Machine=0x8664（`[实测]`，样例 `C:\Users\lrs2187\Applications\InkCanvasForClass.CE.1.8.0.11-x64\InkCanvasForClass.exe`）；源码 `Platforms=AnyCPU;x86;x64;ARM64;ARM64EC`、`AnyCPU` 分支 `Prefer32Bit=false`（`InkCanvasForClass.csproj:32,49`），`RuntimeIdentifiers=win-x86;win-x64;win-arm64`（:3）。虚拟摄像头原生 COM DLL 必须与宿主位数一致（当前交付目标 x64）。`[源码确证+实测]`
12. **工程可直接编译，无需额外 workload**：本机 dotnet SDK `8.0.426/9.0.319/10.0.402`，WindowsDesktop 运行时 `10.0.13`；实测用 `PackageReference InkCanvas.PluginSdk 1.8.0.11-gcaf5b75f81` + `net10.0-windows10.0.19041.0` + `UseWPF` 编译最小插件成功（0 warning/0 error），输出仅 `manifest.json + 入口.dll + 同名.deps.json`（Release 关 PDB 时正好 3 个文件）。`[实测]`

附加三条重要更正/结论（Lead 指定）：
- **manifest 字段必须 PascalCase**：宿主用默认大小写敏感的 `System.Text.Json.JsonSerializer.Deserialize<PluginManifest>`（`PluginManager.cs:553` 安装时、`:709` 扫描时），实测 `{"id":...}` 解析出的 `Id` 为空、插件会被静默跳过；现有安装实例里的真实插件 manifest 也全是 `"Id"/"Name"/"EntranceAssembly"...`。官方 `rules\plugin_sdk.md:34-48` 表格里的小写字段名是文档错误。`[源码确证+实测]`
- **宿主没有“包内文件白名单”运行时校验**：`ProcessPluginPackages` 只检查 `manifest.json` 存在、Id 合法、安全信任级别、zip 条目不越界（`PluginManager.cs:538-583`），随后 `ZipFile.ExtractToDirectory` 全量解压（:596）。“只允许 3 个文件”是 CI 打包工作流 `.github\workflows\plugin-build.yml:105-166` 的约定，不是宿主运行时行为。只读观察现有 `1.8.0.11` 安装实例时，`Plugins\com.icc.easiconnect\` 目录内有 12 个文件（含 `WinRT.Runtime.dll`、`Microsoft.Windows.SDK.NET.dll`、iNKORE 系列），说明宿主安装/加载流程**不会因为多文件而拒绝**该目录（但该实例来源与加载结果未在本会话内复核，只作旁证）。`[源码确证+辅助观察]`
- **ApiVersion / MinHostVersion 建议值**（同时兼容 1.8.0.11 且不被拒）：`ApiVersion: "1.0.0"` + `MinHostVersion: "1.8.0.11"`，不填 `VersionRange`。理由：`ApiVersion` 判定为「主版本相同且 required ≤ HostApiRequirement.CurrentApiVersion（1.13.0）」（`PluginCompatibility.cs:44-53,72-83`），1.0.0 对任何 1.x 宿主都通过；本插件只用到很早存在的 `GetService`/`RegisterToolbarItem`/`ISettingsService`/`IVideoBoothService`，声明 1.0.0 不虚高。`MinHostVersion` 与源码宿主 `HostVersion`（SDK 程序集 FileVersion=1.8.0.11，`HostApiRequirement.cs:28`）相等，1.8.0.11 上必过；若宿主是更老的 API 构建，1.0.0 仍能加载（代码里对 `GetService<IVideoBoothService>()` 判空即可优雅降级）。若只想对齐“源码一致的 1.8.0.11”，也可按官方规范写 `ApiVersion: "1.13.0"`（`rules\plugin_sdk.md:281`），但不要写 `1.14.0`/`2.x`，会被拒。`[源码确证+实测]`

---

## 1. 版本/环境校正（task-2 旧描述的更正）

| 项目 | task-2 旧说法 | 源码/实测事实 | 证据 |
|---|---|---|---|
| 宿主运行时 | .NET Framework WPF | **.NET 10** WPF，TFM `net10.0-windows10.0.19041.0`，框架依赖 | `Ink Canvas\InkCanvasForClass.csproj:7,27-28`；安装版 `InkCanvasForClass.runtimeconfig.json`（frameworks 10.0.0）`[辅助实测]` |
| 插件 SDK | net452 / net6 说法混用 | net10 分支 SDK TFM **net10.0-windows10.0.19041.0**；NuGet 包同 TFM；README 里“最低 .NET 6”只是历史下限说明 | `InkCanvas.PluginSdk\InkCanvas.PluginSdk.csproj:4`；`rules\plugin_sdk.md:5`；NuGet 包 `lib/net10.0-windows10.0.19041/` |
| SDK 版本 | “API 1.13.0” 当成包版本 | 包版本=宿主构建版本 `1.8.0.11-gcaf5b75f81`；**API 版本**=1.13.0 | `HostApiRequirement.cs:18`；`[实测]` probe 输出 |
| 插件包内容 | 3 文件（当作运行时强校验） | CI 约定 3 文件；宿主运行时**不校验白名单**，只做 zip 安全路径检查 | `PluginManager.cs:576-583,596`；`.github\workflows\plugin-build.yml:105-166` |
| 宿主位数 | 待确认 | 安装版 **x64**（PE 0x8664）；源码 AnyCPU+Prefer32Bit=false | `csproj:32,49`；`[实测]` PE 头 |
| manifest 字段名 | 小写（`id`、`apiVersion`） | 必须 **PascalCase**，大小写敏感 | `PluginManager.cs:553,709`；`[实测]` |
| 视频展台窗口 | 待确认 | **主窗口内联**，非独立窗口 | `MainWindow.xaml:145-190,856` |

---

## 2. 路径对比表（问题 1 的结论先行）

| # | 路径 | 能进入宿主下拉框？ | 能内置展台预览/拍照？ | 需改宿主源码？ | 额外依赖/权限 | 工作量/风险 | 结论 |
|---|---|---|---|---|---|---|---|
| A | SDK / `IPluginHost` 扩展点注入摄像头 | ❌ 无此接口 | — | 否 | 无 | 低 | **不可行**（源码确证） |
| B | DI 覆盖宿主 `ICameraService` / `IVideoBoothService` | ❌ 宿主展台不读 DI，只用工厂/自身实现 | ❌ | 否 | 无 | 低 | **无效**（源码确证） |
| C | 插件用 SDK `ICameraService` 自己采集 + 自己 UI | ❌ 不进入宿主列表 | ❌（不是宿主展台） | 否 | 无 | 中 | 只能做“插件自己的预览窗”，不属于本需求 |
| D | `IVideoBoothService` 间接通道 | ❌ 不能加设备 | ⚠️ 只能开关/拍照/缩放/旋转 | 否 | 无 | 低 | **只能用于“打开展台”**，不能注入设备 |
| E | IPC（`Ipc.InvokeAsync`） | ❌ 宿主没有摄像头相关方法 | ❌ | 否 | 无 | 低 | 不可行 |
| F | `icc://` 深链接 | ❌ 无摄像头参数 | ⚠️ `icc://videopresenter` 可打开展台 | 否 | 需开启“启用 URI 协议” | 低 | 仅辅助“打开”，不能选摄像头 |
| G | 写 Settings 预选 `videoPresenterLastCameraName` | ❌ 不能凭空添加设备 | ⚠️ 设备已存在时可预选 | 否 | 无 | 低 | **配合虚拟摄像头使用**（关键一步） |
| H | 反射改 `CameraDevicesComboBox`/私有 `_cameraService._cameras` | ⚠️ 表面上能加条目 | ❌ 预览时仍按 DsDevice/moniker 找真设备，必失败 | 否 | 无 | 高（脆弱、版本敏感、随时崩） | **不推荐**，仅作原理排除 |
| I | **系统层虚拟 DirectShow 视频输入设备** | ✅ | ✅（走宿主原生预览/拍照/分辨率/属性管线） | 否 | 需注册 COM（HKLM 需管理员；HKCU 免管理员待验证） | 高（本任务主要工作量） | **✅ 唯一推荐完整路径** |
| J | Media Foundation 虚拟摄像头（`MFCreateVirtualCamera`） | ❌（宿主只枚举 DirectShow `VideoInputDevice`） | ❌ | 否 | Win11 22H2+ | 中 | 除非另做 DirectShow 桥接，否则**不满足** |
| K | 上游给 ICCCE 增加“camera provider”API | ✅（未来版本） | ✅ | **是** | 需宿主发版 | 中 | 违反本任务“不改宿主源码”前提；可作为长期方案 |
| L | 沿用宿主已有“第三方展台”模式（`videoBoothMode=Custom`） | ❌ | ❌（替换掉内置展台） | 否 | 无 | 低 | 只能作为**降级方案**，不满足“在下拉框里选到它” |

> 结论：**I（虚拟 DirectShow 摄像头）+ G（设置预选）+ D（IVideoBoothService 打开）** 是唯一满足需求的组合。
> 该路径本质上是“插件把系统环境改造一下，让宿主原封不动地把它当普通摄像头”，没有绕过宿主源码，也不依赖未公开 API。

### 2.1 “注入面”逐条排查（带源码出处）

宿主摄像头数据流（唯一真源）：
1. 展台菜单首次打开 → `ToggleVideoPresenterSidebar()` → `EnsureCameraService()`（`MW_VideoPresenter.cs:240-295`，:279）。
2. `EnsureCameraService()` 里 `_cameraService = CameraServiceFactory.Create()` → `new DirectShowCameraService()`（`MW_VideoPresenter.cs:1147-1157`；`CameraServiceFactory.cs:12-15`；`DirectShowCameraService.cs:20,597-602`）。
3. `RefreshVideoPresenterDeviceListCoreAsync()` → `await _cameraService.RefreshCameraListAsync()` → `DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)` 填充 `_cameras`（`MW_VideoPresenter.cs:1592-1610`；`DirectShowCameraService.cs:605-632`）。
4. UI 下拉框逐项 `CameraDevicesComboBox.Items.Add(_cameraService.AvailableCameras[i].Name)`（`MW_VideoPresenter.cs:1641-1644`）。
5. 选中项变化 → 保存设备名 → `EnumerateResolutionsAsync(idx)` → `StartVideoPresenterPreview(idx)`（`MW_VideoPresenter.cs:1786-1829`）。
6. 特殊模式预览 `VideoPresenterFullCanvasImage.VideoCaptureDevice = device` → WPFMediaKit 重新按 `device.DevicePath` 建图（`MW_VideoPresenter.cs:2232-2387`；上游 `BaseClasses.cs` `AddFilterByDevicePath`）。

逐条排查：
- **A. SDK 扩展点**：`IPluginHost` 全部成员为 `Log/LogError/Services/ServiceProvider/GetService/RegisterService/RegisterToolbarItem/RegisterBoardToolbarItem/RegisterIpcHandler/Ipc/EvaluateTrust`（`IPluginHost.cs:14-95`），无摄像头/设备枚举/展台选摄像头接口。`PluginBase` 也只有 `GetService` 等（`PluginBase.cs:44-125`）。`[源码确证]`
- **B. DI 覆盖**：宿主服务注册表见 `App.xaml.cs:347-393`（`ICameraService` :375、`IVideoBoothService` :376）。宿主展台自建 `DirectShowCameraService`，从不 `PluginManager.Instance.GetService<ICameraService>()`；`PluginManager.GetService` 仅供插件调用（`PluginManager.cs:1960-1974`）。插件 `RegisterService` 后添加的注册在 MS DI 里会“后注册覆盖先注册”，但被覆盖的只是插件查询结果，宿主 UI 不动。`[源码确证]`
- **C. SDK `ICameraService`**：接口暴露 `AvailableCameras`（只读，来自 DirectShow）、`StartPreviewAsync(index)`、`FrameReceived`、`GetCurrentFrame()`（`SDK ICameraService.cs:14-45`）；宿主实现 `Plugins\Services\CameraService.cs:14-176` 包装 `CameraServiceFactory.Create()`，与展台“共用设备、会抢占”（接口注释 :10-12）。没有“向宿主列表添加设备”的能力。`[源码确证]`
- **D. `IVideoBoothService`**：`IsActive/IsPhotoPreviewActive/ZoomScale/SwitchToLiveView/Toggle/CapturePhoto/ZoomIn/ZoomOut/ResetZoom/Rotate90`（`IVideoBoothService.cs:12-52`），没有选择设备的参数；实现 `Plugins\Services\VideoBoothService.cs:9-138` 全部转发到 `MainWindow` 的公开入口（`MW_VideoPresenter.cs:4340-4451`）。`[源码确证]`
- **E. IPC**：插件只能 `RegisterIpcHandler`（`IPluginHost.cs:70-84`），`IPluginIpcBus.InvokeAsync` 调对端方法（`IPluginHost.cs:100-128`）；宿主 `PluginIpcService` 没有内置摄像头方法（`Ink Canvas\Plugins\PluginIpcService.cs` 全文只有通用 `RegisterHandler/InvokeAsync`）。`[源码确证]`
- **F. URI**：`icc://videopresenter` 会直接进全屏展台（`MW_UriHandler.cs:15,184-187`），`icc://booth` 只弹菜单（:167-181）；命令解析不接收摄像头参数，且整个路由受 `Settings.Advanced.IsEnableUriScheme` 门禁（`:53-57`）。`[源码确证]`
- **G. Settings 预选**：宿主按 `Settings.Canvas.VideoPresenterLastCameraName` 选设备（`MW_VideoPresenter.cs:1646-1671`），插件可经 `ISettingsService.Set("Canvas.VideoPresenterLastCameraName", name)` 写入并落盘（`SettingsService.cs:25-39`）；**前提是设备本身已在 DirectShow 枚举里**。`[源码确证]`
- **H. 反射**：能拿 `Application.Current.MainWindow` 改 `CameraDevicesComboBox.Items` 或 `_cameraService._cameras`，但后续 `StartVideoCaptureElementPreviewAsync` 会按 `MonikerString/Name` 去 `MultimediaUtil.VideoInputDevices` 找真 `DsDevice`（`MW_VideoPresenter.cs:2290-2318`）；找不到就 `tcs.SetResult(false)`，预览失败。非 DirectShow 源无法凭 UI 伪造。`[源码确证+推测]`
- **I. 虚拟 DirectShow 设备**：一旦注册进 `VideoInputDevice` 分类，上面 1-6 全部自动成立，无需任何插件注入。`[源码确证+推荐]`
- **J. MF 虚拟摄像头**：宿主枚举用 `FilterCategory.VideoInputDevice`（DirectShow），不是 `Windows.Media.Devices`/MediaFoundation；`MFCreateVirtualCamera` 的设备默认只对 MF 客户端可见（`[上游源码/微软文档]`），是否会被系统桥接进 DirectShow 分类**不确定**，不能作为主方案。`[推测/待实机验证]`
- **K. 上游 API**：可行但需宿主改源码发版，不符合本任务约束。
- **L. 第三方展台模式**：宿主 `Settings.Canvas.VideoBoothMode`（`Settings.cs:504-506`，枚举 :652-657）支持 `BuiltIn/Seewo/Custom`；`Custom` 时 `LaunchConfiguredVideoBooth` 直接 `SoftwareLauncher.LaunchCustomBooth(VideoBoothCustomAppPath, Args)`（`MW_VideoPresenter.cs:168-189`；`SoftwareLauncher.cs:13-40`），会**替换**内置展台，不满足“在下拉框里选到它”。`[源码确证]`

---

## 3. 问题 2：虚拟 DirectShow 摄像头必须满足的宿主契约（重点）

### 3.1 宿主会怎么调用这个设备（两条路径 + 拍照）

**路径 A：非特殊模式 / 截图选择器（`DirectShowCameraService`）** `[源码确证]`
1. `RefreshCameraListAsync()`：`DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)`（`DirectShowCameraService.cs:605-632`）。
2. `StartPreviewAsync(index)` → `StartPreviewCore`（:635-734）：
   - `CreateSourceFilterByDevicePath`：按 `DevicePath`/`Name` 重枚举，然后 `dev.Mon.BindToObject(..., IBaseFilter)`（:736-751）——**设备必须能通过 moniker 实例化**。
   - `_filterGraph.AddFilter(source)`（:668-670）。
   - `RefreshNativeResolutions()`：`DsFindPin.ByCategory(source, PinCategory.Capture, 0)` → 转 `IAMStreamConfig` → `GetNumberOfCapabilities`/`GetStreamCaps`（:754-774,782-938）。
   - `ApplyNativeResolution()`：再次 `GetStreamCaps`（**只接受 `FormatType.VideoInfo`**，:1213-1237）→ `SetFormat`（:1247-1254）。
   - `SampleGrabber` 的媒体类型强制设为 `major=Video/subType=RGB24/formatType=VideoInfo`，`SetCallback(this,1)`（:677-697）。
   - 手动连接：source capture pin → grabber in → grabber out → NullRenderer，`IMediaControl.Run()`（:699-721）。
3. 帧回调 `BufferCB`：`TryGetNegotiatedVideoFormat` 从 sample grabber 的 `GetConnectedMediaType` 取 W/H；**只接受 `FormatType.VideoInfo`**，stride 直接按 `(width*3+3)&~3` 计算、忽略 `BiSizeImage`（:1420-1562、:1604-1639）。WPF 侧用 `PixelFormats.Bgr24` WriteableBitmap 显示（:1483-1499）。
4. 拍照（非特殊模式）：`GetCurrentFrameAsBitmap()`（:1662-1677）→ `CameraService_FrameReceived` 缓存 `_lastFrame`（`MW_VideoPresenter.cs:1255-1302`）。

**路径 B：特殊模式全屏预览（WPFMediaKit `VideoCaptureElement` / VMR9 Renderless + D3DImage）** `[源码确证+上游源码]`
1. 宿主先独立枚举分辨率、探测属性（不 Run，不抢占设备）：
   - `EnumerateResolutionsAsync`：`FilterGraphNoThread` + `ICaptureGraphBuilder2.SetFiltergraph` + `AddSourceFilterForMoniker`，`FindInterface(PinCategory.Capture, MediaType.Video, source, IAMStreamConfig)`，`GetNumberOfCapabilities/GetStreamCaps`（`DirectShowCameraService.cs:950-1053`）。
   - `ProbeCameraPropertiesAsync`：同构图，QI `IAMVideoProcAmp`/`IAMCameraControl`（filter 或 capture pin），对 7 个属性 `GetRange`；有支持的属性就把图**常驻**（:143-311，:278-283）。
2. `StartVideoCaptureElementPreviewAsync`（`MW_VideoPresenter.cs:2232-2483`）：
   - 用 `MonikerString`/`Name` 在 `MultimediaUtil.VideoInputDevices` 找 `DsDevice`（:2290-2318）。
   - `VideoCaptureElement.VideoCaptureDevice = device`；`EnableSampleGrabbing=false`；`UseYuv=false`；`LoadedBehavior=Manual`；`DesiredPixelWidth/Height/FPS` 来自 `NativeResolutions[SelectedResolutionIndex]`（:2353-2378）。
   - WPFMediaKit 内部（上游 `VideoCapturePlayer.SetupGraph()`）：`ICaptureGraphBuilder2.FindInterface(PinCategory.Capture, MediaType.Video, captureFilter, IAMStreamConfig)` → `GetFormat` → 改写 `BmiHeader.Width/Height` 与 `AvgTimePerFrame=10000000/FPS` → `SetFormat`；然后创建 VMR9 renderer 并 `RenderStream(PinCategory.Preview, MediaType.Video, captureFilter, null, renderer)`。若设备只有 capture pin，Capture Graph Builder 会自动插入 Smart Tee（微软 `ICaptureGraphBuilder2::RenderStream` 文档明确）。
   - 上游 `VideoCapturePlayer.SetupGraph()` 找设备用的是 `AddFilterByDevicePath(..., VideoCaptureDevice.DevicePath)`（`BaseClasses.cs`：`where d.DevicePath == devicePath`）；**`DevicePath` 为 null 的软件滤波器可能匹配到别的 null 设备（见风险 R2）**。
3. 事件：`MediaOpened` 隐藏占位、启用拍照、复位重试计数（`MW_VideoPresenter.cs:1310-1331`）；`MediaFailed` 写日志+报错（:1337-1360）。
4. **兜底重试**：Play 后 1.5s 若 `MediaOpened` 未触发（且 SearchingText 仍可见），最多重启 2 次，之后提示“摄像头启动失败”（:134-143、:2398-2455）。→ 虚拟摄像头必须**快速成功或快速失败**，不能挂住。

**拍照链路（问题 2 明确要的）** `[源码确证]`
- 特殊模式（默认）：`EnableSampleGrabbing=false` → 不走 SampleGrabber；拍照两条兜底：
  1. `_lastFrame`（通常为 null，因为 NewVideoSample 不触发）；
  2. `VideoPresenterFullCanvasImage.CaptureCurrentFrame()`：反射 `D3DImage.CopyBackBuffer()` 从 GPU 拿 `BitmapSource`（`Ink Canvas\Helpers\CapturableVideoCaptureElement.cs:22-84`）。
  然后应用旋转/镜像、可选 OpenCV 纸框矫正、插入画布（`MW_VideoPresenter.cs:2587-2753`）。
- 非特殊模式：`_cameraService.GetCurrentFrameAsBitmap()` 来自 SampleGrabber BufferCB 缓存（同上）。
- `ScreenshotSelectorWindow` 也用 `DirectShowCameraService`（`Ink Canvas\Windows\ScreenshotSelectorWindow.xaml.cs:37,163`），虚拟摄像头同样可能出现其列表里。

### 3.2 虚拟摄像头契约清单（实现验收标准）

> 级别：**M** = 缺失会直接不工作/预览失败；**S** = 缺失会降级/UI 不完整；**O** = 可选增强。

| # | 契约 | 具体要求 | 违反后果（宿主表现） | 级别 | 证据 |
|---|---|---|---|---|---|
| C1 | 设备注册分类 | 注册进 DirectShow `CLSID_VideoInputDeviceCategory`，`DsDevice.GetDevicesOfCat` 能枚举到 | 不出现在下拉框 | M | `DirectShowCameraService.cs:610` |
| C2 | Moniker 可绑定 | 设备 moniker 的 `BindToObject(IBaseFilter)` 可用（`AddSourceFilterForMoniker` 也要能工作） | 枚举得到但预览/枚举分辨率全部失败 | M | `DirectShowCameraService.cs:736-751,195-196,1015-1016`；WPMediaKit `AddFilterByDevice` |
| C3 | 位数/COM 模型 | x64 in-proc COM（host x64）；**不要假设 STA**：宿主在 `Task.Run` 线程建图（`StartPreviewAsync`/`EnumerateResolutionsAsync`） | `Class not registered`/`0x80040154`/崩溃 | M | `csproj:3,32,49`；`DirectShowCameraService.cs:637,952`；`[实测]` PE x64 |
| C4 | Capture pin 分类 | 至少一个输出 pin，`PinCategory.Capture`（`IKsPropertySet`/`AMPROPSETID_Pin` 正确暴露）；`DsFindPin.ByCategory(...,PinCategory.Capture,0)` 必须拿到 | 非特殊路径空引用/预览失败；WPFMediaKit `FindInterface` 抛异常→MediaFailed | M | `DirectShowCameraService.cs:706-708,760-762,1018-1020,1198-1200`；上游 `VideoCapturePlayer.SetVideoCaptureParameters` |
| C5 | Preview 分类 | 可选：暴露 `PinCategory.Preview`；不暴露也可（Smart Tee 自动插入），但暴露更稳 | 无 preview pin 时依赖 Smart Tee；个别情况下可能预览失败 | S | 微软 `RenderStream` 文档；上游 `VideoCapturePlayer.SetupGraph()` |
| C6 | IAMStreamConfig | capture pin 上实现 `GetNumberOfCapabilities/GetStreamCaps/SetFormat/GetFormat`；`GetStreamCaps` 的 `cbSize` 与 `VIDEO_STREAM_CONFIG_CAPS` 结构一致 | WPFMediaKit `FindInterface` 抛异常→特殊模式 MediaFailed；非特殊模式无分辨率列表 | M | `DirectShowCameraService.cs:1018-1029,789-793`；上游 `VideoCapturePlayer.SetVideoCaptureParameters` |
| C7 | 能力列表 | 至少 1 个 cap；建议给 1920×1080 与 1280×720（宿主默认挑最接近 1920×1080 的项）；`AvgTimePerFrame` 必须非 0（否则默认 30）；可选 `MinFrameInterval/MaxFrameInterval` 范围 | 分辨率下拉框显示“加载中…”；预览回退 1280×720@30 | S | `DirectShowCameraService.cs:798-799,847-892,910-925`；`MW_VideoPresenter.cs:1185-1190,2374-2377` |
| C8 | media type 格式 | `GetFormat`/`GetStreamCaps`/`ReceiveConnection` 首选 `FormatType.VideoInfo`（不是 VideoInfo2）；`BIH.biCompression` 用 RGB24 或 YUY2；`biSizeImage=stride*height` | WPFMediaKit 只按 `VideoInfoHeader` 改写结构，VideoInfo2 会被截断/写坏；宿主 `ApplyNativeResolution` 只匹配 VideoInfo；`TryGetNegotiatedVideoFormat` 只认 VideoInfo | M | `DirectShowCameraService.cs:814-839,1213-1237,1617`；上游 `SetVideoCaptureParameters` |
| C9 | RGB24 像素布局 | 建议只宣传 `MediaSubType.RGB24`、top-down（正 height）、每行 4 字节对齐 stride（`(w*3+3)&~3`）；若同时宣传 YUY2，要确保 SampleGrabber 能通过 DirectShow 智能连接转换 | 宿主非特殊路径花屏/颜色错/黑屏；拍照尺寸错 | M | `DirectShowCameraService.cs:682-691,1604-1639,1564-1601` |
| C10 | SetFormat 语义 | 未连接时也要能 `SetFormat`（宿主在 Connect 前调用）；接受 advertised cap 的 W/H/FPS；返回 S_OK | 分辨率选择不生效/连接失败 | M | `DirectShowCameraService.cs:672-674,1191-1269`；上游 `SetVideoCaptureParameters` |
| C11 | 推帧方式 | push source：worker 线程对已连接下游 `IMemInputPin.Receive(sample)`；`IMediaSample` 设置时间/同步点、实际长度；Stop 时立刻退出线程 | 预览黑屏/卡死/Stop 挂起 | M | DirectShow 常规；宿主 `IMediaControl.Run/Stop`（`DirectShowCameraService.cs:717-719,1291-1346`） |
| C12 | 多实例/重建 | 同一设备允许反复创建/释放 filter 实例；若实现 IAMVideoProcAmp/IAMCameraControl 且探测到支持项，必须允许“常驻探测图 + 流图”**同时**存在（两实例共享帧源，不要各开一路独占 RTSP）。完全不做属性接口时探测图会释放，但仍必须支持反复 Stop→重建 | 展台进入后属性滑块/分辨率枚举失败，或设备被第二实例抢占导致预览失败 | M（实现属性时）/ S（不实现属性时） | `DirectShowCameraService.cs:143-311,950-1053,268-283` + `MW_VideoPresenter.cs:2232-2387` |
| C13 | Stop/Start 可重入 | 宿主会频繁 Stop→置空 Device→250ms 后重建图（`MW_VideoPresenter.cs:2321-2387`）；支持分辨率切换时重建（`MW_VideoPresenter.cs:3131-3138`） | 多次切换/进入展台后黑屏 | M | 同上 |
| C14 | MediaOpened 及时性 | 图构建/`Run()` 要尽快成功（<1.5s），不要等 RTSP 首帧才成功打开；可用“先返回测试画面/黑场，RTSP 异步接入” | 宿主重试 2 次后放弃，提示“摄像头启动失败” | M | `MW_VideoPresenter.cs:134-143,2398-2455` |
| C15 | Failure 语义 | 打开失败要快速 `MediaFailed`（异常）或返回失败 HRESULT，不要既不成功也不失败 | 宿主 1.75s 周期重建图，CPU/COM 压力大（宿主代码注释明确防此死循环） | S | `MW_VideoPresenter.cs:134-143` |
| C16 | 属性接口 | `IAMVideoProcAmp`/`IAMCameraControl` 可选；不实现时宿主会“不支持任何手动属性”并释放探测图（不崩）；实现时 `GetRange` 必须返回合理 hr/flags（`Manual` 位必须置） | 误实现/崩溃会拖垮展台（宿主在 UI 线程外调用但有 try/catch 记录） | O（推荐先不实现） | `DirectShowCameraService.cs:198-311`；`MW_VideoPresenter.cs:3217-3248` |
| C17 | 设备名 | `FriendlyName` 稳定、唯一、可写入 Settings；宿主按 `DsDevice.Name` 大小写不敏感匹配 | 预选失败→回退到第一个摄像头 | M | `MW_VideoPresenter.cs:1641-1671`；`Settings.cs:516-522` |
| C18 | DevicePath | 若可能，提供非空 `DevicePath`；至少保证系统内没有其它 `DevicePath=null` 的 video input filter（否则 WPFMediaKit 可能绑错） | 下拉框选到我们的设备，但预览打开的是别的设备/失败 | **高风险** | 上游 `BaseClasses.cs` `AddFilterByDevicePath`；宿主匹配 `MW_VideoPresenter.cs:2290-2318` |
| C19 | 注册范围 | 每用户（HKCU\Software\Classes）或全机（HKLM）；HKLM 需管理员；HKCU 是否被 DirectShow 设备枚举识别 `[待实机验证]` | 无法注册或枚举不到 | M | 风险 R6 |
| C20 | 卸载/更新 | 反注册干净；DLL 不锁插件目录（宿主插件热重载会删目录；COM 已加载 DLL 会锁文件） | 插件更新/卸载失败 | S | `PluginManager.cs:586-596,1203-1240`（热更新会删插件目录） |

### 3.3 分辨率/帧率细节（宿主算法，决定我们的 cap 怎么报）

- 宿主从 `IAMStreamConfig.GetStreamCaps` 收集 `(W,H,FPS)`，接受 `FormatType.VideoInfo` 与 `VideoInfo2`；从 `AvgTimePerFrame` 得默认 fps（0 当 30）；若有 `MinFrameInterval/MaxFrameInterval` 再补范围两端 fps（`DirectShowCameraService.cs:782-938`）。
- 默认选中项 = 与 1920×1080 曼哈顿距离最小的 cap（:798-799,910-915）；没有任何 cap 则 `SelectedResolutionIndex=0` 或 -1（:924-927）。
- `UniqueResolutions`=按 W,H 去重（fps 取最大）；`AllResolutionFpsCombos`=按 (W,H,FPS) 去重并按像素/帧率降序（:1129-1188）。
- 展台分辨率 ComboBox 优先用 `AllResolutionFpsCombos`，空了才回退 `UniqueResolutions`，再空显示“加载中…”（`MW_VideoPresenter.cs:1168-1232`）；项显示格式 `1920×1080@30fps`（`ICameraService.cs:217-227`）。
- 特殊模式给 WPFMediaKit 的请求 = 选中 cap 的 W/H/FPS；未选中时 **1280×720@30**（`MW_VideoPresenter.cs:2363-2377`）。
- WPFMediaKit 的 `SetFormat` 会保留 `GetFormat` 的 subtype，只改 W/H/AvgTimePerFrame（上游 `SetVideoCaptureParameters`）；所以 `GetFormat` 返回的基线类型要是我们能接受的 RGB24/VideoInfo。

### 3.4 摄像头属性探测（IAMVideoProcAmp/IAMCameraControl）

- 宿主对 7 个属性（Brightness/Contrast/Saturation/WhiteBalance/Gain/Focus/Exposure）逐个 `GetRange`；只有 `hr==0` 且 flags 含 `Manual` 才算支持（`DirectShowCameraService.cs:215-266`）。
- 接口先从 source filter 上 QI，失败再从 capture pin 上 QI（:198-213）。
- 一个属性都不支持时：只记日志、释放图（:268-275）→ **虚拟摄像头不实现这两个接口是最安全的**，展台属性滑块会自动禁用（`MW_VideoPresenter.cs:3191-3211,3217-3248`）。
- 若要实现：GetRange/Get/Set/GetRange 的 hr 与 flags 必须规范；Set 要接受 Manual/Auto。宿主会在探测后把 Settings 里保存的归一化值应用到硬件（`MW_VideoPresenter.cs:3217-3248`）。

### 3.5 其他关键行为

- **WPFMediaKit 的设备缓存**：`MultimediaUtil.VideoInputDevices` 是 static lazy cache（上游 `MultimediaUtil.cs`）；设备必须在宿主进程第一次打开展台前就存在，否则同一个宿主进程内 `StartVideoCaptureElementPreviewAsync` 看不到新设备（`MW_VideoPresenter.cs:2294`）。插件在启动 `Initialize` 注册设备通常满足；热加载插件/展台已打开过则需重启宿主。`[源码确证+上游源码]`
- **`VideoCaptureElement` 的 `DevicePath` 解析**：见 C18，风险 R2。
- **拍照依赖 D3D 渲染**：特殊模式拍照从 D3DImage 回读，所以虚拟摄像头必须在 VMR9 Renderless 下真正出图（不能只靠 SampleGrabber）。`[源码确证]`
- **设备可能被截图功能复用**：`ScreenshotSelectorWindow` 也枚举/使用摄像头（`ScreenshotSelectorWindow.xaml.cs:37,163`），虚拟摄像头出现是正常副作用。

---

## 4. 问题 3：设置持久化与「打开视频展台」调用序列

### 4.1 视频展台相关设置项一览（`Configs\Settings.json`，根对象 `canvas`）

`Settings.cs:26-27` 定义根属性 `Canvas`（`[JsonProperty("canvas")]`）；`SettingsManager.SettingsFileName = Configs/Settings.json`（`Ink Canvas\Windows\SettingsViews\Helpers\SettingsManager.cs:14`），保存走临时文件+原子替换（:39-99），路径基于 `App.RootPath`（`App.xaml.cs:110`）。

| JSON 键（`canvas.*`） | C# 属性 | 说明 | 证据 |
|---|---|---|---|
| `videoPresenterLastCameraName` | `Canvas.VideoPresenterLastCameraName` | **上次选中的摄像头 `DsDevice.Name`**，跨会话恢复；找不到回退第一个 | `Settings.cs:516-522`；恢复 `MW_VideoPresenter.cs:1646-1671`；写入 :1793-1801 |
| `videoPresenterLastResolutionKey` | `Canvas.VideoPresenterLastResolutionKey` | 上次分辨率，格式 `"WxH@FPS"`；恢复逻辑 | `Settings.cs:524-530`；写入 `MainWindow.xaml.cs:3141-3150`；恢复 `MW_VideoPresenter.cs:1694-1784` |
| `videoBoothMode` | `Canvas.VideoBoothMode` | 0=内置/1=希沃/2=自定义 | `Settings.cs:504-506`；枚举 :652-657 |
| `videoBoothCustomAppPath` / `videoBoothCustomAppArgs` | 同上 | 第三方展台程序路径/参数 | `Settings.cs:508-514` |
| `videoPresenterBrightness/Contrast/Saturation/WhiteBalance/Gain/Focus/Exposure` | 同上 | 展台摄像头属性归一化值 -100..100 | `Settings.cs:532-561` |
| `videoPresenterMirrorHorizontal/Vertical` | 同上 | 水平/垂直镜像 | `Settings.cs:563-569` |
| `videoPresenterRotationAngle` | 同上 | 0..3（0/90/180/270°） | `Settings.cs:571-576` |
| `launchSeewoVideoShowcaseForWhiteboardBooth` | 同上（兼容属性） | 旧设置迁移到 `videoBoothMode` | `Settings.cs:486-502`；`SettingsManager.cs:146-154` |

> 插件用 `ISettingsService.Set("Canvas.VideoPresenterLastCameraName", "我们的设备名")` 即可写这条（反射按属性名匹配、大小写不敏感，见 `SettingsService.cs:25-39,71-87`，属性匹配用 `BindingFlags.IgnoreCase` :84-86），并立即 `SettingsManager.SaveSettingsToFile()`（:32）。

### 4.2 打开视频展台的调用序列（源码级）

**方式 1（推荐，插件可用）：`IVideoBoothService.Toggle()`**
1. 插件 `Host.GetService<IVideoBoothService>().Toggle()`（服务实现 `Plugins\Services\VideoBoothService.cs:71-74`，内部切到 UI 线程 :114-137）。
2. `MainWindow.ToggleVideoBooth()`（`MW_VideoPresenter.cs:4375-4403`）：
   - 若已在展台 → 退出（`BtnExitVideoPresenter_Click` :4382；必要时退出白板 :4385-4388）；
   - 若不在白板 → `ImageBlackboard_MouseUp(null,null)` 进入白板（:4394-4398）；
   - `EnterVideoBoothDirect()`（:4411-4444）。
3. `EnterVideoBoothDirect()`：
   - 清理残留（:4415-4425）→ `SyncPhotoCorrectionAccelerationComboBox/SyncBoothBrightnessSlider/SyncBoothMirrorToggles`（:4427-4429）→ `EnsureCameraService()`（:4431）→ `EnterVideoPresenterSpecialMode()`（:4434 → :539-635）→ `RefreshVideoPresenterDeviceList()`（:4436 → :1578-1700）→ `RefreshBoothResolutionComboBox()`（:4438 → :1168-1232）。
4. `RefreshVideoPresenterDeviceListCoreAsync()`：
   - `await _cameraService.RefreshCameraListAsync()`（:1605）→ 填 ComboBox（:1641-1644）→ 按 `VideoPresenterLastCameraName` / 按页索引 / 第一项预选（:1646-1671）→ `EnumerateResolutionsAsync(cameraToSelect)`（:1682）→ `ProbeAndApplyBoothBrightnessAsync()`（:1692 → :3217-3248）→ `TryRestoreResolutionFromSettings()`（:1696 → :1709-1784）→ `StartVideoPresenterPreview(cameraToSelect)`（:1699）。
5. `StartVideoPresenterPreview` → 特殊模式走 `StartVideoCaptureElementPreviewAsync`（:2178-2226、:2232-2483）。
6. 退出：`BtnExitVideoPresenter_Click` → `ExitVideoPresenterSpecialMode()` + `StopVideoPresenterPreviewAndFrameCache`（:1106-1142）。

**方式 2：`icc://videopresenter` 深链接**：`HandleUriCommand` 路由到 `ToggleVideoBooth()`（`MW_UriHandler.cs:184-187`），受 `Settings.Advanced.IsEnableUriScheme` 门控（:53-57）；插件可 `IPluginUriService.OpenUri("icc://videopresenter")`（`PluginManager.cs:2371-2395`），但同样受门控，且不能选摄像头。`[源码确证]`

**方式 3（不满足需求）：`IWindowService`**：只有 `IsTopMost/IsFullscreen/IsCollapsed/IsWhiteboardMode/SetTopMost/Collapse/Expand/EnterWhiteboard/ExitWhiteboard/SetFullscreen/ToggleFullscreen`（`IWindowService.cs:8-76`；实现 `Plugins\Services\WindowService.cs:99-133`），**没有展台接口**，更无法预选摄像头。`[源码确证]`

### 4.3 能否“点插件按钮 → 宿主展台带着我们的虚拟摄像头打开”？

**可以，但有严格前提**（结论：可行，条件式）：
1. 虚拟摄像头已注册且当前进程能枚举到（见 3.2 C1/C2/C19；注意 `MultimediaUtil` 缓存，最好在宿主启动前/插件 `Initialize` 完成注册）。
2. 插件在 `Toggle()` **之前**写 `Settings.Canvas.VideoPresenterLastCameraName = 设备名`（`ISettingsService.Set`）；宿主恢复逻辑用 `DsDevice.Name` 忽略大小写匹配（`MW_VideoPresenter.cs:1646-1671`），匹配不到会回退第一个设备。
3. 再调用 `IVideoBoothService.Toggle()`；若展台已打开，`Toggle()` 是“关闭”，需要再调一次才是重新打开（建议插件先读 `IsActive`，或在已激活时先 `Toggle()` 关闭再写设置再 `Toggle()` 打开）。
4. 局限：宿主没有“打开并指定摄像头”的参数化 API；预选完全依赖全局设置项（会覆盖用户上次选择）；展台已打开时改设置不会即时生效（`RefreshVideoPresenterDeviceList` 只在打开/切换设备时跑）。
5. 如果设备注册晚于宿主第一次打开展台，`MultimediaUtil.VideoInputDevices` 静态缓存可能导致 WPFMediaKit 找不到设备，需要重启宿主（风险 R5）。

---

## 5. 问题 4/5：插件工程、manifest、打包、安装、调试

### 5.1 本机工具链实测结论

| 项 | 实测值 | 结论 |
|---|---|---|
| dotnet SDK | `8.0.426`、`9.0.319`、`10.0.402`（`C:\Program Files\dotnet\sdk`） | 有 .NET 10 SDK，可直接构建 net10 插件 |
| 运行时 | `Microsoft.NETCore.App 10.0.13`、`Microsoft.WindowsDesktop.App 10.0.13` | 可运行 net10 WPF |
| WPF/WindowsDesktop SDK | SDK 自带，无需额外 workload | 实测 `UseWPF=true` 直接编译通过 |
| 插件 SDK 包 | `InkCanvas.PluginSdk 1.8.0.11-gcaf5b75f81`（NuGet 可还原） | 与宿主 1.8.0.11 对齐；SDK 程序集 `AssemblyFileVersion=1.8.0.11`、`CurrentApiVersion=1.13.0` |
| 实测编译 | `_research\tmp-iccce\poc-plugin`、`manifest-probe` 均 0 warning / 0 error | **能编译**（未编译整个宿主） |
| 打包实测 | Release 关 PDB + `CreateIcpx=true` → `.icpx` 内正好 3 个条目 | 可直接产出合规包 |

> 注意：不要引用 ICCCE 仓库里的 `InkCanvas.PluginSdk.csproj` 项目（会改宿主仓库 bin/obj，且 NBGV 生成版本）。用 NuGet 包；或临时复制已构建的 SDK DLL 做离线编译（本机 `_research/tmp-iccce` 已验证 NuGet 方案）。

### 5.2 推荐 `.csproj`（与实测一致，可直接用）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <OutputType>Library</OutputType>
    <AssemblyName>YiouBoothCamera</AssemblyName>
    <RootNamespace>YiouBoothCamera</RootNamespace>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <!-- 纯托管插件：AnyCPU 也可（宿主 x64 进程里同样以 x64 运行）。
         若插件要 P/Invoke x64 原生 DLL，建议同时设置 <PlatformTarget>x64</PlatformTarget>。 -->
    <Platforms>AnyCPU;x64</Platforms>
    <LangVersion>latest</LangVersion>

    <!-- 发布 3 文件 .icpx：CI 用 .github/workflows/plugin-build.yml 手动 staging；
         本地图省事可打开 CreateIcpx 并关 PDB（见 5.5），否则包内会多出 .pdb。 -->
    <CreateIcpx>false</CreateIcpx>
    <DebugType Condition="'$(Configuration)'=='Release'">none</DebugType>
    <DebugSymbols Condition="'$(Configuration)'=='Release'">false</DebugSymbols>
  </PropertyGroup>

  <ItemGroup>
    <!-- 与宿主 1.8.0.11 对应的 NuGet 包；不要用 README 里过时的 1.7.19.9。
         包内 lib/net10.0-windows10.0.19041/InkCanvas.PluginSdk.dll 是宿主内嵌副本的契约，
         运行时由宿主默认 ALC 提供，所以插件里不复制该 DLL。 -->
    <PackageReference Include="InkCanvas.PluginSdk" Version="1.8.0.11-gcaf5b75f81" />
  </ItemGroup>

  <!-- SDK 包自带的 build/InkCanvas.PluginSdk.targets 会把项目根目录的 manifest.json
       自动复制到输出目录（见 SDK targets 的 ItemGroup）。 -->
</Project>
```

实测输出（Release、`DebugType=none`）：`manifest.json`、`YiouBoothCamera.dll`、`YiouBoothCamera.deps.json` 三个文件（见 `_research\tmp-iccce\poc-plugin-notype\bin\Release\net10.0-windows10.0.19041.0\`；对应的 `.icpx` 条目已用 ZipFile 校验）。
说明：普通 `PackageReference` 在 .NET 10 类库项目里**不会**把 `InkCanvas.PluginSdk.dll` 复制到输出（`CopyLocalLockFileAssemblies=false`，实测），但仍建议不要手动把该 DLL 放进包里（与 CI 约定冲突、且宿主已内嵌）。

### 5.3 `manifest.json` 全部字段与合法值

宿主反序列化类型：`InkCanvas.PluginSdk\PluginManifest.cs`（字段 :13-93，依赖类型 :99-115）。**字段名必须与 C# 属性名完全一致（PascalCase）**，因为宿主没有传 `JsonSerializerOptions`（`PluginManager.cs:553,709` 默认大小写敏感）。未知字段被忽略。

| 字段 | 必填？ | 合法值/约束 | 宿主行为/证据 |
|---|---|---|---|
| `Id` | ✅（否则插件被跳过） | 非空、非 `.`/`..`、不含 Windows 非法文件名字符、不含路径分隔符；也是 `Plugins\<Id>` 目录名 | `PluginManager.cs:500-518,554-558,710` |
| `Name` | 建议 | 任意字符串（显示名） | `PluginInfo.Name`（:717） |
| `Version` | 建议 | 任意字符串（建议 SemVer） | 显示/市场版本 |
| `Description` | 可选 | 字符串 | 显示 |
| `Author` | 可选 | 字符串 | 显示 |
| `EntranceAssembly` | 强烈建议 | 插件目录内的 DLL 文件名（如 `YiouBoothCamera.dll`）。留空时宿主回退“取插件目录第一个 *.dll” | `PluginManager.cs:961-980` |
| `ApiVersion` | 建议 | `x.y.z`；**主版本=1 且 ≤ 宿主 `CurrentApiVersion`（1.13.0）**；不填=不限制；无法解析会放行但记警告 | `PluginCompatibility.cs:44-53,72-83,174-179`；`HostApiRequirement.cs:18` |
| `MinHostVersion` | 可选 | `x.y.z`；宿主 `HostVersion`（SDK FileVersion=1.8.0.11）≥ 该值才加载；无法解析放行+警告 | `PluginCompatibility.cs:34-42,88-98`；`HostApiRequirement.cs:28` |
| `VersionRange` | 可选 | npm 风格：`>=1.8.0.11`、`^1.8.0`、`~1.8.0`、`>=1.0.0,<2.0.0` 等 | `PluginCompatibility.cs:56-64,103-158` |
| `Permissions` | 可选 | 任意字符串数组（如 `Settings`/`Network`/`Hotkeys`/`FileSystem`）；**当前版本只用于安装提示，不强制** | `PluginSecurityCheck.cs:54-57,110-113`；全仓检索仅安全提示使用 |
| `Icon` | 可选 | 默认 `"icon.png"`；建议留空（3 文件包里不会带图标） | `PluginManifest.cs:48`；宿主 1.8.0.11 源码未见 `Manifest.Icon` 消费点 |
| `Url` / `SourceUrl` / `License` / `Tags` | 可选 | 任意字符串/数组 | 元数据 |
| `Dependencies` | 可选 | `[{ "Id": "...", "Version": "1.0.0", "IsRequired": true }]`；缺失必需依赖会报错、可选只告警；用于加载顺序 | `PluginDependencyResolver.cs:53-100` |

**推荐 manifest（已随 PoC 编译验证）：**

```json
{
  "Id": "com.yiou.boothcam",
  "Name": "驿欧无线视频展台",
  "Version": "0.1.0",
  "Description": "把驿欧无线视频展台（RTSP）作为虚拟 DirectShow 摄像头接入 ICCCE 内置视频展台。",
  "Author": "iccce-yocamera",
  "EntranceAssembly": "YiouBoothCamera.dll",
  "ApiVersion": "1.0.0",
  "MinHostVersion": "1.8.0.11",
  "Permissions": [ "Settings", "Network" ],
  "License": "MIT",
  "Tags": [ "camera", "booth", "rtsp" ],
  "Dependencies": []
}
```

- 若要求“严格对齐源码一致宿主的能力声明”，把 `ApiVersion` 改为 `"1.13.0"`（`rules\plugin_sdk.md:281`）；`MinHostVersion` 保持 `"1.8.0.11"`。
- 不要填 `VersionRange`（除非确定要封锁后续大版本），漏填不会出问题。
- **不要**照抄 `rules\plugin_sdk.md:34-48` 的小写字段名，宿主会读不到。

### 5.4 最小可加载插件完整示例（已编译通过）

目录：`_research\tmp-iccce\poc-plugin\`（csproj 见 5.2、manifest 见 5.3）。

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using Ink_Canvas.Plugins;

namespace YiouBoothCamera
{
    [PluginEntrance] // 宿主优先找带此特性的 IPlugin 实现（PluginManager.cs:1177-1194）
    public sealed class BoothCameraPlugin : PluginBase
    {
        private const string VirtualCameraDeviceName = "Yiou Wireless Visualizer";

        public override void Initialize(IPluginHost host) // 所有注册动作必须在此完成
        {
            base.Initialize(host); // 必须：PluginBase.Host 在此赋值

            host.Log("BoothCameraPlugin.Initialize");

            host.RegisterToolbarItem(new PluginToolbarItemInfo
            {
                Id = "com.yiou.boothcam.open",
                Surface = PluginToolbarSurface.Floating, // Floating=浮动栏；Whiteboard=白板栏
                DisplayName = "驿欧展台",
                Description = "打开内置视频展台并预选驿欧无线摄像头",
                IconGeometry = "",                        // SVG Path；留空用宿主默认图标
                ViewFactory = CreateButton,               // FrameworkElement 工厂
            });
        }

        private FrameworkElement CreateButton()
        {
            var btn = new Button { Content = "驿欧展台", Padding = new Thickness(8, 4, 8, 4) };
            btn.Click += (s, e) => OpenBoothWithVirtualCamera();
            return btn;
        }

        private void OpenBoothWithVirtualCamera()
        {
            try
            {
                // 1) 预选：宿主按 Settings.Canvas.VideoPresenterLastCameraName（DsDevice.Name）恢复
                Host.GetService<ISettingsService>()?
                    .Set("Canvas.VideoPresenterLastCameraName", VirtualCameraDeviceName);

                // 2) 打开内置展台（主窗口内联全屏预览，不是独立窗口）
                var booth = Host.GetService<IVideoBoothService>();
                if (booth == null) { Host.LogError("IVideoBoothService 未注册", null); return; }
                booth.Toggle();
            }
            catch (Exception ex) { Host.LogError("打开视频展台失败", ex); }
        }

        public override void Shutdown() => Host?.Log("BoothCameraPlugin.Shutdown");
    }
}
```

> 真实交付时还要在此插件里实现「虚拟摄像头注册/安装」与「帧喂入」。`PluginBase` 提供 `Host`/`Manifest`/`PluginConfigFolder`/`PluginFolder`/`Log`/`GetService<T>()`（`PluginBase.cs:14-125`）；`[PluginEntrance]` 非强制，但推荐（`IPlugin.cs:4`；`PluginManager.cs:1177-1194`）。
> 工具栏项字段：`Id/Surface/DisplayName/Description/IconGeometry/ViewFactory/ApplyOrientation/ApplySettings/CustomSettings/PopupContentFactory/BindPopupController/PopupStateChanged`（`IPluginHost.cs:139-181`）。

### 5.5 产出 `.icpx`（两种方式）

**方式 A（官方 CI，推荐）：手动 staging 3 文件**（`.github\workflows\plugin-build.yml:128-166`）：
```powershell
$out   = "<插件项目>\bin\Release\net10.0-windows10.0.19041.0"
$stage = Join-Path $env:TEMP "icpx_YiouBoothCamera"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$out\manifest.json","$out\YiouBoothCamera.dll","$out\YiouBoothCamera.deps.json" $stage
Compress-Archive -Path "$stage\*" -DestinationPath "YiouBoothCamera.icpx" -Force
```
说明：CI 脚本还额外校验输出目录**不得**出现 `InkCanvas.PluginSdk.dll` / `InkCanvas.Controls.dll` / `WinRT.Runtime.dll` / `Microsoft.Windows.SDK.NET.dll`（:113-125）；包内**只允许** 3 个条目（:154-165）。第三方托管依赖要么从宿主解析，要么 ILMerge/ILRepack 进入口 DLL（否则会触发宿主“外部 DLL 授权”弹窗，见 5.7）。

**方式 B：SDK 自带 `CreateIcpx`**：`<CreateIcpx>true</CreateIcpx>` 时 `InkCanvas.PluginSdk.targets` 用 `ZipDirectory` 打包整个输出目录（`InkCanvas.PluginSdk\InkCanvas.PluginSdk.targets:9-26`）。实测：若不关 PDB，包里会多 `YiouBoothCamera.pdb`（4 条目，违反 3 文件约定）；`Release` 设置 `DebugType=none`+`DebugSymbols=false` 后实测正好 3 条目。产物路径 `$(MSBuildProjectDirectory)\icpx\<MSBuildProjectName>.icpx`。
实测样例：`_research\tmp-iccce\poc-plugin-notype\icpx\YiouBoothCamera.icpx`（entries 已校验为 manifest.json / dll / deps.json）。

### 5.6 安装位置与安装流程

- 程序目录：`Plugins\`（已安装）、`PluginPackages\`（待安装 .icpx 缓存）、`PluginConfigs\`、`PluginLogs\`、`PluginMarketCache\`（`PluginManager.cs:115-142`；`rules\plugin_sdk.md:261-271`）。
- 安装目录固定为 `<程序目录>\Plugins\<manifest.Id>\`（`PluginManager.cs:508-518,576-596`）。
- **方式 1（推荐开发用）**：设置 → 插件（插件市场页）→ “从本地安装”→“选择 .icpx”；未受信包会弹安全提示（`PluginMarketplacePage.xaml.cs:652-709`：`EvaluateTrust` → 警告 → 复制到 `PluginPackages\<Id>.icpx` → `InstallPendingPackages(approved...)` 热安装）。
- **方式 2（免 UI 调试）**：把 3 个文件直接解压/复制到 `<安装目录>\Plugins\<Id>\`，重启宿主；`DiscoverPlugins` 会扫描带 `manifest.json` 的子目录（`PluginManager.cs:690-736`）。
- **方式 3（注意陷阱）**：只把 `.icpx` 丢进 `PluginPackages\` 后重启——**未受信本地包在启动阶段会被跳过**：`ProcessPluginPackages` 对 `RequiresUserConfirmation` 的包（本地包=Unknown）要求显式批准，否则 `continue`（`PluginManager.cs:560-574`；`PluginSecurityCheck.cs:127-134`）。所以“丢包重启自动安装”只适用于市场受信包；本地开发请走方式 1/2。
- 插件包文件名建议 `<Id>.icpx`（市场安装流程按 Id 命名，`PluginMarketplacePage.xaml.cs:685-688`）；实际上 `ProcessPluginPackages` 以包内 manifest.Id 为准（`PluginManager.cs:537-558`）。

### 5.7 日志、调试、热重载

- **插件日志**：`<程序目录>\PluginLogs\<plugin-id>\<yyyy-MM-dd>.log`（按日/1MB 轮转，`PluginLogger.cs:8-11,55-61,162-166`；`host.Log/LogError` 经 `PluginHostProxy` 路由，`PluginHostProxy.cs:27-41`；`PluginManager.cs:1032-1035`）。插件禁止自行写文件/写宿主日志（`rules\plugin_sdk.md:68`）。
- **宿主日志**：`<程序目录>\Logs\Log_<AppStartTime>.txt`（`LogHelper.cs:10-13,102-117`）。
- **插件错误/自动禁用**：`Configs\plugin_error_recovery.json`（`PluginErrorRecoveryService.cs:31`）；禁用列表 `Configs\disabled_plugins.json`（`PluginManager.cs:121`）。
- **调试 attach**：VS2022 → 调试 → 附加到进程 → `InkCanvasForClass.exe`。插件 DLL 从字节加载（不锁文件，`PluginManager.cs:986-987,2544-2565`），**若插件目录里放了同名 .pdb，宿主会一并加载，可断点/行号**（:2547-2561）；但 .pdb 不能进 .icpx 包（CI 3 文件规则）。
- **热重载**：`PluginManager.UnloadPlugin` 会撤销注册、释放 ALC、可选删除目录（`PluginManager.cs:1211-1240`）；注意**已被 COM 加载的虚拟摄像头原生 DLL 会锁文件**，插件更新/卸载可能失败，建议把虚拟摄像头 DLL 放在插件目录外（如 `%LOCALAPPDATA%`）并由独立安装器/注册逻辑管理。
- **加载失败排查顺序**：`PluginLogs\host\*.log`/主日志搜 `"Plugin "`、`"incompatible"`；插件页错误详情（`PluginManager.GetPluginError`，`PluginManager.cs:1164-1167`）；兼容性失败消息会打印“要求的 API/宿主版本”和“当前宿主版本/API”（`PluginStrings.resx:329-335`），可据此校正 manifest。

---

## 6. 问题 6：宿主位数、视频展台窗口形态、目录布局

| 问题 | 结论 | 证据 |
|---|---|---|
| 宿主进程位数 | 安装版 **x64**（PE Machine=0x8664）；源码支持 AnyCPU/x86/x64/ARM64/ARM64EC，AnyCPU 分支 `Prefer32Bit=false`，发行版另有 x86 包 | `[实测]` PE 头；`InkCanvasForClass.csproj:3,32,49` |
| 视频展台是否独立窗口 | **否**。全屏预览层在主窗口 `Main_Grid` 内（`VideoPresenterSpecialModeContainer`）；展台菜单是主窗口内 `Popup`；无独立 Window/进程 | `Ink Canvas\MainWindow.xaml:145-190,856`；`MainWindow.xaml.cs:135-142`；`MW_VideoPresenter.cs` 全文无 `new Window` |
| 展台是否全屏 | “特殊模式”= 主窗口画布上铺满深色背景+预览（`EnterVideoPresenterSpecialMode`，`MW_VideoPresenter.cs:539-635`）；是否 OS 全屏取决于主窗口全屏状态（`IWindowService.SetFullscreen` / `SetPluginFullScreen`，`WindowService.cs:123-133`） | 同上 |
| 单进程？ | 是，展台/预览/拍照都在 `InkCanvasForClass.exe` 内；`InkCanvas.IACoreHelper`、`LiquidGlassMagHost` 是其它功能的辅助进程 | `csproj:172-179,599-632` |
| 程序目录结构 | `Plugins/`、`PluginPackages/`、`PluginConfigs/`、`PluginLogs/`、`PluginMarketCache/`、`Configs/`、`Logs/` | `PluginManager.cs:115-142`；`LogHelper.cs:102-117`；`SettingsManager.cs:14` |

---

## 7. 推荐路径（落地架构）

**目标**：用户点插件按钮后，宿主内置视频展台的下拉框能选到（并默认选中）驿欧无线展台，预览/拍照/分辨率/属性 UI 全部正常。**不改 ICCCE 源码。**

推荐分 3 层：

1. **虚拟摄像头层（原生 x64 DirectShow Source Filter，独立 `vcam` DLL/安装器）**
   - 实现 C1–C20 契约；首选 RGB24/VideoInfo；单 capture pin + IAMStreamConfig；可选 preview pin；不实现 IAMVideoProcAmp/IAMCameraControl（先保证不崩）。
   - 帧源：由插件侧（或随插件启动的辅助进程）完成 设备发现 + RTSP 拉流 + H.264 解码，再经**命名共享内存环形缓冲**把 RGB24 帧交给 filter（格式：宽/高/stride/帧号/时间戳/像素数据；丢帧+重连策略）。多实例共享同一帧源。
   - 注册：x64 in-proc COM + `CLSID_VideoInputDeviceCategory`；优先研究 **HKCU\Software\Classes 免管理员注册**是否被 `DsDevice.GetDevicesOfCat` 枚举到（风险 R6，必须先做 PoC）；不行则提供提权安装脚本/安装器。
   - 关键验证：系统内是否存在其它 `DevicePath=null` 的 video input filter（OBS 虚拟摄像头等），避免 WPFMediaKit `AddFilterByDevicePath` 绑错（风险 R2）。
2. **插件层（.icpx，3 文件）**
   - 负责：安装/校验/启动/停止虚拟摄像头（注册、版本检查、异常恢复）；UI（工具栏按钮、设置页配置设备 IP/RTSP URL/分辨率偏好）；调用 `ISettingsService` 预选 `Canvas.VideoPresenterLastCameraName`；调用 `IVideoBoothService.Toggle()`；日志。
   - 如果“3 文件包”必须严格保持，把原生 filter DLL 作为**嵌入资源**打进插件 DLL，运行时释放到 `%LOCALAPPDATA%\ICC-CE\...` 再注册；缺点是首次安装需注册动作（可能要 UAC）。若允许包内多文件，也可直接带 `vcam.dll` + 安装脚本（宿主运行时接受，但违反 CI/市场约定，风险 R8）。
3. **兜底层**
   - 若虚拟摄像头注册失败（权限/枚举问题），插件降级为：自建预览窗口（不进入宿主展台），并在 UI 提示“系统级虚拟摄像头未安装”。
   - 可选：引导用户在宿主设置里把“展台模式”改成 `Custom` 指向插件自带的展台程序（路径 L），但这会替换内置展台，只作为最后手段。

---

## 8. 未解风险与实机验证清单

> 以下均需在「与源码一致的 1.8.0.11 安装宿主」上验证；路径用占位符 `<ICCCE安装目录>`。

| 编号 | 风险 | 影响 | 验证/缓解 |
|---|---|---|---|
| R1 | 实际安装宿主 `CurrentApiVersion` 可能不是 1.13.0（安装实例 commit `dd312cc` 与源码 HEAD `87fe040` 不同） | `ApiVersion:1.13.0` 会被拒 | 先用 `ApiVersion:1.0.0` 打包；看加载错误消息里的“当前宿主 API 为 X”（`PluginStrings.resx:332-334`）再决定是否抬高；或按源码 1.13.0 保守写 1.0.0 |
| R2 | **WPFMediaKit 用 `DevicePath` 重新找设备**；软件注册的虚拟摄像头 `DevicePath` 可能为 null，若系统还有其它 null 设备（OBS 虚拟摄像头、屏幕采集等）会绑错 | 下拉框选中我们的设备，预览却是别的源/失败 | 实机枚举 `VideoInputDevice` 的 `Name/DevicePath`（可用小工具或 PowerShell + DirectShowLib 脚本）；若存在多个 null 设备，考虑注册成 PnP/带 DevicePath 的设备，或说服用户卸载冲突虚拟摄像头；先做 task-3 PoC 验证 |
| R3 | 多实例共存：属性/分辨率探测图（不 Run）与 VMR9 流图同时存在 | 展台进入后黑屏/属性探测失败 | 在 filter 设计上明确支持多实例共享帧源；PoC 用 graf/宿主验证“创建第二个实例不抢设备” |
| R4 | `MediaOpened` 1.5s 兜底限制 | RTSP 建链慢时宿主重试 2 次后放弃 | filter 在 `Play/SetFormat` 后立即成功建图并输出测试帧；RTSP 异步连接，失败再 `MediaFailed` |
| R5 | `MultimediaUtil.VideoInputDevices` static 缓存 | 热加载/运行中注册的设备当前进程看不到 | 设备在宿主启动前已注册；插件 `Initialize` 时完成注册；必要时提示用户重启宿主 |
| R6 | HKCU（免管理员）注册的 COM 设备是否被 DirectShow 设备枚举识别 `[待实机验证]` | 免管理员方案可能不可行，需要 UAC | 先做最小 PoC：写 HKCU\Software\Classes\CLSID + VideoInputDevice category，跑 `DsDevice.GetDevicesOfCat` 看是否枚举到；本机当前进程未提权（whoami: Medium Mandatory Level），`regsvr32` 写 HKLM 需要 UAC |
| R7 | 虚拟摄像头是 in-proc COM，DLL 被宿主进程加载后锁定；插件热更新/卸载会删插件目录（`PluginManager.cs:586-596`） | 更新失败/文件占用 | DLL 放插件目录外稳定路径（`%LOCALAPPDATA%`）；更新前停止/反注册；插件卸载流程处理 |
| R8 | 3 文件包 vs 需要多文件（原生 filter DLL/脚本） | CI 校验失败、市场审核风险 | A：嵌入资源释放（单文件包）；B：接受多文件（运行时能装，但违反约定，需要市场/发布流程例外） |
| R9 | RTSP/发现协议随设备固件变化（task-1） | 无流/黑屏 | task-1 协议规格 + 重连/心跳/休眠唤醒处理 |
| R10 | 拍照依赖 D3DImage 回读（特殊模式）；若 VMR9/VMR 兼容性差 | 有预览但拍照失败 | 实机验证 MediaOpened + 拍照按钮；必要时保留 SampleGrabber 路径做兜底（但宿主特殊模式固定 EnableSampleGrabbing=false） |
| R11 | 宿主有 x86/ARM64 构建 | 只注册 x64 filter 时，x86 宿主看不到 | 交付按 x64（当前安装版）；若需覆盖 x86 再注册 x86 版 filter |
| R12 | 把虚拟摄像头注册进系统后，其它应用（相机、会议软件）也会看到它 | 用户困惑/隐私 | 安装说明里说明；提供开关/卸载；设备名带“ICC-CE”前缀 |
| R13 | SDK 包许可为 `GPL-3.0-only`（`InkCanvas.PluginSdk.csproj:20`），插件链接该 SDK 后的分发许可需确认 | 法律/市场分发风险 | 交付前确认插件自身 License 与 GPL 兼容策略（现有生态插件有 MIT/其它声明，但需自行评估） |

**实机验证步骤（对齐 1.8.0.11 安装宿主）**：
1. 记录宿主版本：`(Get-Item "<ICCCE安装目录>\InkCanvasForClass.exe").VersionInfo`，确认 `FileVersion=1.8.0.11`；读 `InkCanvasForClass.runtimeconfig.json` 确认 net10.0；PE 头确认 x64。
2. 安装最小插件（5.4 的 `com.yiou.boothcam`，仅工具栏按钮）→ 重启宿主 → 设置→插件 应显示“驿欧无线视频展台”，点击“驿欧展台”按钮应进入白板+展台（此时无设备，界面显示“未检测到摄像头设备”）。验证 manifest/加载链路。
3. 注册测试虚拟摄像头（task-3 PoC，固定彩条）→ 重启宿主 → 打开展台 → 下拉框应出现设备名；选中后应预览彩条；切换分辨率应生效；拍照应出图；滑块应显示“不支持”（未实现属性接口时）。
4. 在设备管理器/其它 DirectShow 应用（如 AMCap/graphstudio）里验证设备稳定性；连续开关展台 5 次、切换分辨率 3 次，观察是否黑屏/COM 泄漏。
5. 插件按钮“预选 + Toggle”链路：设备已存在时，点按钮应直接显示我们的设备画面；检查 `Configs\Settings.json` 的 `"videoPresenterLastCameraName"` 是否被写成设备名。
6. 检查依赖 `deps.json`：宿主 `Logs\Log_*.txt` 与 `PluginLogs\com.yiou.boothcam\*.log` 无异常；`PluginLogs\host` 无 `incompatible`/`external dll authorization` 记录。

---

## 9. 附录：关键源码索引（本文引用位置速查）

| 主题 | 文件:行 |
|---|---|
| 宿主 TFM/平台 | `Ink Canvas\InkCanvasForClass.csproj:3,7,27-33,49` |
| SDK TFM/打包 targets | `InkCanvas.PluginSdk\InkCanvas.PluginSdk.csproj:4,37,49`；`InkCanvas.PluginSdk\InkCanvas.PluginSdk.targets:1-34` |
| API 版本/宿主版本 | `InkCanvas.PluginSdk\HostApiRequirement.cs:18,28` |
| 插件清单字段 | `InkCanvas.PluginSdk\PluginManifest.cs:8-116` |
| 插件入口/基类 | `InkCanvas.PluginSdk\IPlugin.cs:4-32`；`PluginBase.cs:9-125`；`PluginEntranceAttribute.cs` |
| 宿主 API | `InkCanvas.PluginSdk\IPluginHost.cs:14-95,100-128,139-244` |
| 版本兼容规则 | `Ink Canvas\Plugins\PluginCompatibility.cs:29-98,103-192` |
| 插件目录/包目录/安装 | `Ink Canvas\Plugins\PluginManager.cs:89-90,115-142,500-518,527-625,690-736` |
| 插件加载/ALC/PDB | `Ink Canvas\Plugins\PluginManager.cs:930-1083,1177-1194,2463-2537,2544-2565` |
| 插件日志/宿主日志 | `Ink Canvas\Plugins\PluginLogger.cs:8-11,55-61,162-166`；`Ink Canvas\Helpers\LogHelper.cs:10-13,102-117` |
| 宿主服务注册 | `Ink Canvas\App.xaml.cs:336-406,1601,1692,1764-1788` |
| 展台 UI/内联 | `Ink Canvas\MainWindow.xaml:145-190,856`；`Ink Canvas\MainWindow.xaml.cs:135-142` |
| 展台设备列表/预选 | `Ink Canvas\MainWindow_cs\MW_VideoPresenter.cs:1578-1700` |
| 展台切设备/持久化 | `Ink Canvas\MainWindow_cs\MW_VideoPresenter.cs:1786-1829`；`Ink Canvas\MainWindow.xaml.cs:3141-3150` |
| 分辨率下拉/恢复 | `MW_VideoPresenter.cs:1166-1232,1709-1784` |
| 特殊模式进入/预览启动 | `MW_VideoPresenter.cs:539-635,2178-2226,2232-2483` |
| 拍照链路 | `MW_VideoPresenter.cs:2587-2753`；`Ink Canvas\Helpers\CapturableVideoCaptureElement.cs:22-84` |
| 插件侧展台服务入口 | `MW_VideoPresenter.cs:4340-4451`；`Ink Canvas\Plugins\Services\VideoBoothService.cs:9-138` |
| DirectShow 枚举/建图/取帧 | `Ink Canvas\Helpers\DirectShowCameraService.cs:605-632,635-751,754-938,950-1053,1191-1269,1420-1639,1646-1677` |
| 属性探测 | `DirectShowCameraService.cs:143-311` |
| 摄像头接口/分辨率类型 | `Ink Canvas\Helpers\ICameraService.cs:13-229` |
| SDK 摄像头/展台服务接口 | `InkCanvas.PluginSdk\ICameraService.cs:14-70`；`IVideoBoothService.cs:12-52` |
| 窗口服务 | `InkCanvas.PluginSdk\IWindowService.cs:8-76`；`Ink Canvas\Plugins\Services\WindowService.cs:99-133` |
| 设置服务/设置项 | `InkCanvas.PluginSdk\ISettingsService.cs:6-31`；`Ink Canvas\Plugins\Services\SettingsService.cs:25-87`；`Ink Canvas\Resources\Settings.cs:26-27,486-576,652-657` |
| 设置落盘 | `Ink Canvas\Windows\SettingsViews\Helpers\SettingsManager.cs:14,39-99,146-154` |
| URI 路由 | `Ink Canvas\MainWindow_cs\MW_UriHandler.cs:15,47-57,167-187`；`PluginManager.cs:2371-2395` |
| 安装安全/本地安装 UI | `Ink Canvas\Plugins\PluginSecurityCheck.cs:31-134,160-198`；`Ink Canvas\Windows\SettingsViews\Pages\PluginMarketplacePage.xaml.cs:652-709` |
| 外部 DLL 授权 | `InkCanvas.PluginSdk\IPluginHost.cs:86-95`；`Ink Canvas\Plugins\PluginAuthorizationService.cs:80-120` |
| CI 打包/3 文件校验 | `.github\workflows\plugin-build.yml:64-73,105-172,226-228` |
| 插件规则文档 | `rules\plugin_sdk.md:5,34-48,52-55,68,70-80,135-197,261-281` |
| WPFMediaKit 3.0.1（上游） | `WPFMediaKit` NuGet 3.0.1，repo `Sascha-L/WPF-MediaKit` commit `86381b3`；`Source/DirectShow/MediaPlayers/VideoCapturePlayer.cs`（`SetupGraph`、`SetVideoCaptureParameters`）、`Source/DirectShow/MediaPlayers/BaseClasses.cs`（`AddFilterByDevicePath`）、`Source/DirectShow/Controls/VideoCaptureElement.cs`、`MultimediaUtil.cs` |

---

## 10. 附录：本机实验产物（`_research/tmp-iccce/`）

| 路径 | 用途 |
|---|---|
| `manifest-probe/` | 用 `InkCanvas.PluginSdk 1.8.0.11-gcaf5b75f81` 实测：`HostVersion=1.8.0.11`、`CurrentApiVersion=1.13.0`、**manifest 大小写敏感**（PascalCase 解析成功、camelCase 全空）、ApiVersion/MinHostVersion 判定复刻 |
| `poc-plugin/` | 最小可加载插件工程（csproj + manifest + PluginBase 子类 + IVideoBoothService/ISettingsService 预选代码），Release 编译成功 |
| `poc-plugin-notype/` | 同上 + `DebugType=none` + `CreateIcpx=true`，实测 `.icpx` 正好 3 条目 |
| `poc-plugin-defaultrefs/` | 对照实验：普通 `PackageReference` 在 .NET 10 类库下不会把 `InkCanvas.PluginSdk.dll` 复制到输出 |

命令记录（可复现）：
```powershell
dotnet --list-sdks
dotnet restore  _research/tmp-iccce/poc-plugin
dotnet build -c Release _research/tmp-iccce/poc-plugin
# 验证包内条目
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead((Resolve-Path '_research/tmp-iccce/poc-plugin-notype/icpx/YiouBoothCamera.icpx')).Entries
```

---

### 一句话给实现团队
**别试图改宿主的摄像头列表；去系统层做一个合格的 DirectShow 虚拟摄像头（x64、RGB24/VideoInfo、IAMStreamConfig、多实例、快启动），插件只做注册+喂帧+写 `canvas.videoPresenterLastCameraName`+`IVideoBoothService.Toggle()`。**
