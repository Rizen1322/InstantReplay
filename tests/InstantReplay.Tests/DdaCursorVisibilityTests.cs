using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaCursorVisibilityTests
{
    [Fact]
    public void Hidden_by_system_does_not_invent_pointer_event()
    {
        // Сценарий из жалобы: игра прячет курсор и держит его в центре, DDA больше
        // не присылает обновлений. Спрятать курсор надо, но позиции при этом НЕ
        // выдумываем: по HasPosition захват решает, изменилась ли картинка, и
        // «спрятан» превращалось в кадр записи на каждое движение мыши.
        var (hasPosition, visible) = DdaCursorVisibility.Merge(
            ddaHasPosition: false, ddaVisible: false, systemShowing: false);

        Assert.False(hasPosition);
        Assert.False(visible);
    }

    [Fact]
    public void Hidden_by_system_keeps_real_pointer_update()
    {
        var (hasPosition, visible) = DdaCursorVisibility.Merge(
            ddaHasPosition: true, ddaVisible: true, systemShowing: false);

        Assert.True(hasPosition);
        Assert.False(visible);
    }

    [Fact]
    public void Shown_by_system_follows_dda()
    {
        Assert.Equal((true, true), DdaCursorVisibility.Merge(true, true, true));
        Assert.Equal((true, false), DdaCursorVisibility.Merge(true, false, true));
    }

    [Fact]
    public void No_dda_update_and_system_showing_changes_nothing()
    {
        Assert.Equal((false, false), DdaCursorVisibility.Merge(false, true, true));
    }

    [Fact]
    public void Unknown_system_state_falls_back_to_dda()
    {
        Assert.Equal((true, true), DdaCursorVisibility.Merge(true, true, null));
        Assert.Equal((false, false), DdaCursorVisibility.Merge(false, true, null));
    }

    [Fact]
    public void State_hides_cursor_left_visible_after_game_hides_it()
    {
        var state = new DdaCursorState();
        state.Reset(generation: 1);

        // Курсор был видим в меню, в центре экрана.
        state.Apply(1, new CaptureCursorUpdate(CaptureCursorMode.Separate, true, true, 960, 540, null));
        Assert.True(state.Current.Visible);

        // Игра перешла в обзор мышью: DDA молчит, система говорит «спрятан».
        // Позиция остаётся прежней — её никто не присылал.
        var (hasPosition, visible) = DdaCursorVisibility.Merge(false, false, systemShowing: false);
        state.Apply(1, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, hasPosition, visible, 0, 0, null, HasVisibility: true));

        Assert.False(state.Current.Visible);
        Assert.Equal((960, 540), (state.Current.X, state.Current.Y));
    }
}
