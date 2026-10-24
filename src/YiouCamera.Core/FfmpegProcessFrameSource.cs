using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace YiouCamera.Core;

/// <summary>ffmpeg 外挂解码参数（协议 §5.1 厂商参数 + §5.3 建议的 TCP 回退）。</summary>
public sealed class FfmpegOptions
{
    /// <summary>ffmpeg.exe 路径；空 = 自动解析（见 <see cref="FfmpegProcessFrameSource.ResolveFfmpegPath"/>）。</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>RTSP 本地绑定网卡（厂商 localaddr，多网卡必设）。</summary>
    public IPAddress? LocalBindAddress { get; set; }

    /// <summary>强制输出尺寸（ffmpeg 加 scale 滤镜；RAW 管道没有帧头，必须已知尺寸）。</summary>
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int TargetFps { get; set; } = 30;

    /// <summary>UDP 失败/断流后回退 <c>-rtsp_transport tcp</c>（厂商未做，协议 §5.3 建议）。</summary>
    public bool EnableTcpFallback { get; set; } = true;

    /// <summary>优先 TCP（默认 UDP，与厂商一致）。</summary>
    public bool PreferTcp { get; set; }

    /// <summary>启动后等待第一帧的上限；超时则 StartAsync 失败由上层重连。</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>运行时多少秒没有新数据判定假死并重启 ffmpeg（厂商 AVIO 中断回调 10s）。</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>ffmpeg 退出/假死后的重启间隔。</summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>RTSP socket I/O 超时（厂商 stimeout=5000000µs=5s）。</summary>
    public TimeSpan RtpTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>是否允许开发机常见 ffmpeg 路径兜底（生产安装脚本释放后不需要）。</summary>
    public bool AllowDevFfmpegFallback { get; set; } = true;

    public Action<string>? Log { get; set; }
}

/// <summary>
/// v1 解码后端：外挂 ffmpeg.exe 子进程拉 <c>rtsp://ip:554/stream0</c>，
/// stdout 输出 <c>-f rawvideo -pix_fmt bgr24</c>，按 Width×Height 切帧。
/// 进程 stdin 不控制、stderr 异步消费（避免管道阻塞），所有等待可取消。
/// </summary>
public sealed class FfmpegProcessFrameSource : IFrameSource
{
    private readonly string _rtspUrl;
    private readonly FfmpegOptions _options;
    private readonly Action<string>? _log;
    private readonly string _ffmpegPath;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private TaskCompletionSource<bool>? _firstFrame;
    private Process? _process;
    private int _state = (int)FrameSourceState.Idle;
    private int _frameIndex;
    private long _framesDecoded;
    private int _hasEverFrame;
    private string _lastStderrTail = string.Empty;

    public FfmpegProcessFrameSource(string rtspUrl, FfmpegOptions? options = null, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl)) throw new ArgumentException("rtspUrl 不能为空", nameof(rtspUrl));
        _rtspUrl = rtspUrl;
        _options = options ?? new FfmpegOptions();
        _log = log ?? _options.Log;
        _ffmpegPath = ResolveFfmpegPath(_options.FfmpegPath, _options.AllowDevFfmpegFallback, _log);
        _log?.Invoke($"[ffmpeg] 可执行文件：{_ffmpegPath}");
    }

    public event Action<Frame>? FrameReady;
    public event Action<string>? Faulted;
    public event Action<FrameSourceState>? StateChanged;

    public FrameSourceState State => (FrameSourceState)Volatile.Read(ref _state);
    public string RtspUrl => _rtspUrl;
    public string FfmpegPath => _ffmpegPath;
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    public string LastStderrTail => Volatile.Read(ref _lastStderrTail);
    public int Width => _options.Width;
    public int Height => _options.Height;
    public int Stride => VcamShmLayout.StrideForWidth(_options.Width);

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_supervisor != null) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _hasEverFrame, 0);
            _supervisor = Task.Run(() => SupervisorLoopAsync(_cts.Token));
        }

        SetState(FrameSourceState.Starting);
        try
        {
            await _firstFrame!.Task.WaitAsync(_options.StartupTimeout, ct).ConfigureAwait(false);
            SetState(FrameSourceState.Running);
            _log?.Invoke($"[ffmpeg] 已出帧：{_options.Width}x{_options.Height} bgr24 stride={Stride}");
        }
        catch (TimeoutException)
        {
            string detail = LastStderrTail;
            SetState(FrameSourceState.Faulted);
            RaiseFaulted($"ffmpeg {_options.StartupTimeout.TotalSeconds:0.#}s 内未解出第一帧{(string.IsNullOrEmpty(detail) ? string.Empty : "：" + detail)}");
            await StopAsync().ConfigureAwait(false);
            throw new TimeoutException($"ffmpeg 未在 {_options.StartupTimeout.TotalSeconds:0.#}s 内出帧。stderr: {detail}");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? supervisor;
        lock (_gate)
        {
            cts = _cts;
            supervisor = _supervisor;
            _cts = null;
            _supervisor = null;
        }

        try { cts?.Cancel(); } catch { }
        KillProcess();
        if (supervisor != null)
        {
            try { await supervisor.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { _log?.Invoke("[ffmpeg] 等待解码线程退出超时（忽略）"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log?.Invoke($"[ffmpeg] 解码线程退出异常：{ex.Message}"); }
        }
        cts?.Dispose();
        SetState(FrameSourceState.Idle);
        _log?.Invoke("[ffmpeg] 已停止");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        // UDP 优先，失败/断流后轮换 TCP（协议 §5.3 建议）
        string[] transports = _options.EnableTcpFallback
            ? new[] { "udp", "tcp" }
            : new[] { _options.PreferTcp ? "tcp" : "udp" };

        int index = _options.PreferTcp ? Array.IndexOf(transports, "tcp") : 0;
        if (index < 0) index = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string transport = transports[Math.Abs(index) % transports.Length];
                bool deliveredFrames = false;
                string reason = "未知原因";
                try
                {
                    SetState(FrameSourceState.Starting);
                    (deliveredFrames, reason) = await RunFfmpegOnceAsync(transport, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    _log?.Invoke($"[ffmpeg] 本次尝试异常（{transport}）：{ex.Message}");
                }

                if (ct.IsCancellationRequested) break;

                index++;
                SetState(FrameSourceState.Faulted);
                if (Volatile.Read(ref _hasEverFrame) == 1)
                {
                    RaiseFaulted($"RTSP 流中断（{transport}）：{reason}");
                }
                else
                {
                    _log?.Invoke($"[ffmpeg] 启动尝试失败（{transport}）：{reason}");
                }

                try { await Task.Delay(_options.RestartDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _firstFrame?.TrySetCanceled();
        }
    }

    private async Task<(bool DeliveredFrames, string Reason)> RunFfmpegOnceAsync(string transport, CancellationToken ct)
    {
        int width = _options.Width;
        int height = _options.Height;
        int stride = VcamShmLayout.StrideForWidth(width);
        int frameBytes = stride * height;

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_ffmpegPath) ?? AppContext.BaseDirectory,
        };
        foreach (string arg in BuildArguments(transport))
            psi.ArgumentList.Add(arg);

        _log?.Invoke($"[ffmpeg] 启动：{_ffmpegPath} {string.Join(' ', psi.ArgumentList)}");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("ffmpeg 进程启动失败");
        _process = process; // 供 StopAsync/假死看门狗统一 Kill

        var stderrTail = new Queue<string>();
        var lastDataTick = new long[1];
        Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount64);
        bool deliveredFrames = false;

        Task stderrTask = Task.Run(() => ReadStderrAsync(process, stderrTail, ct));
        Task<bool> stdoutTask = Task.Run(() => ReadStdoutAsync(process, width, height, stride, frameBytes, lastDataTick, ct));
        Task watchdog = Task.Run(() => StallWatchdogAsync(process, lastDataTick, ct));

        try
        {
            deliveredFrames = await stdoutTask.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_process, process)) _process = null;
            KillProcess(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* 已退出/超时，忽略 */ }

            try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await watchdog.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

            string tail = string.Join(" | ", stderrTail.TakeLast(12));
            Volatile.Write(ref _lastStderrTail, tail);
            if (tail.Length > 0) _log?.Invoke($"[ffmpeg] stderr: {tail}");

            try { process.StandardOutput.Dispose(); } catch { }
            try { process.StandardError.Dispose(); } catch { }
        }

        string reason = process.HasExited
            ? $"ffmpeg 退出码 {SafeExitCode(process)}{(string.IsNullOrEmpty(LastStderrTail) ? string.Empty : "：" + LastStderrTail)}"
            : "ffmpeg 被停止";
        return (deliveredFrames, reason);
    }

    private async Task<bool> ReadStdoutAsync(
        Process process, int width, int height, int stride, int frameBytes, long[] lastDataTick, CancellationToken ct)
    {
        Stream stdout = process.StandardOutput.BaseStream;
        bool delivered = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // RAW 管道无帧头：必须按固定 W×H×3(stride) 精确切帧
                var buffer = new byte[frameBytes];
                int got = 0;
                while (got < frameBytes)
                {
                    int n = await stdout.ReadAsync(buffer.AsMemory(got, frameBytes - got), ct).ConfigureAwait(false);
                    if (n <= 0) return delivered; // EOF：ffmpeg 退出/网络断
                    got += n;
                    Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount64);
                }

                delivered = true;
                var frame = new Frame
                {
                    Width = width,
                    Height = height,
                    Stride = stride,
                    Buffer = buffer,
                    FrameIndex = Interlocked.Increment(ref _frameIndex),
                    TimestampMs = _clock.ElapsedMilliseconds,
                };
                Interlocked.Increment(ref _framesDecoded);

                if (Interlocked.Exchange(ref _hasEverFrame, 1) == 0)
                {
                    SetState(FrameSourceState.Running);
                    lock (_gate) _firstFrame?.TrySetResult(true);
                }

                RaiseFrameReady(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) _log?.Invoke($"[ffmpeg] 读 stdout 异常：{ex.Message}");
        }
        return delivered;
    }

    private async Task ReadStderrAsync(Process process, Queue<string> tail, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) return;
                if (line.Length == 0) continue;
                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 50) tail.Dequeue();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 进程已退出/管道关闭 */ }
    }

    private async Task StallWatchdogAsync(Process process, long[] lastDataTick, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (process.HasExited) return;
                if (Environment.TickCount64 - Interlocked.Read(ref lastDataTick[0]) > (long)_options.StallTimeout.TotalMilliseconds)
                {
                    _log?.Invoke($"[ffmpeg] {_options.StallTimeout.TotalSeconds:0.#}s 无数据，判定假死并重启");
                    KillProcess(process);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private IReadOnlyList<string> BuildArguments(string transport)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-rtsp_transport", transport,
            // 厂商参数（协议 §5.1/§5.3）：低延迟、1MB max_delay、1.6MB 缓冲、5s socket 超时
            "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-max_delay", "1000000",
            "-buffer_size", "1655360",
            "-timeout", ((long)(_options.RtpTimeout.TotalMilliseconds * 1000)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        IPAddress? local = _options.LocalBindAddress;
        if (local != null && !local.Equals(IPAddress.Any))
        {
            // FFmpeg 8.x 的 rtsp demuxer 不再提供 localaddr AVOption（厂商旧版 FFmpeg 有）。
            // 先探测：支持才传；不支持则 RTSP 走系统默认路由，多网卡时需用路由表/网关选择（见 README 已知限制）。
            if (SupportsRtspLocalAddr(_ffmpegPath))
            {
                args.Add("-localaddr");
                args.Add(local.ToString());
            }
            else
            {
                _log?.Invoke($"[ffmpeg] 当前 ffmpeg 不支持 RTSP -localaddr（{Path.GetFileName(_ffmpegPath)}），" +
                             $"RTSP 将走系统默认路由；控制通道仍绑定 {local}");
            }
        }

        args.Add("-i");
        args.Add(_rtspUrl);

        args.AddRange(new[]
        {
            "-an", "-sn", "-dn",
            "-vf", $"scale={_options.Width}:{_options.Height}:flags=fast_bilinear",
            "-fps_mode", "passthrough",
            "-f", "rawvideo",
            "-pix_fmt", "bgr24",
            "pipe:1",
        });
        return args;
    }

    private void SetState(FrameSourceState state)
    {
        int old = Interlocked.Exchange(ref _state, (int)state);
        if ((FrameSourceState)old == state) return;
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { _log?.Invoke($"[ffmpeg] StateChanged 处理器异常：{ex}"); }
    }

    private void RaiseFrameReady(Frame frame)
    {
        try { FrameReady?.Invoke(frame); }
        catch (Exception ex) { _log?.Invoke($"[ffmpeg] FrameReady 处理器异常：{ex.Message}"); }
    }

    private void RaiseFaulted(string message)
    {
        _log?.Invoke($"[ffmpeg] FAULT: {message}");
        try { Faulted?.Invoke(message); }
        catch (Exception ex) { _log?.Invoke($"[ffmpeg] Faulted 处理器异常：{ex.Message}"); }
    }

    private void KillProcess()
    {
        Process? p = _process;
        if (p == null) return;
        KillProcess(p);
        _process = null;
    }

    private void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 已退出/无权杀，忽略
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>
    /// 探测该 ffmpeg 的 RTSP demuxer 是否支持 <c>-localaddr</c>（FFmpeg 8.x 已移除该 AVOption，
    /// 厂商当年用的旧版 FFmpeg 有）。结果按可执行文件缓存，只探测一次。
    /// </summary>
    public static bool SupportsRtspLocalAddr(string ffmpegPath)
    {
        return LocalAddrSupport.GetOrAdd(ffmpegPath, static path =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-h");
                psi.ArgumentList.Add("demuxer=rtsp");
                using var p = Process.Start(psi);
                if (p == null) return false;
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                return output.Contains("localaddr", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }

    private static readonly ConcurrentDictionary<string, bool> LocalAddrSupport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 ffmpeg.exe：显式配置 → 环境变量 YIOU_FFMPEG_PATH → 程序目录 →
    /// PATH → 开发机常见路径（Kdenlive/ffmpeg 安装）。
    /// 生产环境由安装脚本释放 ffmpeg 到插件目录，因此以上都不命中时应视为安装损坏。
    /// </summary>
    public static string ResolveFfmpegPath(string? configured = null, bool allowDevFallback = true, Action<string>? log = null)
    {
        var tried = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            string p = configured.Trim().Trim('"');
            tried.Add(p);
            if (File.Exists(p)) return Path.GetFullPath(p);
            string? viaPath = FindOnPath(p);
            if (viaPath != null) return viaPath;
            log?.Invoke($"[ffmpeg] 配置路径不存在：{p}，继续自动查找");
        }

        string? env = Environment.GetEnvironmentVariable("YIOU_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            tried.Add(env);
            if (File.Exists(env)) return Path.GetFullPath(env);
        }

        string baseDir = AppContext.BaseDirectory;
        foreach (string candidate in new[]
                 {
                     Path.Combine(baseDir, "ffmpeg.exe"),
                     Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                     Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
                     Path.Combine(baseDir, "tools", "ffmpeg.exe"),
                     // 校园部署首选：老师把 ffmpeg.exe 直接放进插件目录
                     Path.Combine(baseDir, "Plugins", "com.yiou.boothcam", "ffmpeg.exe"),
                     Path.Combine(baseDir, "Plugins", "com.yiou.boothcam", "ffmpeg", "ffmpeg.exe"),
                 })
        {
            tried.Add(candidate);
            if (File.Exists(candidate)) return candidate;
        }

        // 校园部署次选：驿欧白板套件自带 ffmpeg（通常在 Program Files 下，且不在 PATH 里）
        foreach (string candidate in EnumerateVendorFfmpegCandidates())
        {
            tried.Add(candidate);
            if (File.Exists(candidate))
            {
                log?.Invoke($"[ffmpeg] 使用驿欧套件自带 ffmpeg：{candidate}");
                return candidate;
            }
        }

        string? onPath = FindOnPath("ffmpeg.exe");
        if (onPath != null) return onPath;
        tried.Add("PATH:ffmpeg.exe");

        if (allowDevFallback)
        {
            foreach (string candidate in new[]
                     {
                         @"C:\Program Files\Kdenlive\bin\ffmpeg.exe",
                         @"C:\ffmpeg\bin\ffmpeg.exe",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                     })
            {
                tried.Add(candidate);
                if (File.Exists(candidate))
                {
                    log?.Invoke($"[ffmpeg] 使用开发机兜底 ffmpeg：{candidate}（生产环境请由安装脚本释放）");
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            "找不到 ffmpeg.exe。请把 ffmpeg.exe 放到插件目录，或设置 YIOU_FFMPEG_PATH / FfmpegOptions.FfmpegPath。" +
            Environment.NewLine + "已尝试：" + string.Join("; ", tried));
    }

    /// <summary>
    /// 枚举「驿欧白板套件」自带的 ffmpeg.exe 候选路径。
    /// 教室 PC 上通常已经装了 YiouBoard，ffmpeg 在 &lt;安装根&gt;\FFmpeg\bin\x86|bin\x64 下，
    /// 且**不在 PATH 里**，所以必须显式探测。x86 的 ffmpeg.exe 在 x64 宿主下作为独立子进程
    /// 运行没有问题（我们只是 spawn 它）。
    /// </summary>
    private static IEnumerable<string> EnumerateVendorFfmpegCandidates()
    {
        var roots = new List<string>();
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            try
            {
                string pf = Environment.GetFolderPath(special);
                if (string.IsNullOrEmpty(pf) || !Directory.Exists(pf)) continue;
                roots.Add(pf);
                // 常见目录名：YiouBoard2.0 / YiouBoard / 驿欧... 用通配扫描，避免硬编码版本号
                foreach (string dir in Directory.EnumerateDirectories(pf))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("Yiou", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("YiouBoard", StringComparison.OrdinalIgnoreCase))
                        roots.Add(dir);
                }
            }
            catch { /* 权限/枚举失败忽略 */ }
        }

        // 注册表卸载信息里的安装位置（驿欧套件可能装在 D:\ 等非默认目录）
        foreach (string root in EnumerateVendorInstallRootsFromRegistry())
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);

        foreach (string root in roots)
        {
            foreach (string sub in new[]
                     {
                         Path.Combine("FFmpeg", "bin", "x64", "ffmpeg.exe"),
                         Path.Combine("FFmpeg", "bin", "x86", "ffmpeg.exe"),
                         Path.Combine("FFmpeg", "ffmpeg.exe"),
                         Path.Combine("ffmpeg", "bin", "ffmpeg.exe"),
                         "ffmpeg.exe",
                     })
            {
                yield return Path.Combine(root, sub);
            }
        }
    }

    /// <summary>
    /// 从卸载注册表里找「驿欧/Yiou」类软件的 InstallLocation。
    /// 教室机器一般装过驿欧白板套件，这样即使装在非 Program Files 目录也能定位到自带 ffmpeg。
    /// </summary>
    private static IEnumerable<string> EnumerateVendorInstallRootsFromRegistry()
    {
        var results = new List<string>();
        string[] uninstallKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        var hives = new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser };
        foreach (var hive in hives)
        {
            foreach (string keyPath in uninstallKeys)
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(keyPath);
                    if (uninstall == null) continue;
                    foreach (string subName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = uninstall.OpenSubKey(subName);
                            if (app == null) continue;
                            string? display = app.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(display)) continue;
                            if (display.IndexOf("Yiou", StringComparison.OrdinalIgnoreCase) < 0 &&
                                display.IndexOf("驿欧", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            string? loc = (app.GetValue("InstallLocation") as string);
                            if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                                results.Add(loc.Trim('"'));
                        }
                        catch { /* 单个子键失败忽略 */ }
                    }
                }
                catch { /* 无权访问忽略 */ }
            }
        }
        return results;
    }

    private static string? FindOnPath(string fileName)
    {
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains('/')) return null;
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { /* 非法 PATH 项 */ }
        }
        return null;
    }
}
