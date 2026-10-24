# v0.3.0 — 驿欧无线展台 ICCCE 插件（camera provider 模式）

把 **驿欧无线视频展台**（YiouBoard 2.0 无线实物展台）接入 ICCCE 内置视频展台。
纯托管、仅宿主内可见、**零系统安装**（不注册虚拟摄像头、不写注册表、不需要管理员权限）。

## 安装

1. **先确认宿主带 camera provider 接口**（`ApiVersion ≥ 1.14.0`）：
   - 官方版 1.8.0.11 **不支持**，需要打补丁版宿主（见下）或等 [PR #739](https://github.com/InkCanvasForClass/community/pull/739) 合并后的版本；
2. 宿主「设置 → 插件 → 从本地安装」选择本 `.icpx`；
   （或把 `.icpx` 放进 `<宿主目录>\PluginPackages\` 后重启，首次可能需要在设置页确认安装）
3. 重启宿主 → 浮动栏出现「驿欧展台」按钮 → 点它。

## 使用

- 点「驿欧展台」：自动发现设备 → 开流 → 解码 → 写预选摄像头 → 打开宿主视频展台全屏；
- 也可直接在「视频展台」菜单的摄像头下拉框里选 **「驿欧无线展台（插件）」**；
- 「驿欧截图」：把当前帧插入画布（可撤销）。

## 依赖

- **ffmpeg.exe**（RTSP 解码）。自动探测顺序：
  设置页配置 → `YIOU_FFMPEG_PATH` → 插件目录 → 宿主目录 → PATH →
  **驿欧白板套件自带**（`<安装根>\FFmpeg\bin\x86|ffmpeg.exe`，含注册表 InstallLocation 探测）→ 开发机兜底。
  已适配 FFmpeg 4.0.1（厂商自带）与 5.1+/6+/7+/8 的参数差异。
- 无需物理摄像头。

## 本版本要点

- camera provider 接入（运行时反射 + `DispatchProxy`，可对着官方 1.13 SDK 编译，新宿主上自动启用）；
- **完全移除虚拟 DirectShow 摄像头**：插件不再有任何系统级安装路径，源码中 `VcamInstaller/vcam_filter/DllInstall` 为 0 处；
- 适配厂商 ffmpeg 4.0.1（`-vsync 0` + `-stimeout`）与新版本（`-fps_mode passthrough` + `-timeout`）；
- 设置页：设备 IP / 网卡 / 配对码 / 分辨率 / ffmpeg 路径 / 推帧统计 / 日志目录。

## 校验

```
SHA256  16886230a307c3108a0ade94b5e2569b3095523925e937026cfd46f15cca22c0
包内    manifest.json, YiouCamera.Plugin.dll, YiouCamera.Plugin.deps.json
```

## 已知限制

- 需要带 camera provider 的宿主（官方原版可加载但不会工作，日志会明确提示）；
- 分辨率切换为重建推流，会有短暂黑屏；
- 画质滑块（亮度/对比度等）对 provider 摄像头不可用（源设备本身也不支持这些命令）；
- 仅验证 x64。
