using System.Buffers.Binary;

namespace YiouCamera.Core;

/// <summary>
/// 虚拟摄像头共享内存布局常量 —— 唯一事实来源是 <c>src/YiouCamera.Vcam/vcam_shm.h</c>
/// （256B 头 + 4 槽 + seqlock）。本类只读该头文件、不修改它。
///
/// 头字段偏移（little-endian, pack(1)）：
///  magic 0, version 4, headerBytes 8, slotCount 12, slotBytes 16,
///  width 20, height 24, stride 28, format 32, fpsNum 36, fpsDen 40,
///  writeSeq 44, readSeq 48, timestamp100ns 52, frameIndex 60, flags 68。
///
/// 像素格式：<b>RGB24（内存中 BGR 字节序）</b>；行方向按 docs/CONTRACTS.md §1 采用 <b>top-down</b>，
/// stride=(w*3+3)&amp;~3。注意 vcam_shm.h 里 VCAM_FMT_RGB24 的注释写的是 “bottom-up rows”，
/// 与冻结契约 §1 的 top-down 冲突；以契约为准，消费端（vcam_filter）需按 top-down 处理（见 README）。
/// </summary>
public static class VcamShmLayout
{
    public const uint Magic = 0x4D414356u; // 'VCAM'
    public const uint Version = 1u;
    public const int HeaderBytes = 256;
    public const int SlotCount = 4;
    public const int FormatRgb24 = 0;
    public const string DefaultName = @"Local\YoCameraVcamFrames";

    /// <summary>契约默认最大分辨率（槽按 1920×1080×3 分配；720p 只写前 stride*h 字节）。</summary>
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    public static int StrideForWidth(int width) => (width * 3 + 3) & ~3;

    public static int SlotBytesFor(int width, int height) => StrideForWidth(width) * height;

    public static int TotalMappingBytes(int maxWidth = MaxWidth, int maxHeight = MaxHeight)
        => HeaderBytes + SlotCount * SlotBytesFor(maxWidth, maxHeight);

    public static class HeaderOffset
    {
        public const int Magic = 0;
        public const int Version = 4;
        public const int HeaderBytes = 8;
        public const int SlotCount = 12;
        public const int SlotBytes = 16;
        public const int Width = 20;
        public const int Height = 24;
        public const int Stride = 28;
        public const int Format = 32;
        public const int FpsNum = 36;
        public const int FpsDen = 40;
        public const int WriteSeq = 44;
        public const int ReadSeq = 48;
        public const int Timestamp100Ns = 52;
        public const int FrameIndex = 60;
        public const int Flags = 68;
    }

    public const uint FlagProducerAlive = 1u << 0;

    /// <summary>解析 256 字节头（用于校验脚本/诊断）。</summary>
    public static ShmHeaderSnapshot ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderBytes) throw new ArgumentException($"头至少 {HeaderBytes} 字节", nameof(header));
        return new ShmHeaderSnapshot
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Magic..]),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Version..]),
            HeaderBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.HeaderBytes..]),
            SlotCount = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.SlotCount..]),
            SlotBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.SlotBytes..]),
            Width = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Width..]),
            Height = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Height..]),
            Stride = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Stride..]),
            Format = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Format..]),
            FpsNum = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.FpsNum..]),
            FpsDen = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.FpsDen..]),
            WriteSeq = BinaryPrimitives.ReadInt32LittleEndian(header[HeaderOffset.WriteSeq..]),
            ReadSeq = BinaryPrimitives.ReadInt32LittleEndian(header[HeaderOffset.ReadSeq..]),
            Timestamp100Ns = BinaryPrimitives.ReadInt64LittleEndian(header[HeaderOffset.Timestamp100Ns..]),
            FrameIndex = BinaryPrimitives.ReadUInt64LittleEndian(header[HeaderOffset.FrameIndex..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderOffset.Flags..]),
        };
    }

    public static byte[] BuildHeaderBytes(in ShmHeaderSnapshot h)
    {
        var buf = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Magic), h.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Version), h.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.HeaderBytes), h.HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.SlotCount), h.SlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.SlotBytes), h.SlotBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Width), h.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Height), h.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Stride), h.Stride);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Format), h.Format);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.FpsNum), h.FpsNum);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.FpsDen), h.FpsDen);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(HeaderOffset.WriteSeq), h.WriteSeq);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(HeaderOffset.ReadSeq), h.ReadSeq);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(HeaderOffset.Timestamp100Ns), h.Timestamp100Ns);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(HeaderOffset.FrameIndex), h.FrameIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(HeaderOffset.Flags), h.Flags);
        return buf;
    }
}

/// <summary>共享内存头快照（只读诊断/校验用）。</summary>
public struct ShmHeaderSnapshot
{
    public uint Magic;
    public uint Version;
    public uint HeaderBytes;
    public uint SlotCount;
    public uint SlotBytes;
    public uint Width;
    public uint Height;
    public uint Stride;
    public uint Format;
    public uint FpsNum;
    public uint FpsDen;
    public int WriteSeq;
    public int ReadSeq;
    public long Timestamp100Ns;
    public ulong FrameIndex;
    public uint Flags;

    public readonly bool IsValid =>
        Magic == VcamShmLayout.Magic &&
        Version == VcamShmLayout.Version &&
        HeaderBytes == VcamShmLayout.HeaderBytes &&
        SlotCount is >= 2 and <= 8 &&
        SlotBytes >= Stride * Height &&
        Stride >= (uint)(Width * 3) &&
        Format == VcamShmLayout.FormatRgb24 &&
        FpsNum > 0 && FpsDen > 0;

    public readonly bool IsSlotIndexValid => SlotCount > 0 && WriteSeq > 0 && WriteSeq % 2 == 0;

    public readonly int CurrentSlot => (WriteSeq / 2 - 1) % (int)SlotCount;

    public override readonly string ToString() =>
        $"magic=0x{Magic:X8} version={Version} headerBytes={HeaderBytes} slots={SlotCount} slotBytes={SlotBytes} " +
        $"size={Width}x{Height} stride={Stride} format={Format} fps={FpsNum}/{FpsDen} writeSeq={WriteSeq} " +
        $"readSeq={ReadSeq} ts100ns={Timestamp100Ns} frameIndex={FrameIndex} flags=0x{Flags:X} valid={IsValid}";
}
