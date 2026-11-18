using System.Globalization;
using System.Text;
using Aura.Core.Saving.Mp4;
using LibVLCSharp.Shared;

namespace Aura.Core.Tools;

public static partial class VideoAssembly
{
    private static readonly Lazy<LibVLC> TranscodeEngine = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--quiet", "--no-lua", "--no-sout-display", "--avcodec-hw=none");
    });

    private static readonly Lazy<LibVLC> AudioTranscodeEngine = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--quiet", "--no-lua", "--no-sout-display");
    });

    private static async Task RenderWithVlcAsync(IReadOnlyList<VideoSegment> segments, string temp,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetDirectoryName(temp)!, ".aura-normalize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var parts = new VideoSegment[segments.Count];
        var files = new List<string>();
        try
        {
            var nativeCompatible = new bool[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                if (!string.Equals(Path.GetExtension(segments[i].Path), ".mp4", StringComparison.OrdinalIgnoreCase)) continue;
                var info = await ReadInfoAsync(segments[i].Path, ct);
                nativeCompatible[i] = info.VideoCodec?.ToLowerInvariant() is "h264" or "avc1" or "avc3" or "mp4v";
            }
            bool skipCompatible = nativeCompatible.Any(c => !c);
            int toNormalize = Enumerable.Range(0, segments.Count).Count(i => !(skipCompatible && nativeCompatible[i]));
            int maxParallel = toNormalize > 1 ? Math.Clamp(Environment.ProcessorCount / 4, 1, 2) : 1;
            int threadsPerJob = Math.Clamp(Environment.ProcessorCount / maxParallel, 2, 8);
            var segmentProgress = new double[segments.Count];
            object progressLock = new();
            void ReportSegment(int idx, double value)
            {
                double total;
                lock (progressLock)
                {
                    segmentProgress[idx] = Math.Clamp(value, 0, 1);
                    total = segmentProgress.Sum() / segments.Count * 0.5;
                }
                progress?.Report(total);
            }
            using var gate = new SemaphoreSlim(maxParallel, maxParallel);
            var normalizeTasks = new Task[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                int index = i;
                normalizeTasks[index] = Task.Run(async () =>
                {
                    var segment = segments[index];
                    if (skipCompatible && nativeCompatible[index])
                    {
                        parts[index] = segment;
                        ReportSegment(index, 1);
                        return;
                    }
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        string source = segment.Path;
                        TimeSpan? stop = segment.End;
                        TimeSpan progressSpan = segment.End;
                        double? prePrefix = null;
                        TimeSpan? nonMp4Start = null;
                        // Вырезаем нужный кусок по ключевым кадрам до VLC: иначе при монтаже
                        // конца двухминутной записи VLC перекодирует все две минуты с начала.
                        if (string.Equals(Path.GetExtension(source), ".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            string cut = Path.Combine(work, index.ToString(CultureInfo.InvariantCulture) + ".cut.mp4");
                            lock (files) files.Add(cut);
                            try
                            {
                                double anchor = await Task.Run(() => Mp4Trim.CutWithAnchor(source, cut, segment.Start.TotalSeconds,
                                    segment.End.TotalSeconds, audioTrack: -2, ct), ct).ConfigureAwait(false);
                                var cutInfo = await ReadInfoAsync(cut, ct).ConfigureAwait(false);
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
                        else if (segment.Start >= TimeSpan.FromSeconds(2))
                        {
                            nonMp4Start = segment.Start;
                            progressSpan = segment.Duration;
                        }
                        string converted = Path.Combine(work, index.ToString(CultureInfo.InvariantCulture) + ".mp4");
                        lock (files) files.Add(converted);
                        await NormalizeWithVlcAsync(source, converted, nonMp4Start, stop, progressSpan, threadsPerJob,
                            new Progress<double>(p => ReportSegment(index, p)), ct).ConfigureAwait(false);
                        if (prePrefix is not null)
                            try { File.Delete(source); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        var result = await ReadInfoAsync(converted, ct).ConfigureAwait(false);
                        TimeSpan expected = prePrefix is not null || nonMp4Start is not null ? segment.Duration : segment.End;
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
                        else if (nonMp4Start is not null)
                        {
                            start = TimeSpan.Zero;
                            end = TimeSpan.FromSeconds(Math.Min(result.Duration.TotalSeconds, segment.Duration.TotalSeconds));
                        }
                        else
                        {
                            end = TimeSpan.FromSeconds(Math.Min(result.Duration.TotalSeconds, segment.End.TotalSeconds));
                            start = TimeSpan.FromSeconds(Math.Min(segment.Start.TotalSeconds, Math.Max(0, end.TotalSeconds - 0.05)));
                        }
                        parts[index] = segment with { Path = converted, Start = start, End = end };
                        ReportSegment(index, 1);
                    }
                    finally { gate.Release(); }
                }, ct);
            }
            await Task.WhenAll(normalizeTasks).ConfigureAwait(false);
            try
            {
                await RenderWindowsAsync(parts, temp, targetMegabytes,
                    new Progress<double>(p => progress?.Report(0.5 + p * 0.5)), ct, audioSegments: segments);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or WindowsTranscodeException)
            {
                ct.ThrowIfCancellationRequested();
                Logging.Log.Warn("Editor", $"Windows не смогла открыть исходный звук ({ex.Message}); извлекаю дорожки через VLC");
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var trackCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in segments.Select(s => s.Path).Concat(segments.SelectMany(s => s.AudioEvents).Select(e => e.SourcePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                    trackCounts[path] = (await ReadInfoAsync(path, ct)).AudioTracks;
                var sounds = AssemblyAudio.Sounds(segments, path => trackCounts[path]).Where(s => !s.Removed && s.Gain > 0).ToArray();
                var audioCache = new Dictionary<(string Path, int Track), string>();
                var keys = sounds.Select(s => (Path: Path.GetFullPath(s.Path), s.Track)).Distinct().ToArray();
                for (int k = 0; k < keys.Length; k++)
                {
                    var key = keys[k];
                    string audioFile = Path.Combine(work, $"audio-{k}.m4a");
                    files.Add(audioFile);
                    await NormalizeAudioWithVlcAsync(key.Path, audioFile, key.Track, ct);
                    audioCache[key] = audioFile;
                }
                var normalizedAudio = parts.Select((p, idx) =>
                {
                    var events = sounds.Where(s => s.Owner == idx).Select(s =>
                        new AudioEvent(0, s.Start, s.End, s.Gain) { SourcePath = audioCache[(Path.GetFullPath(s.Path), s.Track)], Position = s.Position }).ToArray();
                    return p with { AudioTrack = -1, AudioLinked = false, AudioDetached = true, AudioEvents = events };
                }).ToArray();
                await RenderWindowsAsync(parts, temp, targetMegabytes,
                    new Progress<double>(p => progress?.Report(0.5 + p * 0.5)), ct, audioSegments: normalizedAudio);
            }
        }
        finally
        {
            foreach (string file in files)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(work); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static Task NormalizeWithVlcAsync(string input, string output, TimeSpan? startTime, TimeSpan? stopTime, TimeSpan progressSpan,
        int threads, IProgress<double>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        var vlc = TranscodeEngine.Value;
        using var media = new Media(vlc, new Uri(input));
        // shortcut: VLC 3 seek breaks MP4 muxing; normalize each input prefix,
        // replace with seek-safe transcoding if long source videos make this too slow.
        media.AddOption(":avcodec-hw=none");
        media.AddOption(":avcodec-threads=0");
        media.AddOption(":no-sout-audio");
        if (startTime is TimeSpan start)
            media.AddOption(":start-time=" + start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        if (stopTime is TimeSpan stop)
            media.AddOption(":stop-time=" + stop.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        // The bundled VLC has MPEG-4 Part 2, but no 8-bit x264 encoder. Windows can
        // decode this intermediate even without the optional HEVC extension.
        media.AddOption($":sout=#transcode{{vcodec=mp4v,venc=avcodec{{codec=mpeg4,qmin=2,qmax=4}},vb=35000,threads={threads}}}:std{{access=file,mux=mp4,dst=" + ChainValue(output) + "}");
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

    private static Task NormalizeAudioWithVlcAsync(string input, string output, int track, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        var vlc = AudioTranscodeEngine.Value;
        using var media = new Media(vlc, new Uri(input));
        media.AddOption(":no-video");
        media.AddOption(":no-sout-video");
        media.AddOption($":audio-track={track}");
        media.AddOption(":sout=#transcode{acodec=mp4a,ab=192,channels=2,samplerate=48000}:std{access=file,mux=mp4,dst=" + ChainValue(output) + "}");
        using var player = new MediaPlayer(vlc);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => completed.TrySetResult();
        player.EncounteredError += (_, _) => completed.TrySetException(new InvalidOperationException("Не удалось извлечь звук встроенными кодеками."));
        using var cancellation = ct.Register(() => completed.TrySetCanceled(ct));
        try
        {
            if (!player.Play(media)) throw new InvalidOperationException("Не удалось извлечь звук для монтажа.");
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
