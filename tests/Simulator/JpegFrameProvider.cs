using System.Diagnostics;

namespace YiouCamera.Simulator;

/// <summary>RFC 2435 需要的 MJPEG 帧结构：只保留熵编码扫描数据 + 量化表。</summary>
public sealed class MjpegFrame
{
    /// <summary>SOS 之后的熵编码数据（不含 EOI；ffmpeg 的 RTP/JPEG 解包器会自己补头/补 EOI）。</summary>
    public byte[] ScanData = Array.Empty<byte>();

    /// <summary>DQT 量化表原始值（每表 64 字节，按出现顺序拼接，通常 luma+chroma 共 128 字节）。</summary>
    public byte[] QTables = Array.Empty<byte>();

    /// <summary>JPEG 解码参数：1=4:2:0（type 字段），0=4:2:2。</summary>
    public int Type;
}

/// <summary>
/// 测试图源：用本机 ffmpeg 预生成 2 秒 30fps 的 MJPEG 序列并循环播放。
/// 没有真机时，这是 RTSP 服务的“摄像头画面”。生成一次后缓存在 %TEMP%，后续复用。
///
/// 注意：FFmpeg 的 RTP/JPEG 解包器（libavformat/rtpdec_jpeg.c）不会直接收完整 JPEG 文件——
/// 它会根据 RTP 头里的 DQT 表 + 标准 Huffman 表重建 JPEG 头，然后拼接 SOS 之后的扫描数据。
/// 因此这里预先从每个 JPEG 中拆出「DQT 表 + 扫描数据」。
/// </summary>
public sealed class JpegFrameProvider
{
    private readonly List<MjpegFrame> _frames = new();
    private readonly Action<string>? _log;

    public JpegFrameProvider(int width, int height, int fps, string? ffmpegPath, Action<string>? log)
    {
        _log = log;
        string dir = Path.Combine(Path.GetTempPath(), "YiouCameraSimulator", $"mjpeg_{width}x{height}_{fps}");
        Directory.CreateDirectory(dir);

        if (!Directory.Exists(dir) || Directory.GetFiles(dir, "frame_*.jpg").Length < 30)
            Generate(dir, width, height, fps, ffmpegPath);
        else
            _log?.Invoke($"[sim-rtsp] 复用已生成测试帧：{dir}");

        foreach (string file in Directory.GetFiles(dir, "frame_*.jpg").OrderBy(f => f, StringComparer.Ordinal))
        {
            try { _frames.Add(ParseJpeg(File.ReadAllBytes(file))); }
            catch (Exception ex) { _log?.Invoke($"[sim-rtsp] 解析测试帧失败 {Path.GetFileName(file)}：{ex.Message}"); }
        }

        if (_frames.Count == 0)
            throw new InvalidOperationException($"测试帧为空：{dir}");

        Width = width;
        Height = height;
        Fps = fps;
        _log?.Invoke($"[sim-rtsp] 测试帧就绪：{_frames.Count} 帧 {width}x{height}@{fps}，type={_frames[0].Type}，qtable={_frames[0].QTables.Length}B（目录 {dir}）");
    }

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int Count => _frames.Count;
    public MjpegFrame this[int index] => _frames[((index % _frames.Count) + _frames.Count) % _frames.Count];

    private void Generate(string dir, int width, int height, int fps, string? ffmpegPath)
    {
        string ffmpeg = YiouCamera.Core.FfmpegProcessFrameSource.ResolveFfmpegPath(ffmpegPath, allowDevFallback: true, log: _log);
        foreach (string old in Directory.GetFiles(dir, "frame_*.jpg"))
        {
            try { File.Delete(old); } catch { }
        }

        string pattern = Path.Combine(dir, "frame_%04d.jpg");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi",
                     "-i", $"testsrc2=size={width}x{height}:rate={fps}",
                     "-t", "2",
                     "-c:v", "mjpeg",
                     "-huffman", "default",
                     "-q:v", "3",
                     "-f", "image2",
                     pattern,
                 })
            psi.ArgumentList.Add(arg);

        _log?.Invoke($"[sim-rtsp] 生成 MJPEG 测试帧：{ffmpeg} -f lavfi -i testsrc2=size={width}x{height}:rate={fps} -t 2 …");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 启动失败");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (!process.HasExited) { try { process.Kill(true); } catch { } throw new TimeoutException("生成测试帧超时"); }
        if (process.ExitCode != 0 || Directory.GetFiles(dir, "frame_*.jpg").Length == 0)
            throw new InvalidOperationException($"生成测试帧失败（exit={process.ExitCode}）：{stderr}");
    }

    /// <summary>从完整 baseline JPEG 中拆出 DQT 量化表 + SOS 之后的扫描数据。</summary>
    private MjpegFrame ParseJpeg(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("不是 JPEG（缺 SOI）");

        var qtables = new List<byte>(128);
        int type = 1; // 默认 4:2:0
        int scanStart = -1;
        int pos = 2;

        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
            {
                pos++; // 容错跳过填充字节
                continue;
            }
            byte marker = jpeg[pos + 1];
            pos += 2;

            if (marker == 0xD9) break;                          // EOI
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue; // 独立标记

            if (pos + 1 >= jpeg.Length) break;
            int segLen = (jpeg[pos] << 8) | jpeg[pos + 1];       // 含长度字段自身
            if (segLen < 2 || pos + segLen > jpeg.Length) break;

            if (marker == 0xDB) // DQT
            {
                int p = pos + 2;
                int end = pos + segLen;
                while (p < end)
                {
                    byte info = jpeg[p++];
                    int precision = info >> 4;
                    int tableBytes = 64 * (precision + 1);
                    if (p + tableBytes > end) break;
                    // RTP/JPEG 只支持 8-bit 精度；取每表 64 字节
                    if (precision == 0)
                        for (int i = 0; i < 64; i++) qtables.Add(jpeg[p + i]);
                    p += tableBytes;
                }
            }
            else if (marker == 0xC0 || marker == 0xC2) // SOF0/SOF2：取 luma 采样因子判断 4:2:0 / 4:2:2
            {
                int p = pos + 2;
                if (p + 6 <= pos + segLen && p + 6 <= jpeg.Length)
                {
                    int comps = jpeg[p + 5];
                    if (comps >= 1 && p + 6 + 3 <= pos + segLen)
                    {
                        byte hv = jpeg[p + 7]; // 第一个分量(HV)
                        int h = hv >> 4;
                        if (h == 2 && (hv & 0x0F) == 1) type = 0; // 4:2:2
                        else type = 1;                            // 4:2:0
                    }
                }
            }
            else if (marker == 0xDA) // SOS：扫描数据紧随其后
            {
                scanStart = pos + segLen;
                break;
            }

            pos += segLen;
        }

        if (scanStart < 0 || scanStart >= jpeg.Length)
            throw new InvalidDataException("JPEG 缺 SOS/扫描数据");

        // 去掉结尾 EOI（ffmpeg RTP/JPEG 解包器会自己补）
        int scanEnd = jpeg.Length;
        while (scanEnd - 2 >= scanStart && !(jpeg[scanEnd - 2] == 0xFF && jpeg[scanEnd - 1] == 0xD9))
            scanEnd--;
        if (scanEnd - 2 >= scanStart) scanEnd -= 2;

        if (qtables.Count == 0)
            throw new InvalidDataException("JPEG 缺 DQT 量化表");
        if (qtables.Count > 128) qtables.RemoveRange(128, qtables.Count - 128);
        // ffmpeg 的 mjpeg 编码器在 4:2:0 下可能只写 1 张量化表（3 个分量共用）；
        // RTP/JPEG 解包器按 nb_qtable=qtable_len/64 重建 DQT，若只有 1 张则色度表缺失。
        // 复制成 2 张（即 128B），让解包器重建 2 张 DQT，两个表值一致，解码结果不变。
        if (qtables.Count == 64) qtables.AddRange(qtables.ToArray());

        var scan = new byte[Math.Max(0, scanEnd - scanStart)];
        Buffer.BlockCopy(jpeg, scanStart, scan, 0, scan.Length);
        return new MjpegFrame
        {
            ScanData = scan,
            QTables = qtables.ToArray(),
            Type = type,
        };
    }
}
