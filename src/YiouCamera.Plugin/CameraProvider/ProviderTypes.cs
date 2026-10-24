using System;
using System.Threading;
using Ink_Canvas.Plugins;

namespace YiouCamera.Plugin.CameraProvider
{
    /// <summary>插件当前使用的摄像头接入方式。</summary>
    internal enum CameraAccessMode
    {
        /// <summary>宿主不支持 camera provider（官方 API &lt; 1.14.0）：插件不可用（不做任何系统级安装）。</summary>
        UnsupportedHost = 0,

        /// <summary>宿主支持 camera provider：纯托管，插件直接把帧交给宿主。</summary>
        Provider = 1,
    }

    /// <summary>
    /// 一帧原始画面引用（不拷贝）：只在 <c>FrameArrived</c> 回调返回前有效。
    /// 像素格式 BGR24、top-down、stride=(w*3+3)&amp;~3。
    /// 订阅方若需跨线程/延后使用必须自行拷贝。
    /// </summary>
    internal sealed class FrameRef
    {
        public byte[] Buffer;
        public int Width;
        public int Height;
        public int Stride;
        public long FrameIndex;
        public long TimestampMs;
    }

    /// <summary>provider 推帧统计快照（设置页现场诊断用）。</summary>
    internal sealed class ProviderStatsSnapshot
    {
        public long FramesSubmitted;
        public long FramesAccepted;
        public long FramesRejected;
        public long FramesDropped;
        public long BytesAccepted;
        public double Fps;
        public string LastError = "";
        public bool SinkActive;
        public int TargetWidth;
        public int TargetHeight;
        public int TargetFrameRate;

        public override string ToString()
        {
            return "provider 推帧: 提交=" + FramesSubmitted + " 接收=" + FramesAccepted + " 拒绝=" + FramesRejected
                   + " 丢弃=" + FramesDropped + " fps≈" + Fps.ToString("0.0")
                   + " 目标=" + TargetWidth + "x" + TargetHeight + "@" + TargetFrameRate
                   + (string.IsNullOrEmpty(LastError) ? "" : " 最近错误=" + LastError);
        }
    }

    /// <summary>
    /// provider 实现（编译在 patched SDK 下）与插件运行时之间的桥接口。
    /// 该接口不引用 patched SDK 类型，保证插件能在官方旧宿主上正常加载并优雅降级。
    /// </summary>
    internal interface ICameraProviderHost
    {
        string ProviderId { get; }
        string CameraDisplayName { get; }

        /// <summary>宿主 API 入口（Initialize 阶段宿主 DI 容器可能尚未构建，GetService 会走旧字典回退）。</summary>
        IPluginHost Host { get; }

        int TargetWidth { get; }
        int TargetHeight { get; }
        int TargetFrameRate { get; }
        bool SinkActive { get; }

        void Log(string message);
        void LogError(string message, Exception ex);

        /// <summary>宿主请求开始推流（宽高来自 host 的 SetCameraFormat，可能稍后到达）。</summary>
        bool BeginCapture(int width, int height, int frameRate);

        /// <summary>宿主请求停止推流：返回后不得再 Submit。</summary>
        void EndCapture();

        /// <summary>运行中切换输出格式（内部去抖后重启采集会话）。</summary>
        void SetTargetFormat(int width, int height, int frameRate);

        /// <summary>记录一次 Submit 结果（用于诊断统计）。</summary>
        void RecordSubmit(bool accepted, int byteCount);

        void RecordError(string message);

        /// <summary>Core 解出一帧（采集线程同步触发；订阅方必须在返回前完成 Submit）。</summary>
        event Action<FrameRef> FrameArrived;

        ProviderStatsSnapshot GetStats();

        /// <summary>宿主 IServiceProvider，用于运行时探测 ICameraProviderService（旧宿主返回 null 或探测失败）。</summary>
        IServiceProvider ServiceProvider { get; }
    }
}
