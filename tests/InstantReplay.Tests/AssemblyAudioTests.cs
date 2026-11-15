using Aura.Core.Tools;
using Xunit;

namespace InstantReplay.Tests;

public sealed class AssemblyAudioTests
{
    private static readonly VideoSegment Part = new("source.mp4", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));

    [Fact]
    public void Split_audio_does_not_split_video_or_the_other_track()
    {
        var split = AssemblyAudio.Split(Part, 1, TimeSpan.FromSeconds(4));
        Assert.Equal(Part.Start, split.Start);
        Assert.Equal(Part.End, split.End);
        Assert.Equal(2, AssemblyAudio.Events(split, 1).Count);
        Assert.Single(AssemblyAudio.Events(split, 0));
        Assert.Empty(Part.AudioEvents);
        split.Validate();
    }

    [Fact]
    public void Deleted_audio_keeps_a_silent_gap_and_preserves_the_other_track()
    {
        var split = AssemblyAudio.Split(Part, 0, TimeSpan.FromSeconds(4));
        var removed = AssemblyAudio.Change(split, 0, TimeSpan.FromSeconds(3), e => e with { Removed = true });
        Assert.Equal(0, AssemblyAudio.GainAt(removed, 0, 3));
        Assert.Equal(1, AssemblyAudio.GainAt(removed, 0, 5));
        Assert.Equal(1, AssemblyAudio.GainAt(removed, 1, 3));
        Assert.Equal(1, AssemblyAudio.GainAt(split, 0, 3));
    }

    [Fact]
    public void Track_solo_and_volume_are_applied_to_export_without_changing_the_project()
    {
        var changed = AssemblyAudio.Change(Part, 1, TimeSpan.FromSeconds(3), e => e with { Volume = .5 });
        var export = AssemblyAudio.ApplyTracks(changed, 2, [new(), new(.5, Solo: true)]);
        Assert.Equal(0, AssemblyAudio.GainAt(export, 0, 3));
        Assert.Equal(.25, AssemblyAudio.GainAt(export, 1, 3));
        Assert.Equal(.5, AssemblyAudio.GainAt(changed, 1, 3));
    }

    [Fact]
    public void Editing_a_trimmed_part_preserves_edits_outside_the_current_trim()
    {
        var full = Part with { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10) };
        full = AssemblyAudio.Change(full, 0, TimeSpan.FromSeconds(3), e => e with { Volume = .5 });
        var trimmed = full with { Start = Part.Start, End = Part.End };
        var changed = AssemblyAudio.Split(trimmed, 0, TimeSpan.FromSeconds(4));
        var expanded = changed with { Start = full.Start, End = full.End };
        Assert.Equal(.5, AssemblyAudio.GainAt(expanded, 0, 1));
        Assert.Equal(.5, AssemblyAudio.GainAt(expanded, 0, 8));
        expanded.Validate();
    }

    [Fact]
    public void Invalid_or_overlapping_audio_edits_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => (Part with { AudioEvents = [new(0, Part.Start, Part.End, double.NaN)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Part with { AudioEvents = [new(0, Part.Start, Part.End), new(0, Part.Start, Part.End)] }).Validate());
    }
}
