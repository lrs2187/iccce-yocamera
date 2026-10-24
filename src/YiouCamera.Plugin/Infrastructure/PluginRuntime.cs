using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Ink_Canvas.Plugins;
using YiouCamera.Plugin.CameraProvider;
using YiouCamera.Plugin.CoreIntegration;

namespace YiouCamera.Plugin.Infrastructure
{
    /// <summary>
    /// 插件运行时（单例，插件实例持有）：配置、日志、通知、虚拟摄像头、采集会话、设置页。
    /// 所有对外操作都自己吞异常，绝不让异常冒泡到宿主。
    /// </summary>
    internal sealed class PluginRuntime : IDisposable
    {
        public const string SettingsKeyLastCamera = "Canvas.VideoPresenterLastCameraName";

        private readonly IPluginHost _host;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile bool _disposed;
        private UI.SettingsView _settingsView;

        public PluginRuntime(IPluginHost host, string configFolder, string pluginFolder, string version, string pluginId)
        {
            _host = host;
            ConfigFolder = string.IsNullOrWhiteSpace(configFolder) ? "" : configFolder;
            PluginFolder = string.IsNullOrWhiteSpace(pluginFolder) ? "" : pluginFolder;
            Version = string.IsNullOrWhiteSpace(version) ? "0.1.0" : version;
            PluginId = string.IsNullOrWhiteSpace(pluginId) ? "com.yiou.boothcam" : pluginId.Trim();

            Config = PluginConfig.Load(
                ConfigFolder,
                msg => Log("配置: " + msg),
                (msg, ex) => LogError("配置: " + msg, ex));

            Booth = new BoothController(Log, LogError, RaiseStatus, () => Config);
            CameraProvider = new CameraProviderBridge(this);

            try { Directory.CreateDirectory(ConfigFolder); } catch { }
        }

        public IPluginHost Host { get { return _host; } }
        public PluginConfig Config { get; private set; }
        public string ConfigFolder { get; }
        public string PluginFolder { get; }
        public string Version { get; }
        public string PluginId { get; }
        public BoothController Booth { get; }

        /// <summary>camera provider 接入桥（唯一接入方式，纯托管）。</summary>
        public CameraProviderBridge CameraProvider { get; }

        /// <summary>宿主 IServiceProvider（用于运行时探测 provider API；旧宿主/不可用时返回 null）。</summary>
        public IServiceProvider ServiceProvider
        {
            get
            {
                try { return _host?.ServiceProvider; }
                catch { return null; }
            }
        }

        /// <summary>虚拟摄像头/会话/枚举状态变化（可能来自后台线程）。</summary>
        public event Action StatusChanged;

        // ---------------------------------------------------------------------
        // 日志 / 通知 / 服务
        // ---------------------------------------------------------------------
        public void Log(string message)
        {
            try
            {
                _host?.Log("[Yiou] " + message);
            }
            catch
            {
                // 日志失败也不允许抛给宿主。
            }
        }

        public void LogError(string message, Exception ex)
        {
            try
            {
                _host?.LogError("[Yiou] " + message, ex);
            }
            catch
            {
            }
        }

        public T GetService<T>() where T : class
        {
            try
            {
                return _host?.GetService<T>();
            }
            catch (Exception ex)
            {
                LogError("获取宿主服务 " + typeof(T).Name + " 失败", ex);
                return null;
            }
        }

        public void Notify(string title, string message, NotificationLevel level)
        {
            try
            {
                var notification = GetService<INotificationService>();
                notification?.Show(title, message, level);
            }
            catch (Exception ex)
            {
                LogError("发送通知失败: " + message, ex);
            }
        }

        public void RaiseStatus()
        {
            try
            {
                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LogError("StatusChanged 回调异常", ex);
            }
        }

        public bool SaveConfig()
        {
            return Config.Save(ConfigFolder, Log, LogError);
        }

        // ---------------------------------------------------------------------
        // 设置页
        // ---------------------------------------------------------------------
        public object GetSettingsView()
        {
            try
            {
                // 宿主可能在后台线程调 GetSettingsView（设置导航发现阶段）；WPF 控件必须在 UI 线程创建。
                var app = System.Windows.Application.Current;
                if (app != null && !app.Dispatcher.CheckAccess())
                {
                    return app.Dispatcher.Invoke(new Func<object>(GetSettingsView));
                }

                lock (_gate)
                {
                    if (_settingsView == null)
                    {
                        _settingsView = new UI.SettingsView(this);
                    }
                    return _settingsView;
                }
            }
            catch (Exception ex)
            {
                LogError("创建设置页失败", ex);
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // camera provider 注册（必须在 Initialize 阶段调用）
        // ---------------------------------------------------------------------
        public void InitializeCameraProvider()
        {
            try
            {
                CameraProvider.Initialize();
            }
            catch (Exception ex)
            {
                LogError("初始化 camera provider 失败（插件仅支持 provider 路径）", ex);
            }
        }

        // ---------------------------------------------------------------------
        // 后台初始化（Initialize 里只投递，不等待）
        // ---------------------------------------------------------------------
        public void StartBackgroundInit()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, _cts.Token).ConfigureAwait(false);
                    await OnStartupAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LogError("后台初始化失败", ex);
                }
            });
        }

        private async Task OnStartupAsync()
        {
            Log("后台初始化开始：插件版本 " + Version + "；Core 后端 " + Booth.BackendName
                + "；接入模式 " + CameraProvider.ModeDescription
                + (CameraProvider.IsProviderMode ? "；provider 摄像头名=" + CameraProvider.ProviderCameraName
                    : "；provider 探测失败原因=" + CameraProvider.RegistrationError));

            if (CameraProvider.IsProviderMode)
            {
                Log("camera provider 模式：零系统安装（不写 HKCU、不落 DLL；插件不注册任何系统级虚拟摄像头）。");
            }
            else
            {
                Log("宿主不支持 camera provider（需要 API ≥ 1.14.0）：本插件只支持 provider 路径，当前宿主无法接入；请升级到带 camera-provider 补丁的宿主。");
            }

            await RefreshCameraEnumerationAsync("启动检查").ConfigureAwait(false);

            if (string.Equals(Environment.GetEnvironmentVariable("YIOU_BOOTHCAM_SELFTEST"), "1", StringComparison.OrdinalIgnoreCase))
            {
                await RunSelfTestAsync().ConfigureAwait(false);
            }

            RaiseStatus();
        }

        // ---------------------------------------------------------------------
        // 状态刷新 / 枚举
        // ---------------------------------------------------------------------
        public IReadOnlyList<string> LastCameraNames { get; private set; } = Array.Empty<string>();

        public async Task RefreshStatusAsync()
        {
            try
            {
                await RefreshCameraEnumerationAsync("手动刷新").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogError("刷新状态失败", ex);
            }
            finally
            {
                RaiseStatus();
            }
        }

        private async Task<bool> RefreshCameraEnumerationAsync(string reason)
        {
            try
            {
                var camera = GetService<ICameraService>();
                if (camera == null)
                {
                    Log("宿主 ICameraService 不可用，跳过摄像头枚举校验（" + reason + "）。");
                    return false;
                }
                await camera.RefreshCameraListAsync().ConfigureAwait(false);
                var names = new List<string>();
                var cameras = camera.AvailableCameras;
                if (cameras != null)
                {
                    foreach (var item in cameras)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.Name)) names.Add(item.Name);
                    }
                }
                LastCameraNames = names;
                Log("摄像头枚举（" + reason + "）：共 " + names.Count + " 个 → " + string.Join(" | ", names));
                var expected = ResolveExpectedDeviceNames();
                var matched = names.FirstOrDefault(n => expected.Any(e => string.Equals(n, e, StringComparison.OrdinalIgnoreCase)));
                if (!string.IsNullOrEmpty(matched))
                {
                    Log("摄像头枚举校验：找到目标虚拟摄像头 → " + matched);
                }
                else
                {
                    Log("摄像头枚举校验：未找到目标虚拟摄像头（" + string.Join(" / ", expected) + "）。若刚完成注册，宿主需重启或重新打开展台。");
                }
                return true;
            }
            catch (Exception ex)
            {
                LogError("摄像头枚举校验失败（" + reason + "）", ex);
                return false;
            }
        }

        private IEnumerable<string> ResolveExpectedDeviceNames()
        {
            if (!CameraProvider.IsProviderMode) return Array.Empty<string>();
            return new[] { CameraProvider.ProviderCameraName };
        }

        // ---------------------------------------------------------------------
        // 打开宿主视频展台：预写「上次摄像头」（provider 摄像头名）→ Toggle()；采集由宿主 StartCamera 驱动。
        // provider 模式下由宿主自己回调 StartCamera，插件不提前启动采集。
        // ---------------------------------------------------------------------
        public void OpenBooth()
        {
            try
            {
                Log("打开视频展台：开始（接入模式=" + CameraProvider.ModeDescription + "）");

                string deviceName;
                if (CameraProvider.IsProviderMode)
                {
                    deviceName = CameraProvider.ProviderCameraName;
                    Log("provider 模式：预选摄像头名 '" + deviceName
                        + "'（provider 摄像头由宿主在展台打开后回调 StartCamera 启动采集）");
                }
                else
                {
                    LogError("当前宿主不支持 camera provider（API < 1.14.0）：插件只支持 provider 路径，已拒绝打开（不注册任何系统级虚拟摄像头）。", null);
                    Notify("驿欧无线展台",
                        "当前宿主不支持插件摄像头（camera provider，需要 API ≥ 1.14.0）。本插件不再提供系统级虚拟摄像头兼容模式；"
                        + "请升级到带 camera-provider 补丁的宿主。",
                        NotificationLevel.Warning);
                    return;
                }

                var settings = GetService<ISettingsService>();
                if (settings == null)
                {
                    LogError("ISettingsService 不可用，无法预写 Canvas.VideoPresenterLastCameraName", null);
                }
                else
                {
                    settings.Set<string>(SettingsKeyLastCamera, deviceName);
                    Log("已写入设置 " + SettingsKeyLastCamera + " = " + deviceName);
                }

                var booth = GetService<IVideoBoothService>();
                if (booth == null)
                {
                    LogError("IVideoBoothService 未注册（宿主版本过旧？）", null);
                    Notify("驿欧无线展台", "当前宿主没有视频展台服务，无法打开展台。", NotificationLevel.Error);
                    return;
                }

                var wasActive = false;
                try { wasActive = booth.IsActive; } catch { }
                if (wasActive)
                {
                    Log("展台已激活，先关闭一次以便重新枚举摄像头列表。");
                    booth.Toggle();
                }
                booth.Toggle();
                Log("已调用 IVideoBoothService.Toggle()（此前激活=" + wasActive + "）。");
                RaiseStatus();
            }
            catch (Exception ex)
            {
                LogError("打开视频展台失败", ex);
                Notify("驿欧无线展台", "打开展台失败：" + ex.Message, NotificationLevel.Error);
            }
        }

        // ---------------------------------------------------------------------
        // 手动截图：最近帧 → ICanvasInkService.InsertBitmap
        // ---------------------------------------------------------------------
        public void CaptureLatestFrameToCanvas()
        {
            try
            {
                var frame = Booth.TakeLatestFrame();
                if (frame == null)
                {
                    Log("手动截图：当前没有可用帧（采集会话未启动或未出流，Core 后端=" + Booth.BackendName + "）。");
                    Notify("驿欧无线展台", "当前没有可用画面：请先在设置页连接设备，或打开视频展台等待出画面。", NotificationLevel.Warning);
                    return;
                }

                var bitmap = CreateBitmapSource(frame);
                if (bitmap == null)
                {
                    LogError("手动截图：帧数据转换位图失败（" + frame.Width + "x" + frame.Height + "，stride=" + frame.Stride + "）。", null);
                    return;
                }

                var ink = GetService<ICanvasInkService>();
                if (ink == null)
                {
                    LogError("ICanvasInkService 未注册，无法插入画布。", null);
                    Notify("驿欧无线展台", "宿主画布服务不可用，无法插入截图。", NotificationLevel.Error);
                    return;
                }

                var ok = ink.InsertBitmap(bitmap);
                Log("手动截图：已插入画布=" + ok + "，尺寸 " + frame.Width + "x" + frame.Height
                    + "，帧号 " + frame.FrameIndex + "，时间戳 " + frame.TimestampMs + "ms。");
                if (!ok)
                {
                    Notify("驿欧无线展台", "插入画布失败（宿主返回 false）。", NotificationLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                LogError("手动截图插入画布失败", ex);
                Notify("驿欧无线展台", "截图失败：" + ex.Message, NotificationLevel.Error);
            }
        }

        private static BitmapSource CreateBitmapSource(FrameSnapshot frame)
        {
            if (frame.Buffer == null || frame.Width <= 0 || frame.Height <= 0) return null;
            int stride = frame.Stride > 0 ? frame.Stride : ((frame.Width * 3 + 3) & ~3);
            int required = stride * frame.Height;
            if (frame.Buffer.Length < required)
            {
                stride = (frame.Width * 3 + 3) & ~3;
                required = stride * frame.Height;
                if (frame.Buffer.Length < required) return null;
            }
            var bitmap = BitmapSource.Create(
                frame.Width, frame.Height, 96, 96,
                PixelFormats.Bgr24, null, frame.Buffer, stride);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>自测用：最近帧 → 位图 → ICanvasInkService.InsertBitmap（跑完 Undo 保持画布干净）。</summary>
        private bool TryInsertFrameToCanvas(FrameSnapshot frame)
        {
            try
            {
                return RunOnUi(() =>
                {
                    var bitmap = CreateBitmapSource(frame);
                    if (bitmap == null)
                    {
                        Log("SELFTEST: 帧转位图失败（" + frame.Width + "x" + frame.Height + "）");
                        return false;
                    }
                    var ink = GetService<ICanvasInkService>();
                    if (ink == null)
                    {
                        Log("SELFTEST: ICanvasInkService 不可用");
                        return false;
                    }
                    var ok = ink.InsertBitmap(bitmap);
                    Log("SELFTEST: InsertBitmap=" + ok + "（" + frame.Width + "x" + frame.Height + "）");
                    if (ok)
                    {
                        try
                        {
                            ink.Undo();
                            Log("SELFTEST: 已 Undo 撤销测试插入（保持画布干净）。");
                        }
                        catch (Exception ex)
                        {
                            Log("SELFTEST: 测试插入后 Undo 失败：" + ex.Message);
                        }
                    }
                    return ok;
                });
            }
            catch (Exception ex)
            {
                LogError("SELFTEST: 截图插入画布异常", ex);
                return false;
            }
        }

        private bool RunOnUi(Func<bool> action)
        {
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.CheckAccess()) return action();
            return app.Dispatcher.Invoke(action);
        }

        // ---------------------------------------------------------------------
        // 自测（YIOU_BOOTHCAM_SELFTEST=1 时由后台初始化调用；供无人值守验收）
        // ---------------------------------------------------------------------
        private async Task RunSelfTestAsync()
        {
            var pass = true;
            Log("SELFTEST: ===== 开始 =====");
            try
            {
                Log("SELFTEST: 插件版本=" + Version + "；配置目录=" + ConfigFolder + "；插件目录=" + PluginFolder);
                Log("SELFTEST: 接入模式=" + CameraProvider.ModeDescription
                    + "；provider 摄像头名=" + CameraProvider.ProviderCameraName
                    + "；provider 探测失败原因='" + CameraProvider.RegistrationError + "'");
                Log("SELFTEST: 配置 = DeviceIp='" + Config.DeviceIp + "', LocalBindIp='" + Config.LocalBindIp
                    + "', PairingCode.Length=" + (Config.PairingCode ?? "").Length
                    + ", Resolution=" + Config.Resolution + ", FrameRate=" + Config.SafeFrameRate
                    + ", FfmpegPath=" + (Config.FfmpegPath ?? ""));
                Log("SELFTEST: Core 后端=" + Booth.BackendName + "，可用=" + Booth.IsAvailable);

                bool providerMode = CameraProvider.IsProviderMode;
                string expectedDeviceName = providerMode ? CameraProvider.ProviderCameraName : null;

                Log("SELFTEST: [1] provider 注册状态：已注册=" + providerMode
                    + "，ProviderId=" + CameraProvider.ProviderId
                    + "，摄像头名=''" + CameraProvider.ProviderCameraName + "''");
                pass &= providerMode;

                Log("SELFTEST: [4] 摄像头枚举校验（宿主 ICameraService 合并后的列表）...");
                await RefreshCameraEnumerationAsync("自测").ConfigureAwait(false);
                var names = LastCameraNames;
                if (!string.IsNullOrEmpty(expectedDeviceName))
                {
                    bool enumerated = names.Any(n => string.Equals(n, expectedDeviceName, StringComparison.OrdinalIgnoreCase));
                    Log("SELFTEST: 枚举结果名字=" + string.Join(" | ", names) + "；目标设备=''" + expectedDeviceName + "'' 命中=" + enumerated);
                    pass &= enumerated;
                }
                else
                {
                    Log("SELFTEST: 枚举结果名字=" + string.Join(" | ", names) + "；provider 未注册，跳过目标设备断言。");
                }

                Log("SELFTEST: [5] 设置项链路 ISettingsService.Set/Get ...");
                try
                {
                    var settings = GetService<ISettingsService>();
                    if (settings == null)
                    {
                        Log("SELFTEST: ISettingsService 不可用（跳过）");
                    }
                    else
                    {
                        var deviceName = string.IsNullOrEmpty(expectedDeviceName)
                            ? CameraProvider.ProviderCameraName
                            : expectedDeviceName;
                        settings.Set<string>(SettingsKeyLastCamera, deviceName);
                        var readBack = settings.Get<string>(SettingsKeyLastCamera);
                        Log("SELFTEST: " + SettingsKeyLastCamera + " 写入='" + deviceName + "'，读回='" + readBack + "'，一致="
                            + string.Equals(deviceName, readBack, StringComparison.Ordinal));
                        pass &= string.Equals(deviceName, readBack, StringComparison.Ordinal);
                    }
                }
                catch (Exception ex)
                {
                    LogError("SELFTEST: 设置项链路异常", ex);
                    pass = false;
                }

                Log("SELFTEST: [6] 采集会话（编译了真实 Core 且显式开启时）...");
                if (BoothCoreSessionFactory.Available && string.Equals(Environment.GetEnvironmentVariable("YIOU_BOOTHCAM_SELFTEST_SESSION"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var request = new CaptureRequest
                        {
                            Width = Config.Width,
                            Height = Config.Height,
                            FrameRate = Config.SafeFrameRate,
                            // provider 路径：帧由宿主 sink 消费，插件不写共享内存。
                            UseSharedMemory = false,
                        };
                        await Booth.StartAsync(request).ConfigureAwait(false);
                        var running = await Booth.WaitForRunningAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                        Log("SELFTEST: 会话启动，Running=" + running + "，State=" + Booth.StateText + "，LastError=" + Booth.LastError);
                        var frame = Booth.TakeLatestFrame();
                        Log("SELFTEST: 最近帧=" + (frame == null ? "null" : (frame.Width + "x" + frame.Height + " stride=" + frame.Stride + " bytes=" + frame.Buffer.Length + " idx=" + frame.FrameIndex)));
                        var screenshotOk = frame != null && TryInsertFrameToCanvas(frame);
                        Log("SELFTEST: 手动截图链路（最近帧 → ICanvasInkService.InsertBitmap）=" + screenshotOk);
                        await Booth.StopAsync().ConfigureAwait(false);
                        Log("SELFTEST: 会话已停止。");
                        Log("SELFTEST: 会话测试结果：Running=" + running + "，Frame=" + (frame != null) + "，Screenshot=" + screenshotOk);
                        pass &= running && frame != null && screenshotOk;
                    }
                    catch (Exception ex)
                    {
                        LogError("SELFTEST: 采集会话测试异常", ex);
                        pass = false;
                    }
                }
                else
                {
                    Log("SELFTEST: 跳过采集会话测试（编译了 Core=" + BoothCoreSessionFactory.Available
                        + "；无真机时正常；设 YIOU_BOOTHCAM_SELFTEST_SESSION=1 可强制测试）。");
                }

                Log("SELFTEST: [7] 打开视频展台链路（仅预写设置，不实际 Toggle，避免无人值守时进入全屏）...");
                try
                {
                    var booth = GetService<IVideoBoothService>();
                    Log("SELFTEST: IVideoBoothService 可用=" + (booth != null) + "，IsActive=" + (booth != null ? booth.IsActive.ToString() : "n/a")
                        + "；接入模式=" + (providerMode ? "provider" : "unsupported-host"));
                    Log("SELFTEST: provider 统计=" + CameraProvider.GetStats());
                }
                catch (Exception ex)
                {
                    LogError("SELFTEST: 展台服务检查异常", ex);
                }
            }
            catch (Exception ex)
            {
                pass = false;
                LogError("SELFTEST: 未捕获异常", ex);
            }
            Log("SELFTEST: ===== 结束，结果 " + (pass ? "PASS" : "FAIL") + " =====");
        }

        // ---------------------------------------------------------------------
        // 网络接口枚举（设置页用）
        // ---------------------------------------------------------------------
        public static IReadOnlyList<string> GetLocalIpv4Addresses()
        {
            var result = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = addr.Address.ToString();
                        if (!result.Contains(ip)) result.Add(ip);
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        /// <summary>尝试从厂商配置文件读取 8 位配对码。</summary>
        public static string TryReadPairingCodeFromYocamSet()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "YOFiles", "YOCamSet.xml");
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path);
                try
                {
                    var doc = XDocument.Parse(text);
                    var element = doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "PinCode", StringComparison.OrdinalIgnoreCase));
                    if (element != null && !string.IsNullOrWhiteSpace(element.Value)) return element.Value.Trim();
                }
                catch
                {
                }
                var match = System.Text.RegularExpressions.Regex.Match(text, @"PinCode\s*[=:>\s]*""?(\d{4,12})");
                if (match.Success) return match.Groups[1].Value;
            }
            catch
            {
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            // provider 先注销（宿主会停掉活动推流），再停采集会话。
            try { CameraProvider.Dispose(); } catch (Exception ex) { LogError("释放 camera provider 失败", ex); }
            try { Booth.Dispose(); } catch (Exception ex) { LogError("释放采集会话失败", ex); }
            try { _cts.Dispose(); } catch { }
        }
    }
}
