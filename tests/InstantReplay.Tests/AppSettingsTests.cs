using Aura.Core.Settings;
using Xunit;

namespace InstantReplay.Tests;

public class AppSettingsTests
{
    [Fact]
    public void NormalizeОграничиваетПовреждённыеЗначения()
    {
        var settings = new AppSettings
        {
            ReplayLengthSeconds = int.MaxValue,
            VerticalResolution = -1,
            Fps = 0,
            BitrateMbps = -50,
            MonitorIndex = -3,
            MicNoiseGateDb = float.PositiveInfinity,
            AttachmentSizeMb = 0,
            NotificationDurationSeconds = double.NaN,
            SidebarWidth = double.PositiveInfinity,
            UiScale = -4,
            Codec = (VideoCodec)999,
            TrackMode = (AudioTrackMode)999,
            SaveRootPath = null!,
            ScreenshotFolder = "   ",
            FileNameTemplate = "  "
        };

        settings.Normalize();

        Assert.Equal(1800, settings.ReplayLengthSeconds);
        Assert.Equal(360, settings.VerticalResolution);
        Assert.Equal(15, settings.Fps);
        Assert.Equal(1, settings.BitrateMbps);
        Assert.Equal(0, settings.MonitorIndex);
        Assert.Equal(-44, settings.MicNoiseGateDb);
        Assert.Equal(2, settings.AttachmentSizeMb);
        Assert.Equal(3.5, settings.NotificationDurationSeconds);
        Assert.Equal(236, settings.SidebarWidth);
        Assert.Equal(0.75, settings.UiScale);
        Assert.Equal(VideoCodec.H264, settings.Codec);
        Assert.Equal(AudioTrackMode.Mixed, settings.TrackMode);
        Assert.False(string.IsNullOrWhiteSpace(settings.SaveRootPath));
        Assert.False(string.IsNullOrWhiteSpace(settings.ScreenshotFolder));
        Assert.Equal("{game} {date} - {time}", settings.FileNameTemplate);
    }
}
