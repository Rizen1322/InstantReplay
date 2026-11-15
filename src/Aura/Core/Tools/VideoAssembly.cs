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

public sealed record VideoFileInfo(TimeSpan Duration, int Width, int Height, double Fps, int AudioTracks, bool Transformed = false);

public static partial class VideoAssembly
{
    public static Task<VideoFileInfo> ReadInfoAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Видеофайл не найден.", path);
        LibVLCSharp.Shared.Core.Initialize();
        using var engine = new LibVLC("--quiet", "--no-video-title-show");
        using var media = new Media(engine, new Uri(System.IO.Path.GetFullPath(path)));
        using var cancel = ct.Register(media.ParseStop);
        await media.Parse(MediaParseOptions.ParseLocal, timeout: 15000, cancellationToken: ct);
        // Parse may finish on VLC's callback thread; disposing VLC there waits for that same callback.
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
            double.IsFinite(fps) && fps > 0 ? Math.Min(fps, 240) : 30, media.Tracks.Count(t => t.TrackType == TrackType.Audio), video.Orientation != VideoOrientation.TopLeft);
    }, ct);

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
            if (!copied && ffmpeg is not null)
                await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct);
            else if (!copied)
                try { await Task.Run(() => RenderWindowsAsync(segments, temp, targetMegabytes, progress, ct), ct); }
                catch (System.Runtime.InteropServices.COMException ex)
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

    private static async Task<bool> TryRemuxAsync(VideoSegment part, string output, IProgress<double>? progress, CancellationToken ct)
    {
        // shortcut: stream-copy only a complete MP4 here; arbitrary cut boundaries need smart rendering.
        if (part.Start != TimeSpan.Zero || part.Gap != TimeSpan.Zero || !string.Equals(Path.GetExtension(part.Path), ".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        var info = await ReadInfoAsync(part.Path, ct);
        if (info.Transformed || Math.Abs((part.End - info.Duration).TotalSeconds) > .02) return false;
        if (part.AudioTrack >= info.AudioTracks && part.AudioTrack >= 0 || part.AudioEvents.Any(e => (e.SourcePath is null || e.SourcePath == part.Path) && e.Track >= info.AudioTracks))
            throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
        var allSounds = AssemblyAudio.Sounds([part], _ => info.AudioTracks);
        if (allSounds.Any(s => !s.Removed && s.Finish > part.Duration + TimeSpan.FromMilliseconds(1))) return false;
        var sounds = allSounds.Where(s => s.Gain > 0).ToArray();
        if (sounds.Any(s => s.Path != part.Path || s.Gain != 1 || s.Start != TimeSpan.Zero || s.Position != TimeSpan.Zero ||
            Math.Abs((s.End - part.End).TotalSeconds) > .001) || sounds.Select(s => s.Track).Distinct().Count() != sounds.Length) return false;
        var clock = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => Mp4Trim.Remux(part.Path, output, sounds.Select(s => s.Track).ToArray(),
                tracks => AudioMixdown.Mix(tracks, ct), value => progress?.Report(value), ct), ct);
            Logging.Log.Info("Editor", $"Экспорт без перекодирования видео: {clock.ElapsedMilliseconds} мс, {sounds.Length} исходных дорожек звука");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or System.Runtime.InteropServices.COMException)
        {
            File.Delete(output);
            Logging.Log.Info("Editor", "Прямой экспорт недоступен: " + ex.Message);
            return false;
        }
    }

    private static async Task RenderWindowsAsync(IReadOnlyList<VideoSegment> segments, string temp,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? originalAudio = null)
    {
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
        string AudioPath(string path) => originalAudio?.GetValueOrDefault(path) ?? path;
        Logging.Log.Info("Editor", "Windows: подготовка звуковых источников");
        foreach (string path in segments.Select(p => AudioPath(p.Path)).Concat(segments.SelectMany(p => p.AudioEvents).Select(e => e.SourcePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!sources.ContainsKey(path)) sources[path] = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));
        int TrackCount(string path) => sources[AudioPath(path)].EmbeddedAudioTracks.Count;
        foreach (var part in segments)
            if (part.AudioTrack >= TrackCount(part.Path) && part.AudioTrack >= 0 ||
                part.AudioEvents.Any(e => e.Track >= TrackCount(e.SourcePath ?? part.Path)))
                throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
        var sounds = AssemblyAudio.Sounds(segments, TrackCount);
        foreach (var sound in sounds.Where(s => s.Gain > 0))
        {
            string path = AudioPath(sound.Path);
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
            throw new InvalidOperationException($"Windows не смогла обработать формат видео ({result}). Выберите ffmpeg в окне сборки.");
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
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct, bool legacyScript = false)
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
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(ffmpeg),
            Environment = { ["PATH"] = System.IO.Path.GetDirectoryName(ffmpeg) + System.IO.Path.PathSeparator + System.Environment.SystemDirectory }
        };
        void Args(params string[] args) { foreach (string arg in args) psi.ArgumentList.Add(arg); }
        Args("-nostdin", "-hide_banner", "-n", "-progress", "pipe:1", "-nostats");
        foreach (var segment in segments)
        {
            if (segment.Start >= infos[segment.Path].Duration || segment.End > infos[segment.Path].Duration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Границы фрагмента выходят за конец видео.");
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
            int tracks = infos[segment.Path].AudioTracks;
            if (segment.AudioEvents.Any(e => e.Track >= infos[e.SourcePath ?? segment.Path].AudioTracks)) throw new InvalidDataException("Звуковой фрагмент ссылается на отсутствующую дорожку.");
            if (segment.AudioTrack >= tracks && segment.AudioTrack >= 0) throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            graph.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={Number(first.Fps)},format=yuv420p,setpts=PTS-STARTPTS[v{i}];");
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
            Args(legacyScript ? "-filter_complex_script" : "-/filter_complex", script, "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p");
            if (bitrate is uint videoBitrate) Args("-b:v", videoBitrate.ToString(CultureInfo.InvariantCulture));
            else Args("-crf", "18");
            Args("-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", temp);
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
                if (!legacyScript && errors.Any(e => e.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(temp);
                    scriptLock.Dispose(); File.Delete(script);
                    await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct, legacyScript: true);
                    return;
                }
                throw new InvalidOperationException("ffmpeg: " + string.Join(" ", errors.TakeLast(4)));
            }
        }
        finally { scriptLock.Dispose(); File.Delete(script); }
    }
}
