namespace Aura.Core.Tools;

public static class VideoTimeline
{
    public static (int Index, double SourceSeconds) Locate(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        seconds = Math.Max(0, seconds);
        for (int i = 0; i < parts.Count; i++)
        {
            double length = parts[i].Duration.TotalSeconds;
            if (seconds < length || i == parts.Count - 1)
                return (i, parts[i].Start.TotalSeconds + Math.Min(seconds, length));
            seconds -= length;
        }
        return (-1, 0);
    }

    public static double Offset(IReadOnlyList<VideoSegment> parts, int index) =>
        parts.Take(Math.Clamp(index, 0, parts.Count)).Sum(p => p.Duration.TotalSeconds);

    public static int Insertion(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            double length = parts[i].Duration.TotalSeconds;
            if (seconds < offset + length / 2) return i;
            offset += length;
        }
        return parts.Count;
    }

    public static VideoSegment Trim(VideoSegment part, bool left, double delta, double sourceDuration, double fps)
    {
        fps = double.IsFinite(fps) && fps > 0 ? fps : 30;
        double minimum = Math.Max(0.05, 1 / fps);
        double Snap(double value) => Math.Round(value * fps) / fps;
        return left
            ? part with { Start = TimeSpan.FromSeconds(Math.Clamp(Snap(part.Start.TotalSeconds + delta), 0, Math.Max(0, part.End.TotalSeconds - minimum))) }
            : part with { End = TimeSpan.FromSeconds(Math.Clamp(Snap(part.End.TotalSeconds + delta), part.Start.TotalSeconds + minimum, Math.Max(sourceDuration, part.End.TotalSeconds))) };
    }
}
