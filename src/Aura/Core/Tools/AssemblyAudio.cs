namespace Aura.Core.Tools;

public sealed record AudioEvent(int Track, TimeSpan Start, TimeSpan End, double Volume = 1,
    bool Muted = false, bool Removed = false)
{
    public TimeSpan? Position { get; init; }
    public string? SourcePath { get; init; }
    public double Gain => Muted || Removed ? 0 : Volume;
}

public sealed record AssemblySound(int Owner, int Event, string Path, int Track, TimeSpan Start, TimeSpan End,
    TimeSpan Position, double Gain, bool Removed = false)
{
    public TimeSpan Duration => End - Start;
    public TimeSpan Finish => Position + Duration;
    public bool Detached => Event >= 0;
}

public sealed record AssemblyAudioTrack(double Volume = 1, bool Muted = false, bool Solo = false)
{
    public double Gain(bool anySolo) => Muted || anySolo && !Solo ? 0 : Volume;
}

public static class AssemblyAudio
{
    public static IReadOnlyList<AudioEvent> Events(VideoSegment part, int track)
    {
        if (part.AudioDetached) return part.AudioEvents.Where(e => e.Track == track && e.Position is not null).ToArray();
        var events = new List<AudioEvent>();
        TimeSpan cursor = part.Start;
        foreach (var edit in part.AudioEvents.Where(e => e.Position is null && e.Track == track && e.End > part.Start && e.Start < part.End).OrderBy(e => e.Start))
        {
            TimeSpan start = edit.Start > part.Start ? edit.Start : part.Start;
            TimeSpan end = edit.End < part.End ? edit.End : part.End;
            if (start > cursor) events.Add(new(track, cursor, start));
            events.Add(edit with { Start = start, End = end });
            cursor = end;
        }
        if (cursor < part.End) events.Add(new(track, cursor, part.End));
        return events;
    }

    public static VideoSegment Split(VideoSegment part, int track, TimeSpan at)
    {
        var events = Events(part, track).ToList();
        int index = events.FindIndex(e => at - e.Start >= TimeSpan.FromMilliseconds(50) && e.End - at >= TimeSpan.FromMilliseconds(50));
        if (index < 0) return part;
        var original = events[index];
        events[index] = original with { End = at };
        events.Insert(index + 1, original with { Start = at, Position = original.Position + at - original.Start });
        return ReplaceTrack(part, track, events);
    }

    public static VideoSegment Change(VideoSegment part, int track, TimeSpan at, Func<AudioEvent, AudioEvent> change)
    {
        var events = Events(part, track).ToArray();
        int index = Array.FindIndex(events, e => at >= e.Start && at < e.End);
        if (index < 0) return part;
        events[index] = change(events[index]);
        return ReplaceTrack(part, track, events);
    }

    private static VideoSegment ReplaceTrack(VideoSegment part, int track, IEnumerable<AudioEvent> events)
    {
        if (part.AudioDetached) return part with { AudioEvents = part.AudioEvents.Where(e => e.Track != track).Concat(events).ToArray() };
        var outside = part.AudioEvents.Where(e => e.Position is not null || e.Track != track || e.End <= part.Start || e.Start >= part.End).ToList();
        foreach (var edit in part.AudioEvents.Where(e => e.Track == track))
        {
            if (edit.Start < part.Start && edit.End > part.Start) outside.Add(edit with { End = part.Start });
            if (edit.End > part.End && edit.Start < part.End) outside.Add(edit with { Start = part.End });
        }
        return part with { AudioEvents = outside.Concat(events).ToArray() };
    }

    public static VideoSegment ApplyTracks(VideoSegment part, int count, IReadOnlyList<AssemblyAudioTrack> tracks)
    {
        bool solo = tracks.Any(t => t.Solo);
        return part with
        {
            AudioEvents = (part.AudioDetached ? [] : Enumerable.Range(0, count).SelectMany(t => Events(part, t))).Where(e => e.Position is null)
                .Concat(part.AudioEvents.Where(e => e.Position is not null))
                .Select(e => e with { Volume = e.Volume * (e.Track < tracks.Count ? tracks[e.Track].Gain(solo) : solo ? 0 : 1) }).ToArray()
        };
    }

    public static VideoSegment Unlink(VideoSegment part, double offset, int count)
    {
        if (part.AudioDetached) return part with { AudioLinked = false };
        return part with
        {
            AudioLinked = false, AudioDetached = true,
            AudioEvents = part.AudioEvents.Where(e => e.Position is not null).Concat(Enumerable.Range(0, count)
                .Where(t => part.AudioTrack < 0 || part.AudioTrack == t).SelectMany(t => Events(part, t).Select(e => e with
                { Position = TimeSpan.FromSeconds(offset) + e.Start - part.Start, SourcePath = part.Path }))).ToArray()
        };
    }

    public static (VideoSegment Left, VideoSegment Right) SplitVideo(VideoSegment part, TimeSpan at, double offset)
    {
        var left = part.AudioEvents.Where(a => a.Position is null).ToList();
        var right = left.ToList();
        TimeSpan cut = TimeSpan.FromSeconds(offset) + at - part.Start;
        foreach (var audio in part.AudioEvents.Where(a => a.Position is not null))
        {
            if (!part.AudioLinked || audio.SourcePath is not null && audio.SourcePath != part.Path || audio.Position + audio.End - audio.Start <= cut) left.Add(audio);
            else if (audio.Position >= cut) right.Add(audio);
            else
            {
                TimeSpan source = audio.Start + cut - audio.Position!.Value;
                left.Add(audio with { End = source }); right.Add(audio with { Start = source, Position = cut });
            }
        }
        return (part with { End = at, AudioEvents = left.ToArray() }, part with { Id = Guid.NewGuid(), Start = at, Gap = TimeSpan.Zero, AudioEvents = right.ToArray() });
    }

    public static IReadOnlyList<AssemblySound> Sounds(IReadOnlyList<VideoSegment> parts, Func<string, int> trackCount)
    {
        var result = new List<AssemblySound>();
        TimeSpan offset = TimeSpan.Zero;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            offset += part.Gap;
            for (int e = 0; e < part.AudioEvents.Count; e++)
            {
                var audio = part.AudioEvents[e];
                if (audio.Position is TimeSpan position)
                    result.Add(new(i, e, audio.SourcePath ?? part.Path, audio.Track, audio.Start, audio.End, position, audio.Gain, audio.Removed));
            }
            if (!part.AudioDetached)
            for (int track = 0; track < trackCount(part.Path); track++)
            {
                if (part.AudioTrack >= 0 && track != part.AudioTrack) continue;
                foreach (var audio in Events(part, track))
                    result.Add(new(i, -1, part.Path, track, audio.Start, audio.End, offset + audio.Start - part.Start, audio.Gain, audio.Removed));
            }
            offset += part.Duration;
        }
        return result;
    }

    public static double GainAt(VideoSegment part, int track, double sourceSeconds) =>
        sourceSeconds < part.Start.TotalSeconds || sourceSeconds >= part.End.TotalSeconds || part.AudioTrack >= 0 && part.AudioTrack != track ? 0 :
        part.AudioEvents.FirstOrDefault(e => e.Position is null && e.Track == track && sourceSeconds >= e.Start.TotalSeconds && sourceSeconds < e.End.TotalSeconds)?.Gain ?? 1;
}
