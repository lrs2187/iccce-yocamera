using System;
using System.Threading;
using System.Threading.Tasks;
using Ink_Canvas.Plugins;
using YiouCamera.Plugin.CoreIntegration;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin.CameraProvider
{
    /// <summary>
    /// camera provider 接入桥：
    /// - 启动时运行时探测宿主是否支持 <c>ICameraProviderService</c>（API ≥ 1.14.0）：
    ///   支持 → 注册一个 <c>IPluginCameraProvider</c>（纯托管，帧由 Core 的 FrameReady → sink.Submit）；
    ///   不支持 → 旧宿主/无 provider 接口时给出“不支持”状态（不注册任何系统级虚拟摄像头、不抛异常）。
    /// - 宿主（旧版）加载插件时不会触发缺失类型的 JIT：provider 类型只在
    ///   <see cref="PatchedProviderApi"/>（YIOU_HAS_CAMERA_PROVIDER 条件编译）里出现，
    ///   本类仅通过 try/catch 调用它。
    /// - 实现 <see cref="ICameraProviderHost"/>：把 provider 的启停/格式请求翻译成 Core 采集会话操作。
    /// </summary>
    internal sealed class CameraProviderBridge : ICameraProviderHost, IDisposable
    {
        public const string DefaultDisplayName = "驿欧无线展台（插件）";
        public const string ProviderCameraId = "yiou-booth";

        private readonly PluginRuntime _rt;
        private readonly object _gate = new object();
        private readonly object _statsGate = new object();

        private int _targetWidth = 1280;
        private int _targetHeight = 720;
        private int _targetFrameRate = 30;
        private bool _captureRequested;
        private bool _dirty;
        private int _workerBusy;
        private volatile bool _disposed;

        private long _submitted;
        private long _accepted;
        private long _rejected;
        private long _dropped;
        private long _bytesAccepted;
        private long _fpsWindowFrames;
        private long _fpsWindowStartTicks = Environment.TickCount64;
        private double _fps;
        private string _lastError = "";

        private object _providerObject;

        public CameraProviderBridge(PluginRuntime runtime)
        {
            _rt = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _rt.Booth.FrameAvailable += OnBoothFrame;
        }

        // ------------------------------------------------------------------
        // 探测 / 注册 / 注销
        // ------------------------------------------------------------------
        public CameraAccessMode Mode { get; private set; } = CameraAccessMode.UnsupportedHost;

        public bool IsProviderMode { get { return Mode == CameraAccessMode.Provider; } }

        public string ProviderCameraName { get; private set; } = DefaultDisplayName;

        public string RegistrationError { get; private set; } = "";

        public string ModeDescription
        {
            get
            {
                return IsProviderMode
                    ? "插件摄像头（camera provider，纯托管，仅宿主内可见）"
                    : "宿主不支持 camera provider（插件不可用；本插件不提供系统级虚拟摄像头兼容模式）";
            }
        }

        /// <summary>在 Initialize 阶段调用（宿主要求 provider 注册发生在 Initialize 内）。</summary>
        public void Initialize()
        {
            if (_disposed) return;
            try
            {
                if (TryRegisterPlatform(out string error))
                {
                    Mode = CameraAccessMode.Provider;
                    ProviderCameraName = DefaultDisplayName;
                    _rt.Log("接入模式：camera provider 注册成功，摄像头名「" + ProviderCameraName + "」（ProviderId=" + ProviderId + "）");
                }
                else
                {
                    Mode = CameraAccessMode.UnsupportedHost;
                    RegistrationError = error ?? "未知原因";
                    _rt.Log("接入模式：宿主不支持 camera provider（" + RegistrationError + "）");
                }
            }
            catch (Exception ex)
            {
                Mode = CameraAccessMode.UnsupportedHost;
                RegistrationError = ex.GetType().Name + ": " + ex.Message;
                _rt.LogError("camera provider 探测失败（宿主不支持）", ex);
            }
        }

        private bool TryRegisterPlatform(out string error)
        {
            error = null;
            try
            {
                if (ProviderReflectionApi.TryRegister(this, out object provider, out string apiError))
                {
                    _providerObject = provider;
                    return true;
                }
                error = apiError;
                return false;
            }
            catch (Exception ex)
            {
                // 官方旧宿主没有 provider 类型时这里理论上不会抛（反射探测返回 null），
                // 仍兜底一次，确保任何异常都不会冒泡到宿主。
                error = "运行时探测 provider API 失败：" + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private void TryUnregisterPlatform()
        {
            try
            {
                if (_providerObject != null)
                {
                    ProviderReflectionApi.TryUnregister(this, _providerObject);
                    _providerObject = null;
                }
            }
            catch (Exception ex)
            {
                _rt.LogError("camera provider 注销失败（宿主可能已卸载）", ex);
            }
        }

        // ------------------------------------------------------------------
        // ICameraProviderHost：provider → 采集会话
        // ------------------------------------------------------------------
        public string ProviderId { get { return _rt.PluginId; } }

        public string CameraDisplayName { get { return ProviderCameraName; } }

        public IPluginHost Host { get { return _rt.Host; } }

        public void Log(string message)
        {
            _rt.Log("camera provider: " + message);
        }

        public void LogError(string message, Exception ex)
        {
            _rt.LogError("camera provider: " + message, ex);
        }

        public int TargetWidth { get { lock (_gate) return _targetWidth; } }

        public int TargetHeight { get { lock (_gate) return _targetHeight; } }

        public int TargetFrameRate { get { lock (_gate) return _targetFrameRate; } }

        public bool SinkActive { get { lock (_gate) return _captureRequested; } }

        public IServiceProvider ServiceProvider { get { return _rt.ServiceProvider; } }

        public event Action<FrameRef> FrameArrived;

        public bool BeginCapture(int width, int height, int frameRate)
        {
            try
            {
                if (_disposed) return false;
                if (!_rt.Booth.IsAvailable)
                {
                    RecordError("Core 不可用，无法启动采集");
                    _rt.LogError("camera provider: 启动失败——Core 采集会话未编译进来", null);
                    return false;
                }

                lock (_gate)
                {
                    if (width > 0) _targetWidth = width;
                    if (height > 0) _targetHeight = height;
                    if (frameRate > 0) _targetFrameRate = frameRate;
                    _captureRequested = true;
                    _dirty = true;
                }
                _rt.Log("camera provider: 宿主请求开始推流（目标 " + TargetWidth + "x" + TargetHeight + "@" + TargetFrameRate + "）");
                ScheduleCaptureWorker();
                return true;
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                _rt.LogError("camera provider: BeginCapture 异常", ex);
                return false;
            }
        }

        public void EndCapture()
        {
            bool was;
            lock (_gate)
            {
                was = _captureRequested;
                _captureRequested = false;
                _dirty = false;
            }
            if (!was) return;
            _rt.Log("camera provider: 宿主请求停止推流（此后不再 Submit）");
            // 不在调用线程同步等待网络收尾：StopCamera 必须快速返回。
            _ = Task.Run(async () =>
            {
                try
                {
                    await _rt.Booth.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _rt.LogError("camera provider: 停止采集会话失败", ex);
                }
            });
        }

        public void SetTargetFormat(int width, int height, int frameRate)
        {
            if (width <= 0 || height <= 0) return;
            bool changed = false;
            lock (_gate)
            {
                int fps = frameRate > 0 ? frameRate : 30;
                if (width != _targetWidth || height != _targetHeight || fps != _targetFrameRate)
                {
                    _targetWidth = width;
                    _targetHeight = height;
                    _targetFrameRate = fps;
                    _dirty = true;
                    changed = true;
                }
            }
            if (changed)
            {
                _rt.Log("camera provider: 目标格式切换 → " + width + "x" + height + "@" + (frameRate > 0 ? frameRate : 30));
                if (SinkActive) ScheduleCaptureWorker();
            }
        }

        public void RecordSubmit(bool accepted, int byteCount)
        {
            Interlocked.Increment(ref _submitted);
            if (!accepted)
            {
                Interlocked.Increment(ref _rejected);
                return;
            }
            Interlocked.Increment(ref _accepted);
            if (byteCount > 0) Interlocked.Add(ref _bytesAccepted, byteCount);
            long now = Environment.TickCount64;
            Interlocked.Increment(ref _fpsWindowFrames);
            lock (_statsGate)
            {
                long elapsed = now - _fpsWindowStartTicks;
                if (elapsed >= 1000)
                {
                    _fps = _fpsWindowFrames * 1000.0 / elapsed;
                    _fpsWindowFrames = 0;
                    _fpsWindowStartTicks = now;
                }
            }
        }

        public void RecordError(string message)
        {
            _lastError = message ?? "";
        }

        private void OnBoothFrame(FrameRef frame)
        {
            var handler = FrameArrived;
            if (handler == null)
            {
                if (IsProviderMode) Interlocked.Increment(ref _dropped);
                return;
            }
            try
            {
                handler(frame);
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                _rt.LogError("camera provider: 推帧处理异常", ex);
            }
        }

        // ------------------------------------------------------------------
        // 采集启动/重启（去抖 + 串行化）
        // ------------------------------------------------------------------
        private void ScheduleCaptureWorker()
        {
            if (Interlocked.Exchange(ref _workerBusy, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(120).ConfigureAwait(false);

                        CaptureRequest request;
                        lock (_gate)
                        {
                            if (_disposed || !_captureRequested || !_dirty) return;
                            _dirty = false;
                            request = new CaptureRequest
                            {
                                Width = _targetWidth,
                                Height = _targetHeight,
                                FrameRate = _targetFrameRate,
                                UseSharedMemory = false, // provider 路径不写共享内存，避免任何共享内存写入
                            };
                        }

                        try
                        {
                            await _rt.Booth.StopAsync().ConfigureAwait(false);
                            await _rt.Booth.StartAsync(request).ConfigureAwait(false);
                            _rt.Log("camera provider: 采集会话已启动（" + request + "）");
                        }
                        catch (Exception ex)
                        {
                            RecordError(ex.Message);
                            _rt.LogError("camera provider: 启动采集会话失败（" + request + "）", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _rt.LogError("camera provider: 采集调度异常", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _workerBusy, 0);
                    // 退出瞬间如果又有新的格式/启停请求，重新拉起 worker。
                    bool needMore;
                    lock (_gate)
                    {
                        needMore = !_disposed && _captureRequested && _dirty;
                    }
                    if (needMore) ScheduleCaptureWorker();
                }
            });
        }

        // ------------------------------------------------------------------
        // 诊断 / 释放
        // ------------------------------------------------------------------
        public ProviderStatsSnapshot GetStats()
        {
            lock (_gate)
            {
                return new ProviderStatsSnapshot
                {
                    FramesSubmitted = Interlocked.Read(ref _submitted),
                    FramesAccepted = Interlocked.Read(ref _accepted),
                    FramesRejected = Interlocked.Read(ref _rejected),
                    FramesDropped = Interlocked.Read(ref _dropped),
                    BytesAccepted = Interlocked.Read(ref _bytesAccepted),
                    Fps = _fps,
                    LastError = _lastError ?? "",
                    SinkActive = _captureRequested,
                    TargetWidth = _targetWidth,
                    TargetHeight = _targetHeight,
                    TargetFrameRate = _targetFrameRate,
                };
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _rt.Booth.FrameAvailable -= OnBoothFrame;
            }
            catch
            {
            }
            try
            {
                EndCapture();
            }
            catch
            {
            }
            TryUnregisterPlatform();
        }
    }
}
