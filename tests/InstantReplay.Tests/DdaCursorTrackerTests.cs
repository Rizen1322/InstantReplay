using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Трекер отвечает на вопрос «нужен ли кадр записи» по обновлению курсора.
/// Главный случай — игра, которая спрятала курсор и двигает его мышью: раньше
/// каждое такое движение становилось кадром записи с той же картинкой.
/// </summary>
public sealed class DdaCursorTrackerTests
{
    private static CaptureCursorUpdate Moved(int x, int y, bool visible, bool systemKnows = true) =>
        new(CaptureCursorMode.Separate, true, visible, x, y, null, HasVisibility: systemKnows);

    private static CaptureCursorUpdate Visibility(bool visible) =>
        new(CaptureCursorMode.Separate, false, visible, 0, 0, null, HasVisibility: true);

    [Fact]
    public void Hidden_cursor_movement_is_not_a_frame()
    {
        var tracker = new DdaCursorTracker();
        tracker.Reset();

        // Курсор был видим в меню, человек его сдвинул.
        Assert.True(tracker.Track(Moved(10, 10, visible: true)));
        tracker.Delivered();

        // Игра перешла в обзор мышью: курсор спрятан, DDA позицию не прислала.
        Assert.True(tracker.Track(Visibility(false)));
        tracker.Delivered();

        // Мышь на 1000 Гц: DDA присылает позицию, курсор при этом не виден.
        for (int i = 0; i < 100; i++)
            Assert.False(tracker.Track(Moved(i, i, visible: false)));
    }

    [Fact]
    public void Visible_cursor_movement_is_a_frame()
    {
        var tracker = new DdaCursorTracker();
        tracker.Reset();

        Assert.True(tracker.Track(Moved(10, 20, visible: true)));
        tracker.Delivered();

        Assert.True(tracker.Track(Moved(11, 20, visible: true)));
    }

    [Fact]
    public void Repeated_same_position_is_not_a_frame()
    {
        var tracker = new DdaCursorTracker();
        tracker.Reset();

        Assert.True(tracker.Track(Moved(10, 20, visible: true)));
        tracker.Delivered();

        // DDA прислала обновление, но позиция та же: картинка не изменилась.
        Assert.False(tracker.Track(Moved(10, 20, visible: true)));
    }

    [Fact]
    public void Shape_arriving_on_a_dropped_frame_is_not_lost()
    {
        Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4, [1, 2, 3, 255], out var shape, out _));

        var tracker = new DdaCursorTracker();
        tracker.Reset();
        tracker.Track(Visibility(false));
        tracker.Delivered();

        // Курсор скрыт: форма приходит, но картинку не меняет — кадр не нужен.
        Assert.False(tracker.Track(new CaptureCursorUpdate(
            CaptureCursorMode.Separate, false, false, 0, 0, shape, HasVisibility: true)));

        // Она осталась накопленной и уедет со следующим настоящим кадром.
        CaptureCursorUpdate pending = tracker.Capture;
        Assert.Same(shape, pending.Shape);
    }

    [Fact]
    public void Delivered_clears_pending_state()
    {
        var tracker = new DdaCursorTracker();
        tracker.Reset();
        tracker.Track(Moved(5, 6, visible: true));

        tracker.Delivered();

        Assert.True(tracker.Capture.IsEmpty);
    }

    [Fact]
    public void Hidden_to_visible_needs_a_frame()
    {
        var tracker = new DdaCursorTracker();
        tracker.Reset();
        tracker.Track(Visibility(false));
        tracker.Delivered();

        Assert.True(tracker.Track(Visibility(true)));
    }

    [Fact]
    public void Reset_forces_a_frame_and_drops_shape()
    {
        Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4, [1, 2, 3, 255], out var shape, out _));

        var tracker = new DdaCursorTracker();
        tracker.Reset();
        tracker.Track(new CaptureCursorUpdate(CaptureCursorMode.Separate, true, true, 3, 4, shape));
        tracker.Delivered();

        Assert.True(tracker.Track(new CaptureCursorUpdate(
            CaptureCursorMode.Separate, false, false, 0, 0, null, ResetState: true)));

        CaptureCursorUpdate pending = tracker.Capture;
        Assert.True(pending.ResetState);
        Assert.Null(pending.Shape);
    }

    [Fact]
    public void Empty_update_changes_nothing()
    {
        var tracker = new DdaCursorTracker();
        tracker.Reset();

        Assert.False(tracker.Track(default));
        Assert.True(tracker.Capture.IsEmpty);
    }
}
