using System.Globalization;
using System.Text;
using LibVLCSharp.Shared;

namespace Aura.Core.Tools;

public static partial class VideoAssembly
{
    private static async Task RenderWithVlcAsync(IReadOnlyList<VideoSegment> segments, string temp,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetDirectoryName(temp)!, ".aura-normalize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        try
        {
            var inputs = segments.GroupBy(s => Path.GetFullPath(s.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Path: g.Key, End: g.Max(s => s.End))).ToArray();
            for (int i = 0; i < inputs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var input = inputs[i];
                string converted = Path.Combine(work, i.ToString(CultureInfo.InvariantCulture) + ".mp4");
                files.Add(converted);
                int index = i;
                await NormalizeWithVlcAsync(input.Path, converted, input.End,
                    new Progress<double>(p => progress?.Report((index + p) / inputs.Length * 0.5)), ct);
                var result = await ReadInfoAsync(converted, ct);
                if (result.Duration + TimeSpan.FromMilliseconds(100) < input.End)
                    throw new InvalidDataException($"Не удалось подготовить видео для монтажа: длительность {result.Duration.TotalSeconds:F3} из {input.End.TotalSeconds:F3} с.");
                normalized.Add(input.Path, converted);
            }
            var parts = segments.Select(s => s with { Path = normalized[Path.GetFullPath(s.Path)] }).ToArray();
            var originalAudio = normalized.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            await RenderWindowsAsync(parts, temp, targetMegabytes,
                new Progress<double>(p => progress?.Report(0.5 + p * 0.5)), ct, originalAudio);
        }
        finally
        {
            foreach (string file in files)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(work); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static Task NormalizeWithVlcAsync(string input, string output, TimeSpan end,
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
        media.AddOption(":stop-time=" + end.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        // The bundled VLC has MPEG-4 Part 2, but no 8-bit x264 encoder. Windows can
        // decode this intermediate even without the optional HEVC extension.
        media.AddOption(":sout=#transcode{vcodec=mp4v,venc=avcodec{codec=mpeg4},vb=24000}:std{access=file,mux=mp4,dst=" + ChainValue(output) + "}");
        using var player = new MediaPlayer(vlc);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => completed.TrySetResult();
        player.EncounteredError += (_, _) => completed.TrySetException(new InvalidOperationException("Не удалось прочитать видео встроенными кодеками."));
        player.TimeChanged += (_, e) => progress?.Report(Math.Clamp(e.Time / end.TotalMilliseconds, 0, 1));
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
