using System;
using System.Threading;
using System.Threading.Tasks;
using YiouCamera.Plugin.CameraProvider;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin.CoreIntegration
{
    /// <summary>
    /// 未引用 Core 时的安全占位实现：会话启动直接失败、没有帧，
    /// 但插件其余功能（工具栏、设置页、虚拟摄像头管理）照常可用。
    /// </summary>
    internal sealed class UnavailableBoothCoreSession : IBoothCoreSession
    {
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly Action _statusChanged;

        public UnavailableBoothCoreSession(
            Action<string> log,
            Action<string, Exception> logError,
            Action statusChanged)
        {
            _log = log ?? (_ => { });
            _logError = logError ?? ((_, __) => { });
            _statusChanged = statusChanged ?? (() => { });
        }

        public bool IsAvailable { get { return false; } }
        public string BackendName { get { return "未集成（构建时未引用 YiouCamera.Core）"; } }
        public bool IsRunning { get { return false; } }
        public string StateText { get { return "不可用"; } }
        public string LastError { get { return "本次构建未引用 YiouCamera.Core：采集会话不可用。使用 -p:YiouUseCore=true 重新构建即可启用真实采集。"; } }

        public event Action StatusChanged;

        public event Action<FrameRef> FrameAvailable
        {
            add { }
            remove { }
        }

        public Task StartAsync(PluginConfig config, CaptureRequest request, CancellationToken ct)
        {
            _logError("采集会话不可用：本次构建未引用 YiouCamera.Core（设置 -p:YiouUseCore=true 重新构建）。", null);
            try { StatusChanged?.Invoke(); } catch { }
            try { _statusChanged(); } catch { }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public Task<bool> WaitForRunningAsync(TimeSpan timeout, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public FrameSnapshot TakeLatestFrame()
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
