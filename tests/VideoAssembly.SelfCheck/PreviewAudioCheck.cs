using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aura.Core.Tools;
using LibVLCSharp.Shared;
using NAudio.Wave;

internal static class PreviewAudioCheck
{
    public static async Task<string> ExtendFixture(string source, string folder, string? ffmpeg)
    {
        string output = Path.Combine(folder, "preview-long.mp4");
        if (File.Exists(output)) return output;
        if (ffmpeg is null) throw new InvalidOperationException("Create fixtures with a full self-check and a path to FFmpeg before --ui-only.");
        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nostdin", "-n", "-stream_loop", "-1", "-i", source, "-t", "12", "-map", "0", "-c", "copy", output }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        return output;
    }

    public static async Task Run(Window window, int index, string folder)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = window.GetType();
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(window, args);
        var segments = ((ListBox)window.FindName("Segments")).Items.Cast<VideoSegment>().ToArray();
        double offset = segments.Take(index).Sum(p => p.Duration.TotalSeconds);
        var video = (MediaPlayer)type.GetField("_assemblyPlayer", flags)!.GetValue(window)!;
        var mute = (CheckBox)window.FindName("MutePreview");
        type.GetField("_wantAssemblyPlaying", flags)!.SetValue(window, false);
        video.SetPause(true); Call("SyncPreviewAudio", .2, false);
        using var engine = new LibVLC("--quiet", "--no-video", "--aout=directsound");
        using var media = new Media(engine, new Uri(segments[index].Path));
        media.AddOption(":audio-track=0");
        using var reference = new MediaPlayer(engine);
        if (!reference.SetAudioOutput("directsound")) throw new Exception("Reference output unavailable");
        reference.Volume = 89;
        reference.Play(media);
        await Task.Delay(500);
        reference.Volume = 89;
        reference.Mute = false;
        using var sessionVolume = new TestSessionVolume();
        await Task.Delay(300);
        var baseline = await Capture("reference");
        reference.Stop();
        if (baseline.Game < .005) throw new Exception("Loopback did not receive the reference tone; cannot verify audible preview");

        Call("SetAudioTrack", 0, new AssemblyAudioTrack(.7, Solo: true), false);
        Call("SetAudioTrack", 1, new AssemblyAudioTrack(.7), false);
        mute.IsChecked = false;
        await Start();
        var solo = await Capture("solo-game");
        if (solo.Game < baseline.Game * .75 || solo.Game > baseline.Game * 1.25 || solo.Mic > baseline.Game * .1)
            throw new Exception($"Game preview duplicated/wrong track: baseline={baseline}, solo={solo}");
        Call("SetAudioTrack", 0, new AssemblyAudioTrack(.35, Solo: true), false);
        await Task.Delay(200);
        var half = await Capture("half-gain");
        if (half.Game < solo.Game * .35 || half.Game > solo.Game * .65)
            throw new Exception($"Preview gain is not linear: full={solo}, half={half}");
        Call("SetAudioTrack", 0, new AssemblyAudioTrack(.7), false);
        Call("SetAudioTrack", 1, new AssemblyAudioTrack(.7, Solo: true), false);
        await Start();
        var mic = await Capture("solo-mic");
        if (mic.Mic < baseline.Game * .5 || mic.Game > baseline.Game * .1)
            throw new Exception($"Microphone solo outputs the wrong track: baseline={baseline}, mic={mic}");
        Call("SetAudioTrack", 1, new AssemblyAudioTrack(.7), false);
        Call("SetAudioTrack", 0, new AssemblyAudioTrack(.7, Solo: true), false);
        await Start();
        var audioPlayers = (System.Collections.IList)type.GetField("_audioPlayers", flags)!.GetValue(window)!;
        var mutedTrack = (MediaPlayer)audioPlayers[1]!.GetType().GetField("Item1")!.GetValue(audioPlayers[1])!;
        long mutedTime = mutedTrack.Time;
        int pauses = 0;
        void Paused(object? sender, EventArgs e) => Interlocked.Increment(ref pauses);
        video.Paused += Paused; mutedTrack.Paused += Paused;
        try { await Capture("continuous", 4000, solo.Game * .5); }
        finally { video.Paused -= Paused; mutedTrack.Paused -= Paused; }
        if (pauses != 0 || mutedTrack.Time < mutedTime + 3000)
            throw new Exception($"Muted track stopped its clock or playback paused: {mutedTime}->{mutedTrack.Time}, pauses={pauses}, state={mutedTrack.State}, track={mutedTrack.AudioTrack}, video={video.Time}/{video.State}, want={type.GetField("_wantAssemblyPlaying", flags)!.GetValue(window)}, wait={type.GetField("_waitingAssemblyFrame", flags)!.GetValue(window)}, opening={type.GetField("_assemblyOpening", flags)!.GetValue(window)}");
        mute.IsChecked = true;
        await Start();
        var silent = await Capture("muted");
        if (silent.Game > baseline.Game * .05 || silent.Mic > baseline.Game * .05)
            throw new Exception($"Muted preview still outputs source audio: baseline={baseline}, muted={silent}");
        sessionVolume.AssertUnchanged();
        type.GetField("_wantAssemblyPlaying", flags)!.SetValue(window, false);
        video.SetPause(true); Call("SyncPreviewAudio", .2, false);
        Call("SetAudioTrack", 0, new AssemblyAudioTrack(), false);
        Call("SetAudioTrack", 1, new AssemblyAudioTrack(), false);
        Console.WriteLine($"PASS: actual WASAPI loopback: one game copy, isolated solo, linear gain, silent video output, unchanged Windows session ({baseline.Game:F4}/{solo.Game:F4}/{half.Game:F4}/{silent.Game:F4})");

        async Task Start()
        {
            type.GetField("_wantAssemblyPlaying", flags)!.SetValue(window, true);
            Call("SeekAssembly", offset + .2);
            for (int i = 0; i < 200 && ((bool)type.GetField("_assemblyOpening", flags)!.GetValue(window)! ||
                (bool)type.GetField("_waitingAssemblyFrame", flags)!.GetValue(window)!); i++) await Task.Delay(20);
            await Task.Delay(300);
        }

        async Task<(double Game, double Mic)> Capture(string name, int milliseconds = 700, double minimumBlockAmplitude = 0)
        {
            string path = Path.Combine(folder, "preview-loopback-" + name + ".wav");
            using var capture = new WasapiLoopbackCapture();
            using (var writer = new WaveFileWriter(path, capture.WaveFormat))
            {
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
                capture.RecordingStopped += (_, e) => { if (e.Exception is not null) stopped.TrySetException(e.Exception); else stopped.TrySetResult(); };
                capture.StartRecording(); await Task.Delay(milliseconds); capture.StopRecording();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            using var reader = new WaveFileReader(path);
            var samples = reader.ToSampleProvider();
            var block = new float[reader.WaveFormat.Channels * 1024];
            double gameSin = 0, gameCos = 0, micSin = 0, micCos = 0; int frame = 0, have;
            double blockSin = 0, blockCos = 0;
            int blockFrames = reader.WaveFormat.SampleRate / 10;
            while ((have = samples.Read(block, 0, block.Length)) > 0)
            for (int i = 0; i < have; i += reader.WaveFormat.Channels, frame++)
            {
                double phase = 2 * Math.PI * frame / reader.WaveFormat.SampleRate;
                gameSin += block[i] * Math.Sin(440 * phase); gameCos += block[i] * Math.Cos(440 * phase);
                micSin += block[i] * Math.Sin(880 * phase); micCos += block[i] * Math.Cos(880 * phase);
                blockSin += block[i] * Math.Sin(440 * phase); blockCos += block[i] * Math.Cos(440 * phase);
                if ((frame + 1) % blockFrames == 0)
                {
                    double level = 2 * Math.Sqrt(blockSin * blockSin + blockCos * blockCos) / blockFrames;
                    if (minimumBlockAmplitude > 0 && frame >= blockFrames && level < minimumBlockAmplitude)
                        throw new Exception($"Preview audio gap/stutter at {frame / (double)reader.WaveFormat.SampleRate:F2}s: amplitude={level:F4}");
                    blockSin = blockCos = 0;
                }
            }
            return (2 * Math.Sqrt(gameSin * gameSin + gameCos * gameCos) / Math.Max(1, frame),
                2 * Math.Sqrt(micSin * micSin + micCos * micCos) / Math.Max(1, frame));
        }
    }

    // Normalize only this self-check's persisted Windows mixer state, and restore it afterwards.
    private sealed class TestSessionVolume : IDisposable
    {
        private readonly List<(NAudio.CoreAudioApi.AudioSessionControl Session, float Volume, bool Mute)> _sessions = [];
        public TestSessionVolume()
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.GetProcessID != Environment.ProcessId) { session.Dispose(); continue; }
                    _sessions.Add((session, session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute));
                    session.SimpleAudioVolume.Volume = 1; session.SimpleAudioVolume.Mute = false;
                }
            }
        }
        public void AssertUnchanged()
        {
            if (_sessions.Any(s => s.Session.SimpleAudioVolume.Mute || Math.Abs(s.Session.SimpleAudioVolume.Volume - 1) > .001))
                throw new Exception("A preview player changed the shared Windows session volume/mute");
        }
        public void Dispose()
        {
            foreach (var entry in _sessions)
            {
                entry.Session.SimpleAudioVolume.Volume = entry.Volume; entry.Session.SimpleAudioVolume.Mute = entry.Mute;
                entry.Session.Dispose();
            }
        }
    }
}
