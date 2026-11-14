using System.Diagnostics;
using System.Globalization;
using System.Text;
using Aura.Core.Storage;
using LibVLCSharp.Shared;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Aura.Core.Tools;

public sealed record VideoFileInfo(TimeSpan Duration, int Width, int Height, double Fps, int AudioTracks);

public static class VideoAssembly
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
            double.IsFinite(fps) && fps > 0 ? Math.Min(fps, 240) : 30, media.Tracks.Count(t => t.TrackType == TrackType.Audio));
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
            if (ffmpeg is not null)
                await RenderFfmpegAsync(segments, temp, ffmpeg, targetMegabytes, progress, ct);
            else
                try { await RenderWindowsAsync(segments, temp, targetMegabytes, progress, ct); }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException($"Windows не смогла собрать видео: {ex.Message} Для дополнительных форматов выберите ffmpeg.exe кнопкой «Дополнительные форматы…».", ex);
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

    private static async Task RenderWindowsAsync(IReadOnlyList<VideoSegment> segments, string temp,
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct)
    {
        var composition = new MediaComposition();
        TimeSpan offset = TimeSpan.Zero;
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(segment.Path));
            var clip = await MediaClip.CreateFromFileAsync(file);
            if (segment.Start >= clip.OriginalDuration || segment.End > clip.OriginalDuration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException("Границы фрагмента выходят за конец видео.");
            TimeSpan end = segment.End < clip.OriginalDuration ? segment.End : clip.OriginalDuration;
            clip.TrimTimeFromStart = segment.Start;
            clip.TrimTimeFromEnd = clip.OriginalDuration - end;
            int count = clip.EmbeddedAudioTracks.Count;
            if (segment.AudioTrack >= count && segment.AudioTrack >= 0)
                throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            if (count > 0)
                clip.SelectedEmbeddedAudioTrackIndex = segment.AudioTrack < 0 ? 0 :
                    VideoEditor.WindowsAudioTrackIndex(segment.Path, count, segment.AudioTrack);
            composition.Clips.Add(clip);
            for (int i = 0; segment.AudioTrack < 0 && i < count; i++)
            {
                if (i == clip.SelectedEmbeddedAudioTrackIndex) continue;
                var audio = BackgroundAudioTrack.CreateFromEmbeddedAudioTrack(clip.EmbeddedAudioTracks[i]);
                TimeSpan audioEnd = end < audio.OriginalDuration ? end : audio.OriginalDuration;
                if (segment.Start >= audioEnd) continue;
                audio.TrimTimeFromStart = segment.Start;
                audio.TrimTimeFromEnd = audio.OriginalDuration - audioEnd;
                audio.Delay = offset;
                composition.BackgroundAudioTracks.Add(audio);
            }
            offset += end - segment.Start;
        }
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        var original = composition.CreateDefaultEncodingProfile();
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
        int? targetMegabytes, IProgress<double>? progress, CancellationToken ct)
    {
        var infos = new Dictionary<string, VideoFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in segments.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            infos[path] = await ReadInfoAsync(path, ct);
        var first = infos[segments[0].Path];
        double duration = segments.Sum(s => s.Duration.TotalSeconds);
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
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            int tracks = infos[segment.Path].AudioTracks;
            if (segment.AudioTrack >= tracks && segment.AudioTrack >= 0) throw new InvalidDataException("Выбранная звуковая дорожка отсутствует.");
            graph.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={Number(first.Fps)},format=yuv420p,setpts=PTS-STARTPTS[v{i}];");
            if (tracks == 0) graph.Append("anullsrc=r=48000:cl=stereo,");
            else if (segment.AudioTrack < 0 && tracks > 1)
            {
                for (int t = 0; t < tracks; t++) graph.Append($"[{i}:a:{t}]");
                graph.Append($"amix=inputs={tracks}:duration=longest:normalize=0,alimiter=limit=0.97:level=disabled,");
            }
            else graph.Append($"[{i}:a:{Math.Max(0, segment.AudioTrack)}]");
            graph.Append($"aresample=48000,aformat=channel_layouts=stereo,apad,atrim=duration={Number(segment.Duration.TotalSeconds)},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (int i = 0; i < segments.Count; i++) graph.Append($"[v{i}][a{i}]");
        graph.Append($"concat=n={segments.Count}:v=1:a=1[v][a]");
        Args("-filter_complex", graph.ToString(), "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p");
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
        if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg: " + string.Join(" ", errors.TakeLast(4)));
    }
}
