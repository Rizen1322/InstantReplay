using Aura.Core.Engine;
using Xunit;

namespace InstantReplay.Tests;

public sealed class RecordingOutputLockTests
{
    private static readonly (int, int) Qhd = (2560, 1440);
    private static readonly (int, int) Fhd = (1920, 1080);

    // Каждый шаг — пересборка конвейера: что в этот момент вернул реестр и что дал захват.
    private static (int, int) Rebuild(RecordingOutputLock l, (int, int)? desktop, (int, int) source,
                                      int monitor = 0, int vertical = 1440) =>
        RecordingOutputLock.OutputFor(l.Resolve(monitor, vertical, desktop, source, out _), vertical);

    [Fact]
    public void Log_2026_11_11_monitor_wake_keeps_1440p()
    {
        var l = new RecordingOutputLock();
        Assert.Equal(Qhd, Rebuild(l, Qhd, Qhd));   // 14:54:53 старт
        Assert.Equal(Qhd, Rebuild(l, Fhd, Fhd));   // 14:54:55 временный 1080p
        Assert.Equal(Qhd, Rebuild(l, Fhd, Qhd));   // 14:54:56 реестр ещё врёт
    }

    [Fact]
    public void Stretched_4x3_game_and_alt_tab_keep_output()
    {
        var l = new RecordingOutputLock();
        Assert.Equal(Qhd, Rebuild(l, Qhd, Qhd));
        Assert.Equal(Qhd, Rebuild(l, Qhd, (1440, 1080)));  // игра 4:3
        Assert.Equal(Qhd, Rebuild(l, (1440, 1080), Qhd));  // Alt-Tab, границы/реестр устарели
        Assert.Equal(Qhd, Rebuild(l, Qhd, (1440, 1080)));  // обратно в игру
    }

    [Fact]
    public void Rapid_mode_flapping_never_changes_output()
    {
        var l = new RecordingOutputLock();
        Rebuild(l, Qhd, Qhd);
        (int, int)[] modes = [Fhd, (1440, 1080), (1024, 768), Qhd, (1280, 1024)];
        for (int i = 0; i < 50; i++)
        {
            var m = modes[i % modes.Length];
            Assert.Equal(Qhd, Rebuild(l, i % 2 == 0 ? m : null, m));
        }
    }

    [Fact]
    public void Dsr_desktop_is_capped_by_setting()
    {
        var l = new RecordingOutputLock();
        Assert.Equal(Qhd, Rebuild(l, (3840, 2160), (3840, 2160)));
        Assert.Equal(Qhd, Rebuild(l, Qhd, Qhd)); // DSR выключили посреди сеанса
    }

    [Fact]
    public void Other_monitor_or_resolution_setting_relocks()
    {
        var l = new RecordingOutputLock();
        Assert.Equal(Qhd, Rebuild(l, Qhd, Qhd, monitor: 0));
        Assert.Equal(Fhd, Rebuild(l, Fhd, Fhd, monitor: 1));
        Assert.Equal((1280, 720), Rebuild(l, Fhd, Fhd, monitor: 1, vertical: 720));
    }

    [Fact]
    public void Reset_rereads_desktop()
    {
        var l = new RecordingOutputLock();
        Rebuild(l, Qhd, Qhd);
        l.Reset();
        Assert.Equal(Fhd, Rebuild(l, Fhd, Fhd));
    }

    [Fact]
    public void Unreadable_registry_falls_back_to_source_once()
    {
        var l = new RecordingOutputLock();
        Assert.Equal(Qhd, Rebuild(l, null, Qhd));
        Assert.Equal(Qhd, Rebuild(l, null, Fhd));
    }

    [Theory]
    [InlineData(2560, 1440, 1440, 2560, 1440)]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(1920, 1080, 1440, 1920, 1080)] // вверх не растягиваем
    [InlineData(3440, 1440, 1080, 2580, 1080)] // 21:9
    [InlineData(1366, 768, 720, 1280, 720)]
    public void OutputFor_keeps_aspect_and_even_sizes(int bw, int bh, int v, int ew, int eh)
    {
        var (w, h) = RecordingOutputLock.OutputFor((bw, bh), v);
        Assert.Equal((ew, eh), (w, h));
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }
}
