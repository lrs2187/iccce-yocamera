# YiouCamera 插件侧接入示例：从「虚拟 DirectShow 摄像头」切到「camera provider」

> 对应宿主 PR：`docs/PR.md` / `docs/camera-provider.patch`
> 本文为**伪代码级**接入示例，不需要真跑；`src/YiouCamera.*` 的正式改造由插件侧负责。

## 1. 两条路径的关系

| | 路径 A：虚拟 DirectShow 摄像头（现状） | 路径 B：camera provider（本 PR） |
|---|---|---|
| 宿主版本 | 任意（只要能被 DirectShow 枚举） | 需 API ≥ 1.14.0（打上本补丁的宿主） |
| 插件产物 | 原生 `vcam_filter.dll`（x64 COM） | 纯托管，无额外文件 |
| 安装 | `DllInstall` 注册 HKCU COM + 写 `DevicePath` | 无系统级安装 |
| 喂帧 | 命名共享内存 `Local\YoCameraVcamFrames` | `IPluginCameraFrameSink.Submit(bgr24, w, h, stride)` |
| 宿主预览 | VMR9 renderless + D3DImage | WPF `Image`（共享同一套缩放/旋转/镜像变换） |
| 宿主拍照 | D3DImage 回读 | 宿主缓存的最近一帧 |
| 系统可见性 | 全系统可见（相机/会议软件也能选到） | 仅宿主可见 |
| 卸载 | 反注册 COM、删 DLL | 插件卸载即消失 |
| 失败模式 | VMR9 分配器协商 / DevicePath 冲突 / 杀软拦截 | 纯托管，无驱动/COM |

**结论：保留两条路径，运行时自动选择。** 新宿主优先 provider；旧宿主继续 vcam。
两条路径共用同一个采集内核（`YiouCamera.Core` 的 `IFrameSource`），只是“帧出口”不同。

## 2. 插件侧结构（复用现有采集内核）

```
YiouBoothSession (Core：发现 → 控制 → 开流 → 解码)
    │  event Action<Frame> FrameReady     // BGR24 / top-down / stride=(w*3+3)&~3
    ▼
IFrameOutlet（插件新增的一个小接口，两个实现）
    ├─ SharedMemoryFrameOutlet   → 写 Local\YoCameraVcamFrames（路径 A，现状不变）
    └─ ProviderFrameOutlet       → sink.Submit(...)（路径 B，本 PR 新增）
```

插件启动时探测宿主能力，决定用哪个出口：

```csharp
public sealed class YiouBoothPlugin : PluginBase
{
    private ICameraProviderService _cameraProvider;   // 新宿主非 null
    private ProviderFrameOutlet _providerOutlet;
    private SharedMemoryFrameOutlet _shmOutlet;       // 旧路径（现有实现）

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);

        _cameraProvider = host.GetService<ICameraProviderService>();

        if (_cameraProvider != null)
        {
            // 路径 B：纯托管，注册即可，不碰注册表/COM
            _providerOutlet = new ProviderFrameOutlet();
            _cameraProvider.RegisterProvider(new YiouCameraProvider(_providerOutlet));
            Log("camera provider 模式（API " + HostApiRequirement.CurrentApiVersion + "）");
        }
        else
        {
            // 路径 A：旧宿主 → 维持现有 vcam 安装/注册流程
            EnsureVcamInstalled();          // 原安装脚本/DllInstall 逻辑
            _shmOutlet = new SharedMemoryFrameOutlet();
            Log("虚拟 DirectShow 摄像头模式（宿主不支持 camera provider）");
        }
    }

    public override void Shutdown()
    {
        _cameraProvider?.UnregisterProvider(_provider);
        // 路径 A 的卸载逻辑保持现状（保留 vcam 注册，供下次启动；或按用户设置反注册）
    }
}
```

## 3. Provider 实现（路径 B 的核心，约 80 行）

```csharp
public sealed class YiouCameraProvider : IPluginCameraProvider
{
    private readonly ProviderFrameOutlet _outlet;
    private YiouBoothSession _session;

    public YiouCameraProvider(ProviderFrameOutlet outlet) => _outlet = outlet;

    // 约定：ProviderId 直接用插件 manifest.Id，宿主卸载插件时按它兜底清理
    public string ProviderId => "com.yiou.boothcam";

    public IReadOnlyList<PluginCameraDescriptor> GetCameras() => new[]
    {
        new PluginCameraDescriptor
        {
            Id = "yiou-booth",
            // 建议带「（插件）」后缀，与普通 USB 摄像头区分；也是"上次选中摄像头"的持久化值
            DisplayName = "驿欧无线展台（插件）",
            Capabilities = new List<PluginCameraCapability>
            {
                // 宿主分辨率下拉框直接用这个列表；实际帧尺寸以推送为准
                new PluginCameraCapability { Width = 1920, Height = 1080, FrameRate = 30 },
                new PluginCameraCapability { Width = 1280, Height = 720,  FrameRate = 30 },
            },
            SupportsPropertyControl = false,   // 设备不支持画质控制 → 展台属性滑块自动置灰
        },
    };

    public bool StartCamera(string cameraId, IPluginCameraFrameSink sink)
    {
        if (cameraId != "yiou-booth") return false;
        _outlet.Attach(sink);                  // 采集线程后续直接 sink.Submit
        _ = Task.Run(async () =>
        {
            // 复用 Core 现有编排：发现 → 控制 → 开流 → FFmpeg 解码
            _session = await YiouBoothSession.StartAsync(localBind: null);
            _session.FrameReady += frame => _outlet.Push(frame);
            await _session.ConnectAndStreamAsync();
            // 长时间失败时推送「无信号」占位帧（与 vcam 路径一致），保证宿主预览不冻结
        });
        return true;                           // 先返回 true，宿主显示"正在启动摄像头…"直到首帧
    }

    public void StopCamera(string cameraId)
    {
        _outlet.Detach();                      // 之后 Push 直接丢弃，不再 Submit
        var session = _session;
        _session = null;
        session?.Stop();                       // 尽快返回（内部取消令牌 + Dispose）
    }

    public bool SetCameraFormat(string cameraId, int width, int height, int frameRate)
    {
        // 我们的 FFmpeg 解码输出可缩放：有能力就即时切换并返回 true；
        // 返回 false 也可以——宿主会 Stop→Start 重建推流，并按实际帧尺寸渲染。
        return _session?.RequestOutputSize(width, height, frameRate) ?? false;
    }
}
```

帧出口（线程安全、零额外分配的重要性）：

```csharp
public sealed class ProviderFrameOutlet
{
    private readonly object _lock = new object();
    private IPluginCameraFrameSink _sink;
    private bool _lastSubmitAccepted = true;

    public void Attach(IPluginCameraFrameSink sink) { lock (_lock) { _sink = sink; _lastSubmitAccepted = true; } }
    public void Detach() { lock (_lock) _sink = null; }

    public void Push(Frame frame)              // 采集线程调用
    {
        IPluginCameraFrameSink sink;
        lock (_lock) sink = _sink;
        if (sink == null) return;

        // frame.Buffer 已是 BGR24 / top-down；stride 必须是 (w*3+3)&~3
        // 宿主在 Submit 返回前拷贝，可以直接复用 Core 的帧缓冲
        if (!sink.Submit(frame.Buffer, frame.Width, frame.Height, frame.Stride, frame.TimestampMs))
        {
            lock (_lock) _sink = null;          // false = 宿主已停止，等效于 StopCamera
        }
    }
}
```

## 4. 「点插件按钮 → 直接打开带我们画面的展台」

两条路径的调用序列**完全相同**（这也是 provider 方案的最大优势：宿主侧一切照旧）：

```csharp
private void OpenBooth()
{
    var booth = Host.GetService<IVideoBoothService>();
    if (booth == null) { LogError("IVideoBoothService 未注册"); return; }

    // 1) 预选摄像头（值为 PluginCameraDescriptor.DisplayName，大小写不敏感匹配）
    string displayName = _cameraProvider != null
        ? "驿欧无线展台（插件）"      // 路径 B
        : "驿欧无线展台";             // 路径 A（vcam 的 FriendlyName，现有实现）
    Host.GetService<ISettingsService>()?
        .Set("Canvas.VideoPresenterLastCameraName", displayName);

    // 2) 打开内置视频展台（主窗口内联全屏预览）
    if (booth.IsActive) booth.Toggle();    // 已开着 → 先关再开，让预选生效
    booth.Toggle();
}
```

## 5. 两条路径的共存/切换策略

1. **运行时自动选择**：`Initialize` 里按 `GetService<ICameraProviderService>() != null` 决定路径，
   插件 UI 无需变化；用户无感。
2. **设置项显式开关（可选）**：插件设置页提供「接入方式：自动 / 插件摄像头（推荐）/ 虚拟摄像头」，
   供用户在 “新宿主 + 想给相机/会议软件也用” 时回退 vcam。
3. **vcam 保留但按需安装**：路径 B 下**不再自动注册** vcam（避免全系统可见 + OBS 冲突）；
   用户显式选择路径 A 或宿主过旧时才执行现有 `install.ps1` 逻辑。
4. **卸载干净**：路径 B 无系统残留；插件卸载时 `UnregisterProvider` + 宿主 `PluginManager` 兜底，
   下拉框立即消失。路径 A 仍按现有卸载脚本反注册 COM。
5. **迁移顺序建议**：
   - P1：先只加路径 B（宿主 API 可用时），vcam 逻辑原样保留 → 两条路径可同时存在；
   - P2：现场验证通过后，把“自动安装 vcam”降级为可选；
   - P3：上游 PR 合并发版后，插件市场版默认只走路径 B，vcam 仅作为旧宿主兼容。

## 6. 迁移时容易踩的点

- `DisplayName` 是 `canvas.videoPresenterLastCameraName` 的持久化值，**一旦发布就不要再改**，
  否则用户“上次选的摄像头”会失效回退到第一个。
- 分辨率下拉框来自 `Capabilities`；设备真实输出分辨率以推送帧为准，两者不一致时按实际帧渲染，
  PR 文档里已说明，不需要插件强行对齐。
- `StopCamera` 必须**快速返回**且返回后不再 `Submit`；不要在 `StopCamera` 里同步等待 RTSP 断链。
- `Submit` 是“最近一帧”语义，不需要自己做帧队列/背压；连续推比宿主渲染快只会丢旧帧。
- 帧格式固定 **BGR24 / top-down**（与 Core 的 `Frame.Buffer`、vcam 共享内存布局一致），
  不要推 RGB24、也不要 bottom-up。

（以上代码为示例，正式实现以 `src/YiouCamera.Plugin` 为准。）
