using System.Diagnostics;
using System.IO;
using System.Reflection;
using Aura.Core.Audio;
using Aura.Core.Saving.Mp4;
using Aura.Core.Tools;

internal static class AssemblyExportCheck
{
    public static async Task Run(string ffmpeg, string red, string blue, string twoTracks, string folder)
    {
        var placed = new VideoSegment(red, TimeSpan.Zero, TimeSpan.FromSeconds(3))
        {
            AudioDetached = true, AudioLinked = false,
            AudioEvents = [new(0, TimeSpan.Zero, TimeSpan.FromSeconds(1), .5) { SourcePath = blue, Position = TimeSpan.FromSeconds(.5) },
                new(0, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1.5)) { SourcePath = red, Position = TimeSpan.FromSeconds(3.5) }]
        };
        foreach (string renderer in new[] { "windows", "ffmpeg", "fallback" })
        {
            string output = Path.Combine(folder, "placed-" + renderer + ".mp4");
            if (renderer == "fallback")
                await (Task)typeof(VideoAssembly).GetMethod("RenderWithVlcAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [new[] { placed }, output, null, null, CancellationToken.None])!;
            else await VideoAssembly.RenderAsync([placed], output, renderer == "ffmpeg" ? ffmpeg : null);
            var info = await VideoAssembly.ReadInfoAsync(output);
            Require(Math.Abs(info.Duration.TotalSeconds - 4.5) < .15, renderer + ": detached audio tail was truncated");
            var audio = Mp4Trim.ReadAudio(output).Single(); var pcm = new List<float>();
            using (var decoder = new AacDecoder(audio.SampleRate, audio.Channels, audio.Config))
                for (int i = 0; i < audio.Frames.Count; i++) decoder.Decode(audio.Frames[i], (long)i * 1024 * 10000000 / audio.SampleRate, pcm);
            double Amplitude(int frequency, double at)
            {
                int start = (int)(at * audio.SampleRate), length = audio.SampleRate / 5;
                double sin = 0, cos = 0;
                for (int i = 0; i < length; i++)
                {
                    double sample = pcm[(start + i) * audio.Channels], phase = 2 * Math.PI * frequency * i / audio.SampleRate;
                    sin += sample * Math.Sin(phase); cos += sample * Math.Cos(phase);
                }
                return 2 * Math.Sqrt(sin * sin + cos * cos) / length;
            }
            Require(Amplitude(440, .2) < .01 && Amplitude(880, .2) < .01, renderer + ": detached sound started too early");
            Require(Amplitude(880, .8) > .04 && Amplitude(440, .8) < .01, renderer + ": moved audio used the wrong source/time");
            Require(Amplitude(440, 3.7) > .08, renderer + ": sound after the end of video was lost");
        }
        Console.WriteLine("PASS: detached audio position/source/gain and black video tail in Windows, FFmpeg and bundled fallback");

        var shiftedVideo = new VideoSegment(red, TimeSpan.Zero, TimeSpan.FromSeconds(3))
        {
            Gap = TimeSpan.FromSeconds(1), AudioLinked = false, AudioDetached = true,
            AudioEvents = [new AudioEvent(0, TimeSpan.Zero, TimeSpan.FromSeconds(3)) { SourcePath = red, Position = TimeSpan.Zero }]
        };
        foreach (string renderer in new[] { "windows", "ffmpeg", "fallback" })
        {
            string output = Path.Combine(folder, "video-shifted-" + renderer + ".mp4");
            if (renderer == "fallback")
                await (Task)typeof(VideoAssembly).GetMethod("RenderWithVlcAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [new[] { shiftedVideo }, output, null, null, CancellationToken.None])!;
            else await VideoAssembly.RenderAsync([shiftedVideo], output, renderer == "ffmpeg" ? ffmpeg : null);
            var info = await VideoAssembly.ReadInfoAsync(output);
            Require(Math.Abs(info.Duration.TotalSeconds - 4) < .15, renderer + ": video gap was not appended to the movie");
            var audio = Mp4Trim.ReadAudio(output).Single(); var pcm = new List<float>();
            using (var decoder = new AacDecoder(audio.SampleRate, audio.Channels, audio.Config))
                for (int i = 0; i < audio.Frames.Count; i++) decoder.Decode(audio.Frames[i], (long)i * 1024 * 10000000 / audio.SampleRate, pcm);
            double Amplitude(int frequency, double at)
            {
                int start = (int)(at * audio.SampleRate), length = audio.SampleRate / 5;
                double sin = 0, cos = 0;
                for (int i = 0; i < length; i++)
                {
                    double sample = pcm[(start + i) * audio.Channels], phase = 2 * Math.PI * frequency * i / audio.SampleRate;
                    sin += sample * Math.Sin(phase); cos += sample * Math.Cos(phase);
                }
                return 2 * Math.Sqrt(sin * sin + cos * cos) / length;
            }
            Require(Amplitude(440, .2) > .04, renderer + ": detached audio was shifted together with video");
            var (first, atGap) = await FrameColors(output);
            Require(first is { R: < 24, G: < 24, B: < 24 }, renderer + $": gap was not black ({first})");
            Require(atGap is { R: > 200, G: < 60, B: < 60 }, renderer + $": video did not start after the gap ({atGap})");
        }
        Console.WriteLine("PASS: leading video gap keeps audio at zero, exports black filler then video in all three renderers");

        async Task<(System.Drawing.Color First, System.Drawing.Color AtGap)> FrameColors(string path)
        {
            string first = Path.Combine(folder, "gap-first.rgb"), at = Path.Combine(folder, "gap-video.rgb");
            await Command("-v", "error", "-y", "-ss", "0.5", "-i", path, "-frames:v", "1", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", first);
            await Command("-v", "error", "-y", "-ss", "1.5", "-i", path, "-frames:v", "1", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", at);
            var a = File.ReadAllBytes(first); var b = File.ReadAllBytes(at);
            return (System.Drawing.Color.FromArgb(a[0], a[1], a[2]), System.Drawing.Color.FromArgb(b[0], b[1], b[2]));
        }

        string unit = Path.Combine(folder, "hevc10-unit.mp4"), source = Path.Combine(folder, "hevc10-110s.mp4"), fast = Path.Combine(folder, "hevc10-muted-fast.mp4");
        await Command("-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=1280x720:r=30:d=1", "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-an", unit);
        await Command("-v", "error", "-stream_loop", "-1", "-i", unit, "-stream_loop", "-1", "-i", twoTracks, "-map", "0:v:0", "-map", "1:a", "-t", "110", "-c", "copy", source);
        var sourceInfo = await VideoAssembly.ReadInfoAsync(source);
        var muted = new VideoSegment(source, TimeSpan.Zero, sourceInfo.Duration)
        { AudioEvents = Enumerable.Range(0, sourceInfo.AudioTracks).Select(t => new AudioEvent(t, TimeSpan.Zero, sourceInfo.Duration, Muted: true)).ToArray() };
        var clock = Stopwatch.StartNew();
        await VideoAssembly.RenderAsync([muted], fast);
        clock.Stop();
        Require(clock.Elapsed < TimeSpan.FromSeconds(5), "Complete muted HEVC10 video was transcoded instead of copied");
        string originalHash = await Command("-v", "error", "-i", source, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-hash", "sha256", "-");
        string copiedHash = await Command("-v", "error", "-i", fast, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-hash", "sha256", "-");
        Require(originalHash == copiedHash && Mp4Trim.ReadAudio(fast).Count == 0, "Fast export changed HEVC payload or retained muted audio");
        // Removing AAC can shorten movie metadata; compare against an independent video-only remux.
        string reference = Path.Combine(folder, "hevc10-video-reference.mp4");
        await Command("-v", "error", "-i", source, "-map", "0:v:0", "-an", "-c", "copy", reference);
        Require(Math.Abs((await VideoAssembly.ReadInfoAsync(fast)).Duration.TotalSeconds - (await VideoAssembly.ReadInfoAsync(reference)).Duration.TotalSeconds) < .1, "Fast export changed video duration");
        async Task<string> DecodedFrames(string path)
        {
            string report = await Command("-v", "error", "-i", path, "-map", "0:v:0", "-an", "-progress", "pipe:1", "-f", "null", "NUL");
            return System.Text.RegularExpressions.Regex.Matches(report, @"(?m)^frame=(\d+)").Last().Groups[1].Value;
        }
        Require(await DecodedFrames(fast) == await DecodedFrames(reference), "Fast export hid the final B-frames");
        Console.WriteLine($"PASS: 110-second HEVC10 muted export in {clock.ElapsedMilliseconds}ms; original compressed video hash preserved, no audio");

        async Task<string> Command(params string[] args)
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            Require(process.ExitCode == 0, await error); return (await output).Trim();
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
