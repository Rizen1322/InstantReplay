namespace Aura.Core.Tools;

public static class VideoTimeline
{
    public static (int Index, double SourceSeconds) Locate(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        seconds = Math.Max(0, seconds);
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            double length = parts[i].Duration.TotalSeconds;
            if (seconds < offset + length || i == parts.Count - 1)
                return (i, parts[i].Start.TotalSeconds + Math.Clamp(seconds - offset, 0, length));
            offset += length;
        }
        return (-1, 0);
    }

    public static double Offset(IReadOnlyList<VideoSegment> parts, int index)
    {
        double offset = 0;
        for (int i = 0; i < Math.Clamp(index, 0, parts.Count); i++)
            offset += parts[i].Gap.TotalSeconds + parts[i].Duration.TotalSeconds;
        if (index >= 0 && index < parts.Count) offset += parts[index].Gap.TotalSeconds;
        return offset;
    }

    public static int Insertion(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            double length = parts[i].Duration.TotalSeconds;
            if (seconds < offset + length / 2) return i;
            offset += length;
        }
        return parts.Count;
    }

    /// <summary>Индекс клипа под курсором; -1 в пустом промежутке перед клипом.</summary>
    public static int ClipAt(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            double length = parts[i].Duration.TotalSeconds;
            if (seconds >= offset && seconds < offset + length) return i;
            offset += length;
        }
        return -1;
    }

    public static VideoSegment Trim(VideoSegment part, bool left, double delta, double sourceDuration, double fps, bool snap = true)
    {
        fps = double.IsFinite(fps) && fps > 0 ? fps : 30;
        double minimum = Math.Max(0.05, 1 / fps);
        double Snap(double value) => snap ? Math.Round(value * fps) / fps : value;
        return left
            ? part with { Start = TimeSpan.FromSeconds(Math.Clamp(Snap(part.Start.TotalSeconds + delta), 0, Math.Max(0, part.End.TotalSeconds - minimum))) }
            : part with { End = TimeSpan.FromSeconds(Math.Clamp(Snap(part.End.TotalSeconds + delta), part.Start.TotalSeconds + minimum, Math.Max(sourceDuration, part.End.TotalSeconds))) };
    }
}
