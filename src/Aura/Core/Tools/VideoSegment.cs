namespace Aura.Core.Tools;

public sealed record VideoSegment(string Path, TimeSpan Start, TimeSpan End, int AudioTrack = -1)
{
    public IReadOnlyList<AudioEvent> AudioEvents { get; init; } = [];
    public bool AudioLinked { get; init; } = true;
    public bool AudioDetached { get; init; }
    /// <summary>Пустой чёрный участок перед клипом: видео можно сдвинуть, оставив звук на месте.</summary>
    public TimeSpan Gap { get; init; }
    public TimeSpan Duration => End - Start;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Range => $"{Start:hh\\:mm\\:ss\\.fff} — {End:hh\\:mm\\:ss\\.fff}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path)) throw new ArgumentException("Не указан видеофайл.");
        if (Start < TimeSpan.Zero || Duration < TimeSpan.FromMilliseconds(50))
            throw new ArgumentException("Фрагмент должен быть не короче 0,05 секунды.");
        if (Gap < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Gap));
        if (AudioTrack < -1) throw new ArgumentOutOfRangeException(nameof(AudioTrack));
        foreach (var audio in AudioEvents)
            if (audio.Track < 0 || audio.Start < TimeSpan.Zero || audio.End <= audio.Start ||
                audio.Position < TimeSpan.Zero || audio.SourcePath is not null && string.IsNullOrWhiteSpace(audio.SourcePath) ||
                !double.IsFinite(audio.Volume) || audio.Volume is < 0 or > 1)
                throw new ArgumentException("Недопустимый звуковой фрагмент.");
        foreach (var track in AudioEvents.Where(a => a.Position is null).GroupBy(a => a.Track))
        {
            TimeSpan end = TimeSpan.Zero;
            foreach (var audio in track.OrderBy(a => a.Start))
            {
                if (audio.Start < end) throw new ArgumentException("Звуковые фрагменты одной дорожки перекрываются.");
                end = audio.End;
            }
        }
    }

    public static IReadOnlyList<VideoSegment> Remove(
        IReadOnlyList<VideoSegment> segments, TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        var kept = new List<VideoSegment>();
        foreach (var segment in segments)
        {
            if (end <= segment.Start || start >= segment.End) { kept.Add(segment); continue; }
            if (start - segment.Start >= TimeSpan.FromMilliseconds(50)) kept.Add(segment with { End = start });
            if (segment.End - end >= TimeSpan.FromMilliseconds(50)) kept.Add(segment with { Start = end });
        }
        if (kept.Count == 0) throw new InvalidOperationException("Нельзя удалить весь ролик. Оставьте хотя бы один фрагмент.");
        return kept;
    }
}

public static class VideoFiles
{
    public const string Filter = "Видео|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.ts;*.mts;*.m2ts|Все файлы|*.*";
    public static bool IsVideo(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is
        ".mp4" or ".mkv" or ".mov" or ".webm" or ".avi" or ".m4v" or ".wmv" or ".ts" or ".mts" or ".m2ts";
}
