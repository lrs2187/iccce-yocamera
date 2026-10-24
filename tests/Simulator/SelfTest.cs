using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using YiouCamera.Core;

namespace YiouCamera.Simulator;

/// <summary>
/// 无真机闭环自测：
/// 发现(UDP 12811) → 控制(TCP 8888 + 心跳 + 开流 ACK，ACK 故意拆包) → 本地 RTSP(MJPEG) →
/// ffmpeg 解码 bgr24 → 写共享内存 → 独立回读校验头/槽像素/seqlock → 占位帧校验 →
/// YiouBoothSession 编排 → 强制掉线 → 自动重连恢复出帧。
/// </summary>
internal static class SelfTest
{
    private sealed class TestLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly bool _verbose;

        public TestLog(string artifactsDir, bool verbose)
        {
            Directory.CreateDirectory(artifactsDir);
            Path = System.IO.Path.Combine(artifactsDir, $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
            _verbose = verbose;
        }

        public string Path { get; }

        public void Line(string text)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
            _writer.WriteLine(stamped);
            if (_verbose || !Program.IsPacketLog(text)) Console.WriteLine(stamped);
        }

        public void Raw(string text)
        {
            _writer.WriteLine(text);
            Console.WriteLine(text);
        }

        public void Dispose() => _writer.Dispose();
    }

    public static async Task<int> RunAsync(CommandLine cli)
    {
        string artifactsDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "artifacts"));
        using var tlog = new TestLog(artifactsDir, cli.Verbose);
        int passed = 0, failed = 0;
        var failures = new List<string>();

        void Check(bool condition, string name, string? detail = null)
        {
            if (condition)
            {
                passed++;
                tlog.Raw($"[PASS] {name}{(detail == null ? string.Empty : " —— " + detail)}");
            }
            else
            {
                failed++;
                failures.Add(name);
                tlog.Raw($"[FAIL] {name}{(detail == null ? string.Empty : " —— " + detail)}");
            }
        }

        void Log(string text) => tlog.Line(text);

        tlog.Raw("=== YiouCamera.Core selftest（无真机闭环验证） ===");
        tlog.Raw($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        tlog.Raw($"环境: dotnet {Environment.Version}, OS {Environment.OSVersion}, x64={Environment.Is64BitProcess}");
        tlog.Raw($"参数: {cli.Width}x{cli.Height}@{cli.Fps}, UDP {cli.DiscoveryPort}, TCP {cli.ControlPort}, RTSP 554(固定)");
        tlog.Raw(string.Empty);

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        DeviceSimulator? sim = null;

        try
        {
            // ---------- 0. 环境/端口 ----------
            string pin = Program.ResolvePin(cli);
            Check(YocamPinCode.IsValid(pin), "配对码为 8 位数字", pin);

            // 自测默认用独立共享内存名：契约名 Local\YoCameraVcamFrames 可能有 vcam Harness / 插件
            // 的生产者同时存在（两写者会互相破坏 seqlock），selftest 不应去踩它。
            string shmName = cli.ShmNameExplicit
                ? cli.ShmName
                : $@"Local\YiouCameraSelftest-{Environment.ProcessId}";
            if (!cli.ShmNameExplicit)
                Log($"[selftest] 共享内存：{shmName}（独立名，避免与运行中的 vcam Harness/插件生产者冲突）");
            else
                Log($"[selftest] 共享内存：{shmName}（--shm-name 指定）");

            int rtspPort = cli.RtspPort;
            if (rtspPort != YocamProtocol.RtspPort)
                Log($"[selftest] DeviceInfo.RtspUrl 契约固定 554，忽略 --rtsp-port {rtspPort}，改用 554");
            rtspPort = YocamProtocol.RtspPort;

            bool udpFree = IsUdpPortFree(cli.DiscoveryPort);
            bool tcpFree = IsTcpPortFree(cli.ControlPort);
            bool rtspFree = IsTcpPortFree(rtspPort);
            Check(udpFree, $"UDP {cli.DiscoveryPort} 可用");
            Check(tcpFree, $"TCP {cli.ControlPort} 可用");
            Check(rtspFree, $"TCP {rtspPort} 可用（RTSP）");
            if (!udpFree || !tcpFree || !rtspFree)
            {
                tlog.Raw("端口被占用：请先退出厂商 YOCameraDesktop / 上一轮 selftest / 其它 12811/8888/554 监听进程");
                return Finish();
            }

            // ---------- 1. 启动设备仿真器 ----------
            tlog.Raw(string.Empty);
            tlog.Raw("── 阶段 1：启动设备仿真器 ──────────────────────────────");
            sim = new DeviceSimulator(new DeviceSimulatorOptions
            {
                PinCode = pin,
                DiscoveryPort = cli.DiscoveryPort,
                ControlPort = cli.ControlPort,
                RtspPort = rtspPort,
                Width = cli.Width,
                Height = cli.Height,
                Fps = cli.Fps,
                FfmpegPath = cli.FfmpegPath,
                AcceptAnyPin = cli.AcceptAnyPin,
                FragmentedRtspAck = true,
                Log = Log,
            });
            await sim.StartAsync().ConfigureAwait(false);
            Check(true, "UDP 12811 / TCP 8888 / RTSP 554 仿真设备已启动");

            // ---------- 2. UDP 发现 ----------
            tlog.Raw(string.Empty);
            tlog.Raw("── 阶段 2：UDP 12811 发现 ──────────────────────────────");
            var discovery = new UdpDeviceDiscovery(pin, IPAddress.Loopback, Log);
            DeviceInfo? dev = await discovery.DiscoverAsync(TimeSpan.FromSeconds(5), overall.Token).ConfigureAwait(false);
            Check(dev != null, "收到 <pcIp>ACK_GETIP 并解析出 DeviceInfo", dev?.ToString());
            if (dev == null)
            {
                tlog.Raw("发现失败：检查 12811 端口、配对码、UDP 广播/loopback 单播");
                return Finish();
            }
            Check(dev.DeviceIp == IPAddress.Loopback.ToString(), "设备 IP = UDP 源地址", dev.DeviceIp);
            Check(dev.PcIp == IPAddress.Loopback.ToString(), "回包文本前缀 = PC IP", dev.PcIp);
            Check(dev.RtspUrl == $"rtsp://127.0.0.1:{YocamProtocol.RtspPort}/stream0", "RTSP URL 拼接符合契约", dev.RtspUrl);

            // ---------- 3. TCP 8888 控制通道 ----------
            tlog.Raw(string.Empty);
            tlog.Raw("── 阶段 3：TCP 8888 控制通道 / 心跳 / 开流 ACK ───────────");
            await using (var control = new TcpDeviceControl(new TcpControlOptions
            {
                LocalBindAddress = IPAddress.Loopback,
                Log = Log,
            }, Log))
            {
                Check(await control.ConnectAsync(dev, overall.Token).ConfigureAwait(false), "TCP 8888 已连接（无握手）");

                await Task.Delay(2400, overall.Token).ConfigureAwait(false);
                Check(control.IsConnected && control.HeartbeatAlive, "1s CMD_DATA_SYNC 心跳存活");
                Check(sim.Heartbeats >= 2, "设备端收到 >=2 次 CMD_DATA_SYNC", $"heartbeats={sim.Heartbeats}");

                bool ack = await control.StartRtspStreamAsync(TimeSpan.FromSeconds(5), overall.Token).ConfigureAwait(false);
                Check(ack, "CMD_RTSP_TRANS_START → ACK_RTSP_TRANS_START（ACK 被故意拆成 2 个 TCP 段）", $"rtspStarts={sim.RtspStartRequests}");

                bool wifi = await control.SendWifiStaAsync(TimeSpan.FromSeconds(2), overall.Token).ConfigureAwait(false);
                Check(wifi && sim.WifiStaRequests == 1, "CMD_WIFI_STA → ACK_WIFI_STA");

                await control.StopLiveAsync(255).ConfigureAwait(false);
                Check(sim.LiveStopRequests == 1, "CMD_LIVE_STOP255 已发送并收到 ACK_LIVE_STOP");
            }

            // ---------- 4. ffmpeg 拉流解码 + 共享内存 ----------
            tlog.Raw(string.Empty);
            tlog.Raw("── 阶段 4：ffmpeg 解码 → 帧 → 共享内存（seqlock）─────────");
            long published = 0;
            Frame? lastFrame = null;
            var frameGate = new object();

            await using (var source = new FfmpegProcessFrameSource(dev.RtspUrl, new FfmpegOptions
            {
                Width = cli.Width,
                Height = cli.Height,
                TargetFps = cli.Fps,
                LocalBindAddress = IPAddress.Loopback,
                FfmpegPath = cli.FfmpegPath ?? string.Empty,
                StartupTimeout = TimeSpan.FromSeconds(20),
                StallTimeout = TimeSpan.FromSeconds(10),
            }, Log))
            using (var sink = new SharedMemoryFrameSink(shmName, fpsNum: cli.Fps, log: Log))
            {
                source.FrameReady += frame =>
                {
                    try { sink.PublishFrame(frame); }
                    catch (Exception ex) { Log($"[selftest] 写 SHM 失败：{ex.Message}"); return; }
                    lock (frameGate)
                    {
                        lastFrame = frame;
                        published++;
                    }
                };

                using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                streamCts.CancelAfter(TimeSpan.FromSeconds(45));
                await source.StartAsync(streamCts.Token).ConfigureAwait(false);
                Check(source.State == FrameSourceState.Running, "ffmpeg 已启动并解出第一帧（bgr24 rawvideo）", source.FfmpegPath);

                await Task.Delay(3000, streamCts.Token).ConfigureAwait(false);
                Check(published >= 20, "3 秒内解码帧数 >= 20", $"frames={published}");
                Check(sim.RtspServer.PlayCount >= 1 && sim.RtspServer.DescribeCount >= 1, "RTSP DESCRIBE/PLAY 已建立",
                    $"describe={sim.RtspServer.DescribeCount}, play={sim.RtspServer.PlayCount}");

                int readOk = 0;
                for (int i = 0; i < 20; i++)
                {
                    if (SharedMemoryFrameSink.TryReadLatestFrame(shmName, out _)) readOk++;
                    await Task.Delay(25, streamCts.Token).ConfigureAwait(false);
                }
                Check(readOk >= 18, "seqlock 并发读取成功 >=18/20（撕裂检测生效）", $"{readOk}/20");

                await source.StopAsync().ConfigureAwait(false);
                await Task.Delay(200, overall.Token).ConfigureAwait(false);

                long finalPublished;
                Frame expected;
                lock (frameGate)
                {
                    finalPublished = published;
                    expected = lastFrame!;
                }

                byte[] raw = ReadRawHeader(shmName);
                Check(raw.Length == 256, "共享内存头 256 字节可读");
                uint slotBytes = BitConverter.ToUInt32(raw, 16);
                uint width = BitConverter.ToUInt32(raw, 20);
                uint height = BitConverter.ToUInt32(raw, 24);
                uint stride = BitConverter.ToUInt32(raw, 28);
                uint format = BitConverter.ToUInt32(raw, 32);
                uint fpsNum = BitConverter.ToUInt32(raw, 36);
                int writeSeq = BitConverter.ToInt32(raw, 44);
                long ts100 = BitConverter.ToInt64(raw, 52);
                ulong frameIndex = BitConverter.ToUInt64(raw, 60);
                uint flags = BitConverter.ToUInt32(raw, 68);

                Check(BitConverter.ToUInt32(raw, 0) == 0x4D414356, "头 offset 0 magic = 'VCAM'", $"0x{BitConverter.ToUInt32(raw, 0):X8}");
                Check(BitConverter.ToUInt32(raw, 4) == 1, "头 offset 4 version = 1", $"{BitConverter.ToUInt32(raw, 4)}");
                Check(BitConverter.ToUInt32(raw, 8) == 256, "头 offset 8 headerBytes = 256", $"{BitConverter.ToUInt32(raw, 8)}");
                Check(BitConverter.ToUInt32(raw, 12) == 4, "头 offset 12 slotCount = 4", $"{BitConverter.ToUInt32(raw, 12)}");
                Check(slotBytes == 1920 * 1080 * 3, "头 offset 16 slotBytes = 1920x1080x3（720p 只写前 stride*h）", $"{slotBytes}");
                Check(width == (uint)cli.Width && height == (uint)cli.Height, "头 offset 20/24 width/height 为实际分辨率", $"{width}x{height}");
                Check(stride == (uint)((cli.Width * 3 + 3) & ~3), "头 offset 28 stride=(w*3+3)&~3", $"{stride}");
                Check(format == 0, "头 offset 32 format=0（RGB24/BGR）", $"{format}");
                Check(fpsNum == (uint)cli.Fps, "头 offset 36 fpsNum", $"{fpsNum}");
                Check(writeSeq > 0 && writeSeq % 2 == 0, "头 offset 44 writeSeq 为偶数（seqlock 稳定态）", $"{writeSeq}");
                Check(ts100 > 0, "头 offset 52 timestamp100ns 单调时间戳", $"{ts100}");
                Check(frameIndex == (ulong)finalPublished && frameIndex > 0, "头 offset 60 frameIndex == 已发布帧数", $"header={frameIndex}, published={finalPublished}");
                Check((flags & 1) != 0, "头 offset 68 flags bit0 producer alive", $"0x{flags:X}");

                Check(SharedMemoryFrameSink.TryReadHeader(shmName, out var parsed) && parsed.IsValid, "Core 头解析器判定合法", parsed.ToString());
                Check(parsed.SlotBytes == slotBytes && parsed.CurrentSlot is >= 0 and < 4, "当前槽索引合法", $"slot={parsed.CurrentSlot}");

                bool frameRead = SharedMemoryFrameSink.TryReadLatestFrame(shmName, out Frame shmFrame);
                Check(frameRead, "从共享内存读出最新帧");
                if (frameRead)
                {
                    int length = expected.Stride * expected.Height;
                    ulong expectedHash = Fnv1a(expected.Buffer, length);
                    ulong actualHash = Fnv1a(shmFrame.Buffer, length);
                    Check(shmFrame.Width == cli.Width && shmFrame.Height == cli.Height && shmFrame.Stride == expected.Stride,
                        "回读帧尺寸/stride 与发布帧一致", $"{shmFrame.Width}x{shmFrame.Height} stride={shmFrame.Stride}");
                    Check(expectedHash == actualHash, "SHM 槽字节与最后发布帧完全一致（BGR24/top-down 行序）",
                        $"fnv=0x{actualHash:X16}, len={length}");
                    Check(shmFrame.FrameIndex == (long)frameIndex, "回读帧 frameIndex 与头一致", $"{shmFrame.FrameIndex}");
                    int distinct = CountDistinct(expected.Buffer, Math.Min(expected.Buffer.Length, 1 << 20));
                    Check(distinct >= 16, "解码图像非空白（抽样不同像素值 >=16）", $"distinct={distinct}");
                }

                // 占位帧
                long beforePlaceholder = sink.PublishedFrames;
                sink.PublishPlaceholder(cli.Width, cli.Height);
                bool phRead = SharedMemoryFrameSink.TryReadLatestFrame(shmName, out Frame placeholder);
                Check(phRead && placeholder.FrameIndex == beforePlaceholder + 1, "无信号占位帧已发布并递增 frameIndex",
                    $"frameIndex={placeholder.FrameIndex}");
                if (phRead)
                {
                    double grayRatio = GrayRatio(placeholder.Buffer, placeholder.Stride, placeholder.Height);
                    Check(grayRatio > 0.5, "占位帧为灰底 + 文字（灰像素占比 >50%）", $"gray={grayRatio:P1}");
                }
            }

            // ---------- 4b. RTSP TCP 回退（ffmpeg -rtsp_transport tcp → interleaved RTP）----------
            tlog.Raw(string.Empty);
            tlog.Raw("── 阶段 4b：RTSP TCP 回退路径 ──────────────────────────");
            await using (var tcpSource = new FfmpegProcessFrameSource(dev.RtspUrl, new FfmpegOptions
            {
                Width = cli.Width,
                Height = cli.Height,
                TargetFps = cli.Fps,
                LocalBindAddress = IPAddress.Loopback,
                FfmpegPath = cli.FfmpegPath ?? string.Empty,
                PreferTcp = true,
                EnableTcpFallback = false, // 强制 TCP，验证 interleaved RTP 解码
                StartupTimeout = TimeSpan.FromSeconds(15),
            }, Log))
            {
                using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                tcpCts.CancelAfter(TimeSpan.FromSeconds(25));
                long tcpFrames = 0;
                tcpSource.FrameReady += _ => Interlocked.Increment(ref tcpFrames);
                await tcpSource.StartAsync(tcpCts.Token).ConfigureAwait(false);
                await Task.Delay(1500, tcpCts.Token).ConfigureAwait(false);
                Check(tcpFrames >= 5, "RTSP TCP 回退（RTP over TCP interleaved）可解码出帧", $"frames={tcpFrames}");
                await tcpSource.StopAsync().ConfigureAwait(false);
            }

            // ---------- 5. YiouBoothSession 编排 + 断线重连 ----------
            tlog.Raw(string.Empty);
            tlog.Raw("── 阶段 5：YiouBoothSession 编排（发现→控制→开流→解码→SHM）──");
            await using (var session = new YiouBoothSession(new YiouBoothSessionOptions
            {
                PinCode = pin,
                LocalBindAddress = IPAddress.Loopback,
                Width = cli.Width,
                Height = cli.Height,
                TargetFps = cli.Fps,
                FfmpegPath = cli.FfmpegPath ?? string.Empty,
                SharedMemoryName = shmName,
                DiscoveryTimeout = TimeSpan.FromSeconds(5),
                RtspStartTimeout = TimeSpan.FromSeconds(8),
                FrameStarvationTimeout = TimeSpan.FromSeconds(20),
                Log = Log,
            }, Log))
            {
                await session.StartAsync(overall.Token).ConfigureAwait(false);
                bool running = await session.WaitForRunningAsync(TimeSpan.FromSeconds(25), overall.Token).ConfigureAwait(false);
                Check(running && session.State == BoothSessionState.Running, "会话进入 Running（首帧已写 SHM）",
                    $"state={session.State}, frames={session.FramesDecoded}");

                long f1 = session.FramesDecoded;
                await Task.Delay(2000, overall.Token).ConfigureAwait(false);
                long f2 = session.FramesDecoded;
                Check(f2 > f1 + 20, "会话持续解码出帧", $"{f1} → {f2}");
                Check(session.LatestFrame != null, "LatestFrame 可供插件手动截图");
                Check(SharedMemoryFrameSink.TryReadHeader(shmName, out var sessionHeader) && sessionHeader.IsValid && sessionHeader.FrameIndex > 0,
                    "会话自动创建并写入共享内存，头合法", sessionHeader.ToString());

                Log("[selftest] 强制断开控制通道 + RTSP 会话，验证自动重连……");
                sim.DropControlClients();
                sim.RtspServer.DropSessions();

                bool recovered = await WaitUntilAsync(
                    () => session.State == BoothSessionState.Running &&
                          session.ReconnectCount >= 1 &&
                          session.FramesDecoded > f2 + 10,
                    TimeSpan.FromSeconds(30), overall.Token).ConfigureAwait(false);
                Check(recovered, "掉线后自动重连并恢复出帧",
                    $"state={session.State}, reconnects={session.ReconnectCount}, frames={session.FramesDecoded}");

                await session.StopAsync().ConfigureAwait(false);
                Check(session.State == BoothSessionState.Stopped, "StopAsync → Stopped");

                if (SharedMemoryFrameSink.TryReadHeader(shmName, out var afterStop))
                    Log($"[selftest] 停止后 SHM 头仍可读：{afterStop}");
            }
        }
        catch (OperationCanceledException)
        {
            Check(false, "selftest 超时（150s）");
        }
        catch (Exception ex)
        {
            tlog.Raw($"[FAIL] 未捕获异常：{ex}");
            failed++;
            failures.Add("未捕获异常");
        }
        finally
        {
            if (sim != null)
            {
                try { await sim.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }

        return Finish();

        int Finish()
        {
            tlog.Raw(string.Empty);
            tlog.Raw($"=== 结果：{passed} passed, {failed} failed ===");
            if (failures.Count > 0)
            {
                tlog.Raw("失败项：");
                foreach (string f in failures) tlog.Raw("  - " + f);
            }
            tlog.Raw($"日志：{tlog.Path}");
            return failed == 0 && passed > 0 ? 0 : 1;
        }
    }

    private static bool IsUdpPortFree(int port)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static bool IsTcpPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static byte[] ReadRawHeader(string name)
    {
        using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var view = mmf.CreateViewAccessor(0, 256, MemoryMappedFileAccess.Read);
        var bytes = new byte[256];
        view.ReadArray(0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static ulong Fnv1a(byte[] data, int length)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < length; i++)
        {
            hash ^= data[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static int CountDistinct(byte[] data, int length)
    {
        Span<bool> seen = stackalloc bool[256];
        int distinct = 0;
        for (int i = 0; i < length; i++)
        {
            if (seen[data[i]]) continue;
            seen[data[i]] = true;
            distinct++;
        }
        return distinct;
    }

    private static double GrayRatio(byte[] buffer, int stride, int height)
    {
        long total = 0, gray = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < stride; x += 3)
            {
                byte b = buffer[row + x];
                byte g = buffer[row + x + 1];
                byte r = buffer[row + x + 2];
                total++;
                if (b == 0x70 && g == 0x70 && r == 0x70) gray++;
            }
        }
        return total == 0 ? 0 : (double)gray / total;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (condition()) return true;
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return condition();
    }
}
