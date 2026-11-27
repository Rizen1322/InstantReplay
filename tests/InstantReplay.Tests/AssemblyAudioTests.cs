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
    public void Muted_placed_audio_does_not_mute_the_linked_track()
    {
        var part = Part with
        {
            AudioEvents = [new AudioEvent(0, Part.Start, Part.End, Muted: true) { Position = TimeSpan.FromSeconds(1) }]
        };
        Assert.Equal(1, AssemblyAudio.GainAt(part, 0, 3));
        Assert.Equal(0, AssemblyAudio.Sounds([part], _ => 1).Single(s => s.Detached).Gain);
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

    [Fact]
    public void Unlinked_audio_keeps_its_position_when_video_is_trimmed_or_reordered()
    {
        var detached = AssemblyAudio.Unlink(Part, 7, 2);
        var changedVideo = detached with { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4) };
        var sounds = AssemblyAudio.Sounds([changedVideo, Part], _ => 2);
        Assert.Equal(TimeSpan.FromSeconds(7), sounds[0].Position);
        Assert.Equal(Part.Start, sounds[0].Start);
        Assert.Equal(Part.End, sounds[0].End);
        var reordered = AssemblyAudio.Sounds([Part, changedVideo], _ => 2).Where(s => s.Detached).ToArray();
        Assert.All(reordered, s => Assert.Equal(TimeSpan.FromSeconds(7), s.Position));
    }

    [Fact]
    public void Splitting_unlinked_video_does_not_duplicate_or_cut_its_audio()
    {
        var detached = AssemblyAudio.Unlink(Part, 0, 2);
        var (left, right) = AssemblyAudio.SplitVideo(detached, TimeSpan.FromSeconds(4), 0);
        var sounds = AssemblyAudio.Sounds([left, right], _ => 2);
        Assert.Equal(2, sounds.Count);
        Assert.All(sounds, s => Assert.Equal(Part.Duration, s.Duration));
        var linked = detached with { AudioLinked = true };
        (left, right) = AssemblyAudio.SplitVideo(linked, TimeSpan.FromSeconds(4), 0);
        sounds = AssemblyAudio.Sounds([left, right], _ => 2);
        Assert.Equal(4, sounds.Count);
        Assert.Equal(Part.Duration.TotalSeconds * 2, sounds.Sum(s => s.Duration.TotalSeconds));
    }

    [Fact]
    public void Detached_audio_preserves_source_and_gain_when_owned_by_another_video()
    {
        var detached = AssemblyAudio.Unlink(Part, 1, 2);
        var other = Part with { Path = "other.mp4", AudioEvents = detached.AudioEvents };
        var export = AssemblyAudio.ApplyTracks(other, 2, [new(.5), new(Muted: true)]);
        var sounds = AssemblyAudio.Sounds([export], _ => 2).Where(s => s.Detached).ToArray();
        Assert.All(sounds, s => Assert.Equal(Part.Path, s.Path));
        Assert.Equal(.5, sounds[0].Gain); Assert.Equal(0, sounds[1].Gain);
    }

    [Fact]
    public void Video_gap_delays_linked_sound_but_does_not_move_detached_sound()
    {
        var gapped = Part with { Gap = TimeSpan.FromSeconds(2) };
        var linked = AssemblyAudio.Sounds([gapped], _ => 1).Single();
        Assert.Equal(TimeSpan.FromSeconds(2), linked.Position);
        var detached = AssemblyAudio.Unlink(gapped, 2, 1);
        var sound = AssemblyAudio.Sounds([detached], _ => 1).Single();
        Assert.Equal(TimeSpan.FromSeconds(2), sound.Position);
        Assert.Equal(Part.Start, sound.Start);
        Assert.Equal(Part.End, sound.End);
    }

    [Fact]
    public void Split_video_keeps_the_gap_only_on_the_left_part()
    {
        var gapped = Part with { Gap = TimeSpan.FromSeconds(2) };
        var (left, right) = AssemblyAudio.SplitVideo(gapped, TimeSpan.FromSeconds(4), 2);
        Assert.Equal(TimeSpan.FromSeconds(2), left.Gap);
        Assert.Equal(TimeSpan.Zero, right.Gap);
    }

    [Fact]
    public void Negative_gap_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => (Part with { Gap = TimeSpan.FromSeconds(-1) }).Validate());
}
