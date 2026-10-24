# iccce-yocamera

[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/tag/lrs2187/iccce-yocamera?label=release)](https://github.com/lrs2187/iccce-yocamera/tags)
[![Build](https://github.com/lrs2187/iccce-yocamera/actions/workflows/build.yml/badge.svg)](https://github.com/lrs2187/iccce-yocamera/actions/workflows/build.yml)

把 **驿欧无线视频展台**（YiouBoard 2.0 的无线实物展台，只输出 WiFi RTSP 流）接入
[ICCCE / InkCanvasForClass-CE](https://github.com/InkCanvasForClass/community) 的内置视频展台。

![在 ICCCE 视频展台中预览](src/YiouCamera.Plugin/verify/provider-only-booth.png)

- **纯托管**：UDP 发现 → TCP 控制/开流 → ffmpeg 解码 → 交给宿主渲染
- **零系统安装**：不注册任何虚拟摄像头、不写注册表、不需要管理员权限
- **不需要物理摄像头**
- **只对 ICCCE 可见**：其它软件（相机、会议软件）看不到这个"摄像头"

```powershell
git clone https://github.com/lrs2187/iccce-yocamera.git
cd iccce-yocamera
pwsh -File tools\build-all.ps1
```

## 为什么要 `patched-host`

ICCCE 1.8.0.11 的摄像头列表只来自 DirectShow 枚举，插件 SDK 没有注入点。
本项目向上游提出了 **camera provider** 接口（分支 `feat/plugin-camera-provider`，
补丁见 [docs/camera-provider.patch](docs/camera-provider.patch)），
在宿主内用 WPF `Image` 渲染插件推的帧，从而完全绕开 DirectShow / VMR9。

> 我们也实现过「系统级虚拟 DirectShow 摄像头」路线（`src/YiouCamera.Vcam`，已冻结）：
> 在目标机器上 VMR9 renderless 的输入分配器 `Commit` 恒返回 `E_OUTOFMEMORY`，
> 无法验收出图，故改用 provider 方案。详见 [docs/PLAN.md](docs/PLAN.md) §13。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/YiouCamera.Core/` | 采集内核：设备发现、TCP 控制/心跳、RTSP、ffmpeg 解码、帧源、共享内存 |
| `src/YiouCamera.Plugin/` | ICCCE 插件（camera provider 接入、工具栏、设置页、截图插画布） |
| `src/YiouCamera.Vcam/` | 虚拟 DirectShow 摄像头（**已冻结**，仅存档） |
| `tests/Simulator/` | 设备端仿真器（UDP 12811 / TCP 8888 / 最小 RTSP），无真机联调用 |
| `tests/Harness/` | WPFMediaKit 渲染路径回归工具 |
| `tools/` | 构建 / 安装 / 卸载 / 校验脚本 |
| `docs/` | 方案、契约、PR 物料、现场手册 |
| `_research/*.md` | 逆向协议规格、宿主接入面分析等调研文档（中间产物不入库） |

## 快速开始

```powershell
# 1. 构建（Core + Plugin → build/out/plugin，3 文件产物）
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\build-all.ps1

# 2. 安装到宿主（patched-host 或自编译的带 camera provider 的宿主）
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1 -HostDir D:\ICCCE-Patched

# 3. 校验
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1 -HostDir D:\ICCCE-Patched
```

无真机联调：`tests/Simulator` 做设备端，插件走自动发现。
现场部署与排障见 **[docs/HANDOFF.md](docs/HANDOFF.md)**。

## 文档

- [docs/HANDOFF.md](docs/HANDOFF.md) — 现场交付包 / 操作手册
- [docs/PLAN.md](docs/PLAN.md) — 方案与决策记录（含 native 线冻结原因）
- [docs/CONTRACTS.md](docs/CONTRACTS.md) — 模块契约
- [docs/PR.md](docs/PR.md) — 上游 PR 物料
- [docs/plugin-integration-example.md](docs/plugin-integration-example.md) — 插件接入示例
- [_research/yocam-protocol.md](_research/yocam-protocol.md) — 驿欧无线展台通信协议逆向规格

## 许可

本项目以 **GNU General Public License v3.0** 发布，见 [LICENSE](LICENSE)。

之所以是 GPL-3.0：`InkCanvas.PluginSdk` 本身是 `GPL-3.0-only`，链接它的插件需遵循同一许可。
