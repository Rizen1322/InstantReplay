using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Aura.Core.Storage;
using LibVLCSharp.Shared;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Aura.Core.Audio;
using Aura.Core.Saving.Mp4;

namespace Aura.Core.Tools;

public sealed record VideoFileInfo(TimeSpan Duration, int Width, int Height, double Fps, int AudioTracks, bool Transformed = false)
{
    /// <summary>FourCC видеокодека по данным VLC ("av01", "hevc", "h264"…); null — неизвестен.</summary>
    public string? VideoCodec { get; init; }
    public bool Anamorphic { get; init; }
}

/// <summary>В ffmpeg нет декодера для кодека исходника (например, AV1 в минимальной сборке).</summary>
public sealed class FfmpegDecoderMissingException(string message) : InvalidOperationException(message);

/// <summary>Windows (Media Foundation) не смогла перекодировать ролик.</summary>
public sealed class WindowsTranscodeException(string message) : InvalidOperationException(message);

public static partial class VideoAssembly
{
    // ReadInfoAsync вызывается на каждый файл и каждую проверку границ, а создание
    // LibVLC заново грузит нативные модули и кодеки — это дорого. Поэтому один общий
    // движок на процесс, потокобезопасно создаётся Lazy. Не освобождаем его вообще:
    // Media.Parse завершается в потоке колбэка VLC, и освобождение движка оттуда
    // зависает (движок ждёт тот же колбэк). Media по-прежнему создаётся на вызов.
    private static readonly Lazy<LibVLC> InfoEngine = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--quiet", "--no-video-title-show");
    });

    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, VideoFileInfo Info)> InfoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> LegacyFilterScriptCache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<VideoFileInfo> ReadInfoAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        string full = Path.GetFullPath(path);
        var fi = new FileInfo(full);
        if (!fi.Exists) throw new FileNotFoundException("Видеофайл не найден.", path);
        if (InfoCache.TryGetValue(full, out var cached) && cached.Length == fi.Length && cached.WriteUtc == fi.LastWriteTimeUtc)
            return cached.Info;
        using var media = new Media(InfoEngine.Value, new Uri(full));
        using var cancel = ct.Register(media.ParseStop);
        await media.Parse(MediaParseOptions.ParseLocal, timeout: 15000, cancellationToken: ct);
        // Parse может завершиться в потоке колбэка VLC: продолжение уводим в пул потоков,
        // чтобы не работать с Media в чужом колбэке.
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var videos = media.Tracks.Where(t => t.TrackType == TrackType.Video).ToArray();
        if (videos.Length == 0 || media.Duration <= 0) throw new InvalidDataException("В файле нет доступного видео или не определена длительность.");
        var video = videos[0].Data.Video;
        double fps = video.FrameRateDen > 0 ? (double)video.FrameRateNum / video.FrameRateDen : 30;
        bool anamorphic = video.SarDen > 0 && video.SarNum > 0 && video.SarNum != video.SarDen;
        int width = checked((int)Math.Round(video.Width * (video.SarDen > 0 ? (double)video.SarNum / video.SarDen : 1)));
        int height = (int)video.Height;
        if (video.Orientation is VideoOrientation.LeftTop or VideoOrientation.LeftBottom or VideoOrientation.RightTop or VideoOrientation.RightBottom)
            (width, height) = (height, width);
        var info = new VideoFileInfo(TimeSpan.FromMilliseconds(media.Duration), width, height,
            double.IsFinite(fps) && fps > 0 ? Math.Min(fps, 240) : 30, media.Tracks.Count(t => t.TrackType == TrackType.Audio), video.Orientation != VideoOrientation.TopLeft)
        { VideoCodec = FourCC(videos[0].Codec), Anamorphic = anamorphic };
        InfoCache[full] = (fi.Length, fi.LastWriteTimeUtc, info);
        return info;
    }, ct);

    private static string? FourCC(uint codec)
    {
        if (codec == 0) return null;
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((codec >> (8 * i)) & 0xFF);
            chars[i] = c is >= ' ' and <= '~' ? c : ' ';
        }
        string value = new string(chars).Trim();
        return value.Length == 0 ? null : value;
    }

    public static async Task<string> RenderAsync(IReadOnlyList<VideoSegment> segments, string output,
        string? ffmpeg = null, int? targetMegabytes = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (segments.Count == 0) throw new ArgumentException("Добавьте хотя бы один фрагмент.");
        foreach (var segment in segments) segment.Validate();
        output = System.IO.Path.GetFullPath(output);
        if (segments.Any(s => string.Equals(System.IO.Path.GetFullPath(s.Path), output, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Выберите новое имя: исходные видео нельзя перезаписывать.");
        if (File.Exists(output)) throw new IOException("Файл уже существует. Выберите новое имя.");
        if (targetMegabytes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(targetMegabytes));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, $".aura-assembly-{Guid.NewGuid():N}.part.mp4");
        try
        {
            bool tryRemux = segments.Count == 1;
            if (tryRemux && targetMegabytes is int estLimit)
            {
                var info = await ReadInfoAsync(segments[0].Path, ct);
                double totalSec = Math.Max(0.1, info.Duration.TotalSeconds);
                double estimatedBytes = new FileInfo(segments[0].Path).Length * (segments[0].Duration.TotalSeconds / totalSec);
                tryRemux = estimatedBytes <= estLimit * 1024.0 * 1024.0 * 1.15;
            }
            bool copied = tryRemux && await TryRemuxAsync(segments[0], temp, progress, ct);
            if (copied && targetMegabytes is int remuxLimit && new FileInfo(temp).Length > remuxLimit * 1024L * 1024L)
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                copied = false;
            }
            if (!copied && segments.Count > 1 && await CanTryFastConcatAsync(segments, targetMegabytes, ct))
            {
                copied = await TryConcatAsync(segments, temp, progress, ct);
                if (copied && targetMegabytes is int concatLimit && new FileInfo(temp).Length > concatLimit * 1024L * 1024L)
                {
                    try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    copied = false;
                }
            }
            if (!copied && ffmpeg is not null && await UndecodableCodecAsync(segments, ffmpeg, ct) is string codec)
            {
                // Минимальный ffmpeg не умеет, например, AV1 (запись Aura на RTX 40):
                // такие исходники монтирует Windows или VLC, а не ffmpeg.
                Logging.Log.Info("Editor", $"ffmpeg не декодирует {codec}; монтаж через Windows");
                ffmpeg = null;
            }
            async Task TranscodeAsync(double bitrateScale)
            {
                bool done = false;
                bool preferWindowsOverMpeg4 = ffmpeg is not null &&
                    string.Equals(Ffmpeg.SelectVideoEncoder(ffmpeg), "mpeg4", StringComparison.OrdinalIgnoreCase);
                if (ffmpeg is not null && !preferWindowsOverMpeg4)
                {
                    try
                    {
                        await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, bitrateScale: bitrateScale);
                        done = true;
                    }
                    catch (FfmpegDecoderMissingException ex)
                    {
                        ct.ThrowIfCancellationRequested();
                        Logging.Log.Info("Editor", "ffmpeg не смог прочитать исходник, монтаж через Windows: " + ex.Message);
                        try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                if (!done)
                    try { await Task.Run(() => RenderWindowsAsync(segments, temp, targetMegabytes, progress, ct, bitrateScale: bitrateScale), ct); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or WindowsTranscodeException)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (ffmpeg is not null && preferWindowsOverMpeg4)
                        {
                            try
                            {
                                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                                await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, bitrateScale: bitrateScale);
                                return;
                            }
                            catch (FfmpegDecoderMissingException) { }
                        }
                        Logging.Log.Warn("Editor", $"Windows не смогла выполнить монтаж (0x{ex.HResult:X8}: {ex.Message}); резервный путь с промежуточным перекодированием");
                        try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        await Task.Run(() => RenderWithVlcAsync(segments, temp, targetMegabytes, progress, ct), ct);
                    }
            }
            if (!copied) await TranscodeAsync(0.90);
            ct.ThrowIfCancellationRequested();
            long size = new FileInfo(temp).Length;
            if (size == 0) throw new InvalidDataException("Не удалось записать видео.");
            if (targetMegabytes is int limit && size > limit * 1024L * 1024L)
            {
                double retryScale = Math.Clamp(0.90 * (limit * 1024.0 * 1024.0) / size * 0.96, 0.20, 0.88);
                Logging.Log.Info("Editor", $"Размер {size / (1024.0 * 1024.0):F2} МБ превысил лимит {limit} МБ; повтор с коэффициентом {retryScale:F2}");
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                await TranscodeAsync(retryScale);
                ct.ThrowIfCancellationRequested();
                size = new FileInfo(temp).Length;
                if (size == 0) throw new InvalidDataException("Не удалось записать видео.");
                if (size > limit * 1024L * 1024L)
                    throw new InvalidOperationException("Видео получилось больше выбранного лимита. Уменьшите длительность или увеличьте лимит.");
            }
            File.Move(temp, output);
            progress?.Report(1);
            return output;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static async Task<bool> CanTryFastConcatAsync(IReadOnlyList<VideoSegment> segments, int? targetMegabytes, CancellationToken ct)
    {
        VideoFileInfo? baseline = null;
        double estimatedBytes = 0;
        foreach (var part in segments)
        {
            if (part.Gap != TimeSpan.Zero || part.AudioDetached || !string.Equals(Path.GetExtension(part.Path), ".mp4", StringComparison.OrdinalIgnoreCase))
                return false;
            var info = await ReadInfoAsync(part.Path, ct);
            baseline ??= info;
            if (info.Width != baseline.Width || info.Height != baseline.Height ||
                !string.Equals(info.VideoCodec, baseline.VideoCodec, StringComparison.OrdinalIgnoreCase))
                return false;
            if (targetMegabytes is not null)
                estimatedBytes += new FileInfo(part.Path).Length * (part.Duration.TotalSeconds / Math.Max(0.1, info.Duration.TotalSeconds));
        }
        return targetMegabytes is not int limit || estimatedBytes <= limit * 1024.0 * 1024.0 * 1.15;
    }

    /// <summary>Первый видеокодек исходников, который этот ffmpeg не декодирует; null — все читаются.</summary>
    private static async Task<string?> UndecodableCodecAsync(IReadOnlyList<VideoSegment> segments, string ffmpeg, CancellationToken ct)
    {
        foreach (string path in segments.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = await ReadInfoAsync(path, ct);
            if (!Ffmpeg.CanDecode(ffmpeg, info.VideoCodec)) return info.VideoCodec;
        }
        return null;
    }

    /// <summary>
    /// Быстрый путь для одного MP4: видео копируется байт в байт, звук — выбранная
    /// дорожка или все дорожки. Границы режет список правок MP4, поэтому обычная
    /// обрезка и удаление дорожки не запускают перекодирование.
    /// </summary>
    private static async Task<bool> TryRemuxAsync(VideoSegment part, string output, IProgress<double>? progress, CancellationToken ct)
    {
        if (part.Gap != TimeSpan.Zero || part.AudioDetached || !string.Equals(Path.GetExtension(part.Path), ".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        var info = await ReadInfoAsync(part.Path, ct);
        if (info.Transformed || part.End > info.Duration + TimeSpan.FromMilliseconds(20)) return false;
        if (part.AudioTrack >= info.AudioTracks && part.AudioTrack >= 0 ||
            part.AudioEvents.Any(e => (e.SourcePath is null || e.SourcePath == part.Path) && e.Track >= info.AudioTracks))
            throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");

        var sounds = AssemblyAudio.Sounds([part], _ => info.AudioTracks).Where(s => !s.Removed).ToArray();
        if (sounds.Any(s => s.Finish > part.Duration + TimeSpan.FromMilliseconds(10))) return false;
        var audible = sounds.Where(s => s.Gain > 0).ToArray();
        if (audible.Any(s => s.Path != part.Path)) return false;
        var tracks = audible.Select(s => s.Track).Distinct().OrderBy(t => t).ToArray();
        int audioTrack = tracks.Length switch { 0 => -2, 1 => tracks[0], _ => -1 };
        bool directCopy = tracks.Length == 0 ||
            tracks.Length == 1 && audible.Length == 1 && Math.Abs(audible[0].Gain - 1) < 1e-4 &&
            audible[0].Start == part.Start && audible[0].End == part.End;
        Mp4Trim.AudioMixer? mix = directCopy ? null
            : tracks.Length == 1
                ? clips => AudioMixdown.Mix(clips, ct, (_, sec) => (float)AssemblyAudio.GainAt(part, tracks[0], sec))
                : clips => AudioMixdown.Mix(clips, ct, (t, sec) => (float)AssemblyAudio.GainAt(part, t, sec));

        var clock = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => Mp4Trim.Cut(part.Path, output, part.Start.TotalSeconds, part.End.TotalSeconds,
                audioTrack, value => progress?.Report(value), ct, mix), ct);
            Logging.Log.Info("Editor", $"Экспорт фрагмента без перекодирования видео: {clock.ElapsedMilliseconds} мс, " +
                $"звук {(mix is not null ? "сведён в одну дорожку" : audioTrack < 0 ? "без звука" : "дорожка " + (audioTrack + 1))}");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            File.Delete(output);
            Logging.Log.Info("Editor", "Быстрый экспорт фрагмента недоступен: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Склейка нескольких простых фрагментов без перекодирования: каждый кусок сначала
    /// режется своим MP4-списком правок, затем дорожки копируются в общий файл.
    /// </summary>
    private static async Task<bool> TryConcatAsync(IReadOnlyList<VideoSegment> segments, string output,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (segments.Count < 2) return false;
        var infos = new Dictionary<string, VideoFileInfo>(StringComparer.OrdinalIgnoreCase);
        var tracks = new List<int>();
        bool needsMix = false;
        foreach (var part in segments)
        {
            if (part.Gap != TimeSpan.Zero || part.AudioDetached || !string.Equals(Path.GetExtension(part.Path), ".mp4", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!infos.TryGetValue(part.Path, out var info)) infos[part.Path] = info = await ReadInfoAsync(part.Path, ct);
            if (info.Transformed || part.Start < TimeSpan.Zero || part.End > info.Duration + TimeSpan.FromMilliseconds(20))
                return false;
            if (part.AudioTrack >= info.AudioTracks && part.AudioTrack >= 0 ||
                part.AudioEvents.Any(e => (e.SourcePath is null || e.SourcePath == part.Path) && e.Track >= info.AudioTracks))
                throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            var sounds = AssemblyAudio.Sounds([part], _ => info.AudioTracks).Where(s => !s.Removed).ToArray();
            if (sounds.Any(s => s.Finish > part.Duration + TimeSpan.FromMilliseconds(10))) return false;
            var audible = sounds.Where(s => s.Gain > 0).ToArray();
            if (audible.Any(s => s.Path != part.Path)) return false;
            var audibleTracks = audible.Select(s => s.Track).Distinct().ToArray();
            bool segmentNeedsMix = audibleTracks.Length > 1 ||
                audible.Any(s => Math.Abs(s.Gain - 1) >= 1e-4 || s.Start != part.Start || s.End != part.End);
            needsMix |= segmentNeedsMix;
            tracks.Add(audibleTracks.Length == 0 ? -2 : audibleTracks.Length == 1 ? audibleTracks[0] : -1);
        }
        if (!needsMix && tracks.Where(t => t >= 0).Distinct().Count() > 1) needsMix = true;

        var tempParts = new List<string>();
        try
        {
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string cut = output + $".cut{i}.mp4";
                tempParts.Add(cut);
                var part = segments[i];
                int index = i;
                int audioTrack = tracks[index] == -2 ? -2 : needsMix ? -1 : tracks[index];
                Mp4Trim.AudioMixer? mix = audioTrack == -2 || !needsMix
                    ? null
                    : clips => AudioMixdown.Mix(clips, ct, (t, sec) => (float)AssemblyAudio.GainAt(part, t, sec));
                await Task.Run(() => Mp4Trim.Cut(part.Path, cut, part.Start.TotalSeconds, part.End.TotalSeconds,
                    audioTrack, value => progress?.Report((index + value) / segments.Count * 0.7), ct, mix), ct);
            }
            var durations = segments.Select(s => s.Duration.TotalSeconds).ToArray();
            var clock = Stopwatch.StartNew();
            await Task.Run(() => Mp4Trim.Concat(tempParts, durations, output,
                value => progress?.Report(0.7 + value * 0.3), ct), ct);
            Logging.Log.Info("Editor", $"Экспорт монтажа без перекодирования видео: {segments.Count} фрагментов, {clock.ElapsedMilliseconds} мс");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            File.Delete(output);
            Logging.Log.Info("Editor", "Быстрая склейка фрагментов недоступна: " + ex.Message);
            return false;
        }
        finally
        {
            foreach (string cut in tempParts)
            {
                try { File.Delete(cut); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task RenderWindowsAsync(IReadOnlyList<VideoSegment> segments, string temp,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct,
        IReadOnlyList<VideoSegment>? audioSegments = null, double bitrateScale = 0.90)
    {
        audioSegments ??= segments;
        var composition = new MediaComposition();
        Logging.Log.Info("Editor", $"Windows: подготовка {segments.Count} видеофрагментов");
        var sources = new Dictionary<string, MediaClip>(StringComparer.OrdinalIgnoreCase);
        TimeSpan offset = TimeSpan.Zero;
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            if (segment.Gap > TimeSpan.Zero)
            {
                composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), segment.Gap));
                offset += segment.Gap;
            }
            var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(segment.Path));
            var clip = await MediaClip.CreateFromFileAsync(file);
            sources[segment.Path] = clip;
            if (segment.Start >= clip.OriginalDuration || segment.End > clip.OriginalDuration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Границы фрагмента выходят за конец видео.");
            TimeSpan end = segment.End < clip.OriginalDuration ? segment.End : clip.OriginalDuration;
            clip.TrimTimeFromStart = segment.Start;
            clip.TrimTimeFromEnd = clip.OriginalDuration - end;
            clip.Volume = 0;
            composition.Clips.Add(clip);
            offset += end - segment.Start;
        }
        Logging.Log.Info("Editor", "Windows: подготовка звуковых источников");
        foreach (string path in audioSegments.Select(p => p.Path).Concat(audioSegments.SelectMany(p => p.AudioEvents).Select(e => e.SourcePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!sources.ContainsKey(path)) sources[path] = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));
        int TrackCount(string path) => sources[path].EmbeddedAudioTracks.Count;
        foreach (var part in audioSegments)
            if (part.AudioTrack >= TrackCount(part.Path) && part.AudioTrack >= 0 ||
                part.AudioEvents.Any(e => e.Track >= TrackCount(e.SourcePath ?? part.Path)))
                throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
        var sounds = AssemblyAudio.Sounds(audioSegments, TrackCount);
        foreach (var sound in sounds.Where(s => s.Gain > 0))
        {
            string path = sound.Path;
            var source = sources[path];
            int nativeTrack = (int)VideoEditor.WindowsAudioTrackIndex(path, source.EmbeddedAudioTracks.Count, sound.Track);
            var audio = BackgroundAudioTrack.CreateFromEmbeddedAudioTrack(source.EmbeddedAudioTracks[nativeTrack]);
            TimeSpan audioEnd = sound.End < audio.OriginalDuration ? sound.End : audio.OriginalDuration;
            if (sound.Start >= audioEnd) continue;
            audio.TrimTimeFromStart = sound.Start; audio.TrimTimeFromEnd = audio.OriginalDuration - audioEnd;
            audio.Delay = sound.Position; audio.Volume = sound.Gain;
            composition.BackgroundAudioTracks.Add(audio);
        }
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        var original = composition.CreateDefaultEncodingProfile();
        TimeSpan audioFinish = sounds.Where(s => !s.Removed).Select(s => s.Finish).DefaultIfEmpty(TimeSpan.Zero).Max();
        if (audioFinish > offset)
        {
            composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), audioFinish - offset));
            offset = audioFinish;
        }
        var firstProps = sources[segments[0].Path].GetVideoEncodingProperties();
        uint srcWidth = firstProps.Width > 0 && firstProps.Height > 0 ? firstProps.Width : original.Video.Width;
        uint srcHeight = firstProps.Width > 0 && firstProps.Height > 0 ? firstProps.Height : original.Video.Height;
        var srcRate = firstProps.Width > 0 && firstProps.Height > 0 && firstProps.FrameRate.Numerator > 0 ? firstProps.FrameRate : original.Video.FrameRate;
        profile.Video.Width = srcWidth;
        profile.Video.Height = srcHeight;
        if (srcRate.Denominator > 0 && (double)srcRate.Numerator / srcRate.Denominator is >= 1 and <= 240)
        {
            profile.Video.FrameRate.Numerator = srcRate.Numerator;
            profile.Video.FrameRate.Denominator = srcRate.Denominator;
        }
        else
        {
            profile.Video.FrameRate.Numerator = 30;
            profile.Video.FrameRate.Denominator = 1;
        }
        profile.Video.Bitrate = targetMegabytes is int limit
            ? TargetBitrate(limit, offset.TotalSeconds, bitrateScale) : Math.Max(20_000_000u, original.Video.Bitrate);
        var size = OutputSize((int)profile.Video.Width, (int)profile.Video.Height, targetMegabytes.HasValue ? profile.Video.Bitrate : null);
        profile.Video.Width = (uint)size.Width; profile.Video.Height = (uint)size.Height;
        profile.Audio.Bitrate = 128000;
        profile.Audio.SampleRate = 48000;
        profile.Audio.ChannelCount = 2;
        var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(temp)!);
        var target = await folder.CreateFileAsync(System.IO.Path.GetFileName(temp), CreationCollisionOption.FailIfExists);
        Logging.Log.Info("Editor", $"Windows: кодирование {profile.Video.Width}×{profile.Video.Height}, {profile.Video.FrameRate.Numerator} fps, звук {composition.BackgroundAudioTracks.Count} фрагментов, длительность {offset.TotalSeconds:F3} с");
        var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
        render.Progress = (_, value) => progress?.Report(value / 100.0);
        using var cancel = ct.Register(render.Cancel);
        var result = await render;
        ct.ThrowIfCancellationRequested();
        if (result != TranscodeFailureReason.None)
            throw new WindowsTranscodeException($"Windows не смогла обработать формат видео ({result}). Выберите ffmpeg в окне сборки.");
    }

    private static uint TargetBitrate(int megabytes, double seconds, double scale = 0.90)
    {
        double bitrate = megabytes * 1024.0 * 1024 * 8 * scale / seconds - 140000;
        if (bitrate < 100000) throw new InvalidOperationException("Лимит слишком мал для такой длительности. Уменьшите его или сократите ролик.");
        return (uint)Math.Min(bitrate, 100_000_000);
    }

    private static (int Width, int Height) OutputSize(int width, int height, uint? bitrate)
    {
        int maxHeight = bitrate switch { < 700000 => 480, < 1500000 => 720, < 4500000 => 1080, < 8000000 => 1440, _ => height };
        double scale = Math.Min(1, (double)maxHeight / Math.Max(1, height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }

    private static async Task RenderFfmpegAsync(IReadOnlyList<VideoSegment> segments, string temp, string ffmpeg,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct, bool legacyScript = false, bool softwareEncoder = false, double bitrateScale = 0.90)
    {
        var infos = new Dictionary<string, VideoFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in segments.Select(s => s.Path).Concat(segments.SelectMany(s => s.AudioEvents).Select(e => e.SourcePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            infos[path] = await ReadInfoAsync(path, ct);
        var first = infos[segments[0].Path];
        var sounds = AssemblyAudio.Sounds(segments, path => infos[path].AudioTracks);
        double videoDuration = segments.Sum(s => s.Gap.TotalSeconds + s.Duration.TotalSeconds);
        double duration = Math.Max(videoDuration, sounds.Where(s => !s.Removed).Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max());
        var audible = sounds.Where(s => s.Gain > 0).ToArray();
        uint? bitrate = targetMegabytes is int requestedLimit ? TargetBitrate(requestedLimit, duration, bitrateScale) : null;
        var (width, height) = OutputSize(first.Width, first.Height, bitrate);
        string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        ffmpeg = ExecutableTrust.RequireProtected(ffmpeg);
        if (!legacyScript && LegacyFilterScriptCache.GetValueOrDefault(ffmpeg)) legacyScript = true;
        string encoder = softwareEncoder ? Ffmpeg.SelectSoftwareEncoder(ffmpeg) : Ffmpeg.SelectVideoEncoder(ffmpeg);
        Logging.Log.Info("Editor", $"FFmpeg: {segments.Count} фрагментов, видео {encoder}, {width}×{height}");
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = System.IO.Path.GetDirectoryName(ffmpeg),
            Environment = { ["PATH"] = System.IO.Path.GetDirectoryName(ffmpeg) + System.IO.Path.PathSeparator + System.Environment.SystemDirectory }
        };
        void Args(params string[] args) { foreach (string arg in args) psi.ArgumentList.Add(arg); }
        Args("-nostdin", "-hide_banner", "-n", "-progress", "pipe:1", "-nostats");
        bool useCuda = segments.Count == 1 && videoDuration > 15;
        foreach (var segment in segments)
        {
            if (segment.Start >= infos[segment.Path].Duration || segment.End > infos[segment.Path].Duration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Границы фрагмента выходят за конец видео.");
            // CUDA-декодер на нескольких входах/MKV иногда встаёт намертво; MP4-входы
            // проверены на длинных фрагментах, а MKV и короткие надёжнее отдать CPU/d3d11va.
            bool isMp4 = string.Equals(System.IO.Path.GetExtension(segment.Path), ".mp4", StringComparison.OrdinalIgnoreCase);
            if ((encoder is "h264_nvenc" or "hevc_nvenc") && isMp4 && useCuda)
                Args("-hwaccel", "cuda");
            else if (string.Equals(infos[segment.Path].VideoCodec, "av01", StringComparison.OrdinalIgnoreCase))
                Args("-hwaccel", "d3d11va");
            Args("-thread_queue_size", "512", "-ss", Number(segment.Start.TotalSeconds), "-t", Number(segment.Duration.TotalSeconds), "-i", System.IO.Path.GetFullPath(segment.Path));
        }
        var graph = new StringBuilder();
        var audioSources = new string[audible.Length];
        int extraAudioCount = 0;
        for (int i = 0; i < audible.Length; i++)
        {
            var sound = audible[i];
            if (sound.Track >= infos[sound.Path].AudioTracks || sound.Start >= infos[sound.Path].Duration || sound.End > infos[sound.Path].Duration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Звуковой фрагмент выходит за границы исходника.");
            var owner = segments[sound.Owner];
            if (!sound.Detached && string.Equals(Path.GetFullPath(sound.Path), Path.GetFullPath(owner.Path), StringComparison.OrdinalIgnoreCase) &&
                sound.Start >= owner.Start && sound.End <= owner.End)
            {
                double relStart = (sound.Start - owner.Start).TotalSeconds;
                double relEnd = (sound.End - owner.Start).TotalSeconds;
                audioSources[i] = $"[{sound.Owner}:a:{sound.Track}]atrim=start={Number(relStart)}:end={Number(relEnd)},asetpts=PTS-STARTPTS";
            }
            else
            {
                int extraIdx = segments.Count + extraAudioCount++;
                Args("-thread_queue_size", "512", "-ss", Number(sound.Start.TotalSeconds), "-t", Number(sound.Duration.TotalSeconds), "-i", Path.GetFullPath(sound.Path));
                audioSources[i] = $"[{extraIdx}:a:{sound.Track}]atrim=duration={Number(sound.Duration.TotalSeconds)},asetpts=PTS-STARTPTS";
            }
        }
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var source = infos[segment.Path];
            int tracks = source.AudioTracks;
            if (segment.AudioEvents.Any(e => e.Track >= infos[e.SourcePath ?? segment.Path].AudioTracks)) throw new InvalidDataException("Звуковой фрагмент ссылается на отсутствующую дорожку.");
            if (segment.AudioTrack >= tracks && segment.AudioTrack >= 0) throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            bool samePicture = !source.Anamorphic && source.Width == width && source.Height == height && Math.Abs(source.Fps - first.Fps) < .01;
            string prepare = samePicture
                ? "setsar=1,format=yuv420p,setpts=PTS-STARTPTS"
                : $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={Number(first.Fps)},format=yuv420p,setpts=PTS-STARTPTS";
            graph.Append($"[{i}:v:0]{prepare}[v{i}];");
        }
        int pieces = 0;
        var videoParts = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].Gap > TimeSpan.Zero)
            {
                videoParts.Append($"color=c=black:s={width}x{height}:r={Number(first.Fps)}:d={Number(segments[i].Gap.TotalSeconds)},setsar=1[g{i}];[g{i}]");
                pieces++;
            }
            videoParts.Append($"[v{i}]");
            pieces++;
        }
        if (duration > videoDuration + .001)
            graph.Append(videoParts).Append($"concat=n={pieces}:v=1:a=0[vbase];color=c=black:s={width}x{height}:r={Number(first.Fps)}:d={Number(duration - videoDuration)}[tail];[vbase][tail]concat=n=2:v=1:a=0[v];");
        else graph.Append(videoParts).Append($"concat=n={pieces}:v=1:a=0[v];");
        for (int i = 0; i < audible.Length; i++)
        {
            var sound = audible[i];
            graph.Append($"{audioSources[i]},volume={Number(sound.Gain)},aresample=48000,aformat=channel_layouts=stereo,adelay={Number(sound.Position.TotalMilliseconds)}:all=1[a{i}];");
        }
        for (int i = 0; i < audible.Length; i++) graph.Append($"[a{i}]");
        graph.Append(audible.Length == 0 ? "anullsrc=r=48000:cl=stereo," : audible.Length == 1 ? "anull," : $"amix=inputs={audible.Length}:duration=longest:normalize=0,alimiter=limit=0.97:level=disabled,");
        graph.Append($"apad,atrim=duration={Number(duration)},asetpts=PTS-STARTPTS[a]");
        string script = temp + ".filters.txt";
        using var scriptLock = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            scriptLock.Write(new UTF8Encoding(false).GetBytes(graph.ToString())); scriptLock.Flush();
            Args(legacyScript ? "-filter_complex_script" : "-/filter_complex", script, "-map", "[v]", "-map", "[a]", "-c:v", encoder);
            if (encoder == "h264_nvenc")
            {
                Args("-preset", "p4", "-tune", "hq", "-rc", "vbr", "-spatial-aq", "1");
                if (bitrate is uint nvencBitrate) Args("-b:v", ((uint)(nvencBitrate * 0.96)).ToString(CultureInfo.InvariantCulture), "-maxrate", ((uint)(nvencBitrate * 1.04)).ToString(CultureInfo.InvariantCulture), "-bufsize", ((uint)(nvencBitrate * 1.5)).ToString(CultureInfo.InvariantCulture));
                else Args("-cq", "19", "-b:v", "0");
            }
            else if (encoder == "h264_qsv")
            {
                Args("-preset", "veryfast", "-profile:v", "high");
                if (bitrate is uint qsvBitrate) Args("-b:v", ((uint)(qsvBitrate * 0.95)).ToString(CultureInfo.InvariantCulture), "-maxrate", qsvBitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", qsvBitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-global_quality", "20");
            }
            else if (encoder == "h264_amf")
            {
                Args("-quality", "balanced", "-profile:v", "high");
                if (bitrate is uint amfBitrate) Args("-rc", "vbr_peak", "-b:v", ((uint)(amfBitrate * 0.95)).ToString(CultureInfo.InvariantCulture), "-maxrate", amfBitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", amfBitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-rc", "cqp", "-qp_i", "20", "-qp_p", "22");
            }
            else if (encoder == "mpeg4")
            {
                if (bitrate is uint mpeg4Bitrate) Args("-b:v", ((uint)(mpeg4Bitrate * 0.95)).ToString(CultureInfo.InvariantCulture), "-maxrate", mpeg4Bitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", mpeg4Bitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-q:v", "3");
            }
            else
            {
                Args("-preset", "veryfast");
                if (bitrate is uint videoBitrate) Args("-b:v", ((uint)(videoBitrate * 0.95)).ToString(CultureInfo.InvariantCulture), "-maxrate", videoBitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", videoBitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-crf", "18");
            }
            Args("-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", temp);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
            var errors = new Queue<string>();
            var errorReader = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                { errors.Enqueue(line); if (errors.Count > 40) errors.Dequeue(); }
            });
            var progressReader = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out long micros))
                        progress?.Report(Math.Clamp(micros / 1_000_000.0 / duration, 0, 0.99));
            });
            try { await process.WaitForExitAsync(ct); }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            finally { await Task.WhenAll(errorReader, progressReader); }
            if (process.ExitCode != 0)
            {
                // Кодека/контейнера исходника в этой сборке нет — другой энкодер не поможет.
                bool hasAv1 = infos.Values.Any(v => string.Equals(v.VideoCodec, "av01", StringComparison.OrdinalIgnoreCase));
                if (errors.FirstOrDefault(e => e.Contains("no decoder found", StringComparison.OrdinalIgnoreCase) ||
                                               e.Contains("Decoder not found", StringComparison.OrdinalIgnoreCase) ||
                                               e.Contains("Demuxer not found", StringComparison.OrdinalIgnoreCase) ||
                                               e.Contains("Cannot find a decoder", StringComparison.OrdinalIgnoreCase) ||
                                               (softwareEncoder || hasAv1) && (
                                                   e.Contains("Failed to get pixel format", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Contains("Hardware is lacking", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Contains("Error while decoding", StringComparison.OrdinalIgnoreCase))) is string missing)
                    throw new FfmpegDecoderMissingException(missing.Trim());
                if (!legacyScript && errors.Any(e => e.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase)))
                {
                    LegacyFilterScriptCache[ffmpeg] = true;
                    File.Delete(temp);
                    scriptLock.Dispose(); File.Delete(script);
                    await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, legacyScript: true, softwareEncoder, bitrateScale);
                    return;
                }
                if (!softwareEncoder && encoder != "libx264")
                {
                    Logging.Log.Info("Editor", $"Аппаратный энкодер {encoder} не подошёл, повторяю на libx264: {string.Join(" ", errors.TakeLast(2))}");
                    File.Delete(temp);
                    scriptLock.Dispose(); File.Delete(script);
                    await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, legacyScript, softwareEncoder: true, bitrateScale);
                    return;
                }
                throw new InvalidOperationException("ffmpeg: " + string.Join(" ", errors.TakeLast(4)));
            }
        }
        finally { scriptLock.Dispose(); File.Delete(script); }
    }
}
