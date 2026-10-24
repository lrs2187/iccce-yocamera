using System;
using System.Threading;
using System.Threading.Tasks;
using YiouCamera.Plugin.CameraProvider;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin.CoreIntegration
{
    /// <summary>
    /// 采集会话生命周期管理：按需启停、串行化、异常只记日志。
    /// UI/工具栏操作与 camera provider 都走这里，保证 Initialize 阶段零阻塞。
    /// </summary>
    internal sealed class BoothController : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly Action _statusChanged;
        private readonly Func<PluginConfig> _configProvider;

        private IBoothCoreSession _session;
        private IBoothCoreSession _lastSession;
        private CancellationTokenSource _sessionCts;
        private int _disposed;
        private string _lastError = "";

        public BoothController(
            Action<string> log,
            Action<string, Exception> logError,
            Action statusChanged,
            Func<PluginConfig> configProvider)
        {
            _log = log ?? (_ => { });
            _logError = logError ?? ((_, __) => { });
            _statusChanged = statusChanged ?? (() => { });
            _configProvider = configProvider ?? (() => new PluginConfig());
        }

        /// <summary>Core 解出一帧（供 camera provider 推给宿主）。</summary>
        public event Action<FrameRef> FrameAvailable;

        public bool IsAvailable
        {
            get
            {
                var s = GetSession();
                return s != null ? s.IsAvailable : BoothCoreSessionFactory.Available;
            }
        }

        public bool IsRunning
        {
            get
            {
                var s = GetSession();
                return s != null && s.IsRunning;
            }
        }

        public string BackendName
        {
            get
            {
                var s = GetSession();
                return s != null ? s.BackendName : "未创建";
            }
        }

        public string StateText
        {
            get
            {
                var s = GetSession();
                return s != null ? s.StateText : "未启动";
            }
        }

        public string LastError
        {
            get
            {
                var s = GetSession();
                if (s != null && !string.IsNullOrEmpty(s.LastError)) return s.LastError;
                return _lastError ?? "";
            }
        }

        private IBoothCoreSession GetSession()
        {
            lock (_gate)
            {
                return _session ?? _lastSession;
            }
        }

        private IBoothCoreSession EnsureSession()
        {
            lock (_gate)
            {
                if (_session != null) return _session;
                var session = BoothCoreSessionFactory.Create(_log, _logError, _statusChanged);
                session.StatusChanged += OnSessionStatusChanged;
                session.FrameAvailable += OnSessionFrameAvailable;
                _session = session;
                _lastSession = session;
                return session;
            }
        }

        private void OnSessionStatusChanged()
        {
            try { _statusChanged(); } catch { }
        }

        private void OnSessionFrameAvailable(FrameRef frame)
        {
            var handler = FrameAvailable;
            if (handler == null) return;
            try { handler(frame); } catch (Exception ex) { _logError("转发 Core 帧回调异常", ex); }
        }

        /// <summary>启动会话（幂等；立即返回，后台完成发现/连接/开流）。默认按配置尺寸 + 写共享内存。</summary>
        public Task StartAsync()
        {
            var config = _configProvider() ?? new PluginConfig();
            return StartAsync(new CaptureRequest
            {
                Width = config.Width,
                Height = config.Height,
                FrameRate = config.SafeFrameRate,
                UseSharedMemory = true,
            });
        }

        /// <summary>按指定采集请求启动会话（camera provider 用 UseSharedMemory=false）。</summary>
        public Task StartAsync(CaptureRequest request)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
                if (request == null) return StartAsync();
                var session = EnsureSession();
                var config = _configProvider() ?? new PluginConfig();
                lock (_gate)
                {
                    if (_sessionCts == null)
                    {
                        _sessionCts = new CancellationTokenSource();
                    }
                }
                _log("开始启动采集会话（" + request + "）。");
                _lastError = "";
                return session.StartAsync(config, request, _sessionCts.Token);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logError("启动采集会话失败", ex);
                return Task.CompletedTask;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                IBoothCoreSession session;
                CancellationTokenSource cts;
                lock (_gate)
                {
                    session = _session;
                    cts = _sessionCts;
                    _sessionCts = null;
                }
                try { cts?.Cancel(); } catch { }
                try { cts?.Dispose(); } catch { }
                cts = null;
                if (session == null) return;
                session.StatusChanged -= OnSessionStatusChanged;
                session.FrameAvailable -= OnSessionFrameAvailable;
                _log("停止采集会话...");
                await session.StopAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (ReferenceEquals(_session, session)) _session = null;
                }
                try { session.Dispose(); } catch { }
                _log("采集会话已停止。");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logError("停止采集会话失败", ex);
            }
            finally
            {
                try { _statusChanged(); } catch { }
            }
        }

        public Task<bool> WaitForRunningAsync(TimeSpan timeout)
        {
            var session = GetSession();
            if (session == null) return Task.FromResult(false);
            return session.WaitForRunningAsync(timeout, CancellationToken.None);
        }

        /// <summary>取最近一帧（线程安全；无帧返回 null）。</summary>
        public FrameSnapshot TakeLatestFrame()
        {
            var session = GetSession();
            if (session == null) return null;
            try
            {
                return session.TakeLatestFrame();
            }
            catch (Exception ex)
            {
                _logError("读取最近帧失败", ex);
                return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                IBoothCoreSession session;
                CancellationTokenSource cts;
                lock (_gate)
                {
                    session = _session;
                    cts = _sessionCts;
                    _session = null;
                    _sessionCts = null;
                }
                try { cts?.Cancel(); } catch { }
                try { cts?.Dispose(); } catch { }
                if (session != null)
                {
                    session.StatusChanged -= OnSessionStatusChanged;
                    session.FrameAvailable -= OnSessionFrameAvailable;
                    try { session.StopAsync().Wait(2000); } catch { }
                    try { session.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logError("BoothController.Dispose 异常", ex);
            }
        }
    }
}
