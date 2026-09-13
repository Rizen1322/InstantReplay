using System.Runtime.InteropServices;

namespace Aura.Core.Capture.GameHook;

internal enum GameHookCommand
{
    Idle = 0,
    Capture = 1,
    Stop = 2
}

internal enum GameHookState
{
    Empty = 0,
    Starting = 1,
    Ready = 2,
    Capturing = 3,
    Stopping = 4,
    Stopped = 5,
    Failed = 6
}

internal enum GameHookError
{
    None = 0,
    ProtocolMismatch = 1,
    TargetMismatch = 2,
    UnsupportedOpenGlReadback = 3,
    SharedMemoryUnavailable = 4,
    HookInstallationFailed = 5,
    CaptureFailed = 6
}

internal enum GameHookPixelFormat
{
    Unknown = 0,
    Bgra8 = 1
}

[StructLayout(LayoutKind.Explicit, Pack = 8, Size = GameHookProtocol.BootstrapHeaderSize)]
internal unsafe struct GameHookBootstrapHeader
{
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ushort Version;
    [FieldOffset(6)] public ushort HeaderSize;
    [FieldOffset(8)] public int ControllerPid;
    [FieldOffset(12)] public int TargetPid;
    [FieldOffset(16)] public long TargetProcessStartTicks;
    [FieldOffset(24)] public ulong TargetHwnd;
    [FieldOffset(32)] public int NonceByteCount;
    [FieldOffset(36)] public int Reserved;
    [FieldOffset(40)] public fixed byte Nonce[32];
}

[StructLayout(LayoutKind.Explicit, Pack = 8, Size = GameHookProtocol.HeaderSize)]
internal struct GameHookHeader
{
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ushort Version;
    [FieldOffset(6)] public ushort HeaderSize;
    [FieldOffset(8)] public long MappingSize;
    [FieldOffset(16)] public int ControllerPid;
    [FieldOffset(20)] public int TargetPid;
    [FieldOffset(24)] public long TargetProcessStartTicks;
    [FieldOffset(32)] public ulong TargetHwnd;
    [FieldOffset(40)] public long TargetRevision;
    [FieldOffset(48)] public long CaptureGeneration;
    [FieldOffset(56)] public long RouteEpoch;
    [FieldOffset(64)] public GameHookCommand Command;
    [FieldOffset(68)] public GameHookState State;
    [FieldOffset(72)] public GameHookError Error;
    [FieldOffset(76)] public int TargetFps;
    [FieldOffset(80)] public int Width;
    [FieldOffset(84)] public int Height;
    [FieldOffset(88)] public int Stride;
    [FieldOffset(92)] public GameHookPixelFormat PixelFormat;
    [FieldOffset(96)] public int SlotCount;
    [FieldOffset(100)] public int SlotHeaderSize;
    [FieldOffset(104)] public long SlotStride;
    [FieldOffset(112)] public long NewestSequence;
    [FieldOffset(120)] public long ControllerHeartbeat100ns;
    [FieldOffset(128)] public long HookHeartbeat100ns;
    [FieldOffset(136)] public long FramesIssued;
    [FieldOffset(144)] public long FramesPublished;
    [FieldOffset(152)] public long FramesDropped;
}

[StructLayout(LayoutKind.Explicit, Pack = 8, Size = GameHookProtocol.SlotHeaderSize)]
internal struct GameHookFrameSlotHeader
{
    [FieldOffset(0)] public long SequenceLock;
    [FieldOffset(8)] public long FrameSequence;
    [FieldOffset(16)] public long Timestamp100ns;
    [FieldOffset(24)] public long RouteEpoch;
    [FieldOffset(32)] public int Width;
    [FieldOffset(36)] public int Height;
    [FieldOffset(40)] public int Stride;
    [FieldOffset(44)] public int ByteCount;
}

internal static class GameHookProtocol
{
    public const uint Magic = 0x48475541; // "AUGH" in little-endian memory.
    public const uint BootstrapMagic = 0x42475541; // "AUGB" in little-endian memory.
    public const ushort Version = 1;
    public const int BootstrapHeaderSize = 256;
    public const int HeaderSize = 256;
    public const int SlotHeaderSize = 64;
    public const int SlotCount = 3;
    public const int BytesPerPixel = 4;
    public const int MaxWidth = 7680;
    public const int MaxHeight = 4320;

    public static int CalculateFrameByteCount(int width, int height)
    {
        ValidateDimensions(width, height);
        return checked(width * height * BytesPerPixel);
    }

    public static long CalculateSlotStride(int width, int height)
    {
        long unaligned = checked(SlotHeaderSize + (long)CalculateFrameByteCount(width, height));
        return checked((unaligned + 63L) & ~63L);
    }

    public static long CalculateMappingSize(int width, int height) =>
        checked(HeaderSize + SlotCount * CalculateSlotStride(width, height));

    private static void ValidateDimensions(int width, int height)
    {
        if (width is <= 0 or > MaxWidth)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(height));
    }
}
