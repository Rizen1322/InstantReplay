using Aura.Core.Tools;
using Xunit;

namespace InstantReplay.Tests;

public sealed class VideoSegmentTests
{
    private static VideoSegment Part(double start, double end) => new("clip.mp4", TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)) { Id = Guid.Empty };
    private static VideoSegment[] WithoutIds(IEnumerable<VideoSegment> parts) => parts.Select(p => p with { Id = Guid.Empty }).ToArray();

    [Fact]
    public void Removing_middle_keeps_ordered_parts_with_original_timestamps()
    {
        var parts = VideoSegment.Remove([Part(0, 10)], TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7));
        Assert.Equal([Part(0, 3), Part(7, 10)], WithoutIds(parts));
        Assert.Equal(TimeSpan.FromSeconds(6), TimeSpan.FromTicks(parts.Sum(p => p.Duration.Ticks)));
    }

    [Fact]
    public void Further_cut_can_cross_an_existing_gap_without_resurrecting_removed_video()
    {
        var parts = VideoSegment.Remove([Part(0, 3), Part(7, 10)], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
        Assert.Equal([Part(0, 2), Part(8, 10)], WithoutIds(parts));
    }

    [Theory]
    [InlineData(0, 2, 2, 10)]
    [InlineData(8, 20, 0, 8)]
    [InlineData(10, 12, 0, 10)]
    [InlineData(0, 0.02, 0.02, 10)]
    public void Edge_cuts_do_not_create_empty_fragments(double start, double end, double keptStart, double keptEnd)
    {
        Assert.Equal([Part(keptStart, keptEnd)], WithoutIds(VideoSegment.Remove([Part(0, 10)], TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end))));
    }

    [Fact]
    public void Cannot_remove_all_video_and_the_original_plan_remains_unchanged()
    {
        var original = new[] { Part(0, 10) };
        Assert.Throws<InvalidOperationException>(() => VideoSegment.Remove(original, TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        Assert.Equal([Part(0, 10)], WithoutIds(original));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 1)]
    [InlineData(0, 0.01)]
    public void Invalid_export_ranges_are_rejected(double start, double end) => Assert.Throws<ArgumentException>(() => Part(start, end).Validate());

    [Theory]
    [InlineData("C:/video.MKV", true)]
    [InlineData("C:/video.webm", true)]
    [InlineData("C:/video.mov", true)]
    [InlineData("C:/video.mp4.exe", false)]
    [InlineData("C:/folder", false)]
    [InlineData("C:/image.png", false)]
    public void Import_accepts_video_extensions_only(string path, bool accepted) => Assert.Equal(accepted, VideoFiles.IsVideo(path));
}
