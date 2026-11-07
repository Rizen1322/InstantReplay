using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Полный путь «кадр DDA → видимость курсора» (Resolve + трекер + состояние брокера).
/// Главный случай: мышь стоит, а картинка меняется. DDA про указатель молчит, и
/// раньше курсор от этого прятался на каждом таком кадре.
/// </summary>
public sealed class DdaCursorResolveTests
{
    private sealed class Pipeline
    {
        private readonly DdaCursorTracker _tracker = new();
        private bool _lastDdaVisible;
        public readonly DdaCursorState State = new();

        public Pipeline() { _tracker.Reset(); State.Reset(1); }

        /// <summary>Кадр DDA: ddaUpdate — пришло ли обновление указателя.</summary>
        public void Frame(bool ddaUpdate, bool ddaVisible, bool? systemShowing, int x = 100, int y = 100)
        {
            if (ddaUpdate) _lastDdaVisible = ddaVisible;
            var (hasPosition, visible, hasVisibility) =
                DdaCursorVisibility.Resolve(ddaUpdate, ddaVisible, systemShowing, _lastDdaVisible);
            _tracker.Track(new CaptureCursorUpdate(CaptureCursorMode.Separate, hasPosition, visible,
                ddaUpdate ? x : 0, ddaUpdate ? y : 0, null, HasVisibility: hasVisibility));
            State.Apply(1, _tracker.Capture);   // каждый кадр публикуется
            _tracker.Delivered();
        }
    }

    [Fact]
    public void Still_visible_cursor_stays_visible_while_screen_changes()
    {
        var p = new Pipeline();
        p.Frame(ddaUpdate: true, ddaVisible: true, systemShowing: true, x: 500, y: 300);
        Assert.True(p.State.Current.Visible);

        // Мышь стоит, картинка меняется: DDA молчит, система — «показан»
        for (int i = 0; i < 10; i++)
            p.Frame(ddaUpdate: false, ddaVisible: false, systemShowing: true);

        Assert.True(p.State.Current.Visible);
        Assert.Equal((500, 300), (p.State.Current.X, p.State.Current.Y));
    }

    [Fact]
    public void Game_hides_then_shows_cursor_without_dda_update()
    {
        var p = new Pipeline();
        p.Frame(ddaUpdate: true, ddaVisible: true, systemShowing: true, x: 960, y: 540);

        // Игра спрятала курсор ShowCursor(FALSE), DDA этого не заметила
        p.Frame(ddaUpdate: false, ddaVisible: false, systemShowing: false);
        Assert.False(p.State.Current.Visible);

        // Игра вернула курсор (меню), мышь не трогали
        p.Frame(ddaUpdate: false, ddaVisible: false, systemShowing: true);
        Assert.True(p.State.Current.Visible);
        Assert.Equal((960, 540), (p.State.Current.X, p.State.Current.Y));
    }

    [Fact]
    public void Cursor_hidden_by_dda_is_not_shown_by_system_flag()
    {
        var p = new Pipeline();
        p.Frame(ddaUpdate: true, ddaVisible: false, systemShowing: true);
        p.Frame(ddaUpdate: false, ddaVisible: false, systemShowing: true);
        Assert.False(p.State.Current.Visible);
    }

    [Fact]
    public void Unknown_system_and_silent_dda_change_nothing()
    {
        Assert.Equal((false, false, false), DdaCursorVisibility.Resolve(false, true, null, lastDdaVisible: true));
    }

    [Fact]
    public void System_hide_wins_over_dda_update()
    {
        Assert.Equal((true, false, true), DdaCursorVisibility.Resolve(true, true, false, lastDdaVisible: true));
    }
}
