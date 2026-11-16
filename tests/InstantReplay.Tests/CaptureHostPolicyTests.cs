using Aura.Core.Capture;
using Aura.Core.Capture.Host;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureHostPolicyTests
{
    [Theory]
    [InlineData(19045, null, true)]      // Windows 10: изоляция DDA по умолчанию
    [InlineData(19045, "0", false)]      // явное выключение для диагностики
    [InlineData(22621, null, false)]     // Windows 11: штатный WGC
    [InlineData(22621, "1", true)]       // принудительная проверка на Windows 11
    [InlineData(22621, "off", false)]
    public void Remote_dda_selection_is_deterministic(int build, string? overrideValue, bool expected) =>
        Assert.Equal(expected, CaptureHostPolicy.UseRemoteDdaFor(build, overrideValue));

    [Fact]
    public void Control_block_layout_does_not_overlap_and_fits_mapping()
    {
        Assert.True(CaptureHostProtocol.ValidateLayout());
        Assert.True(CaptureHostProtocol.SlotMetadataOffset(CaptureHostProtocol.SlotCount - 1) +
                    CaptureHostProtocol.SlotMetadataBytes <= CaptureHostProtocol.HeaderBytes);
        Assert.True(CaptureHostProtocol.OffPublishedEpoch + sizeof(long) <= CaptureHostProtocol.SlotMetadataBase);
        Assert.Equal(CaptureHostProtocol.HeaderBytes, CaptureHostProtocol.SlotShapeOffset(0));
        Assert.Equal(CaptureHostProtocol.SlotCount * CaptureHostProtocol.SlotShapeCapacity +
                     CaptureHostProtocol.HeaderBytes, CaptureHostProtocol.MappingBytes);
    }

    [Fact]
    public void Cursor_wire_roundtrip_keeps_position_visibility_and_shape()
    {
        var shape = new DdaCursorShape(DdaCursorShapeKind.Color, 2, 2, new byte[16]);
        var update = new CaptureCursorUpdate(
            CaptureCursorMode.Separate, HasPosition: true, Visible: true,
            X: 17, Y: -4, Shape: shape, ResetState: true, HasVisibility: true);

        CaptureCursorWire wire = CaptureCursorWire.From(update, shape.Pixels.Length);
        CaptureCursorUpdate restored = wire.ToUpdate(shape);

        Assert.Equal((int)CaptureCursorMode.Separate, wire.Mode);
        Assert.True(wire.HasPosition);
        Assert.True(wire.Visible);
        Assert.True(wire.HasVisibility);
        Assert.True(wire.ResetState);
        Assert.Equal(17, restored.X);
        Assert.Equal(-4, restored.Y);
        Assert.Equal(16, wire.ShapeLength);
        Assert.Equal((int)DdaCursorShapeKind.Color, wire.ShapeKind);
        Assert.Equal(2, wire.ShapeWidth);
        Assert.Equal(2, wire.ShapeHeight);
    }

    [Fact]
    public void Cursor_wire_without_shape_has_zero_description()
    {
        CaptureCursorWire wire = CaptureCursorWire.From(CaptureCursorUpdate.SystemComposed, 0);
        Assert.Equal(0, wire.ShapeLength);
        Assert.Equal(0, wire.ShapeKind);
        Assert.False(wire.HasPosition);
        Assert.Equal((int)CaptureCursorMode.SystemComposed, wire.Mode);
    }

    [Fact]
    public void Failure_budget_allows_exactly_three_consecutive_failures()
    {
        RemoteCaptureFailureBudget.ResetForTests();
        try
        {
            Assert.False(RemoteCaptureFailureBudget.Exhausted);
            for (int i = 1; i < RemoteCaptureFailureBudget.MaxConsecutiveFailures; i++)
            {
                RemoteCaptureFailureBudget.NoteFailure();
                Assert.False(RemoteCaptureFailureBudget.Exhausted);
            }
            RemoteCaptureFailureBudget.NoteFailure();
            Assert.True(RemoteCaptureFailureBudget.Exhausted);
        }
        finally
        {
            RemoteCaptureFailureBudget.ResetForTests();
        }
    }

    [Fact]
    public void Healthy_worker_resets_failure_budget()
    {
        RemoteCaptureFailureBudget.ResetForTests();
        try
        {
            RemoteCaptureFailureBudget.NoteFailure();
            RemoteCaptureFailureBudget.NoteFailure();
            RemoteCaptureFailureBudget.NoteHealthy(RemoteCaptureFailureBudget.HealthyFrames - 1);
            Assert.Equal(2, RemoteCaptureFailureBudget.ConsecutiveFailures);
            RemoteCaptureFailureBudget.NoteHealthy(RemoteCaptureFailureBudget.HealthyFrames);
            Assert.Equal(0, RemoteCaptureFailureBudget.ConsecutiveFailures);
            Assert.False(RemoteCaptureFailureBudget.Exhausted);
        }
        finally
        {
            RemoteCaptureFailureBudget.ResetForTests();
        }
    }

    [Fact]
    public void Unretired_worker_blocks_new_sessions_until_restart()
    {
        RemoteCaptureFailureBudget.ResetForTests();
        try
        {
            Assert.False(RemoteCaptureFailureBudget.HasUnretiredWorker);
            RemoteCaptureFailureBudget.NoteUnretiredWorker();
            Assert.True(RemoteCaptureFailureBudget.HasUnretiredWorker);
        }
        finally
        {
            RemoteCaptureFailureBudget.ResetForTests();
        }
        Assert.False(RemoteCaptureFailureBudget.HasUnretiredWorker);
    }

    [Fact]
    public void Unavailable_exception_tells_user_to_save_replay_and_restart()
    {
        var error = new RemoteCaptureHostUnavailableException("воркер не поднимается");
        Assert.Contains("сохранить", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("перезапустите Aura", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
