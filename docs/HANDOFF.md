# 周一现场交付包 / 操作手册

> 版本：2026-10-24 16:30
> 目标：在教室电脑上，让 ICCCE 视频展台的摄像头选择里出现并使用「驿欧无线视频展台」。

---

## 0. 一句话原理

插件在宿主进程内采集「驿欧无线展台」的 RTSP 流（UDP 发现 → TCP 控制/开流 → ffmpeg 解码），
然后通过 **camera provider** 接口把帧推给宿主，宿主用 WPF `Image` 渲染。

- **不安装任何系统级虚拟摄像头**（无 COM、无注册表、无提权、无残留）；
- **不需要物理摄像头**；
- 只对 ICCCE 可见，其它软件（相机、会议软件）看不到。

---

## 1. 需要带走的文件

| 目录/文件 | 说明 |
|---|---|
| `build/patched-host/` | 打了 camera provider 补丁的 ICCCE（**整个目录**，约 170 MB / 70 文件） |
| `build/patched-host/Plugins/com.yiou.boothcam/` | 插件（manifest.json + YiouCamera.Plugin.dll + deps.json，约 173 KB） |
| `docs/HANDOFF.md` | 本文件 |

> 插件若不在 `Plugins/` 里，手工建目录 `Plugins\com.yiou.boothcam\` 把三个文件放进去即可。

---

## 2. 前置条件

1. **Windows 10/11 x64**；
2. **.NET 10 Desktop Runtime (x64)** —— 如果这台机器原本能跑 ICCCE 1.8.0.11 就已经装好了；
3. 教室电脑需要**能连上驿欧展台**（同一 WiFi / 展台 AP）。插件会自动广播发现（UDP 12811），不需要手动填 IP；
4. **ffmpeg.exe**（用于 RTSP 解码）。自动探测顺序：
   `设置页显式配置` → 环境变量 `YIOU_FFMPEG_PATH` → **插件目录**（`Plugins\com.yiou.boothcam\ffmpeg.exe`）
   → 宿主目录 → PATH → **驿欧白板套件自带**（`<安装根>\FFmpeg\bin\x86|x64\ffmpeg.exe`，含注册表 InstallLocation 探测）→ 开发机兜底。
   - 教室机器通常装过驿欧白板套件，**大概率能自动找到**；
   - 万一找不到：从 `YiouBoard2.0\FFmpeg\bin\x86\ffmpeg.exe` 复制一份到 `Plugins\com.yiou.boothcam\ffmpeg.exe` 即可（该 exe 是独立进程，x86 在 x64 上跑没问题）。

---

## 3. 现场操作（约 2 分钟）

1. 把 `patched-host` 整个目录拷到教室电脑（建议 `D:\ICCCE-Patched`，**不要覆盖**学校原有的官方 ICCCE 目录）；
2. 双击 `InkCanvasForClass.exe` 启动（首次会走官方 OOBE 向导，正常走完）；
3. 打开驿欧展台电源，确认它已开机并在广播（绿灯/网络连通）；
4. 底部浮动栏点 **「驿欧展台」** 按钮（插件注册的工具栏项）；
   - 插件会自动：设置预选摄像头 → 启动采集会话 → 调用 `IVideoBoothService.Toggle()` 打开宿主视频展台；
5. 展台全屏后应看到实时画面。

> 备用入口：直接打开宿主「视频展台」菜单，摄像头下拉框里选 **「驿欧无线展台（插件）」**。

---

## 4. 30 秒自检清单

| # | 检查 | 期望 |
|---|---|---|
| 1 | 插件是否加载 | `PluginLogs\com.yiou.boothcam\<日期>.log` 有 `接入模式：camera provider 注册成功` |
| 2 | 系统是否干净 | 注册表 `HKCU\Software\Classes\CLSID\{9A3E7D21-…}` **不存在**；`%LOCALAPPDATA%\ICC-CE` **不存在** |
| 3 | 摄像头是否出现在下拉框 | 列表里有 `驿欧无线展台（插件）` |
| 4 | 发现是否成功 | 插件日志有 `发现设备 DeviceIp=…`（UDP 12811） |
| 5 | 开流是否成功 | 有 `RX ACK_RTSP_TRANS_START` |
| 6 | 解码是否出帧 | 有 `[ffmpeg] 已出帧 1280x720 bgr24` 或 `状态变化 → Running` |
| 7 | 画面是否出图 | 展台全屏有实时画面（讲义/桌面） |
| 8 | 拍照是否正常 | 点展台「拍照」→ 照片栏/画布出现照片 |

---

## 5. 常见问题

| 现象 | 排查 |
|---|---|
| 日志 `宿主不支持 camera provider（需要 API ≥ 1.14.0）` | 用的是**官方原版**宿主，不是 `patched-host`。改用交付目录里的那个 exe。 |
| 一直 `Discovering` 找不到设备 | ① 展台是否开机/连同一网络；② 多网卡/VPN 时在插件设置里指定本机网卡 IP；③ 配对码是否与设备匹配（见下） |
| `找不到 ffmpeg.exe` | 按 §2.4 把 ffmpeg.exe 放进插件目录，或在插件设置页填绝对路径 |
| 出 `ACK_RTSP_TRANS_START` 但 RTSP 连不上 | 先用 `ffprobe rtsp://<设备IP>:554/stream0` 单验；必要时在设置里改用 TCP 传输 |
| 画面黑但日志 Running | 检查是否被其它程序占用展台网络；重启宿主与展台 |
| 首帧慢（2–3 秒） | 正常（发现 1s 节拍 + 开流 + ffmpeg 探测），不超过 5 秒 |

**配对码**：插件优先读 `%USERPROFILE%\Documents\YOFiles\YOCamSet.xml` 的 `PinCode`（厂商软件已配对过的值）；
读不到时在插件设置页手填 8 位数字。是否强校验待现场确认（协议文档 V1）。

---

## 6. 回滚（零残留）

1. 关掉宿主；
2. 删掉 `D:\ICCCE-Patched` 整个目录；
3. 完成 —— 没有注册表、没有系统服务、没有驱动。

---

## 7. 与官方版的差异 & PR

- 差异仅一处：新增插件 camera provider 接口（13 文件，+1981/−33），**旧插件与 DirectShow 摄像头路径完全不受影响**；
- 补丁：`docs/camera-provider.patch`（对上游 `net10` `87fe0409` 可 `git apply`）；
- 上游 PR：分支 `feat/plugin-camera-provider`（worktree `C:\Users\lrs2187\Projects\iccce-provider-pr`，commit `d28f1d13`），PR 正文见 `docs/PR.md` §9；
- 一旦上游合并并发布新版本，就可以去掉 patched-host，回归官方版。

---

## 8. 现场待验证清单（真机）

来自 `_research/yocam-protocol.md` §12，按优先级：

1. **V1 配对码是否强校验**（决定插件是读 YOCamSet.xml 还是必须手填/扫码配对）；
2. **V4 RTSP 是否需要鉴权**（当前 URL 无凭据）；
3. **编码与分辨率**：`ffprobe rtsp://<ip>:554/stream0` 确认 H.264 / 1080p / 帧率；
4. **V8 停心跳是否断流**（插件会同时保心跳与 RTSP 会话，一般不受影响）；
5. UDP 丢包严重时切 `rtsp_transport=tcp` 是否稳定；
6. 长时间（一节课 40 分钟）稳定性与内存；
7. 拍照/旋转/矫正/切分辨率与宿主原有按钮的配合。

---

## 9. 已知限制

- 分辨率切换是**重建推流**（宿主 provider 契约如此），会有短暂黑屏；
- 画质滑块（亮度/对比度等）对 provider 摄像头**不可用**（片源设备也不支持这些命令，属预期）；
- 仅验证 x64；
- 官方原版宿主上插件可加载但**不会工作**（明确日志提示），这是设计如此（避免任何系统级副作用）。
