using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaCursorVisibilityTests
{
    [Fact]
    public void Hidden_by_system_hides_even_when_dda_reports_visible()
    {
        var (hasPosition, visible) = DdaCursorVisibility.Merge(
            ddaHasPosition: true, ddaVisible: true, systemShowing: false);

        Assert.True(hasPosition);
        Assert.False(visible);
    }

    [Fact]
    public void Hidden_by_system_hides_when_dda_sent_no_update()
    {
        // Сценарий из жалобы: игра прячет курсор и держит его в центре, DDA больше
        // не присылает обновлений. Без этого правила курсор так и висел в записи.
        var (hasPosition, visible) = DdaCursorVisibility.Merge(
            ddaHasPosition: false, ddaVisible: false, systemShowing: false);

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
        var (hasPosition, visible) = DdaCursorVisibility.Merge(false, false, systemShowing: false);
        state.Apply(1, new CaptureCursorUpdate(CaptureCursorMode.Separate, hasPosition, visible, 0, 0, null));

        Assert.False(state.Current.Visible);
    }
}
