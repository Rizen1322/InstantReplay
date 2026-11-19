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

    [Fact]
    public void GapAt_identifies_leading_and_mid_timeline_gaps()
    {
        var gapped = new[]
        {
            Parts[0] with { Gap = TimeSpan.FromSeconds(1.5) },
            Parts[1] with { Gap = TimeSpan.FromSeconds(2) }
        };
        Assert.Equal((0, 0, 1.5), VideoTimeline.GapAt(gapped, 0.5));
        Assert.Equal((-1, 0, 0), VideoTimeline.GapAt(gapped, 1.5));
        Assert.Equal((-1, 0, 0), VideoTimeline.GapAt(gapped, 3.0));
        var mid = VideoTimeline.GapAt(gapped, 5.25);
        Assert.Equal(1, mid.Index);
        Assert.Equal(4.5, mid.Start, 6);
        Assert.Equal(6.5, mid.End, 6);
        Assert.Equal((-1, 0, 0), VideoTimeline.GapAt(gapped, 6.5));
    }

    [Fact]
    public void PlanMove_lets_split_second_clip_slide_right_and_snap_back()
    {
        var split = new[]
        {
            new VideoSegment("a.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(2)),
            new VideoSegment("a.mp4", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5))
        };
        var movedRight = VideoTimeline.PlanMove(split, 1, 1.5, 4.5, 30, snapToFrames: true, [0, 2], snapTolerance: 0.08);
        Assert.Equal(1, movedRight.TargetIndex);
        Assert.Equal(TimeSpan.Zero, movedRight.Parts[0].Gap);
        Assert.Equal(1.5, movedRight.Parts[1].Gap.TotalSeconds, 4);

        var snappedBack = VideoTimeline.PlanMove(movedRight.Parts, 1, -1.46, 3.5, 30, snapToFrames: true, [0, 2], snapTolerance: 0.08);
        Assert.Equal(1, snappedBack.TargetIndex);
        Assert.Equal(TimeSpan.Zero, snappedBack.Parts[1].Gap);
        Assert.Equal(2.0, snappedBack.SnapGuide);
    }

    [Fact]
    public void PlanMove_slides_first_clip_within_gap_without_shifting_second_clip()
    {
        var gapped = new[]
        {
            new VideoSegment("a.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(2)),
            new VideoSegment("b.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(2)) { Gap = TimeSpan.FromSeconds(3) }
        };
        var slid = VideoTimeline.PlanMove(gapped, 0, 1.0, 2.0, 30, snapToFrames: true, [0, 5, 7], snapTolerance: 0.08);
        Assert.Equal(0, slid.TargetIndex);
        Assert.Equal(1.0, slid.Parts[0].Gap.TotalSeconds, 4);
        Assert.Equal(2.0, slid.Parts[1].Gap.TotalSeconds, 4);

        var snappedToSecond = VideoTimeline.PlanMove(slid.Parts, 0, 1.96, 4.5, 30, snapToFrames: true, [0, 5, 7], snapTolerance: 0.08);
        Assert.Equal(3.0, snappedToSecond.Parts[0].Gap.TotalSeconds, 4);
        Assert.Equal(TimeSpan.Zero, snappedToSecond.Parts[1].Gap);
        Assert.Equal(5.0, snappedToSecond.SnapGuide);
    }

    [Fact]
    public void PlanMove_reorders_clips_cleanly_and_handles_relinked_vs_unlinked_audio()
    {
        var first = new VideoSegment("a.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(2))
        {
            AudioLinked = false,
            AudioDetached = true,
            AudioEvents = [new AudioEvent(0, TimeSpan.Zero, TimeSpan.FromSeconds(2)) { SourcePath = "a.mp4", Position = TimeSpan.Zero }]
        };
        var second = new VideoSegment("b.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(3))
        {
            AudioLinked = true,
            AudioDetached = true,
            AudioEvents = [new AudioEvent(0, TimeSpan.Zero, TimeSpan.FromSeconds(3)) { SourcePath = "b.mp4", Position = TimeSpan.FromSeconds(2) }]
        };
        var plan = VideoTimeline.PlanMove([first, second], 1, -3.0, 0.5, 30, snapToFrames: true, [0, 2], snapTolerance: 0.08);
        Assert.Equal(0, plan.TargetIndex);
        Assert.Equal("b.mp4", plan.Parts[0].Path);
        Assert.Equal(TimeSpan.Zero, plan.Parts[0].Gap);
        Assert.Equal("a.mp4", plan.Parts[1].Path);
        Assert.Equal(TimeSpan.Zero, plan.Parts[1].Gap);
        Assert.Equal(TimeSpan.Zero, plan.Parts[0].AudioEvents[0].Position);
        Assert.Equal(TimeSpan.Zero, plan.Parts[1].AudioEvents[0].Position);
    }

    [Fact]
    public void SnapRange_and_SnapEdge_snap_left_or_right_edge_within_tolerance()
    {
        var (startRight, guideRight) = VideoTimeline.SnapRange(1.95, 3.0, [5.0], 0.08);
        Assert.Equal(2.0, startRight, 6);
        Assert.Equal(5.0, guideRight);

        var (unchanged, noGuide) = VideoTimeline.SnapRange(1.5, 3.0, [5.0], 0.08);
        Assert.Equal(1.5, unchanged, 6);
        Assert.Null(noGuide);

        var (edge, edgeGuide) = VideoTimeline.SnapEdge(4.04, [4.0], 0.08, 0, 10);
        Assert.Equal(4.0, edge, 6);
        Assert.Equal(4.0, edgeGuide);
    }
}
