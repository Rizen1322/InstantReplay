using Aura.Core.Saving.Mp4;
using Xunit;

namespace InstantReplay.Tests;

public sealed class Mp4KeyframesTests
{
    private static readonly double[] Keys = [0, 2, 4, 6];

    [Fact]
    public void Start_snaps_back_to_keyframe_so_audio_starts_with_video()
    {
        Assert.Equal(2, Mp4Keyframes.StartFor(Keys, 3.1, 8));
        Assert.Equal(4, Mp4Keyframes.StartFor(Keys, 4, 8));
        Assert.Equal(6, Mp4Keyframes.StartFor(Keys, 20, 30));
    }

    [Fact]
    public void Start_before_first_keyframe_uses_first_inside_range()
    {
        double[] late = [0.5, 2.5];
        Assert.Equal(0.5, Mp4Keyframes.StartFor(late, 0.1, 3));
        Assert.Null(Mp4Keyframes.StartFor(late, 0.1, 0.4));
        Assert.Null(Mp4Keyframes.StartFor([], 1, 3));
    }

    [Fact]
    public void Missing_file_gives_no_keyframes()
    {
        Assert.Empty(Mp4Keyframes.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4")));
    }
}
