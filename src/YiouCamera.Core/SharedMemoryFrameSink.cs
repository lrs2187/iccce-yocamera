using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace YiouCamera.Core;

/// <summary>
/// 唯一的共享内存帧写入端（Producer）。布局严格按 <c>src/YiouCamera.Vcam/vcam_shm.h</c>：
/// 256B 头 + 4 槽，槽按 1920×1080×3 分配（6,220,800B），seqlock 发布：
/// <c>writeSeq++(奇) → 写槽 → writeSeq++(偶)</c>。消费端按 <c>slot=(writeSeq/2-1)%slotCount</c> 取。
/// </summary>
public sealed unsafe class SharedMemoryFrameSink : IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly byte* _basePointer;
    private readonly int _slotBytes;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly Action<string>? _log;

    private byte[]? _placeholder;
    private int _placeholderStride;
    private long _publishedFrames;
    private int _disposed;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public SharedMemoryFrameSink(
        string name = VcamShmLayout.DefaultName,
        int maxWidth = VcamShmLayout.MaxWidth,
        int maxHeight = VcamShmLayout.MaxHeight,
        int fpsNum = 30,
        int fpsDen = 1,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("共享内存名不能为空", nameof(name));
        if (maxWidth is < 16 or > 4096 || maxHeight is < 16 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "最大分辨率必须在 16..4096");
        Name = name;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _slotBytes = VcamShmLayout.SlotBytesFor(maxWidth, maxHeight);
        _log = log;

        long capacity = VcamShmLayout.HeaderBytes + (long)VcamShmLayout.SlotCount * _slotBytes;
        _mmf = MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _basePointer = pointer;
        _base = pointer + _view.PointerOffset;

        InitHeader(maxWidth, maxHeight, fpsNum, fpsDen);
        _log?.Invoke($"[shm] 已创建 '{name}'：header {VcamShmLayout.HeaderBytes}B + {VcamShmLayout.SlotCount}×{_slotBytes}B " +
                     $"= {capacity / 1024.0 / 1024.0:0.0} MB（槽按 {maxWidth}x{maxHeight} 分配）");
    }

    public string Name { get; }
    public long PublishedFrames => Interlocked.Read(ref _publishedFrames);
    public int SlotBytes => _slotBytes;
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>发布一帧 BGR24/top-down 图像（拷贝进 SHM，调用返回后 buffer 可复用）。</summary>
    public void PublishFrame(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("帧尺寸非法", nameof(frame));
        int stride = frame.Stride > 0 ? frame.Stride : VcamShmLayout.StrideForWidth(frame.Width);
        if (stride < frame.Width * 3)
            throw new ArgumentException($"stride({stride}) < width*3({frame.Width * 3})", nameof(frame));
        int length = stride * frame.Height;
        if (frame.Buffer.Length < length)
            throw new ArgumentException($"Buffer 长度 {frame.Buffer.Length} < stride*height {length}", nameof(frame));
        if (length > _slotBytes)
            throw new ArgumentException($"帧 {frame.Width}x{frame.Height}({length} bytes) 超过槽大小 {_slotBytes}（最大 {_maxWidth}x{_maxHeight}）");

        lock (_gate)
        {
            long ordinal = Interlocked.Read(ref _publishedFrames);
            int slot = (int)(ordinal % VcamShmLayout.SlotCount);
            int seqOdd = (int)((ordinal + 1) * 2 - 1);
            int seqEven = seqOdd + 1;
            long ts100ns = frame.TimestampMs * 10_000L;
            if (ts100ns <= 0) ts100ns = Clock.ElapsedMilliseconds * 10_000L;

            // seqlock：奇数 = 正在写；先让消费端看到奇数，再改头/数据，最后写偶数
            Volatile.Write(ref *(int*)(_base + VcamShmLayout.HeaderOffset.WriteSeq), seqOdd);

            Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Width), (uint)frame.Width);
            Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Height), (uint)frame.Height);
            Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Stride), (uint)stride);
            Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Format), VcamShmLayout.FormatRgb24);

            byte* dst = _base + VcamShmLayout.HeaderBytes + (long)slot * _slotBytes;
            new ReadOnlySpan<byte>(frame.Buffer, 0, length).CopyTo(new Span<byte>(dst, _slotBytes));

            *(long*)(_base + VcamShmLayout.HeaderOffset.Timestamp100Ns) = ts100ns;
            *(ulong*)(_base + VcamShmLayout.HeaderOffset.FrameIndex) = (ulong)(ordinal + 1);
            Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Flags), VcamShmLayout.FlagProducerAlive);

            Volatile.Write(ref *(int*)(_base + VcamShmLayout.HeaderOffset.WriteSeq), seqEven);
            Interlocked.Increment(ref _publishedFrames);
        }
    }

    /// <summary>写「无信号」占位帧（灰底 + NO SIGNAL 文字），保证宿主预览不冻结（契约 §2）。</summary>
    public void PublishPlaceholder(int width, int height)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (width <= 0 || height <= 0) return;
        if (width > _maxWidth) width = _maxWidth;
        if (height > _maxHeight) height = _maxHeight;

        int stride = VcamShmLayout.StrideForWidth(width);
        byte[] buffer = EnsurePlaceholder(width, height, stride, out bool created);
        if (created) PaintNoSignal(buffer, width, height, stride);

        PublishFrame(new Frame
        {
            Width = width,
            Height = height,
            Stride = stride,
            Buffer = buffer,
            TimestampMs = Clock.ElapsedMilliseconds,
        });
        _log?.Invoke($"[shm] 占位帧 {width}x{height}");
    }

    /// <summary>标记 producer 存活位（0=停止刷新，宿主可据此判断）。</summary>
    public void MarkProducerAlive(bool alive)
    {
        if (IsDisposed) return;
        lock (_gate)
        {
            uint flags = *(uint*)(_base + VcamShmLayout.HeaderOffset.Flags);
            flags = alive ? (flags | VcamShmLayout.FlagProducerAlive) : (flags & ~VcamShmLayout.FlagProducerAlive);
            Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Flags), flags);
        }
    }

    private byte[] EnsurePlaceholder(int width, int height, int stride, out bool created)
    {
        int length = stride * height;
        if (_placeholder == null || _placeholder.Length != length || _placeholderStride != stride)
        {
            _placeholder = new byte[length];
            _placeholderStride = stride;
            created = true;
        }
        else
        {
            created = false;
        }
        return _placeholder;
    }

    private void InitHeader(int width, int height, int fpsNum, int fpsDen)
    {
        int stride = VcamShmLayout.StrideForWidth(width);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Magic), VcamShmLayout.Magic);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Version), VcamShmLayout.Version);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.HeaderBytes), VcamShmLayout.HeaderBytes);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.SlotCount), VcamShmLayout.SlotCount);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.SlotBytes), (uint)_slotBytes);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Width), (uint)width);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Height), (uint)height);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Stride), (uint)stride);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Format), VcamShmLayout.FormatRgb24);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.FpsNum), (uint)fpsNum);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.FpsDen), (uint)fpsDen);
        Volatile.Write(ref *(int*)(_base + VcamShmLayout.HeaderOffset.WriteSeq), 0);
        Volatile.Write(ref *(int*)(_base + VcamShmLayout.HeaderOffset.ReadSeq), 0);
        Interlocked.Exchange(ref *(long*)(_base + VcamShmLayout.HeaderOffset.Timestamp100Ns), 0);
        Interlocked.Exchange(ref *(ulong*)(_base + VcamShmLayout.HeaderOffset.FrameIndex), 0UL);
        Volatile.Write(ref *(uint*)(_base + VcamShmLayout.HeaderOffset.Flags), 0u);
    }

    // ---------------- 占位帧绘制（不依赖 System.Drawing） ----------------

    private static void PaintNoSignal(byte[] buffer, int width, int height, int stride)
    {
        // 灰底（BGR 同值）
        new Span<byte>(buffer, 0, stride * height).Fill(0x70);

        int line1Scale = Math.Max(2, height / 72);
        int line2Scale = Math.Max(1, height / 144);
        const string line1 = "NO SIGNAL";
        const string line2 = "YIOU WIRELESS BOOTH CAMERA";

        DrawCentered(buffer, width, height, stride, line1, line1Scale, height / 2 - line1Scale * 8, 0xE0, 0xE0, 0xE0);
        DrawCentered(buffer, width, height, stride, line2, line2Scale, height / 2 + line2Scale * 10, 0xB0, 0xB0, 0xB0);
    }

    private static void DrawCentered(byte[] buffer, int width, int height, int stride,
        string text, int scale, int y, byte b, byte g, byte r)
    {
        int textWidth = MeasureWidth(text, scale);
        int x = Math.Max(0, (width - textWidth) / 2);
        for (int i = 0; i < text.Length; i++)
        {
            if (!Glyphs.TryGetValue(text[i], out byte[]? rows)) continue;
            int cx = x + i * 6 * scale;
            for (int row = 0; row < 7; row++)
            {
                int py = y + row * scale;
                if (py < 0 || py >= height) continue;
                int bits = rows[row];
                for (int col = 0; col < 5; col++)
                {
                    if ((bits & (1 << (4 - col))) == 0) continue;
                    int px = cx + col * scale;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int yy = py + sy;
                        if (yy < 0 || yy >= height) continue;
                        int rowBase = yy * stride;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int xx = px + sx;
                            if (xx < 0 || xx >= width) continue;
                            int off = rowBase + xx * 3;
                            buffer[off] = b;
                            buffer[off + 1] = g;
                            buffer[off + 2] = r;
                        }
                    }
                }
            }
        }
    }

    private static int MeasureWidth(string text, int scale) => text.Length * 6 * scale;

    /// <summary>5×7 点阵字体（只含占位帧用到的字符）。</summary>
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
        ['-'] = new byte[] { 0, 0, 0, 0b11111, 0, 0, 0 },
        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['I'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b11111 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
        ['N'] = new byte[] { 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b10001, 0b10001 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
    };

    // ---------------- 校验/诊断读取（消费端或 selftest 用） ----------------

    /// <summary>读取共享内存头（不存在返回 false）。</summary>
    public static bool TryReadHeader(string name, out ShmHeaderSnapshot header)
    {
        header = default;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, VcamShmLayout.HeaderBytes, MemoryMappedFileAccess.Read);
            var bytes = new byte[VcamShmLayout.HeaderBytes];
            view.ReadArray(0, bytes, 0, bytes.Length);
            header = VcamShmLayout.ParseHeader(bytes);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// seqlock 安全读取「最新一帧」（拷贝出来）。用于自检/诊断；vcam 消费端代码是同一算法。
    /// </summary>
    public static bool TryReadLatestFrame(string name, out Frame frame, int maxAttempts = 8)
    {
        frame = null!;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var headerBytes = new byte[VcamShmLayout.HeaderBytes];

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                view.ReadArray(0, headerBytes, 0, headerBytes.Length);
                ShmHeaderSnapshot h = VcamShmLayout.ParseHeader(headerBytes);
                if (!h.IsValid || h.WriteSeq <= 0 || (h.WriteSeq & 1) != 0) continue;

                int slot = h.CurrentSlot;
                if (slot < 0 || slot >= h.SlotCount) continue;

                int length = (int)h.Stride * (int)h.Height;
                if (length <= 0 || length > h.SlotBytes) continue;

                long offset = VcamShmLayout.HeaderBytes + (long)slot * h.SlotBytes;
                var buffer = new byte[length];
                view.ReadArray(offset, buffer, 0, length);

                var seqBytes = new byte[4];
                view.ReadArray(VcamShmLayout.HeaderOffset.WriteSeq, seqBytes, 0, 4);
                int seq2 = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(seqBytes);
                if (seq2 != h.WriteSeq) continue;

                frame = new Frame
                {
                    Width = (int)h.Width,
                    Height = (int)h.Height,
                    Stride = (int)h.Stride,
                    Buffer = buffer,
                    FrameIndex = (long)h.FrameIndex,
                    TimestampMs = h.Timestamp100Ns / 10_000L,
                };
                return true;
            }
        }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_gate)
        {
            try { MarkProducerAlive(false); } catch { }
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            try { _view.Dispose(); } catch { }
            try { _mmf.Dispose(); } catch { }
        }
        _log?.Invoke($"[shm] 已关闭 '{Name}'（共发布 {PublishedFrames} 帧）");
    }
}
