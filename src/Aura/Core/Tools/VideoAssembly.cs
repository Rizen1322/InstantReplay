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

    public static Task<VideoFileInfo> ReadInfoAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Видеофайл не найден.", path);
        using var media = new Media(InfoEngine.Value, new Uri(System.IO.Path.GetFullPath(path)));
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
        int width = checked((int)Math.Round(video.Width * (video.SarDen > 0 ? (double)video.SarNum / video.SarDen : 1)));
        int height = (int)video.Height;
        if (video.Orientation is VideoOrientation.LeftTop or VideoOrientation.LeftBottom or VideoOrientation.RightTop or VideoOrientation.RightBottom)
            (width, height) = (height, width);
        return new VideoFileInfo(TimeSpan.FromMilliseconds(media.Duration), width, height,
            double.IsFinite(fps) && fps > 0 ? Math.Min(fps, 240) : 30, media.Tracks.Count(t => t.TrackType == TrackType.Audio), video.Orientation != VideoOrientation.TopLeft)
        { VideoCodec = FourCC(videos[0].Codec) };
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
            bool copied = targetMegabytes is null && segments.Count == 1 && await TryRemuxAsync(segments[0], temp, progress, ct);
            // Склейка с несколькими elst быстрая, но браузеры читают только первую правку.
            // Поэтому при наличии ffmpeg многоклиповый монтаж идёт через аппаратный транскод,
            // а без ffmpeg остаётся быстрый запасной путь.
            if (!copied && targetMegabytes is null && segments.Count > 1 && ffmpeg is null)
                copied = await TryConcatAsync(segments, temp, progress, ct);
            if (!copied && ffmpeg is not null && await UndecodableCodecAsync(segments, ffmpeg, ct) is string codec)
            {
                // Минимальный ffmpeg не умеет, например, AV1 (запись Aura на RTX 40):
                // такие исходники монтирует Windows или VLC, а не ffmpeg.
                Logging.Log.Info("Editor", $"ffmpeg не декодирует {codec}; монтаж через Windows");
                ffmpeg = null;
            }
            if (!copied && ffmpeg is not null)
            {
                try
                {
                    await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct);
                    copied = true;
                }
                catch (FfmpegDecoderMissingException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    Logging.Log.Info("Editor", "ffmpeg не смог прочитать исходник, монтаж через Windows: " + ex.Message);
                    File.Delete(temp);
                }
            }
            if (!copied)
                try { await Task.Run(() => RenderWindowsAsync(segments, temp, targetMegabytes, progress, ct), ct); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or WindowsTranscodeException)
                {
                    ct.ThrowIfCancellationRequested();
                    Logging.Log.Warn("Editor", $"Windows не смогла выполнить монтаж (0x{ex.HResult:X8}: {ex.Message}); резервный путь с промежуточным перекодированием");
                    File.Delete(temp);
                    await Task.Run(() => RenderWithVlcAsync(segments, temp, targetMegabytes, progress, ct), ct);
                }
            ct.ThrowIfCancellationRequested();
            long size = new FileInfo(temp).Length;
            if (size == 0) throw new InvalidDataException("Не удалось записать видео.");
            if (targetMegabytes is int limit && size > limit * 1024L * 1024L)
                throw new InvalidOperationException("Видео получилось больше выбранного лимита. Уменьшите длительность или увеличьте лимит.");
            File.Move(temp, output);
            progress?.Report(1);
            return output;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
        if (part.Gap != TimeSpan.Zero || !string.Equals(Path.GetExtension(part.Path), ".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        var info = await ReadInfoAsync(part.Path, ct);
        if (info.Transformed || part.End > info.Duration + TimeSpan.FromMilliseconds(20)) return false;
        if (part.AudioTrack >= info.AudioTracks && part.AudioTrack >= 0 ||
            part.AudioEvents.Any(e => (e.SourcePath is null || e.SourcePath == part.Path) && e.Track >= info.AudioTracks))
            throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");

        var sounds = AssemblyAudio.Sounds([part], _ => info.AudioTracks).Where(s => !s.Removed).ToArray();
        var audible = sounds.Where(s => s.Gain > 0).ToArray();
        // Потоковое копирование умеет только целые дорожки без сдвига и усиления.
        if (audible.Any(s => s.Path != part.Path || s.Gain != 1 || s.Position != TimeSpan.Zero ||
            s.Start != part.Start || s.End != part.End)) return false;
        var tracks = audible.Select(s => s.Track).Distinct().OrderBy(t => t).ToArray();
        // Выбрать произвольное подмножество дорожек нельзя: только одну или все.
        if (tracks.Length > 1 && tracks.Length != info.AudioTracks) return false;
        int audioTrack = tracks.Length switch { 0 => -2, 1 => tracks[0], _ => -1 };
        bool mixAudio = tracks.Length > 1;

        var clock = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => Mp4Trim.Cut(part.Path, output, part.Start.TotalSeconds, part.End.TotalSeconds,
                audioTrack, value => progress?.Report(value), ct,
                mixAudio ? clips => AudioMixdown.Mix(clips, ct) : null), ct);
            Logging.Log.Info("Editor", $"Экспорт фрагмента без перекодирования видео: {clock.ElapsedMilliseconds} мс, " +
                $"звук {(mixAudio ? "сведён в одну дорожку" : audioTrack < 0 ? "без звука" : "дорожка " + (audioTrack + 1))}");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or System.Runtime.InteropServices.COMException or ArgumentException)
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
        foreach (var part in segments)
        {
            if (part.Gap != TimeSpan.Zero || !string.Equals(Path.GetExtension(part.Path), ".mp4", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!infos.TryGetValue(part.Path, out var info)) infos[part.Path] = info = await ReadInfoAsync(part.Path, ct);
            if (info.Transformed || part.Start < TimeSpan.Zero || part.End > info.Duration + TimeSpan.FromMilliseconds(20))
                return false;
            if (part.AudioTrack >= info.AudioTracks && part.AudioTrack >= 0 ||
                part.AudioEvents.Any(e => (e.SourcePath is null || e.SourcePath == part.Path) && e.Track >= info.AudioTracks))
                throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            var sounds = AssemblyAudio.Sounds([part], _ => info.AudioTracks).Where(s => !s.Removed).ToArray();
            if (sounds.Any(s => s.Finish > part.Duration + TimeSpan.FromMilliseconds(1))) return false;
            var audible = sounds.Where(s => s.Gain > 0).ToArray();
            if (audible.Any(s => s.Path != part.Path || s.Gain != 1 || s.Position != TimeSpan.Zero ||
                s.Start != part.Start || s.End != part.End)) return false;
            var audibleTracks = audible.Select(s => s.Track).Distinct().ToArray();
            if (audibleTracks.Length > 1) return false;
            tracks.Add(audibleTracks.Length == 0 ? -2 : audibleTracks[0]);
        }

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
                await Task.Run(() => Mp4Trim.Cut(part.Path, cut, part.Start.TotalSeconds, part.End.TotalSeconds,
                    tracks[index], value => progress?.Report((index + value) / segments.Count * 0.7), ct), ct);
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
        IReadOnlyList<VideoSegment>? audioSegments = null)
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
        profile.Video.Width = original.Video.Width;
        profile.Video.Height = original.Video.Height;
        profile.Video.FrameRate.Numerator = (uint)Math.Clamp(Math.Round((double)original.Video.FrameRate.Numerator / Math.Max(1u, original.Video.FrameRate.Denominator)), 1, 240);
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = targetMegabytes is int limit
            ? TargetBitrate(limit, offset.TotalSeconds) : Math.Max(8_000_000u, original.Video.Bitrate);
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

    private static uint TargetBitrate(int megabytes, double seconds)
    {
        double bitrate = megabytes * 1024.0 * 1024 * 8 * 0.94 / seconds - 128000;
        if (bitrate < 100000) throw new InvalidOperationException("Лимит слишком мал для такой длительности. Увеличьте его или сократите ролик.");
        return (uint)Math.Min(bitrate, 100_000_000);
    }

    private static (int Width, int Height) OutputSize(int width, int height, uint? bitrate)
    {
        int maxHeight = bitrate switch { < 700000 => 480, < 1500000 => 720, < 3500000 => 1080, _ => height };
        double scale = Math.Min(1, (double)maxHeight / Math.Max(1, height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }

    private static async Task RenderFfmpegAsync(IReadOnlyList<VideoSegment> segments, string temp, string ffmpeg,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct, bool legacyScript = false, bool softwareEncoder = false)
    {
        var infos = new Dictionary<string, VideoFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in segments.Select(s => s.Path).Concat(segments.SelectMany(s => s.AudioEvents).Select(e => e.SourcePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            infos[path] = await ReadInfoAsync(path, ct);
        var first = infos[segments[0].Path];
        var sounds = AssemblyAudio.Sounds(segments, path => infos[path].AudioTracks);
        double videoDuration = segments.Sum(s => s.Gap.TotalSeconds + s.Duration.TotalSeconds);
        double duration = Math.Max(videoDuration, sounds.Where(s => !s.Removed).Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max());
        var audible = sounds.Where(s => s.Gain > 0).ToArray();
        var audioInputs = audible.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        uint? bitrate = targetMegabytes is int requestedLimit ? TargetBitrate(requestedLimit, duration) : null;
        var (width, height) = OutputSize(first.Width, first.Height, bitrate);
        string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        ffmpeg = ExecutableTrust.RequireProtected(ffmpeg);
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
        foreach (var segment in segments)
        {
            if (segment.Start >= infos[segment.Path].Duration || segment.End > infos[segment.Path].Duration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Границы фрагмента выходят за конец видео.");
            // CUDA-декодер на нескольких входах/MKV иногда встаёт намертво; MP4-входы
            // проверены, а MKV надёжнее отдать программному декодеру.
            if ((encoder is "h264_nvenc" or "hevc_nvenc") &&
                string.Equals(System.IO.Path.GetExtension(segment.Path), ".mp4", StringComparison.OrdinalIgnoreCase))
                Args("-hwaccel", "cuda");
            Args("-ss", Number(segment.Start.TotalSeconds), "-t", Number(segment.Duration.TotalSeconds), "-i", System.IO.Path.GetFullPath(segment.Path));
        }
        var graph = new StringBuilder();
        foreach (var sound in audible)
        {
            if (sound.Track >= infos[sound.Path].AudioTracks || sound.Start >= infos[sound.Path].Duration || sound.End > infos[sound.Path].Duration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Звуковой фрагмент выходит за границы исходника.");
        }
        foreach (string path in audioInputs) Args("-i", Path.GetFullPath(path));
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var source = infos[segment.Path];
            int tracks = source.AudioTracks;
            if (segment.AudioEvents.Any(e => e.Track >= infos[e.SourcePath ?? segment.Path].AudioTracks)) throw new InvalidDataException("Звуковой фрагмент ссылается на отсутствующую дорожку.");
            if (segment.AudioTrack >= tracks && segment.AudioTrack >= 0) throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            bool samePicture = source.Width == width && source.Height == height && Math.Abs(source.Fps - first.Fps) < .01;
            string prepare = samePicture
                ? "format=yuv420p,setpts=PTS-STARTPTS"
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
        graph.Append(videoParts).Append($"concat=n={pieces}:v=1:a=0[vbase];");
        if (duration > videoDuration + .001)
            graph.Append($"color=c=black:s={width}x{height}:r={Number(first.Fps)}:d={Number(duration - videoDuration)}[tail];[vbase][tail]concat=n=2:v=1:a=0[v];");
        else graph.Append("[vbase]null[v];");
        for (int i = 0; i < audible.Length; i++)
        {
            var sound = audible[i];
            int input = Array.FindIndex(audioInputs, p => string.Equals(p, sound.Path, StringComparison.OrdinalIgnoreCase));
            graph.Append($"[{segments.Count + input}:a:{sound.Track}]atrim=start={Number(sound.Start.TotalSeconds)}:end={Number(sound.End.TotalSeconds)},asetpts=PTS-STARTPTS,volume={Number(sound.Gain)},aresample=48000,aformat=channel_layouts=stereo,adelay={Number(sound.Position.TotalMilliseconds)}:all=1[a{i}];");
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
                Args("-preset", "p4", "-tune", "hq", "-rc", "vbr");
                if (bitrate is uint nvencBitrate) Args("-b:v", nvencBitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-cq", "19", "-b:v", "0");
            }
            else if (encoder == "h264_qsv")
            {
                Args("-preset", "veryfast", "-global_quality", "20");
                if (bitrate is uint qsvBitrate) Args("-b:v", qsvBitrate.ToString(CultureInfo.InvariantCulture));
            }
            else if (encoder == "h264_amf")
            {
                Args("-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "22");
                if (bitrate is uint amfBitrate) Args("-b:v", amfBitrate.ToString(CultureInfo.InvariantCulture));
            }
            else if (encoder == "mpeg4")
            {
                if (bitrate is uint mpeg4Bitrate) Args("-b:v", mpeg4Bitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-q:v", "3");
            }
            else
            {
                Args("-preset", "veryfast");
                if (bitrate is uint videoBitrate) Args("-b:v", videoBitrate.ToString(CultureInfo.InvariantCulture));
                else Args("-crf", "18");
            }
            Args("-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", temp);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
            var errors = new Queue<string>();
            var errorReader = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                { errors.Enqueue(line); if (errors.Count > 12) errors.Dequeue(); }
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
                // Кодека исходника в этой сборке нет — другой энкодер не поможет.
                if (errors.FirstOrDefault(e => e.Contains("no decoder found", StringComparison.OrdinalIgnoreCase) ||
                                               e.Contains("Decoder not found", StringComparison.OrdinalIgnoreCase)) is string missing)
                    throw new FfmpegDecoderMissingException(missing.Trim());
                if (!softwareEncoder && encoder != "libx264")
                {
                    Logging.Log.Info("Editor", $"Аппаратный энкодер {encoder} не подошёл, повторяю на libx264: {string.Join(" ", errors.TakeLast(2))}");
                    File.Delete(temp);
                    scriptLock.Dispose(); File.Delete(script);
                    await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, legacyScript, softwareEncoder: true);
                    return;
                }
                if (!legacyScript && errors.Any(e => e.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(temp);
                    scriptLock.Dispose(); File.Delete(script);
                    await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, legacyScript: true, softwareEncoder);
                    return;
                }
                throw new InvalidOperationException("ffmpeg: " + string.Join(" ", errors.TakeLast(4)));
            }
        }
        finally { scriptLock.Dispose(); File.Delete(script); }
    }
}
