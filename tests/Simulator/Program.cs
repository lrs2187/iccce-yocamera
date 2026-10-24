using System.Net;
using System.Text;
using YiouCamera.Core;

namespace YiouCamera.Simulator;

/// <summary>命令行参数。</summary>
internal sealed class CommandLine
{
    public string Mode { get; set; } = "serve";
    public string? Pin { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int DiscoveryPort { get; set; } = YocamProtocol.DiscoveryPort;
    public int ControlPort { get; set; } = YocamProtocol.ControlPort;
    public int RtspPort { get; set; } = YocamProtocol.RtspPort;
    public string? FfmpegPath { get; set; }
    public string ShmName { get; set; } = VcamShmLayout.DefaultName;
    public bool ShmNameExplicit { get; set; }
    public int DurationSeconds { get; set; }
    public bool Verbose { get; set; }
    public bool AcceptAnyPin { get; set; }

    /// <summary>serve 模式下同时启动 Core 的 YiouBoothSession（让 SHM 里有真实帧，可给 vcam/harness 消费）。</summary>
    public bool WithCore { get; set; }

    public static CommandLine Parse(string[] args)
    {
        var cli = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string name)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{name} 缺少参数值");
                return args[++i]!;
            }

            switch (a)
            {
                case "--selftest": cli.Mode = "selftest"; break;
                case "--serve": cli.Mode = "serve"; break;
                case "--rtsp-only": cli.Mode = "rtsp-only"; break;
                case "--shm-dump": cli.Mode = "shm-dump"; break;
                case "--pin": cli.Pin = Next(a); break;
                case "--width": cli.Width = int.Parse(Next(a)); break;
                case "--height": cli.Height = int.Parse(Next(a)); break;
                case "--fps": cli.Fps = int.Parse(Next(a)); break;
                case "--udp-port": cli.DiscoveryPort = int.Parse(Next(a)); break;
                case "--tcp-port": cli.ControlPort = int.Parse(Next(a)); break;
                case "--rtsp-port": cli.RtspPort = int.Parse(Next(a)); break;
                case "--ffmpeg": cli.FfmpegPath = Next(a); break;
                case "--shm-name": cli.ShmName = Next(a); cli.ShmNameExplicit = true; break;
                case "--duration": cli.DurationSeconds = int.Parse(Next(a)); break;
                case "--verbose": cli.Verbose = true; break;
                case "--accept-any-pin": cli.AcceptAnyPin = true; break;
                case "--with-core": cli.WithCore = true; break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"未知参数：{a}（--help 查看用法）");
            }
        }
        return cli;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            YiouCamera.Simulator - 驿欧无线展台设备仿真器 / 自测

            用法：
              dotnet run --project tests/Simulator -- --selftest [--verbose]
              dotnet run --project tests/Simulator -- --serve [--pin 92271885] [--duration 60]
              dotnet run --project tests/Simulator -- --rtsp-only          # 只起 RTSP，调试用
              dotnet run --project tests/Simulator -- --shm-dump          # 读取并校验共享内存头

            参数：
              --pin <8位>        配对码（serve 模式默认读 YOCamSet.xml；selftest 默认同样规则）
              --width/--height   仿真画面/解码目标尺寸（默认 1280 720）
              --fps              帧率（默认 30）
              --udp-port         发现端口（默认 12811）
              --tcp-port         控制端口（默认 8888）
              --rtsp-port        RTSP 端口（默认 554）
              --ffmpeg <path>    ffmpeg.exe 路径（默认自动解析）
              --shm-name         共享内存名（默认 Local\YoCameraVcamFrames）
              --duration <sec>   serve/rtsp-only 运行时长，0=直到 Ctrl+C
              --verbose          打印每条收发报文
              --accept-any-pin   仿真器忽略配对码校验（验证 V1 用）
              --with-core        serve 时同时启动 YiouBoothSession，让 SHM 里有真实帧（给 vcam/harness 消费）

            selftest 流程：UDP 发现 → TCP 8888 心跳 → CMD_RTSP_TRANS_START/ACK（ACK 故意拆包）
                          → 本机 RTSP(MJPEG) → ffmpeg 解码出帧 → 写 SHM → 回读校验头/像素
                          → YiouBoothSession 编排 → 强制掉线 → 自动重连恢复出帧
            """);
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            CommandLine cli = CommandLine.Parse(args);
            return cli.Mode switch
            {
                "selftest" => await SelfTest.RunAsync(cli).ConfigureAwait(false),
                "serve" => await ServeAsync(cli).ConfigureAwait(false),
                "rtsp-only" => await RtspOnlyAsync(cli).ConfigureAwait(false),
                "shm-dump" => ShmDump(cli),
                _ => Fail($"未知模式：{cli.Mode}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SIM] FATAL: {ex}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
        return 2;
    }

    private static void Log(string line)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
    }

    private static async Task<int> ServeAsync(CommandLine cli)
    {
        Console.WriteLine("=== YiouCamera 设备仿真器（无真机替代） ===");
        string pin = ResolvePin(cli);
        var options = new DeviceSimulatorOptions
        {
            PinCode = pin,
            DiscoveryPort = cli.DiscoveryPort,
            ControlPort = cli.ControlPort,
            RtspPort = cli.RtspPort,
            Width = cli.Width,
            Height = cli.Height,
            Fps = cli.Fps,
            FfmpegPath = cli.FfmpegPath,
            AcceptAnyPin = cli.AcceptAnyPin,
            Verbose = cli.Verbose,
            Log = line => { if (cli.Verbose || !IsPacketLog(line)) Log(line); },
        };

        await using var sim = new DeviceSimulator(options);
        await sim.StartAsync().ConfigureAwait(false);

        YiouBoothSession? session = null;
        if (cli.WithCore)
        {
            session = new YiouBoothSession(new YiouBoothSessionOptions
            {
                PinCode = pin,
                LocalBindAddress = IPAddress.Loopback,
                Width = cli.Width,
                Height = cli.Height,
                TargetFps = cli.Fps,
                FfmpegPath = cli.FfmpegPath ?? string.Empty,
                SharedMemoryName = cli.ShmName,
                DiscoveryTimeout = TimeSpan.FromSeconds(5),
                RtspStartTimeout = TimeSpan.FromSeconds(8),
                FrameStarvationTimeout = TimeSpan.FromSeconds(20),
                Log = line => { if (cli.Verbose || !IsPacketLog(line)) Log(line); },
            });
            session.StateChanged += s => Log($"[core-session] state → {s}");
            session.Faulted += msg => Log($"[core-session] FAULT: {msg}");
            await session.StartAsync().ConfigureAwait(false);
        }

        Console.WriteLine($"""
            [SIM] READY
              配对码 : {pin}{(cli.AcceptAnyPin ? "（仿真器接受任意配对码）" : string.Empty)}
              发现   : UDP {cli.DiscoveryPort}（PC 发 "CMD_GETIP{pin}"，回 "<PCIP>ACK_GETIP"）
              控制   : TCP {cli.ControlPort}（CMD_DATA_SYNC / CMD_RTSP_TRANS_START / CMD_LIVE_STOP / CMD_WIFI_STA）
              RTSP   : rtsp://127.0.0.1:{cli.RtspPort}/stream0（MJPEG 测试图，非真机 H.264）
              共享内存: {(cli.WithCore ? cli.ShmName + "（Core 会话正在写帧）" : "未启用（加 --with-core 后由 Core 会话写）")}
            [SIM] 保持运行中…… Ctrl+C 退出
            """);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (cli.DurationSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(cli.DurationSeconds), cts.Token).ConfigureAwait(false);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RtspOnlyAsync(CommandLine cli)
    {
        string pin = ResolvePin(cli);
        Log($"启动最小 RTSP 服务（MJPEG，{cli.Width}x{cli.Height}@{cli.Fps}，pin={pin} 与 RTSP 无关）");
        var frames = new JpegFrameProvider(cli.Width, cli.Height, cli.Fps, cli.FfmpegPath, Log);
        await using var server = new MiniRtspServer(frames, cli.RtspPort, Log);
        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"[SIM] RTSP READY：rtsp://127.0.0.1:{cli.RtspPort}/stream0");
        Console.WriteLine("      可另开窗口验证：ffprobe -rtsp_transport udp -timeout 5000000 rtsp://127.0.0.1:" + cli.RtspPort + "/stream0");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (cli.DurationSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(cli.DurationSeconds), cts.Token).ConfigureAwait(false);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        return 0;
    }

    /// <summary>读取并独立校验共享内存头（字段偏移按 vcam_shm.h 硬编码，不依赖 Core 的解析器）。</summary>
    private static int ShmDump(CommandLine cli)
    {
        Console.WriteLine($"=== SHM dump：{cli.ShmName} ===");
        if (!SharedMemoryFrameSink.TryReadHeader(cli.ShmName, out var header))
        {
            Console.Error.WriteLine("找不到共享内存（生产者未运行？名字不对？）");
            return 1;
        }

        byte[] raw = ReadRawHeader(cli.ShmName, out bool ok);
        int failed = 0;
        void Check(bool cond, string name, string detail)
        {
            if (cond) Console.WriteLine($"[PASS] {name}: {detail}");
            else { failed++; Console.WriteLine($"[FAIL] {name}: {detail}"); }
        }

        if (!ok)
        {
            Console.Error.WriteLine("无法读取 256B 头");
            return 1;
        }

        Check(raw.Length == 256, "头长度", $"{raw.Length} (期望 256)");
        Check(BitConverter.ToUInt32(raw, 0) == 0x4D414356, "magic @0", $"0x{BitConverter.ToUInt32(raw, 0):X8} (期望 0x4D414356 'VCAM')");
        Check(BitConverter.ToUInt32(raw, 4) == 1, "version @4", $"{BitConverter.ToUInt32(raw, 4)} (期望 1)");
        Check(BitConverter.ToUInt32(raw, 8) == 256, "headerBytes @8", $"{BitConverter.ToUInt32(raw, 8)} (期望 256)");
        Check(BitConverter.ToUInt32(raw, 12) == 4, "slotCount @12", $"{BitConverter.ToUInt32(raw, 12)} (期望 4)");
        Check(BitConverter.ToUInt32(raw, 16) == 6_220_800, "slotBytes @16", $"{BitConverter.ToUInt32(raw, 16)} (期望 1920*1080 的 stride*height = 6,220,800)");
        uint width = BitConverter.ToUInt32(raw, 20);
        uint height = BitConverter.ToUInt32(raw, 24);
        uint stride = BitConverter.ToUInt32(raw, 28);
        Check(stride == (uint)((width * 3 + 3) & ~3), "stride @28", $"{stride} (期望 (w*3+3)&~3 = {(width * 3 + 3) & ~3})");
        Check(BitConverter.ToUInt32(raw, 32) == 0, "format @32", $"{BitConverter.ToUInt32(raw, 32)} (期望 0 = RGB24/BGR)");
        Check(BitConverter.ToUInt32(raw, 36) > 0, "fpsNum @36", $"{BitConverter.ToUInt32(raw, 36)}");
        int writeSeq = BitConverter.ToInt32(raw, 44);
        Check(writeSeq > 0 && writeSeq % 2 == 0, "writeSeq @44", $"{writeSeq} (期望 >0 且偶数)");
        Check(BitConverter.ToInt64(raw, 52) >= 0, "timestamp100ns @52", $"{BitConverter.ToInt64(raw, 52)}");
        ulong frameIndex = BitConverter.ToUInt64(raw, 60);
        Check(frameIndex > 0, "frameIndex @60", $"{frameIndex}");
        uint flags = BitConverter.ToUInt32(raw, 68);
        Check((flags & 1) != 0, "flags @68 bit0 producer alive", $"0x{flags:X}");
        Check(header.IsValid, "Core 解析器判定合法", header.ToString());

        if (SharedMemoryFrameSink.TryReadLatestFrame(cli.ShmName, out var frame))
        {
            Console.WriteLine($"[INFO] 最新帧：{frame.Width}x{frame.Height} stride={frame.Stride} frameIndex={frame.FrameIndex} ts={frame.TimestampMs}ms len={frame.Buffer.Length}");
            int nonZero = 0;
            byte min = 255, max = 0;
            foreach (byte b in frame.Buffer) { if (b != 0) nonZero++; if (b < min) min = b; if (b > max) max = b; }
            Console.WriteLine($"[INFO] 像素统计：非零 {nonZero}/{frame.Buffer.Length}，min={min} max={max}，slot=(writeSeq/2-1)%4={header.CurrentSlot}");
        }
        else
        {
            Console.WriteLine("[INFO] 尚无有效帧（writeSeq=0 或读取撕裂）");
        }

        Console.WriteLine(failed == 0 ? "=== SHM 头校验 PASS ===" : $"=== SHM 头校验 FAIL（{failed} 项）===");
        return failed == 0 ? 0 : 1;
    }

    private static byte[] ReadRawHeader(string name, out bool ok)
    {
        try
        {
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
                name, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 256, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            var bytes = new byte[256];
            view.ReadArray(0, bytes, 0, bytes.Length);
            ok = true;
            return bytes;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取失败：{ex.Message}");
            ok = false;
            return Array.Empty<byte>();
        }
    }

    internal static string ResolvePin(CommandLine cli)
    {
        if (YocamPinCode.IsValid(cli.Pin)) return cli.Pin!;
        if (YocamPinCode.TryReadFromConfig(out string filePin))
        {
            Log($"[SIM] 已从 YOCamSet.xml 读取 PinCode={filePin}");
            return filePin;
        }
        string random = YocamPinCode.GenerateRandom();
        Log($"[SIM] 未找到 YOCamSet.xml，随机 PinCode={random}（服务端/客户端需一致）");
        return random;
    }

    internal static bool IsPacketLog(string line)
        => line.Contains(" RX(", StringComparison.Ordinal) || line.Contains(" TX(", StringComparison.Ordinal);
}
