# 补丁版宿主（Patched Host）使用说明

> 产物：`build\patched-host\`（ICCCE 1.8.0.11 + plugin camera provider 补丁）
> 分支/补丁：`feat/plugin-camera-provider` / `docs/camera-provider.patch`
> 用途：**周一现场主线方案**。插件（驿欧无线展台）通过 camera provider 把自己的 RTSP 画面交给宿主，
> 不需要注册虚拟 DirectShow 摄像头、不需要管理员权限、不会与 OBS 等虚拟设备冲突。

## 1. 这是什么

这是 ICCCE **1.8.0.11（net10 / x64）** 的完整可运行构建，只额外合入了本项目的
`camera-provider.patch`（新增插件摄像头提供者能力，`CurrentApiVersion` 1.13.0 → 1.14.0）。
除该补丁外与官方 1.8.0.11 一致：

- 原有 DirectShow 摄像头、视频展台、白板、插件体系全部为官方逻辑；
- 新增：插件可在运行时把自己的画面注册进「视频展台 → 摄像头下拉框」；
- 官方安装目录**不需要**任何改动，本目录是 portable 副本，可与官方版并存。

## 2. 目录内容

`build\patched-host\` 是一个**完整的可运行目录**（framework-dependent，需 .NET 10 Desktop Runtime；共 187 个文件 / 约 170 MB），整体拷走即可：

```
build\patched-host\
├─ InkCanvasForClass.exe          ← 双击启动
├─ InkCanvasForClass.dll          ← 主程序（含 camera provider 补丁）
├─ InkCanvasForClass.deps.json / .runtimeconfig.json / .dll.config
├─ InkCanvas.PluginSdk.xml        ← SDK 文档（插件开发用）
├─ EVRPresenter64.dll
├─ OpenCvSharpExtern.dll
├─ opencv_videoio_ffmpeg4130_64.dll
├─ System.Management.dll          ← 及运行库依赖
└─ BUILD-INFO.txt                 ← 本构建的来源、补丁 hash、构建命令
```

> **不要只拷 exe**；整个目录一起拷。目录内的 `Configs/`、`Plugins/`、`Logs/`、`PluginLogs/`
> 会在首次运行时自动生成（与官方版一致，使用程序目录作为 `App.RootPath`）。

## 3. 放到教室电脑（portable 安装）

1. 把 `build\patched-host\` 整个目录拷到目标机器，例如 `D:\ICCCE-Patched\`
   （**不要**覆盖 `C:\Program Files\...` 或官方安装目录）。
2. 确认目标机器已安装 **.NET 10 Desktop Runtime（x64）**——能正常运行官方 1.8.0.11 的机器都已满足。
3. 双击 `InkCanvasForClass.exe`。首次启动会走官方「欢迎向导」（OOBE），按向导完成即可；
   所有配置写入 `D:\ICCCE-Patched\Configs\`。
4. 需要的话创建桌面快捷方式指向该 exe（不要指向官方版）。

> 首次运行 Windows 可能弹出防火墙提示（插件要访问局域网设备），允许即可。
> 如果提示缺少 `hostfxr.dll` / `.NET Desktop Runtime`，说明机器没装 .NET 10，请先安装。

## 4. 装插件

插件必须是**支持 camera provider 的版本**（`manifest.json` 里 `ApiVersion` ≥ `1.14.0`，
且实现了 `ICameraProviderService`）。安装方式与官方版完全相同：

1. 把插件目录放到 `D:\ICCCE-Patched\Plugins\<插件Id>\`，目录内至少包含：
   `manifest.json` + 入口 DLL + 同名 `.deps.json`（如 `YiouCamera.Plugin.dll`）。
2. 重启宿主（或走「设置 → 插件」的本地安装，若插件提供 `.icpx`）。
3. 打开「视频展台」→ 摄像头下拉框应出现插件注册的摄像头（如「驿欧无线展台（插件）」）。
   选中后即可预览/拍照/切分辨率，展台原有按钮（缩放/旋转/镜像/批注/照片栏）全部照常。

> **本构建下不需要**安装/注册任何虚拟 DirectShow 摄像头（vcam_filter.dll）：
> 不要运行 vcam 的注册脚本，也不要让插件回退到 vcam 路径。插件在检测到
> `ICameraProviderService` 存在时应自动优先 camera provider。

## 5. 从官方版切换 / 回滚

- **切换**：直接用本目录的 exe 启动即可；官方版仍保留在原安装目录，二者配置互不影响
  （配置、插件、日志都在各自程序目录下）。
- **回滚**：关闭本目录的宿主，删掉 `D:\ICCCE-Patched\` 即可，系统无任何残留
  （没有注册 COM、没有写注册表、没有系统服务）。继续用官方版 exe 启动。
- **临时对照**：两个版本不要同时运行（避免同时抢占同一摄像头/单实例冲突），
  先用哪个就先关掉另一个。

## 6. 与原版的行为差异

| 项 | 官方 1.8.0.11 | 本构建 |
|---|---|---|
| 视频展台摄像头列表 | 仅 DirectShow 设备 | DirectShow 设备 **+ 插件 provider 摄像头** |
| 插件 API 版本 | 1.13.0 | **1.14.0**（新增 `ICameraProviderService`） |
| 插件摄像头预览 | 不支持 | WPF Image 渲染，共享既有缩放/旋转/镜像/批注变换 |
| 插件摄像头拍照 | 不支持 | 取宿主缓存的最近一帧 → 走现有插入画布/照片栏逻辑 |
| 插件摄像头分辨率 | 不支持 | 由插件声明的能力列表填充下拉框 |
| 其它功能 | — | **与官方一致（补丁不触碰）** |

## 7. 现场自检清单（装好后 30 秒）

1. 双击 exe → 进入白板界面，无报错弹窗。
2. 打开「视频展台」→ 摄像头下拉框能看到插件摄像头。
3. 选中 → 画面出现，延迟可接受；旋转/镜像/缩放/批注正常。
4. 点「拍照」→ 照片出现在右下角页码预览，可切页/批注。
5. 再切回官方 USB 摄像头（若有）→ 行为正常；退出展台 → 插件日志出现 `Provider.StopCamera`。
6. 若异常：先看 `D:\ICCCE-Patched\Logs\Log_*.txt` 与 `PluginLogs\<插件Id>\*.log`，
   再把这两个文件带回排查。

## 8. 已知限制（与 PR 文档一致）

- 属性调节（亮度/对比度等）对插件摄像头不生效，滑块置灰；
- 分辨率切换 = 重建推流（短暂黑一下），实际按插件推送帧尺寸渲染；
- 仅 x64；插件热加载后下拉框会自动刷新，无需重启宿主（首次注册建议在启动时完成）。
