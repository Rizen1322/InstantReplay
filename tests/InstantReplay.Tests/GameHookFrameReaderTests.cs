using System.Runtime.InteropServices;
using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameHookFrameReaderTests
{
    [Fact]
    public unsafe void Reads_the_newest_complete_slot_into_caller_owned_memory()
    {
        TestMapping mapping = TestMapping.Valid(sequence: 4, width: 2, height: 2);
        var reader = new GameHookFrameReader();
        Span<byte> destination = stackalloc byte[16];

        fixed (byte* pointer = mapping.Bytes)
        {
            var view = new UnmanagedGameHookMemoryView(pointer, mapping.Bytes.LongLength);
            Assert.True(reader.TryRead(
                view,
                mapping.Expected,
                destination,
                out GameHookFrameSnapshot frame,
                out GameHookFrameReadResult result));

            Assert.Equal(GameHookFrameReadResult.Success, result);
            Assert.Equal(4, frame.Sequence);
            Assert.Equal(777, frame.Timestamp100ns);
            Assert.Equal(2, frame.Width);
            Assert.Equal(2, frame.Height);
            Assert.Equal(8, frame.Stride);
            Assert.Equal(16, frame.ByteCount);
            Assert.Equal(mapping.Expected.RouteEpoch, frame.RouteEpoch);
            Assert.Equal(Enumerable.Range(1, 16).Select(i => (byte)i), destination.ToArray());
        }
    }

    [Fact]
    public void Odd_or_changed_seqlock_is_rejected_without_advancing_reader()
    {
        TestMapping odd = TestMapping.Valid(sequence: 1);
        odd.WriteSlotHeader(odd.ReadSlotHeader() with { SequenceLock = 3 });
        var oddView = new ArrayMemoryView(odd.Bytes);
        var reader = new GameHookFrameReader();
        byte[] destination = new byte[odd.FrameByteCount];

        Assert.False(reader.TryRead(
            oddView,
            odd.Expected,
            destination,
            out _,
            out GameHookFrameReadResult oddResult));
        Assert.Equal(GameHookFrameReadResult.TornSlot, oddResult);

        TestMapping changed = TestMapping.Valid(sequence: 2);
        var changedView = new ArrayMemoryView(changed.Bytes)
        {
            AfterCopy = () =>
            {
                GameHookFrameSlotHeader header = changed.ReadSlotHeader();
                changed.WriteSlotHeader(header with { SequenceLock = header.SequenceLock + 2 });
            }
        };

        Assert.False(reader.TryRead(
            changedView,
            changed.Expected,
            destination,
            out _,
            out GameHookFrameReadResult changedResult));
        Assert.Equal(GameHookFrameReadResult.TornSlot, changedResult);

        changedView.AfterCopy = null;
        Assert.True(reader.TryRead(changedView, changed.Expected, destination, out var frame, out _));
        Assert.Equal(2, frame.Sequence);
    }

    [Fact]
    public void Header_identity_generation_and_epoch_are_all_part_of_admission()
    {
        TestMapping valid = TestMapping.Valid();
        var cases = new (GameHookFrameReadResult Expected, Action<TestMapping> Mutate)[]
        {
            (GameHookFrameReadResult.InvalidHeader, m => m.WriteHeader(m.ReadHeader() with { Magic = 0 })),
            (GameHookFrameReadResult.InvalidHeader, m => m.WriteHeader(m.ReadHeader() with { Version = 99 })),
            (GameHookFrameReadResult.InvalidHeader, m => m.WriteHeader(m.ReadHeader() with { HeaderSize = 12 })),
            (GameHookFrameReadResult.InvalidHeader, m => m.WriteHeader(m.ReadHeader() with { MappingSize = m.Bytes.LongLength + 1 })),
            (GameHookFrameReadResult.InvalidHeader, m => m.WriteHeader(m.ReadHeader() with { PixelFormat = GameHookPixelFormat.Unknown })),
            (GameHookFrameReadResult.IdentityMismatch, m => m.WriteHeader(m.ReadHeader() with { TargetPid = 77 })),
            (GameHookFrameReadResult.IdentityMismatch, m => m.WriteHeader(m.ReadHeader() with { TargetProcessStartTicks = 88 })),
            (GameHookFrameReadResult.IdentityMismatch, m => m.WriteHeader(m.ReadHeader() with { TargetHwnd = 0x9999 })),
            (GameHookFrameReadResult.IdentityMismatch, m => m.WriteHeader(m.ReadHeader() with { TargetRevision = 99 })),
            (GameHookFrameReadResult.IdentityMismatch, m => m.WriteHeader(m.ReadHeader() with { CaptureGeneration = 99 })),
            (GameHookFrameReadResult.StaleEpoch, m => m.WriteHeader(m.ReadHeader() with { RouteEpoch = 99 }))
        };

        foreach ((GameHookFrameReadResult expected, Action<TestMapping> mutate) in cases)
        {
            TestMapping mapping = valid.Clone();
            mutate(mapping);
            AssertRejected(mapping, expected);
        }
    }

    [Fact]
    public void Malformed_slot_and_insufficient_destination_are_rejected()
    {
        TestMapping valid = TestMapping.Valid(width: 2, height: 2);
        var cases = new (GameHookFrameReadResult Expected, Action<TestMapping> Mutate)[]
        {
            (GameHookFrameReadResult.InvalidSlot, m => m.WriteSlotHeader(m.ReadSlotHeader() with { FrameSequence = 98 })),
            (GameHookFrameReadResult.InvalidSlot, m => m.WriteSlotHeader(m.ReadSlotHeader() with { Timestamp100ns = 0 })),
            (GameHookFrameReadResult.StaleEpoch, m => m.WriteSlotHeader(m.ReadSlotHeader() with { RouteEpoch = 98 })),
            (GameHookFrameReadResult.InvalidSlot, m => m.WriteSlotHeader(m.ReadSlotHeader() with { Width = 3 })),
            (GameHookFrameReadResult.InvalidSlot, m => m.WriteSlotHeader(m.ReadSlotHeader() with { Height = 3 })),
            (GameHookFrameReadResult.InvalidSlot, m => m.WriteSlotHeader(m.ReadSlotHeader() with { Stride = 7 })),
            (GameHookFrameReadResult.InvalidSlot, m => m.WriteSlotHeader(m.ReadSlotHeader() with { ByteCount = 15 }))
        };

        foreach ((GameHookFrameReadResult expected, Action<TestMapping> mutate) in cases)
        {
            TestMapping mapping = valid.Clone();
            mutate(mapping);
            AssertRejected(mapping, expected);
        }

        var reader = new GameHookFrameReader();
        Assert.False(reader.TryRead(
            new ArrayMemoryView(valid.Bytes),
            valid.Expected,
            new byte[valid.FrameByteCount - 1],
            out _,
            out GameHookFrameReadResult result));
        Assert.Equal(GameHookFrameReadResult.DestinationTooSmall, result);
    }

    [Fact]
    public void Zero_sequence_and_sequence_rollback_are_not_frames()
    {
        TestMapping mapping = TestMapping.Valid(sequence: 5);
        var reader = new GameHookFrameReader();
        byte[] destination = new byte[mapping.FrameByteCount];
        var view = new ArrayMemoryView(mapping.Bytes);

        Assert.True(reader.TryRead(view, mapping.Expected, destination, out _, out _));

        mapping.Publish(sequence: 4);
        Assert.False(reader.TryRead(
            view,
            mapping.Expected,
            destination,
            out _,
            out GameHookFrameReadResult rollback));
        Assert.Equal(GameHookFrameReadResult.SequenceRollback, rollback);

        TestMapping empty = TestMapping.Valid(sequence: 1);
        empty.WriteHeader(empty.ReadHeader() with { NewestSequence = 0 });
        Assert.False(new GameHookFrameReader().TryRead(
            new ArrayMemoryView(empty.Bytes),
            empty.Expected,
            destination,
            out _,
            out GameHookFrameReadResult noFrame));
        Assert.Equal(GameHookFrameReadResult.NoFrame, noFrame);
    }

    [Fact]
    public void Session_names_are_random_pid_scoped_and_never_global()
    {
        GameHookSessionNames first = GameHookSessionNames.Create(targetPid: 123, controllerPid: 456);
        GameHookSessionNames second = GameHookSessionNames.Create(targetPid: 123, controllerPid: 456);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal("Local\\Aura.GameCapture.123.Bootstrap", first.BootstrapMapping);
        Assert.Contains("456", first.FrameMapping, StringComparison.Ordinal);
        Assert.Contains(first.Nonce, first.FrameMapping, StringComparison.Ordinal);
        Assert.Contains(first.Nonce, first.FrameReadyEvent, StringComparison.Ordinal);
        Assert.Contains(first.Nonce, first.ControlEvent, StringComparison.Ordinal);
        Assert.DoesNotContain("Global\\", first.BootstrapMapping, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global\\", first.FrameMapping, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRejected(TestMapping mapping, GameHookFrameReadResult expected)
    {
        var reader = new GameHookFrameReader();
        Assert.False(reader.TryRead(
            new ArrayMemoryView(mapping.Bytes),
            mapping.Expected,
            new byte[mapping.FrameByteCount],
            out _,
            out GameHookFrameReadResult result));
        Assert.Equal(expected, result);
    }

    private sealed class ArrayMemoryView(byte[] bytes) : IGameHookMemoryView
    {
        public Action? AfterCopy { get; set; }
        public long Capacity => bytes.LongLength;

        public GameHookHeader ReadHeader() =>
            MemoryMarshal.Read<GameHookHeader>(bytes);

        public GameHookFrameSlotHeader ReadSlotHeader(long offset) =>
            MemoryMarshal.Read<GameHookFrameSlotHeader>(bytes.AsSpan(checked((int)offset)));

        public long ReadInt64Volatile(long offset) =>
            MemoryMarshal.Read<long>(bytes.AsSpan(checked((int)offset)));

        public void CopyTo(long offset, Span<byte> destination)
        {
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
            AfterCopy?.Invoke();
        }
    }

    private sealed class TestMapping
    {
        private TestMapping(byte[] bytes, GameHookFrameExpectedIdentity expected, int frameByteCount)
        {
            Bytes = bytes;
            Expected = expected;
            FrameByteCount = frameByteCount;
        }

        public byte[] Bytes { get; }
        public GameHookFrameExpectedIdentity Expected { get; }
        public int FrameByteCount { get; }

        public static TestMapping Valid(long sequence = 1, int width = 2, int height = 1)
        {
            long mappingSize = GameHookProtocol.CalculateMappingSize(width, height);
            var expected = new GameHookFrameExpectedIdentity(
                TargetPid: 321,
                TargetProcessStartTicks: 123,
                TargetHwnd: 0x1234,
                TargetRevision: 7,
                CaptureGeneration: 8,
                RouteEpoch: 9);
            var mapping = new TestMapping(
                new byte[checked((int)mappingSize)],
                expected,
                GameHookProtocol.CalculateFrameByteCount(width, height));
            mapping.WriteHeader(new GameHookHeader
            {
                Magic = GameHookProtocol.Magic,
                Version = GameHookProtocol.Version,
                HeaderSize = GameHookProtocol.HeaderSize,
                MappingSize = mappingSize,
                ControllerPid = 456,
                TargetPid = expected.TargetPid,
                TargetProcessStartTicks = expected.TargetProcessStartTicks,
                TargetHwnd = expected.TargetHwnd,
                TargetRevision = expected.TargetRevision,
                CaptureGeneration = expected.CaptureGeneration,
                RouteEpoch = expected.RouteEpoch,
                Command = GameHookCommand.Capture,
                State = GameHookState.Capturing,
                TargetFps = 60,
                Width = width,
                Height = height,
                Stride = checked(width * 4),
                PixelFormat = GameHookPixelFormat.Bgra8,
                SlotCount = GameHookProtocol.SlotCount,
                SlotHeaderSize = GameHookProtocol.SlotHeaderSize,
                SlotStride = GameHookProtocol.CalculateSlotStride(width, height),
                NewestSequence = sequence
            });
            mapping.Publish(sequence);
            return mapping;
        }

        public TestMapping Clone() =>
            new((byte[])Bytes.Clone(), Expected, FrameByteCount);

        public GameHookHeader ReadHeader() =>
            MemoryMarshal.Read<GameHookHeader>(Bytes);

        public void WriteHeader(GameHookHeader header) =>
            MemoryMarshal.Write(Bytes, in header);

        public GameHookFrameSlotHeader ReadSlotHeader()
        {
            long offset = SlotOffset(ReadHeader().NewestSequence);
            return MemoryMarshal.Read<GameHookFrameSlotHeader>(Bytes.AsSpan(checked((int)offset)));
        }

        public void WriteSlotHeader(GameHookFrameSlotHeader header)
        {
            long offset = SlotOffset(ReadHeader().NewestSequence);
            MemoryMarshal.Write(Bytes.AsSpan(checked((int)offset)), in header);
        }

        public void Publish(long sequence)
        {
            GameHookHeader header = ReadHeader();
            header.NewestSequence = sequence;
            WriteHeader(header);
            long offset = SlotOffset(sequence);
            var slot = new GameHookFrameSlotHeader
            {
                SequenceLock = sequence * 2,
                FrameSequence = sequence,
                Timestamp100ns = 777,
                RouteEpoch = Expected.RouteEpoch,
                Width = header.Width,
                Height = header.Height,
                Stride = header.Stride,
                ByteCount = FrameByteCount
            };
            MemoryMarshal.Write(Bytes.AsSpan(checked((int)offset)), in slot);
            for (int i = 0; i < FrameByteCount; i++)
                Bytes[checked((int)offset + GameHookProtocol.SlotHeaderSize + i)] = (byte)(i + 1);
        }

        private long SlotOffset(long sequence)
        {
            long index = (sequence - 1) % GameHookProtocol.SlotCount;
            return checked(GameHookProtocol.HeaderSize + index * ReadHeader().SlotStride);
        }
    }
}
