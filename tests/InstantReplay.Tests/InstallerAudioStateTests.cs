using InstantReplaySetup;
using Xunit;

namespace InstantReplay.Tests;

public sealed class InstallerAudioStateTests
{
    [Fact]
    public void StartsQuietAndUnmuted()
    {
        var audio = new InstallerAudioState();

        Assert.False(audio.IsMuted);
        Assert.Equal(0.12, audio.Volume, precision: 3);
    }

    [Fact]
    public void ToggleMutesAndRestoresQuietVolume()
    {
        var audio = new InstallerAudioState();

        audio.ToggleMute();
        Assert.True(audio.IsMuted);
        Assert.Equal(0, audio.Volume);

        audio.ToggleMute();
        Assert.False(audio.IsMuted);
        Assert.Equal(0.12, audio.Volume, precision: 3);
    }
}
