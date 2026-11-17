using System.Globalization;
using System.Text;
using Aura.Core.Saving.Mp4;
using LibVLCSharp.Shared;

namespace Aura.Core.Tools;

public static partial class VideoAssembly
{
    private static async Task RenderWithVlcAsync(IReadOnlyList<VideoSegment> segments, string temp,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetDirectoryName(temp)!, ".aura-normalize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var parts = new VideoSegment[segments.Count];
        var files = new List<string>();
        try
        {
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var segment = segments[i];
                string source = segment.Path;
                TimeSpan? stop = segment.End;
                TimeSpan progressSpan = segment.End;
                double? prePrefix = null;
                // Вырезаем нужный кусок по ключевым кадрам до VLC: иначе при монтаже
                // конца двухминутной записи VLC перекодирует все две минуты с начала.
                if (string.Equals(Path.GetExtension(source), ".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string cut = Path.Combine(work, i.ToString(CultureInfo.InvariantCulture) + ".cut.mp4");
                    files.Add(cut);
                    try
                    {
                        double anchor = await Task.Run(() => Mp4Trim.CutWithAnchor(source, cut, segment.Start.TotalSeconds,
                            segment.End.TotalSeconds, audioTrack: -2, ct), ct);
                        var cutInfo = await ReadInfoAsync(cut, ct);
                        source = cut;
                        stop = null;
                        progressSpan = cutInfo.Duration > TimeSpan.Zero ? cutInfo.Duration : segment.Duration;
                        prePrefix = Math.Max(0, segment.Start.TotalSeconds - anchor);
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException)
                    {
                        try { File.Delete(cut); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                string converted = Path.Combine(work, i.ToString(CultureInfo.InvariantCulture) + ".mp4");
                files.Add(converted);
                int index = i;
                await NormalizeWithVlcAsync(source, converted, stop, progressSpan,
                    new Progress<double>(p => progress?.Report((index + p) / segments.Count * 0.5)), ct);
                if (prePrefix is not null)
                    try { File.Delete(source); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var result = await ReadInfoAsync(converted, ct);
                TimeSpan expected = prePrefix is not null ? segment.Duration : segment.End;
                if (result.Duration + TimeSpan.FromMilliseconds(100) < expected)
                    throw new InvalidDataException($"Не удалось подготовить видео для монтажа: длительность {result.Duration.TotalSeconds:F3} из {expected.TotalSeconds:F3} с.");
                TimeSpan start, end = segment.End;
                if (prePrefix is double prefix)
                {
                    // Если VLC при transcode проигнорировал elst, видео начинается с ключевого кадра anchor,
                    // а в хвосте могут быть лишние B-кадры: берём точный сдвиг от anchor, не от result.Duration.
                    double withElst = segment.Duration.TotalSeconds;
                    double withoutElst = withElst + prefix;
                    bool ignoredElst = prefix > 0.05 &&
                        Math.Abs(result.Duration.TotalSeconds - withoutElst) < Math.Abs(result.Duration.TotalSeconds - withElst);
                    start = ignoredElst ? TimeSpan.FromSeconds(prefix) : TimeSpan.Zero;
                    end = TimeSpan.FromSeconds(Math.Min(result.Duration.TotalSeconds, start.TotalSeconds + withElst));
                }
                else
                {
                    start = segment.Start;
                }
                parts[i] = segment with { Path = converted, Start = start, End = end };
            }
            await RenderWindowsAsync(parts, temp, targetMegabytes,
                new Progress<double>(p => progress?.Report(0.5 + p * 0.5)), ct, audioSegments: segments);
        }
        finally
        {
            foreach (string file in files)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(work); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static Task NormalizeWithVlcAsync(string input, string output, TimeSpan? stopTime, TimeSpan progressSpan,
        IProgress<double>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--quiet", "--no-lua", "--no-sout-display", "--avcodec-hw=none");
        using var media = new Media(vlc, new Uri(input));
        // shortcut: VLC 3 seek breaks MP4 muxing; normalize each input prefix,
        // replace with seek-safe transcoding if long source videos make this too slow.
        media.AddOption(":avcodec-hw=none");
        media.AddOption(":no-sout-audio");
        if (stopTime is TimeSpan stop)
            media.AddOption(":stop-time=" + stop.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        // The bundled VLC has MPEG-4 Part 2, but no 8-bit x264 encoder. Windows can
        // decode this intermediate even without the optional HEVC extension.
        media.AddOption(":sout=#transcode{vcodec=mp4v,venc=avcodec{codec=mpeg4},vb=24000}:std{access=file,mux=mp4,dst=" + ChainValue(output) + "}");
        using var player = new MediaPlayer(vlc);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => completed.TrySetResult();
        player.EncounteredError += (_, _) => completed.TrySetException(new InvalidOperationException("Не удалось прочитать видео встроенными кодеками."));
        double totalMs = Math.Max(1, progressSpan.TotalMilliseconds);
        player.TimeChanged += (_, e) => progress?.Report(Math.Clamp(e.Time / totalMs, 0, 1));
        using var cancellation = ct.Register(() => completed.TrySetCanceled(ct));
        try
        {
            if (!player.Play(media)) throw new InvalidOperationException("Не удалось подготовить видео для монтажа.");
            await completed.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        finally { player.Stop(); }
    }, ct);

    /// <summary>
    /// Значение опции для строки :sout. VLC разбирает её как цепочку (config_ChainCreate)
    /// и раскрывает значения через config_StringUnescape: специальные символы \, " и '
    /// экранируются обратной косой чертой, а само значение заключается в ДВОЙНЫЕ кавычки.
    /// Внутри кавычек } , и : — обычные символы, поэтому путь с такими знаками не ломает
    /// цепочку. Разделители пути Windows заменяются прямыми слэшами.
    /// </summary>
    internal static string ChainValue(string path)
    {
        var value = new StringBuilder(path.Length + 2);
        value.Append('"');
        foreach (char c in path)
        {
            if (c == '\\') value.Append('/');
            else
            {
                if (c is '"' or '\'') value.Append('\\');
                value.Append(c);
            }
        }
        return value.Append('"').ToString();
    }
}
