#if YIOU_HAS_CORE
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using YiouCamera.Core;
using YiouCamera.Plugin.CameraProvider;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin.CoreIntegration
{
    /// <summary>
    /// 真实 Core 会话适配器（仅在引用 src/YiouCamera.Core 时编译）。
    /// 用法严格按 CONTRACTS §3 + core-dev 冻结签名：
    ///   new YiouBoothSession(YiouBoothSessionOptions, Action&lt;string&gt; log)
    ///   session.StartAsync(ct) 立即返回，后台完成 发现→TCP8888→心跳→开流→ffmpeg→SHM/帧回调；
    ///   session.LatestFrame / TryGetLatestFrame(out Frame) 取最近帧；
    ///   session.FrameReady 是 provider 路径的帧出口（UseSharedMemory=false 时不写共享内存）。
    /// </summary>
    internal sealed class RealBoothCoreSession : IBoothCoreSession
    {
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly Action _statusChanged;
        private readonly FrameRef _frameRef = new FrameRef();

        private YiouBoothSession _session;
        private string _stateText = "Idle";
        private string _lastError = "";
        private int _disposed;

        public RealBoothCoreSession(
            Action<string> log,
            Action<string, Exception> logError,
            Action statusChanged)
        {
            _log = log ?? (_ => { });
            _logError = logError ?? ((_, __) => { });
            _statusChanged = statusChanged ?? (() => { });
        }

        public bool IsAvailable { get { return true; } }

        public string BackendName { get { return "YiouCamera.Core / YiouBoothSession"; } }

        public bool IsRunning
        {
            get
            {
                var s = _session;
                return s != null && s.State == BoothSessionState.Running;
            }
        }

        public string StateText { get { return _stateText ?? "Idle"; } }

        public string LastError { get { return _lastError ?? ""; } }

        public event Action StatusChanged;

        public event Action<FrameRef> FrameAvailable;

        public async Task StartAsync(PluginConfig config, CaptureRequest request, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_session != null) return;
            if (config == null) config = new PluginConfig();
            if (request == null)
            {
                request = new CaptureRequest
                {
                    Width = config.Width,
                    Height = config.Height,
                    FrameRate = config.SafeFrameRate,
                    UseSharedMemory = true,
                };
            }

            try
            {
                var options = new YiouBoothSessionOptions
                {
                    Width = request.Width > 0 ? request.Width : 1280,
                    Height = request.Height > 0 ? request.Height : 720,
                    TargetFps = request.FrameRate > 0 ? request.FrameRate : 30,
                    FfmpegPath = config.FfmpegPath ?? "",
                    UseSharedMemory = request.UseSharedMemory,
                };

                if (!string.IsNullOrWhiteSpace(config.PairingCode))
                {
                    var pin = config.PairingCode.Trim();
                    options.PinCode = pin;
                    _log("Core: 使用配置的配对码（长度 " + pin.Length + "）。");
                }

                if (IPAddress.TryParse((config.LocalBindIp ?? "").Trim(), out var bindIp))
                {
                    options.LocalBindAddress = bindIp;
                    _log("Core: 本机绑定网卡 IP = " + bindIp);
                }

                if (IPAddress.TryParse((config.DeviceIp ?? "").Trim(), out var deviceIp))
                {
                    options.DeviceIpOverride = deviceIp;
                    _log("Core: 跳过发现，直接使用设备 IP = " + deviceIp);
                }

                var session = new YiouBoothSession(options, msg => _log("Core: " + msg));
                session.StateChanged += OnStateChanged;
                session.Faulted += OnFaulted;
                session.FrameReady += OnCoreFrameReady;
                _session = session;
                _stateText = session.State.ToString();
                Raise();

                _log("Core: StartAsync 调用（" + options.Width + "x" + options.Height + "@" + options.TargetFps
                     + "，共享内存=" + options.UseSharedMemory + "）。");
                await session.StartAsync(ct).ConfigureAwait(false);
                _log("Core: StartAsync 已返回（后台编排中），当前状态 " + session.State + "。");
                Raise();
            }
            catch (OperationCanceledException)
            {
                _log("Core: StartAsync 已取消。");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logError("Core: 启动采集会话失败", ex);
                Raise();
            }
        }

        public async Task StopAsync()
        {
            var session = _session;
            _session = null;
            if (session == null) return;
            try
            {
                session.StateChanged -= OnStateChanged;
                session.Faulted -= OnFaulted;
                session.FrameReady -= OnCoreFrameReady;
                await session.StopAsync().ConfigureAwait(false);
                _log("Core: 采集会话已停止。");
            }
            catch (Exception ex)
            {
                _logError("Core: 停止采集会话失败", ex);
            }
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError("Core: 释放采集会话失败", ex);
            }
            finally
            {
                _stateText = "Stopped";
                Raise();
            }
        }

        public async Task<bool> WaitForRunningAsync(TimeSpan timeout, CancellationToken ct)
        {
            var session = _session;
            if (session == null) return false;
            try
            {
                return await session.WaitForRunningAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logError("Core: 等待 Running 状态失败", ex);
                return false;
            }
        }

        public FrameSnapshot TakeLatestFrame()
        {
            var session = _session;
            if (session == null) return null;
            try
            {
                Frame frame = null;
                if (!session.TryGetLatestFrame(out frame) || frame == null) return null;
                if (frame.Buffer == null || frame.Buffer.Length == 0) return null;

                // 拷贝一份，避免 Core 复用缓冲导致插入画布时花屏/撕裂。
                var copy = new byte[frame.Buffer.Length];
                Buffer.BlockCopy(frame.Buffer, 0, copy, 0, copy.Length);
                return new FrameSnapshot
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    Stride = frame.Stride,
                    Buffer = copy,
                    FrameIndex = frame.FrameIndex,
                    TimestampMs = frame.TimestampMs,
                };
            }
            catch (Exception ex)
            {
                _logError("Core: 读取最近帧失败", ex);
                return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                var session = _session;
                _session = null;
                if (session != null)
                {
                    session.StateChanged -= OnStateChanged;
                    session.Faulted -= OnFaulted;
                    session.FrameReady -= OnCoreFrameReady;
                    try { session.StopAsync().Wait(1500); } catch { }
                    try { session.DisposeAsync().AsTask().Wait(1500); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logError("Core: Dispose 异常", ex);
            }
        }

        private void OnCoreFrameReady(Frame frame)
        {
            var handler = FrameAvailable;
            if (handler == null || frame == null || frame.Buffer == null) return;
            // 复用 FrameRef 实例（回调同步消费；provider sink 在 Submit 返回前完成拷贝）。
            _frameRef.Buffer = frame.Buffer;
            _frameRef.Width = frame.Width;
            _frameRef.Height = frame.Height;
            _frameRef.Stride = frame.Stride;
            _frameRef.FrameIndex = frame.FrameIndex;
            _frameRef.TimestampMs = frame.TimestampMs;
            try
            {
                handler(_frameRef);
            }
            catch (Exception ex)
            {
                _logError("Core: 帧回调处理异常", ex);
            }
        }

        private void OnStateChanged(BoothSessionState state)
        {
            _stateText = state.ToString();
            _log("Core: 状态变化 → " + _stateText);
            Raise();
        }

        private void OnFaulted(string message)
        {
            _lastError = message ?? "";
            _logError("Core: 采集会话故障: " + _lastError, null);
            Raise();
        }

        private void Raise()
        {
            try { StatusChanged?.Invoke(); } catch { }
            try { _statusChanged(); } catch { }
        }
    }
}
#endif
