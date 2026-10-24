# YiouCamera.Plugin —— ICCCE「驿欧无线展台」插件（camera provider 版）

把**驿欧无线视频展台**（UDP 广播发现 + TCP 8888 控制 + RTSP 取流）通过 ICCCE 的
**camera provider** 接口接入宿主内置「视频展台」，并提供一键打开 / 手动截图。

- 宿主：ICC-CE **1.8.0.11** + **camera provider 补丁**（`InkCanvas.PluginSdk` API **≥ 1.14.0**）
- 插件 Id：`com.yiou.boothcam`，入口程序集：`YiouCamera.Plugin.dll`
- provider 摄像头名：**`驿欧无线展台（插件）`**（同时写入宿主的「上次选中摄像头」设置）
- **零系统安装**：不写注册表、不注册 COM、不在磁盘落虚拟摄像头 DLL；插件卸载/宿主退出即消失
- 采集内核：`src/YiouCamera.Core`（`YiouBoothSession`，构建时合并进入口 DLL）

> **仅支持 camera provider 路径。** 官方原版宿主（API 1.13.0）没有该接口：插件可以正常加载，
> 但会明确提示「当前宿主不支持 camera provider」，不会注册任何系统级虚拟摄像头。

---

## 1. 功能

| 能力 | 说明 |
|---|---|
| 工具栏按钮「驿欧展台」 | 预写宿主设置 `Canvas.VideoPresenterLastCameraName = 驿欧无线展台（插件）` → `IVideoBoothService.Toggle()`，宿主展台带着 provider 摄像头打开 |
| 工具栏按钮「驿欧截图」 | 取 Core 最近一帧 → `ICanvasInkService.InsertBitmap()` 插入画布 |
| camera provider | 声明 1280×720@30 / 1920×1080@30 能力；Core 的 `FrameReady` → `IPluginCameraFrameSink.Submit(BGR24)`；宿主负责渲染、旋转/镜像/缩放、拍照、分辨率下拉 |
| 设置页 | 接入模式 / provider 推帧统计（fps、提交/接收/拒绝、目标格式）、设备 IP / 网卡 / 配对码 / 分辨率 / ffmpeg 路径、打开日志 |
| 生命周期 | `Initialize` 立即返回；provider 注册在 Initialize 内完成；`Shutdown` 注销 provider 并停会话；所有异常只 `LogError`，绝不抛给宿主 |

## 2. 环境要求

- Windows 10/11 x64
- .NET SDK **10.0.402**（或兼容 .NET 10 SDK）
- **带 camera provider 补丁的 ICCCE 宿主**（API ≥ 1.14.0），本仓库约定产物目录：
  `build/patched-host/`（由 `C:\Users\lrs2187\Projects\iccce-provider-pr` worktree 构建/发布）
- 可选：`dotnet tool install --global dotnet-ilrepack`（把 `YiouCamera.Core.dll` 合并进入口 DLL，产物保持 3 文件）
- 采集需要 `ffmpeg.exe`（Core 自动解析：宿主目录 / PATH / `YIOU_FFMPEG_PATH`，也可在设置页指定）

## 3. 构建

```powershell
cd C:\Users\lrs2187\Projects\iccce-yocamera\src\YiouCamera.Plugin
dotnet tool install --global dotnet-ilrepack   # 一次性
dotnet build -c Release
```

产物目录 `bin\Release\net10.0-windows10.0.19041.0\`，正常 3 个文件：

```
manifest.json
YiouCamera.Plugin.dll          ← 含 Core（ILRepack 合并）
YiouCamera.Plugin.deps.json
```

> **硬约束**：产物目录不能出现 `InkCanvas.PluginSdk.dll` / `InkCanvas.Controls.dll`
> （宿主 ALC 会加载出第二份 `IPlugin` 类型导致插件加载失败）。SDK 包已 `ExcludeAssets=runtime`。
> `-p:YiouUseCore=false` 可构建不含 Core 的占位版；`-p:YiouMergeCore=false` 可关闭合并（产物多一个 Core.dll）。

## 4. 本地安装（免打包调试）

```powershell
$HOST  = 'C:\Users\lrs2187\Projects\iccce-yocamera\build\patched-host'
$out   = 'C:\Users\lrs2187\Projects\iccce-yocamera\src\YiouCamera.Plugin\bin\Release\net10.0-windows10.0.19041.0'
$dst   = Join-Path $HOST 'Plugins\com.yiou.boothcam'

New-Item -ItemType Directory $dst -Force | Out-Null
Copy-Item "$out\manifest.json", "$out\YiouCamera.Plugin.dll", "$out\YiouCamera.Plugin.deps.json" $dst -Force
# 然后启动 $HOST\InkCanvasForClass.exe
```

安装后**不需要**注册任何系统设备；宿主启动后插件在 `Initialize` 里注册 provider，
视频展台的摄像头下拉框会出现「驿欧无线展台（插件）」。

模拟器联调（无真机）：

```powershell
cd C:\Users\lrs2187\Projects\iccce-yocamera
dotnet run --project tests\Simulator -c Release -- --serve --pin 92271885
# 插件设置页填：设备 IP 留空（走自动发现）、本机网卡 IP=127.0.0.1、配对码=92271885
# 或写 <HOST>\PluginConfigs\com.yiou.boothcam\config.json：
# { "DeviceIp": "", "LocalBindIp": "127.0.0.1", "PairingCode": "92271885", "Resolution": "1280x720", "FrameRate": 30 }
# 启动宿主后点工具栏「驿欧展台」→ 展台应出现模拟器彩条画面；分辨率/旋转/拍照均由宿主负责。
```

> 同一时间只能有一个共享内存写者。provider 路径**不写** `Local\YoCameraVcamFrames`；
> 不要同时跑仿真器 `--with-core`（它默认也写该 SHM）。

## 5. 设置页字段

| 字段 | 说明 |
|---|---|
| 设备 IP（留空自动发现） | 已知设备 IP 时跳过 UDP 12811 广播发现 |
| 本机网卡 IP | UDP/TCP 绑定网卡；「自动（推荐）」= Core 自动挑非虚拟网卡；多网卡建议显式指定 |
| 配对码（8 位） | 留空时 Core 自动读 `%USERPROFILE%\Documents\YOFiles\YOCamSet.xml` 的 `PinCode` |
| 分辨率 / 帧率 | 1280×720@30 / 1920×1080@30（provider 能力列表由插件声明，实际以推送帧为准） |
| ffmpeg.exe 路径 | 留空自动解析 |
| 接入模式 / provider 统计 | 只读：当前模式、provider 摄像头名、推流 fps/帧数/目标格式/最近错误 |
| 连接 / 断开 / 打开视频展台 / 抓图插入画布 | 手动控制采集与宿主展台 |

配置保存在 `<HOST>\PluginConfigs\com.yiou.boothcam\config.json`（插件自己的配置目录，不写宿主 Settings.json）。

## 6. 日志与自测

- 插件日志：`<HOST>\PluginLogs\com.yiou.boothcam\yyyy-MM-dd.log`（只通过 `host.Log/LogError` 写）
- 宿主日志：`<HOST>\Logs\Log_<时间>.txt`、`<HOST>\PluginLogs\host\yyyy-MM-dd.log`
- 无人值守自测：

```powershell
$env:YIOU_BOOTHCAM_SELFTEST='1'          # 自测 provider 注册/枚举/设置项（不会安装任何系统设备）
$env:YIOU_BOOTHCAM_SELFTEST_SESSION='1'  # 额外测采集会话 + 最近帧 + 截图插画布（需真机/仿真器）
& "<HOST>\InkCanvasForClass.exe"
```

自测日志关键行示例：

```
[Yiou] camera provider: provider 反射代理已创建（运行时实现 IPluginCameraProvider，不依赖编译期类型）
[Yiou] 接入模式：camera provider 注册成功，摄像头名「驿欧无线展台（插件）」（ProviderId=com.yiou.boothcam）
[Yiou] camera provider 模式：零系统安装（不写 HKCU、不落 DLL；插件不注册任何系统级虚拟摄像头）。
[Yiou] 摄像头枚举（自测）：... → HIK 1080P Camera | 驿欧无线展台（插件）
[Yiou] SELFTEST: [1] provider 注册状态：已注册=True ... 命中=True
[Yiou] SELFTEST: ===== 结束，结果 PASS =====
```

## 7. 排障

| 现象 | 说明 / 处理 |
|---|---|
| 设置页显示「当前宿主不支持 camera provider（API < 1.14.0）」 | 用的是官方原版宿主：本插件只支持 provider，请换 `build/patched-host`（或等官方合并补丁后的版本） |
| 下拉框没有「驿欧无线展台（插件）」 | 确认插件日志「接入模式：camera provider 注册成功」；宿主需在启动时加载插件（热加载插件后重新打开展台即可） |
| 点「驿欧展台」后展台没选中本摄像头 | 检查宿主 `Configs\Settings.json` 的 `videoPresenterLastCameraName` 是否为 `驿欧无线展台（插件）`；名字必须完全一致 |
| 预览长时间「正在启动摄像头...」 | 采集未出帧：检查设备/仿真器、`ffmpeg.exe`、配对码、网卡选择；设置页 provider 统计可看帧数 |
| 预览黑屏但帧统计在涨 | provider 帧已到宿主，问题在宿主渲染（请抓宿主日志 `[VideoPresenter] 插件摄像头...`） |
| 相机/会议软件里看不到本设备 | 预期行为：provider 只对宿主可见。本插件**不再提供**系统级虚拟摄像头 |
| 系统里仍有旧的「驿欧无线展台」DirectShow 设备 | v0.2 及更早版本的 vcam 兼容模式遗留；本版本不再安装/维护它。卸载旧残留：删除 `HKCU\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}` 与 `HKCU\Software\Classes\CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}`，并删除 `%LOCALAPPDATA%\ICC-CE\YiouCamera\vcam` 后重启宿主 |

## 8. 验收证据（本机）

`verify/` 下包含 provider 路径的实测证据：

- `provider-booth-preview.png` / `provider-zero-install-preview.png`：仿真器彩条画面出现在宿主视频展台（provider 摄像头）
- `pm-rotate.png`：旋转 180° + 右侧相册出现拍照 #1
- `pm-resolution-switched.png`：分辨率下拉切到 1280×720@30 后宿主重建推流
- 插件日志：`plugin-log-e2e-*.log`；宿主日志：`host-main-log-*.txt`
- 本机验收同时确认：provider 模式启动后 `HKCU\...\CLSID\{9A3E7D21-...}` 与
  `%LOCALAPPDATA%\ICC-CE\YiouCamera\vcam` **均不存在**（零系统安装）
