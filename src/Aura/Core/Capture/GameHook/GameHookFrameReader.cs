using System.Runtime.CompilerServices;

namespace Aura.Core.Capture.GameHook;

internal enum GameHookFrameReadResult
{
    Success,
    NoFrame,
    InvalidHeader,
    IdentityMismatch,
    StaleEpoch,
    InvalidSlot,
    TornSlot,
    SequenceRollback,
    DestinationTooSmall
}

internal readonly record struct GameHookFrameExpectedIdentity(
    int TargetPid,
    long TargetProcessStartTicks,
    ulong TargetHwnd,
    long TargetRevision,
    long CaptureGeneration,
    long RouteEpoch);

internal readonly record struct GameHookFrameSnapshot(
    long Sequence,
    long Timestamp100ns,
    long RouteEpoch,
    int Width,
    int Height,
    int Stride,
    int ByteCount,
    bool CursorVisible);

internal interface IGameHookMemoryView
{
    long Capacity { get; }
    GameHookHeader ReadHeader();
    GameHookFrameSlotHeader ReadSlotHeader(long offset);
    long ReadInt64Volatile(long offset);
    void CopyTo(long offset, Span<byte> destination);
}

/// <summary>Не владеющий указателем view поверх уже открытого memory-mapped файла.</summary>
internal sealed unsafe class UnmanagedGameHookMemoryView : IGameHookMemoryView
{
    private readonly byte* _pointer;

    public UnmanagedGameHookMemoryView(byte* pointer, long capacity)
    {
        if (pointer is null) throw new ArgumentNullException(nameof(pointer));
        if (capacity < GameHookProtocol.HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _pointer = pointer;
        Capacity = capacity;
    }

    public long Capacity { get; }

    public GameHookHeader ReadHeader() =>
        Unsafe.ReadUnaligned<GameHookHeader>(ref *At(0, GameHookProtocol.HeaderSize));

    public GameHookFrameSlotHeader ReadSlotHeader(long offset) =>
        Unsafe.ReadUnaligned<GameHookFrameSlotHeader>(
            ref *At(offset, GameHookProtocol.SlotHeaderSize));

    public long ReadInt64Volatile(long offset)
    {
        if ((offset & 7) != 0)
            throw new ArgumentException("Atomic offset должен быть выровнен по 8 байтам", nameof(offset));

        ref long value = ref Unsafe.As<byte, long>(ref *At(offset, sizeof(long)));
        return Volatile.Read(ref value);
    }

    public void CopyTo(long offset, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        new ReadOnlySpan<byte>(At(offset, destination.Length), destination.Length).CopyTo(destination);
    }

    private byte* At(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Capacity - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _pointer + checked((nint)offset);
    }
}

/// <summary>
/// Проверяет полный identity и seqlock до публикации кадра. Состояние читателя
/// меняется только после успешной второй проверки seqlock.
/// </summary>
internal sealed class GameHookFrameReader
{
    private long _lastAcceptedSequence;

    public bool TryRead(
        IGameHookMemoryView view,
        in GameHookFrameExpectedIdentity expected,
        Span<byte> destination,
        out GameHookFrameSnapshot frame,
        out GameHookFrameReadResult result)
    {
        ArgumentNullException.ThrowIfNull(view);
        frame = default;

        if (view.Capacity < GameHookProtocol.HeaderSize)
            return Reject(GameHookFrameReadResult.InvalidHeader, out result);

        GameHookHeader header;
        try
        {
            header = view.ReadHeader();
            if (!HeaderIsValid(header, view.Capacity))
                return Reject(GameHookFrameReadResult.InvalidHeader, out result);
        }
        catch (ArgumentException)
        {
            return Reject(GameHookFrameReadResult.InvalidHeader, out result);
        }
        catch (OverflowException)
        {
            return Reject(GameHookFrameReadResult.InvalidHeader, out result);
        }

        if (header.TargetPid != expected.TargetPid ||
            header.TargetProcessStartTicks != expected.TargetProcessStartTicks ||
            header.TargetHwnd != expected.TargetHwnd ||
            header.TargetRevision != expected.TargetRevision ||
            header.CaptureGeneration != expected.CaptureGeneration)
        {
            return Reject(GameHookFrameReadResult.IdentityMismatch, out result);
        }

        if (header.RouteEpoch != expected.RouteEpoch)
            return Reject(GameHookFrameReadResult.StaleEpoch, out result);

        long sequence = view.ReadInt64Volatile(112);
        if (sequence <= 0)
            return Reject(GameHookFrameReadResult.NoFrame, out result);
        if (sequence < _lastAcceptedSequence)
            return Reject(GameHookFrameReadResult.SequenceRollback, out result);
        if (sequence == _lastAcceptedSequence)
            return Reject(GameHookFrameReadResult.NoFrame, out result);

        long slotIndex = (sequence - 1) % GameHookProtocol.SlotCount;
        long slotOffset = checked(GameHookProtocol.HeaderSize + slotIndex * header.SlotStride);
        long payloadOffset = checked(slotOffset + GameHookProtocol.SlotHeaderSize);
        if (slotOffset < GameHookProtocol.HeaderSize ||
            payloadOffset > header.MappingSize ||
            header.SlotStride > header.MappingSize - slotOffset)
        {
            return Reject(GameHookFrameReadResult.InvalidSlot, out result);
        }

        long firstLock = view.ReadInt64Volatile(slotOffset);
        if (firstLock <= 0 || (firstLock & 1) != 0)
            return Reject(GameHookFrameReadResult.TornSlot, out result);

        GameHookFrameSlotHeader slot = view.ReadSlotHeader(slotOffset);
        if (slot.SequenceLock != firstLock || (slot.SequenceLock & 1) != 0)
            return Reject(GameHookFrameReadResult.TornSlot, out result);
        if (slot.RouteEpoch != expected.RouteEpoch)
            return Reject(GameHookFrameReadResult.StaleEpoch, out result);
        if (!SlotIsValid(slot, header, sequence))
            return Reject(GameHookFrameReadResult.InvalidSlot, out result);
        if (destination.Length < slot.ByteCount)
            return Reject(GameHookFrameReadResult.DestinationTooSmall, out result);

        view.CopyTo(payloadOffset, destination[..slot.ByteCount]);

        long secondLock = view.ReadInt64Volatile(slotOffset);
        if (secondLock != firstLock || (secondLock & 1) != 0)
            return Reject(GameHookFrameReadResult.TornSlot, out result);

        _lastAcceptedSequence = sequence;
        frame = new GameHookFrameSnapshot(
            sequence,
            slot.Timestamp100ns,
            slot.RouteEpoch,
            slot.Width,
            slot.Height,
            slot.Stride,
            slot.ByteCount,
            slot.CursorVisible != 0);
        result = GameHookFrameReadResult.Success;
        return true;
    }

    private static bool HeaderIsValid(in GameHookHeader header, long capacity)
    {
        if (header.Magic != GameHookProtocol.Magic ||
            header.Version != GameHookProtocol.Version ||
            header.HeaderSize != GameHookProtocol.HeaderSize ||
            header.PixelFormat != GameHookPixelFormat.Bgra8 ||
            header.SlotCount != GameHookProtocol.SlotCount ||
            header.SlotHeaderSize != GameHookProtocol.SlotHeaderSize ||
            header.TargetFps is <= 0 or > 240 ||
            header.Stride != checked(header.Width * GameHookProtocol.BytesPerPixel))
        {
            return false;
        }

        long minimumStride = GameHookProtocol.CalculateSlotStride(header.Width, header.Height);
        long expectedSize = checked(
            GameHookProtocol.HeaderSize + GameHookProtocol.SlotCount * header.SlotStride);
        return header.SlotStride >= minimumStride &&
               (header.SlotStride & 63) == 0 &&
               header.MappingSize == expectedSize &&
               header.MappingSize <= capacity;
    }

    private static bool SlotIsValid(
        in GameHookFrameSlotHeader slot,
        in GameHookHeader header,
        long sequence)
    {
        int expectedByteCount = GameHookProtocol.CalculateFrameByteCount(
            header.Width,
            header.Height);
        return slot.FrameSequence == sequence &&
               slot.Timestamp100ns > 0 &&
               slot.Width == header.Width &&
               slot.Height == header.Height &&
               slot.Stride == header.Stride &&
               slot.ByteCount == expectedByteCount;
    }

    private static bool Reject(GameHookFrameReadResult reason, out GameHookFrameReadResult result)
    {
        result = reason;
        return false;
    }
}
