using System;
using System.Threading;
using System.Threading.Tasks;
using YiouCamera.Plugin.CameraProvider;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin.CoreIntegration
{
    /// <summary>
    /// 一帧最新画面的快照（已脱离 Core 内部缓冲，可跨线程/跨次使用）。
    /// 像素格式：BGR24 字节序、top-down、stride=(w*3+3)&amp;~3。
    /// </summary>
    internal sealed class FrameSnapshot
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public byte[] Buffer { get; set; }
        public long FrameIndex { get; set; }
        public long TimestampMs { get; set; }
    }

    /// <summary>
    /// 一次采集请求：输出尺寸/帧率 + 帧出口（共享内存 or provider 帧回调）。
    /// </summary>
    internal sealed class CaptureRequest
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int FrameRate { get; set; } = 30;

        /// <summary>
        /// true = 同时写 <c>Local\YoCameraVcamFrames</c>（Core 侧能力；插件固定传 false，只走 provider 帧回调）；
        /// false = 不创建/不写共享内存（camera provider 路径，只通过 FrameAvailable 推帧）。
        /// </summary>
        public bool UseSharedMemory { get; set; } = true;

        public override string ToString()
        {
            return Width + "x" + Height + "@" + FrameRate + (UseSharedMemory ? " +SHM" : " 无SHM");
        }
    }

    /// <summary>
    /// 插件与 Core（YiouCamera.Core）之间的薄抽象。
    /// - 真实实现：编译期引用 Core（YIOU_HAS_CORE）。
    /// - 占位实现：未引用 Core 时安全失败并说明原因。
    /// </summary>
    internal interface IBoothCoreSession : IDisposable
    {
        bool IsAvailable { get; }
        string BackendName { get; }
        bool IsRunning { get; }
        string StateText { get; }
        string LastError { get; }
        event Action StatusChanged;

        /// <summary>
        /// Core 每解出一帧触发（采集线程同步触发）。订阅方必须在回调返回前完成消费；
        /// 需要跨线程使用时自行拷贝。provider 路径用它把帧 Submit 给宿主。
        /// </summary>
        event Action<FrameRef> FrameAvailable;

        Task StartAsync(PluginConfig config, CaptureRequest request, CancellationToken ct);
        Task StopAsync();
        Task<bool> WaitForRunningAsync(TimeSpan timeout, CancellationToken ct);
        FrameSnapshot TakeLatestFrame();
    }

    internal static class BoothCoreSessionFactory
    {
        /// <summary>本次构建是否编译了真实 Core 适配器（不代表会话已创建）。</summary>
        public static bool Available
        {
            get
            {
#if YIOU_HAS_CORE
                return true;
#else
                return false;
#endif
            }
        }

        public static IBoothCoreSession Create(
            Action<string> log,
            Action<string, Exception> logError,
            Action statusChanged)
        {
#if YIOU_HAS_CORE
            log?.Invoke("Core 后端：RealBoothCoreSession（编译时已引用 YiouCamera.Core）");
            return new RealBoothCoreSession(log, logError, statusChanged);
#else
            log?.Invoke("Core 后端：UnavailableBoothCoreSession（本次构建未引用 YiouCamera.Core）");
            return new UnavailableBoothCoreSession(log, logError, statusChanged);
#endif
        }
    }
}
