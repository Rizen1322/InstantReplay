using System.Runtime.InteropServices;
using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameHookProtocolTests
{
    [Fact]
    public void Header_layout_is_a_fixed_cross_process_contract()
    {
        Assert.Equal(0x48475541u, GameHookProtocol.Magic);
        Assert.Equal(2, GameHookProtocol.Version);
        Assert.Equal(3, GameHookProtocol.SlotCount);
        Assert.Equal(256, Marshal.SizeOf<GameHookHeader>());

        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Magic), 0);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Version), 4);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.HeaderSize), 6);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.MappingSize), 8);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.ControllerPid), 16);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.TargetPid), 20);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.TargetProcessStartTicks), 24);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.TargetHwnd), 32);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.TargetRevision), 40);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.CaptureGeneration), 48);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.RouteEpoch), 56);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Command), 64);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.State), 68);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Error), 72);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.TargetFps), 76);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Width), 80);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Height), 84);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.Stride), 88);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.PixelFormat), 92);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.SlotCount), 96);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.SlotHeaderSize), 100);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.SlotStride), 104);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.NewestSequence), 112);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.ControllerHeartbeat100ns), 120);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.HookHeartbeat100ns), 128);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.FramesIssued), 136);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.FramesPublished), 144);
        AssertOffset<GameHookHeader>(nameof(GameHookHeader.FramesDropped), 152);
    }

    [Fact]
    public void Frame_slot_layout_keeps_all_atomic_values_naturally_aligned()
    {
        Assert.Equal(64, Marshal.SizeOf<GameHookFrameSlotHeader>());
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.SequenceLock), 0);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.FrameSequence), 8);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.Timestamp100ns), 16);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.RouteEpoch), 24);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.Width), 32);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.Height), 36);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.Stride), 40);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.ByteCount), 44);
        AssertOffset<GameHookFrameSlotHeader>(nameof(GameHookFrameSlotHeader.CursorVisible), 48);
    }

    [Fact]
    public void Bootstrap_layout_can_be_found_by_the_target_before_nonce_is_known()
    {
        Assert.Equal(256, Marshal.SizeOf<GameHookBootstrapHeader>());
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.Magic), 0);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.Version), 4);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.HeaderSize), 6);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.ControllerPid), 8);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.TargetPid), 12);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.TargetProcessStartTicks), 16);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.TargetHwnd), 24);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.NonceByteCount), 32);
        AssertOffset<GameHookBootstrapHeader>(nameof(GameHookBootstrapHeader.Nonce), 40);
    }

    [Theory]
    [InlineData(1, 1, 4, 128, 640)]
    [InlineData(16, 1, 64, 128, 640)]
    [InlineData(17, 1, 68, 192, 832)]
    [InlineData(1920, 1080, 8294400, 8294464, 24883648)]
    public void Mapping_size_is_checked_and_each_slot_is_cache_line_aligned(
        int width,
        int height,
        int expectedBytes,
        long expectedSlotStride,
        long expectedMappingSize)
    {
        Assert.Equal(expectedBytes, GameHookProtocol.CalculateFrameByteCount(width, height));
        Assert.Equal(expectedSlotStride, GameHookProtocol.CalculateSlotStride(width, height));
        Assert.Equal(expectedMappingSize, GameHookProtocol.CalculateMappingSize(width, height));
        Assert.Equal(0, expectedSlotStride % 64);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(7681, 4320)]
    [InlineData(7680, 4321)]
    [InlineData(2147483647, 2147483647)]
    public void Invalid_or_unsafe_dimensions_are_rejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GameHookProtocol.CalculateMappingSize(width, height));
    }

    private static void AssertOffset<T>(string field, int expected)
        where T : struct =>
        Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(field));
}
