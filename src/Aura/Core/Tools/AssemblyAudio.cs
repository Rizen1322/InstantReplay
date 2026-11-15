namespace Aura.Core.Tools;

public sealed record AudioEvent(int Track, TimeSpan Start, TimeSpan End, double Volume = 1,
    bool Muted = false, bool Removed = false)
{
    public double Gain => Muted || Removed ? 0 : Volume;
}

public sealed record AssemblyAudioTrack(double Volume = 1, bool Muted = false, bool Solo = false)
{
    public double Gain(bool anySolo) => Muted || anySolo && !Solo ? 0 : Volume;
}

public static class AssemblyAudio
{
    public static IReadOnlyList<AudioEvent> Events(VideoSegment part, int track)
    {
        var events = new List<AudioEvent>();
        TimeSpan cursor = part.Start;
        foreach (var edit in part.AudioEvents.Where(e => e.Track == track && e.End > part.Start && e.Start < part.End).OrderBy(e => e.Start))
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
        events.Insert(index + 1, original with { Start = at });
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
        var outside = part.AudioEvents.Where(e => e.Track != track || e.End <= part.Start || e.Start >= part.End).ToList();
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
            AudioEvents = Enumerable.Range(0, count).SelectMany(t => Events(part, t)
                .Select(e => e with { Volume = e.Volume * (t < tracks.Count ? tracks[t].Gain(solo) : solo ? 0 : 1) })).ToArray()
        };
    }

    public static double GainAt(VideoSegment part, int track, double sourceSeconds) =>
        sourceSeconds < part.Start.TotalSeconds || sourceSeconds >= part.End.TotalSeconds || part.AudioTrack >= 0 && part.AudioTrack != track ? 0 :
        part.AudioEvents.FirstOrDefault(e => e.Track == track && sourceSeconds >= e.Start.TotalSeconds && sourceSeconds < e.End.TotalSeconds)?.Gain ?? 1;
}
