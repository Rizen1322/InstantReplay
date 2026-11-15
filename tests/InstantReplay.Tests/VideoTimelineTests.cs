using Aura.Core.Tools;
using Xunit;
namespace InstantReplay.Tests;
public sealed class VideoTimelineTests
{
    private static readonly VideoSegment[] Parts = [new("a", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8)), new("b", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14))];
    [Theory]
    [InlineData(-1, 0, 5)] [InlineData(2, 0, 7)] [InlineData(3, 1, 10)] [InlineData(99, 1, 14)]
    public void Position_maps_to_original_source(double time, int index, double source) => Assert.Equal((index, source), VideoTimeline.Locate(Parts, time));
    [Fact] public void Empty_timeline_has_no_source() => Assert.Equal(-1, VideoTimeline.Locate([], 1).Index);
    [Theory]
    [InlineData(0, 0)] [InlineData(2, 1)] [InlineData(6, 2)]
    public void Drop_snaps_to_nearest_clip_boundary(double time, int insertion) => Assert.Equal(insertion, VideoTimeline.Insertion(Parts, time));
    [Fact] public void Dragging_left_edge_preserves_end_and_audio()
    {
        var result = VideoTimeline.Trim(Parts[0] with { AudioTrack = 1 }, true, -99, 20, 60);
        Assert.Equal(TimeSpan.Zero, result.Start); Assert.Equal(Parts[0].End, result.End); Assert.Equal(1, result.AudioTrack);
    }
    [Fact] public void Dragging_right_edge_stops_at_end_of_source() => Assert.Equal(TimeSpan.FromSeconds(20), VideoTimeline.Trim(Parts[0], false, 99, 20, 60).End);
    [Fact] public void Edges_cannot_cross_or_create_empty_clip() => Assert.True(VideoTimeline.Trim(Parts[0], true, 99, 20, 60).Duration >= TimeSpan.FromMilliseconds(50));
    [Fact] public void Trimming_snaps_to_source_frame() => Assert.Equal(TimeSpan.FromSeconds(5.5), VideoTimeline.Trim(Parts[0], true, .501, 20, 30).Start);
    [Fact] public void Disabling_frame_snap_preserves_the_requested_trim() => Assert.Equal(TimeSpan.FromSeconds(5.501), VideoTimeline.Trim(Parts[0], true, .501, 20, 30, snap: false).Start);
    [Fact]
    public void Leading_gap_moves_video_but_keeps_audio_positions()
    {
        var shifted = new[] { Parts[0] with { Gap = TimeSpan.FromSeconds(2) }, Parts[1] };
        Assert.Equal(2, VideoTimeline.Offset(shifted, 0));
        Assert.Equal(5, VideoTimeline.Offset(shifted, 1));
        Assert.Equal(9, VideoTimeline.Offset(shifted, 2));
        Assert.Equal(-1, VideoTimeline.ClipAt(shifted, 1.5));
        Assert.Equal(0, VideoTimeline.ClipAt(shifted, 2.5));
        Assert.Equal((0, 5.5), VideoTimeline.Locate(shifted, 2.5));
        Assert.Equal((1, 10.5), VideoTimeline.Locate(shifted, 5.5));
    }
    [Fact] public void Empty_gap_does_not_change_offsets() => Assert.Equal(VideoTimeline.Offset(Parts, 1), VideoTimeline.Offset([Parts[0] with { Gap = TimeSpan.Zero }, Parts[1]], 1));
}
