using System.IO;
using System.Security.Cryptography;
using Aura.Core.Tools;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;
using Aura.Core.Audio;
using Aura.Core.Saving.Mp4;
using Vortice.MediaFoundation;

static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
bool uiOnly = args.Length == 2 && args[0] == "--ui-only";
string root = uiOnly ? Path.GetFullPath(args[1]) : Path.GetFullPath(Path.Combine("artifacts", "tests", "assembly-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
MediaFactory.MFStartup();
string? mixedPreviewSource = null;
async Task<string> Fixture(string name, Color color, int width, int height, double seconds, int frequency)
{
    var composition = new MediaComposition();
    composition.Clips.Add(MediaClip.CreateFromColor(color, TimeSpan.FromSeconds(seconds)));
    string wav = Path.Combine(root, name + ".wav");
    using (var writer = new BinaryWriter(File.Create(wav)))
    {
        int samples = (int)(seconds * 48000), bytes = samples * 2;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes);
        for (int i = 0; i < samples; i++) writer.Write((short)(8000 * Math.Sin(2 * Math.PI * frequency * i / 48000)));
    }
    composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav)));
    var folder = await StorageFolder.GetFolderFromPathAsync(root);
    var output = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
    var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
    profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
    profile.Audio.SampleRate = 48000;
    profile.Video.FrameRate.Numerator = 30; profile.Video.FrameRate.Denominator = 1;
    Require(await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile) == TranscodeFailureReason.None, "Fixture render failed");
    return output.Path;
}
string red = uiOnly ? Path.Combine(root, "красный ' [1].mp4") : await Fixture("красный ' [1].mp4", Color.FromArgb(255, 255, 0, 0), 320, 180, 3, 440);
string blue = uiOnly ? Path.Combine(root, "синий ; [2].mp4") : await Fixture("синий ; [2].mp4", Color.FromArgb(255, 0, 0, 255), 180, 320, 2, 880);
if (uiOnly) { mixedPreviewSource = Path.Combine(root, "две дорожки.mp4"); await CheckUi(); MediaFactory.MFShutdown(); return; }
string redHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(red)));
string blueHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(blue)));
var redInfo = await VideoAssembly.ReadInfoAsync(red);
Require(redInfo.Width == 320 && redInfo.Height == 180 && Math.Abs(redInfo.Duration.TotalSeconds - 3) < 0.1, "Input probe wrong");
VideoSegment[] parts = [new(red, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5)), new(blue, TimeSpan.Zero, TimeSpan.FromSeconds(1.5)), new(red, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5))];
string rendered = await VideoAssembly.RenderAsync(parts, Path.Combine(root, "assembled.mp4"));
var result = await VideoAssembly.ReadInfoAsync(rendered);
Require(Math.Abs(result.Duration.TotalSeconds - 3) < 0.15, "Output duration differs from sum of selected ranges: " + result.Duration);
Require(result.Width == 320 && result.Height == 180, "Output resolution not normalized");
Mp4Trim.AacClip? audio = null;
Mp4Trim.MixAudio(rendered, Path.Combine(root, "audio.m4a"), tracks => audio = tracks[0]);
Require(audio is not null, "Assembled audio missing");
var pcm = new List<float>();
using (var decoder = new AacDecoder(audio!.SampleRate, audio.Channels, audio.Config))
    for (int i = 0; i < audio.Frames.Count; i++) decoder.Decode(audio.Frames[i], (long)i * 1024 * 10000000 / audio.SampleRate, pcm);
double Amplitude(double hz, double at)
{
    double re = 0, im = 0;
    int count = 4800, first = (int)(at * audio.SampleRate);
    for (int i = 0; i < count; i++)
    {
        re += pcm[(first + i) * audio.Channels] * Math.Cos(2 * Math.PI * hz * i / audio.SampleRate);
        im += pcm[(first + i) * audio.Channels] * Math.Sin(2 * Math.PI * hz * i / audio.SampleRate);
    }
    return Math.Sqrt(re * re + im * im) / count;
}
Require(Amplitude(440, 0.2) > 0.05 && Amplitude(880, 1.2) > 0.05 && Amplitude(440, 2.7) > 0.05, "Audio ordering or joins are wrong");
Require(redHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(red))) && blueHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(blue))), "Source changed");
Console.WriteLine("PASS: native composition, multiple ranges, mixed dimensions, duration, audio ordering, sources unchanged");
try { await VideoAssembly.RenderAsync(parts, red); throw new Exception("Source overwrite allowed"); }
catch (InvalidOperationException) { }
try { await VideoAssembly.RenderAsync(parts, rendered); throw new Exception("Existing output overwrite allowed"); }
catch (IOException) { }
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await VideoAssembly.RenderAsync(parts, Path.Combine(root, "cancelled.mp4"), ct: cancelled.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { }
Require(!File.Exists(Path.Combine(root, "cancelled.mp4")) && !Directory.GetFiles(root, "*.part.mp4").Any(), "Cancelled output leaked");
using var cancelNative = new CancellationTokenSource();
try
{
    await VideoAssembly.RenderAsync(Enumerable.Repeat(parts, 8).SelectMany(p => p).ToArray(), Path.Combine(root, "cancel-native.mp4"),
        progress: new CancelOnProgress(cancelNative), ct: cancelNative.Token);
    throw new Exception("Running Windows render cancellation ignored");
}
catch (OperationCanceledException) { }
Require(!File.Exists(Path.Combine(root, "cancel-native.mp4")) && !Directory.GetFiles(root, "*.part.mp4").Any(), "Windows cancellation leaked partial output");
string limited = await VideoAssembly.RenderAsync(parts, Path.Combine(root, "limited.mp4"), targetMegabytes: 1);
Require(new FileInfo(limited).Length <= 1024 * 1024, "Size limit ignored");
Console.WriteLine("PASS: overwrite guards, cancellation, partial cleanup, size limit");
if (args.Length > 0)
{
    string ffmpeg = args[0];
    string encoded = await VideoAssembly.RenderAsync(parts, Path.Combine(root, "ffmpeg.mp4"), ffmpeg, targetMegabytes: 1);
    var encodedInfo = await VideoAssembly.ReadInfoAsync(encoded);
    Require(Math.Abs(encodedInfo.Duration.TotalSeconds - 3) < 0.15 && encodedInfo.AudioTracks == 1, "FFmpeg output duration/audio incorrect");
    Require(new FileInfo(encoded).Length <= 1024 * 1024, "FFmpeg size limit ignored");
    string foreign = Path.Combine(root, "сторонний.mkv"), silent = Path.Combine(root, "без звука.mkv");
    async Task Run(params string[] parameters)
    {
        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string parameter in parameters) start.ArgumentList.Add(parameter);
        using var process = System.Diagnostics.Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, error);
    }
    await Run("-nostdin", "-n", "-i", red, "-f", "lavfi", "-i", "sine=frequency=880:duration=3:sample_rate=48000,volume=2", "-map", "0:v:0", "-map", "0:a:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-shortest", foreign);
    await Run("-nostdin", "-n", "-i", blue, "-an", "-c:v", "copy", silent);
    Require((await VideoAssembly.ReadInfoAsync(foreign)).AudioTracks == 2, "Foreign track probe failed");
    Require(!new Aura.Core.Library.ClipItem(foreign, root).IsScreenshot, "Project media classified an MKV as an image");
    Require((await VideoAssembly.ReadInfoAsync(silent)).AudioTracks == 0, "Silent track probe failed");
    string twoTrackMp4 = Path.Combine(root, "две дорожки.mp4");
    mixedPreviewSource = twoTrackMp4;
    await Run("-nostdin", "-n", "-i", foreign, "-map", "0", "-c", "copy", twoTrackMp4);
    // Сборка силами Windows (без ffmpeg): явный выбор дорожки должен давать именно её
    foreach (string nativeSource in new[] { foreign, twoTrackMp4 })
    foreach (var (track, keep, drop) in new[] { (0, 440, 880), (1, 880, 440) })
    foreach (bool precise in new[] { false, true })
    {
        string native = precise
            ? await VideoEditor.TrimPreciseAsync(nativeSource, TimeSpan.Zero, TimeSpan.FromSeconds(1), track)
            : await VideoAssembly.RenderAsync([new(nativeSource, TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioTrack: track)],
                Path.Combine(root, $"native-{Path.GetExtension(nativeSource)}-track{track}.mp4"));
        Mp4Trim.AacClip? picked = null;
        Mp4Trim.MixAudio(native, Path.ChangeExtension(native, ".m4a"), tracks => picked = tracks[0]);
        var nativePcm = new List<float>();
        using (var decoder = new AacDecoder(picked!.SampleRate, picked.Channels, picked.Config))
            for (int i = 0; i < picked.Frames.Count; i++) decoder.Decode(picked.Frames[i], (long)i * 1024 * 10000000 / picked.SampleRate, nativePcm);
        audio = picked; pcm = nativePcm;
        Require(Amplitude(keep, 0.3) > 0.04 && Amplitude(drop, 0.3) < 0.01,
            $"Windows render picked the wrong audio track for {nativeSource}, {track}, precise={precise}: {keep} Гц {Amplitude(keep, 0.3):F3}, {drop} Гц {Amplitude(drop, 0.3):F3}, " +
            $"дорожек в файле {(await VideoAssembly.ReadInfoAsync(native)).AudioTracks}");
    }
    {
        string together = await VideoAssembly.RenderAsync([new(foreign, TimeSpan.Zero, TimeSpan.FromSeconds(1))],
            Path.Combine(root, "native-together.mp4"));
        Mp4Trim.AacClip? mixedNative = null;
        Mp4Trim.MixAudio(together, Path.Combine(root, "native-together.m4a"), tracks => mixedNative = tracks[0]);
        var togetherPcm = new List<float>();
        using (var decoder = new AacDecoder(mixedNative!.SampleRate, mixedNative.Channels, mixedNative.Config))
            for (int i = 0; i < mixedNative.Frames.Count; i++) decoder.Decode(mixedNative.Frames[i], (long)i * 1024 * 10000000 / mixedNative.SampleRate, togetherPcm);
        audio = mixedNative; pcm = togetherPcm;
        Require(Amplitude(440, 0.3) > 0.04 && Amplitude(880, 0.3) > 0.04, "Windows render «together» lost an audio track");
    }
    Console.WriteLine("PASS: Windows render explicit audio track and together");
    var independent = new VideoSegment(twoTrackMp4, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(2.5)) { AudioLinked = false };
    independent = AssemblyAudio.Split(independent, 0, TimeSpan.FromSeconds(1.5));
    independent = AssemblyAudio.Split(independent, 1, TimeSpan.FromSeconds(1.5));
    independent = AssemblyAudio.Change(independent, 0, TimeSpan.FromSeconds(1), a => a with { Removed = true });
    independent = AssemblyAudio.Change(independent, 1, TimeSpan.FromSeconds(1), a => a with { Volume = .25 });
    independent = AssemblyAudio.Change(independent, 1, TimeSpan.FromSeconds(2), a => a with { Muted = true });
    foreach (string renderer in new[] { "windows", "ffmpeg", "fallback" })
    {
        string editedAudio = Path.Combine(root, "independent-audio-" + renderer + ".mp4");
        if (renderer == "fallback")
            await (Task)typeof(VideoAssembly).GetMethod("RenderWithVlcAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [new[] { independent }, editedAudio, null, null, CancellationToken.None])!;
        else await VideoAssembly.RenderAsync([independent], editedAudio, renderer == "ffmpeg" ? ffmpeg : null);
        Mp4Trim.AacClip? edited = null;
        Mp4Trim.MixAudio(editedAudio, Path.ChangeExtension(editedAudio, ".m4a"), tracks => edited = tracks[0]);
        var samples = new List<float>();
        using (var decoder = new AacDecoder(edited!.SampleRate, edited.Channels, edited.Config))
            for (int i = 0; i < edited.Frames.Count; i++) decoder.Decode(edited.Frames[i], (long)i * 1024 * 10000000 / edited.SampleRate, samples);
        audio = edited; pcm = samples;
        Require(Amplitude(440, .3) < .01 && Amplitude(880, .3) is > .015 and < .1,
            $"{renderer}: deleted/attenuated audio events ignored: {Amplitude(440, .3):F3}, {Amplitude(880, .3):F3}");
        Require(Amplitude(440, 1.3) > .04 && Amplitude(880, 1.3) < .01,
            $"{renderer}: audio-only split/mute damaged the other track: {Amplitude(440, 1.3):F3}, {Amplitude(880, 1.3):F3}");
        Require(Math.Abs((await VideoAssembly.ReadInfoAsync(editedAudio)).Duration.TotalSeconds - 2) < .15,
            "Deleting audio changed video duration");
    }
    var waves = AudioWaveform.Read(twoTrackMp4, CancellationToken.None);
    Require(waves.Count == 2 && waves.All(w => w.Peak(.3, .6) > .05), "Waveforms did not decode both actual audio tracks");
    var peakCheck = new AudioWaveform(Enumerable.Range(0, 50000).Select(i => (float)(i % 101) / 100).ToArray(), 0);
    var peakClock = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 10000; i++) Require(peakCheck.Peak(0, 1000) == 1, "Waveform peak hierarchy lost the maximum");
    Require(peakClock.ElapsedMilliseconds < 1000, "Zoomed-out waveform lookup scales with the whole source");
    Console.WriteLine("PASS: independent audio split, deletion, gain and mute in Windows, FFmpeg and bundled fallback; real waveform peaks");
    await AssemblyExportCheck.Run(ffmpeg, red, blue, twoTrackMp4, root);
    string previewAudio = Path.Combine(root, "preview-mix.m4a");
    await Ffmpeg.MixAudioAsync(ffmpeg, foreign, previewAudio, 2, CancellationToken.None);
    Require(File.Exists(previewAudio) && new FileInfo(previewAudio).Length > 1000, "Foreign preview mix missing");
    try { await Ffmpeg.MixAudioAsync(ffmpeg, foreign, foreign, 2, CancellationToken.None); throw new Exception("Preview mix source overwrite allowed"); }
    catch (IOException) { }
    string foreignOutput = await VideoAssembly.RenderAsync([
        new(foreign, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
        new(foreign, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), AudioTrack: 1),
        new(silent, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))], Path.Combine(root, "foreign.mp4"), ffmpeg);
    Require(Math.Abs((await VideoAssembly.ReadInfoAsync(foreignOutput)).Duration.TotalSeconds - 2.5) < 0.15, "Foreign/silent assembly duration incorrect");
    Mp4Trim.AacClip? mixed = null;
    Mp4Trim.MixAudio(foreignOutput, Path.Combine(root, "foreign.m4a"), tracks => mixed = tracks[0]);
    var foreignPcm = new List<float>();
    using (var decoder = new AacDecoder(mixed!.SampleRate, mixed.Channels, mixed.Config))
        for (int i = 0; i < mixed.Frames.Count; i++) decoder.Decode(mixed.Frames[i], (long)i * 1024 * 10000000 / mixed.SampleRate, foreignPcm);
    audio = mixed; pcm = foreignPcm;
    Require(Amplitude(440, 0.2) > 0.04 && Amplitude(880, 0.2) > 0.04, "Together omitted an audio track");
    Require(Amplitude(440, 1.2) < 0.01 && Amplitude(880, 1.2) > 0.04, "Explicit audio track selection ignored");
    Require(Amplitude(440, 2.2) < 0.01 && Amplitude(880, 2.2) < 0.01, "Silent segment contains previous audio");
    using var cancelRender = new CancellationTokenSource();
    try
    {
        await VideoAssembly.RenderAsync(Enumerable.Repeat(parts, 20).SelectMany(p => p).ToArray(), Path.Combine(root, "cancel-ffmpeg.mp4"), ffmpeg,
            progress: new CancelOnProgress(cancelRender), ct: cancelRender.Token);
        throw new Exception("Running FFmpeg cancellation ignored");
    }
    catch (OperationCanceledException) { }
    Require(!File.Exists(Path.Combine(root, "cancel-ffmpeg.mp4")) && !Directory.GetFiles(root, "*.part.mp4").Any() &&
        !Directory.GetFiles(root, "*.filters.txt").Any(), "FFmpeg cancellation leaked partial output or filter scripts");
    Console.WriteLine("PASS: FFmpeg size limit, foreign MKV, multiple audio tracks, together, explicit track, silence, running cancellation");
    string fallbackRoot = Path.Combine(root, "встроенные кодеки ' проверка");
    Directory.CreateDirectory(fallbackRoot);
    var fallbackMethod = typeof(VideoAssembly).GetMethod("RenderWithVlcAsync",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    VideoSegment[] fallbackParts = [new(twoTrackMp4, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), 0),
        new(twoTrackMp4, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5), 1),
        new(twoTrackMp4, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))];
    for (int repeat = 0; repeat < 3; repeat++)
    {
        string fallbackOutput = Path.Combine(fallbackRoot, $"fallback-{repeat}.mp4");
        await (Task)fallbackMethod.Invoke(null, [fallbackParts, fallbackOutput, null, null, CancellationToken.None])!;
        Require(Math.Abs((await VideoAssembly.ReadInfoAsync(fallbackOutput)).Duration.TotalSeconds - 2.5) < 0.15,
            "Built-in codec fallback changed montage duration");
        Mp4Trim.AacClip? fallbackAudio = null;
    Mp4Trim.MixAudio(fallbackOutput, Path.ChangeExtension(fallbackOutput, ".m4a"), tracks => fallbackAudio = tracks[0]);
        var fallbackPcm = new List<float>();
        using (var decoder = new AacDecoder(fallbackAudio!.SampleRate, fallbackAudio.Channels, fallbackAudio.Config))
            for (int i = 0; i < fallbackAudio.Frames.Count; i++)
                decoder.Decode(fallbackAudio.Frames[i], (long)i * 1024 * 10000000 / fallbackAudio.SampleRate, fallbackPcm);
        audio = fallbackAudio; pcm = fallbackPcm;
        Require(Amplitude(440, 0.2) > 0.04 && Amplitude(880, 0.2) < 0.01, "VLC fallback changed audio track zero");
        Require(Amplitude(440, 1.2) < 0.01 && Amplitude(880, 1.2) > 0.04, "VLC fallback changed audio track one");
        Require(Amplitude(440, 2.2) > 0.04 && Amplitude(880, 2.2) > 0.04, "VLC fallback omitted mixed audio");
        Require(!Directory.GetDirectories(fallbackRoot, ".aura-normalize-*").Any(), "VLC fallback leaked intermediates");
    }
    using var cancelFallback = new CancellationTokenSource();
    try
    {
        await (Task)fallbackMethod.Invoke(null, [fallbackParts, Path.Combine(fallbackRoot, "cancelled.mp4"), null,
            new CancelOnProgress(cancelFallback), cancelFallback.Token])!;
        throw new Exception("Running built-in codec cancellation ignored");
    }
    catch (OperationCanceledException) { }
    Require(!Directory.GetDirectories(fallbackRoot, ".aura-normalize-*").Any(), "Cancelled VLC fallback leaked intermediates");
    Console.WriteLine("PASS: bundled codec fallback preserves selected/mixed audio, cuts and Unicode/apostrophe paths; cancellation cleans intermediates");
}
Console.WriteLine("Fixtures: " + root);
await CheckUi();
MediaFactory.MFShutdown();

async Task CheckUi()
{
var uiFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var uiThread = new Thread(() =>
{
    try
    {
        Console.WriteLine("UI: initialize resources");
        var app = new System.Windows.Application();
        foreach (string resource in new[] { "Palette.Dark", "Icons", "Base", "Controls", "Clips" })
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            { Source = new Uri($"/Aura;component/Theme/{resource}.xaml", UriKind.Relative) });
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var settings = Aura.Services.Settings;
        settings.Current.SaveRootPath = root;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Console.WriteLine("UI: editor handlers");
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var editorType = typeof(Aura.Views.ClipEditorWindow);
                var editor = (System.Windows.Window)Activator.CreateInstance(editorType, flags, null,
                    [new Aura.Core.Library.ClipItem(red, root)], null)!;
                void Set(string field, object value) => editorType.GetField(field, flags)!.SetValue(editor, value);
                object? Call(string method, params object?[] values) => editorType.GetMethod(method, flags)!.Invoke(editor, values);
                Call("ConfigureTimeline", 3.0);
                Set("_startSeconds", 0.5); Set("_endSeconds", 1.5);
                Require(editor.FindName("LimitExportSize") is null && editor.FindName("ExportSizeLimit") is null,
                    "Size limit UI must not exist in ClipEditorWindow");
                Call("RemoveRange_Click", null, new System.Windows.RoutedEventArgs());
                var kept = (IReadOnlyList<VideoSegment>)Call("SelectedSegments")!;
                Require(kept.Count == 2 && kept.Sum(s => s.Duration.TotalSeconds) == 2, "Editor deletion wiring failed");
                Call("UndoCut_Click", null, new System.Windows.RoutedEventArgs());
                Require(((IReadOnlyList<VideoSegment>)Call("SelectedSegments")!).Count == 1, "Editor undo wiring failed");
                Set("_startSeconds", 0.5); Set("_endSeconds", 1.5);
                Call("RemoveRange_Click", null, new System.Windows.RoutedEventArgs());
                Set("_isMuted", true);
                editor.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                editor.Left = -30000; editor.Top = -30000; editor.ShowActivated = false; editor.ShowInTaskbar = false;
                editor.Show();
                for (int i = 0; i < 150 && (bool)editorType.GetField("_pauseOnFirstFrame", flags)!.GetValue(editor)!; i++) await Task.Delay(100);
                Require(!(bool)editorType.GetField("_pauseOnFirstFrame", flags)!.GetValue(editor)!, "Actual editor preview did not initialize");
                ((System.Windows.Threading.DispatcherTimer)editorType.GetField("_timer", flags)!.GetValue(editor)!).Stop();
                var player = (LibVLCSharp.Shared.MediaPlayer)editorType.GetField("_player", flags)!.GetValue(editor)!;
                player.SetPause(false);
                for (int i = 0; i < 30 && !player.IsPlaying; i++) await Task.Delay(50);
                Call("Seek", 1.0);
                for (int i = 0; i < 30 && (player.Time < 900 || player.Time >= 1500); i++) await Task.Delay(10);
                Set("_lastSeekApplied", Environment.TickCount64 + 1000);
                Require((bool)Call("SkipRemovedSection")!, "Preview did not recognize a removed section");
                long cutSeekTick = (long)editorType.GetField("_seekTick", flags)!.GetValue(editor)!;
                await Task.Delay(25);
                Require(player.Time < 1490, "Deferred-seek fixture did not keep the old playback timestamp");
                Require((bool)Call("SkipRemovedSection")!, "Preview lost its pending jump across the cut");
                Require((long)editorType.GetField("_seekTick", flags)!.GetValue(editor)! == cutSeekTick,
                    "Preview keeps restarting the same seek before the decoder reaches the next part");
                Set("_watchNext", 0L);
                Call("Timer_Tick", null, EventArgs.Empty);
                Require((long)editorType.GetField("_watchNext", flags)!.GetValue(editor)! > 0,
                    "Pending cut jump bypassed the frozen-video watchdog");
                for (int i = 0; i < 50 && player.Time < 1490; i++) await Task.Delay(20);
                Require(player.Time >= 1490, "Preview did not seek across the removed section");
                var previewMedia = (LibVLCSharp.Shared.Media)editorType.GetField("_media", flags)!.GetValue(editor)!;
                long displayedAfterCut = previewMedia.Statistics.DisplayedPictures;
                for (int i = 0; i < 70 && previewMedia.Statistics.DisplayedPictures < displayedAfterCut + 5; i++)
                {
                    Call("Timer_Tick", null, EventArgs.Empty);
                    await Task.Delay(20);
                }
                Require(previewMedia.Statistics.DisplayedPictures >= displayedAfterCut + 5,
                    "Video output froze after crossing the cut although player time advanced");
                player.SetPause(true);
                Snapshot(editor, "editor-cuts.png", 1180, 760);
                Set("_selectForAssembly", true);
                Call("Export_Click", null, new System.Windows.RoutedEventArgs());
                Console.WriteLine("UI: assembly handlers");
                var clipsPage = new Aura.Views.ClipsPage();
                var selectedClips = new List<Aura.Core.Library.ClipItem>
                {
                    new(red, root) { IsSelected = true }, new(blue, root) { IsSelected = true }
                };
                typeof(Aura.Views.ClipsPage).GetField("_shown", flags)!.SetValue(clipsPage, selectedClips);
                var editMenuItem = new System.Windows.Controls.MenuItem { Name = "EditItem" };
                var card = new System.Windows.Controls.Button { DataContext = selectedClips[0], ContextMenu = new() };
                card.ContextMenu.Items.Add(editMenuItem);
                void OpenCardMenu()
                {
                    var menuArgs = (System.Windows.Controls.ContextMenuEventArgs)Activator.CreateInstance(typeof(System.Windows.Controls.ContextMenuEventArgs), flags, null, [card, true], null)!;
                    typeof(Aura.Views.ClipsPage).GetMethod("Card_ContextMenuOpening", flags)!.Invoke(clipsPage, [card, menuArgs]);
                }
                OpenCardMenu();
                Require(ReferenceEquals(editMenuItem.Command, Aura.Views.ClipCommands.EditMany) &&
                    ((IReadOnlyList<Aura.Core.Library.ClipItem>)editMenuItem.CommandParameter).SequenceEqual(selectedClips), "Context Edit lost multiple selected clips");
                var unselectedClip = new Aura.Core.Library.ClipItem(red, root);
                card.DataContext = unselectedClip;
                OpenCardMenu();
                Require(ReferenceEquals(editMenuItem.Command, Aura.Views.ClipCommands.Edit) && ReferenceEquals(editMenuItem.CommandParameter, unselectedClip),
                    "Context Edit reused an old multiple selection for an unselected card");
                Console.WriteLine("PASS: context Edit routes multiple selected videos to montage and unselected cards to single editor");
                Require(((IReadOnlyList<VideoSegment>)editorType.GetField("_pickedSegments", flags)!.GetValue(editor)!).Count == 2,
                    "Editor did not return remaining ranges to the assembly");
                var assemblyType = typeof(Aura.Views.VideoAssemblyWindow);
                var assembly = (System.Windows.Window)Activator.CreateInstance(assemblyType, nonPublic: true)!;
                await (Task)assemblyType.GetMethod("AddFilesAsync", flags)!.Invoke(assembly, [new[] { red, blue }])!;
                var list = (System.Windows.Controls.ListBox)assembly.FindName("Segments");
                Require(list.Items.Count == 2, "Assembly import UI failed");
                list.SelectedIndex = 0;
                assemblyType.GetMethod("Move", flags)!.Invoke(assembly, [1]);
                Require(((VideoSegment)list.Items[0]).Path == blue, "Assembly reorder UI failed");
                assembly.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                assembly.Left = -30000; assembly.Top = -30000; assembly.ShowActivated = false; assembly.ShowInTaskbar = false;
                ((System.Windows.Controls.CheckBox)assembly.FindName("MutePreview")).IsChecked = true;
                assembly.Show();
                for (int i = 0; i < 150 && assemblyType.GetField("_assemblyPlayer", flags)!.GetValue(assembly) is null; i++) await Task.Delay(100);
                var assemblyPlayer = (LibVLCSharp.Shared.MediaPlayer?)assemblyType.GetField("_assemblyPlayer", flags)!.GetValue(assembly);
                Require(assemblyPlayer is not null, "Shared assembly preview did not initialize");
                list.SelectedIndex = 0;
                await (Task)assemblyType.GetMethod("SelectAssemblyClipAsync", flags)!.Invoke(assembly, [null])!;
                TimeSpan originalAssemblyEnd = ((VideoSegment)list.Items[0]).End;
                ((System.Windows.Controls.Slider)assembly.FindName("TrimStart")).Value = 0.5;
                ((System.Windows.Controls.Slider)assembly.FindName("TrimEnd")).Value = 1.5;
                Require(((VideoSegment)list.Items[0]).Start == TimeSpan.FromSeconds(0.5) &&
                    ((VideoSegment)list.Items[0]).End == TimeSpan.FromSeconds(1.5), "Inline assembly trimming did not update export ranges: " +
                    string.Join("; ", list.Items.Cast<VideoSegment>().Select(s => s.Range)) + $"; selected={list.SelectedIndex}; changing={assemblyType.GetField("_changingAssembly", flags)!.GetValue(assembly)}; " +
                    $"sliders={((System.Windows.Controls.Slider)assembly.FindName("TrimStart")).Value}..{((System.Windows.Controls.Slider)assembly.FindName("TrimEnd")).Value}");
                assemblyType.GetMethod("UndoAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                Require(((VideoSegment)list.Items[0]).End == originalAssemblyEnd, "Assembly undo did not restore trim");
                assemblyType.GetMethod("SeekAssembly", flags)!.Invoke(assembly, [2.75]);
                for (int i = 0; i < 100 && (int)assemblyType.GetField("_previewIndex", flags)!.GetValue(assembly)! != 1; i++) await Task.Delay(50);
                Require((int)assemblyType.GetField("_previewIndex", flags)!.GetValue(assembly)! == 1, "Common timeline did not seek into the second clip");
                assemblyType.GetMethod("SeekAssembly", flags)!.Invoke(assembly, [0.0]);
                for (int i = 0; i < 100 && (bool)assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)!; i++) await Task.Delay(50);
                assemblyType.GetMethod("PlayAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                for (int i = 0; i < 120 && (int)assemblyType.GetField("_previewIndex", flags)!.GetValue(assembly)! != 1; i++) await Task.Delay(50);
                Require((int)assemblyType.GetField("_previewIndex", flags)!.GetValue(assembly)! == 1, "Assembly preview did not automatically advance between clips");
                if (mixedPreviewSource is not null)
                {
                    mixedPreviewSource = await PreviewAudioCheck.ExtendFixture(mixedPreviewSource, root, uiOnly ? null : args.FirstOrDefault());
                    await (Task)assemblyType.GetMethod("AddFilesAsync", flags)!.Invoke(assembly, [new[] { mixedPreviewSource }])!;
                    list.SelectedIndex = 2;
                    await (Task)assemblyType.GetMethod("SelectAssemblyClipAsync", flags)!.Invoke(assembly, [null])!;
                    for (int i = 0; i < 100 && (bool)assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)!; i++) await Task.Delay(50);
                    var audioPlayers = (System.Collections.IList)assemblyType.GetField("_audioPlayers", flags)!.GetValue(assembly)!;
                    Require(audioPlayers.Count == 2, "Both source audio tracks did not get independent preview players");
                    await PreviewAudioCheck.Run(assembly, 2, root);
                    ((System.Windows.Controls.CheckBox)assembly.FindName("SnapEdits")).IsChecked = false;
                    int beforeSplit = list.Items.Count;
                    assemblyType.GetMethod("SplitAssemblyAt", flags)!.Invoke(assembly, [1.0]);
                    Require(list.Items.Count == beforeSplit + 1 && ((VideoSegment)list.Items[2]).End == TimeSpan.FromSeconds(1) &&
                        ((VideoSegment)list.Items[3]).Start == TimeSpan.FromSeconds(1), "Inline split lost its boundary");
                    assemblyType.GetMethod("UndoAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                    Require(list.Items.Count == beforeSplit, "Assembly undo did not restore split");
                    var audioTimeline = (System.Windows.FrameworkElement)assembly.FindName("Timeline");
                    var audioTimelineType = audioTimeline.GetType();
                    Require((int)audioTimelineType.GetProperty("AudioTracks")!.GetValue(audioTimeline)! == 2 &&
                        ((System.Windows.Controls.StackPanel)assembly.FindName("AudioTrackHeaders")).Children.Count == 2,
                        "Two source audio tracks were not displayed as separate rows");
                    object? AudioCall(string method, params object?[] values) => assemblyType.GetMethod(method, flags)!.Invoke(assembly, values);
                    audioTimelineType.GetProperty("SelectedAudioTrack")!.SetValue(audioTimeline, 1);
                    audioTimelineType.GetProperty("SelectedAudioSeconds")!.SetValue(audioTimeline, .5);
                    AudioCall("SetAudioLink", false);
                    AudioCall("SplitAssemblyAt", 1.0);
                    Require(list.Items.Count == beforeSplit && AssemblyAudio.Events((VideoSegment)list.Items[2], 1).Count == 2 &&
                        AssemblyAudio.Events((VideoSegment)list.Items[2], 0).Count == 1, "Audio-only split changed video or the other track");
                    audioTimelineType.GetProperty("SelectedAudioSeconds")!.SetValue(audioTimeline, .5);
                    AudioCall("Remove_Click", null, new System.Windows.RoutedEventArgs());
                    Require(AssemblyAudio.GainAt((VideoSegment)list.Items[2], 1, .5) == 0 &&
                        AssemblyAudio.GainAt((VideoSegment)list.Items[2], 0, .5) == 1, "Audio Delete removed video or the wrong track");
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    Require(AssemblyAudio.GainAt((VideoSegment)list.Items[2], 1, .5) == 1, "Undo did not restore deleted audio");
                    AudioCall("RedoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    Require(AssemblyAudio.GainAt((VideoSegment)list.Items[2], 1, .5) == 0, "Redo did not restore the audio deletion");
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    Require(((VideoSegment)list.Items[2]).AudioLinked && ((VideoSegment)list.Items[2]).AudioEvents.Count == 0,
                        "Undo did not restore grouping and audio boundaries");
                    for (int i = 0; i < 100 && (bool)assemblyType.GetField("_assemblyOpening", flags)!.GetValue(assembly)!; i++) await Task.Delay(25);
                    assemblyType.GetField("_wantAssemblyPlaying", flags)!.SetValue(assembly, true);
                    assemblyPlayer!.SetPause(false);
                    await Task.Delay(300);
                    AudioCall("ToggleAudioTrackMute", 0);
                    var currentAudio = (System.Collections.IList)assemblyType.GetField("_audioPlayers", flags)!.GetValue(assembly)!;
                    LibVLCSharp.Shared.MediaPlayer AudioPlayer(int track) => (LibVLCSharp.Shared.MediaPlayer)currentAudio[track]!.GetType().GetField("Item1")!.GetValue(currentAudio[track])!;
                    var trackStates = (List<AssemblyAudioTrack>)assemblyType.GetField("_audioTracks", flags)!.GetValue(assembly)!;
                    Require(trackStates[0].Muted && !trackStates[1].Muted && (AudioPlayer(0).Mute || AudioPlayer(0).AudioTrack == -1),
                        "Mute control did not update/isolate the selected track");
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    AudioCall("ToggleAudioTrackSolo", 1);
                    Require(trackStates[1].Solo && !trackStates[0].Solo && trackStates[0].Gain(true) == 0 && trackStates[1].Gain(true) == 1,
                        "Solo control did not isolate the selected track");
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    for (int i = 0; i < 120 && ((bool)assemblyType.GetField("_assemblyOpening", flags)!.GetValue(assembly)! ||
                        (bool)assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)!); i++) await Task.Delay(25);
                    assemblyType.GetField("_fitTimeline", flags)!.SetValue(assembly, false);
                    audioTimelineType.GetProperty("PixelsPerSecond")!.SetValue(audioTimeline, 100.0);
                    assemblyType.GetField("_wantAssemblyPlaying", flags)!.SetValue(assembly, true);
                    assemblyPlayer!.SetPause(false);
                    int loadBeforeVolume = (int)assemblyType.GetField("_assemblyLoadRevision", flags)!.GetValue(assembly)!;
                    double wheelTime = list.Items.Cast<VideoSegment>().Take(2).Sum(p => p.Duration.TotalSeconds) + .5;
                    for (int step = 0; step < 10; step++)
                        audioTimelineType.GetMethod("AdjustAudioAt")!.Invoke(audioTimeline, [new System.Windows.Point(wheelTime * 100, 220), -120]);
                    Require((int)assemblyType.GetField("_assemblyLoadRevision", flags)!.GetValue(assembly)! == loadBeforeVolume &&
                        (bool)assemblyType.GetField("_wantAssemblyPlaying", flags)!.GetValue(assembly)!, "Volume scrolling restarted or paused playback");
                    Require(Math.Abs(trackStates[1].Volume - .5) < .001 && AssemblyAudio.GainAt((VideoSegment)list.Items[2], 1, .5) == 1,
                        "Wheel over linked audio lane did not update track volume or stacked event gain");
                    long playingTime = assemblyPlayer.Time;
                    using var playingMedia = assemblyPlayer.Media!;
                    int pictures = playingMedia.Statistics.DisplayedPictures;
                    await Task.Delay(450);
                    Require(assemblyPlayer.Time > playingTime + 100 && playingMedia.Statistics.DisplayedPictures > pictures,
                        $"Video froze while adjusting audio: time={playingTime}->{assemblyPlayer.Time}, pictures={pictures}->{playingMedia.Statistics.DisplayedPictures}, state={assemblyPlayer.State}; " +
                        $"want={assemblyType.GetField("_wantAssemblyPlaying", flags)!.GetValue(assembly)}, wait={assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)}, opening={assemblyType.GetField("_assemblyOpening", flags)!.GetValue(assembly)}, scrub={assemblyType.GetField("_assemblyScrubbing", flags)!.GetValue(assembly)}, " +
                        $"gap={assemblyType.GetField("_gapPosition", flags)!.GetValue(assembly)}, tail={assemblyType.GetField("_tailPosition", flags)!.GetValue(assembly)}");
                    AudioCall("UndoAssembly_Click", null, new System.Windows.RoutedEventArgs());
                    Require(trackStates[1].Volume == 1, "A wheel gesture required multiple undo steps");
                    AudioCall("SeekAssembly", list.Items.Cast<VideoSegment>().Take(2).Sum(p => p.Duration.TotalSeconds) + 1.5);
                    for (int i = 0; i < 120 && ((bool)assemblyType.GetField("_assemblyOpening", flags)!.GetValue(assembly)! ||
                        (bool)assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)!); i++) await Task.Delay(25);
                    long beforeReverse = assemblyPlayer.Time;
                    AudioCall("ShuttleAssembly", System.Windows.Input.Key.J);
                    await Task.Delay(300);
                    Require(assemblyPlayer.Time < beforeReverse - 50, "J did not scrub the selected source backwards");
                    AudioCall("ShuttleAssembly", System.Windows.Input.Key.K);
                    Require(!(bool)assemblyType.GetField("_wantAssemblyPlaying", flags)!.GetValue(assembly)!, "K did not pause preview");
                    AudioCall("ShuttleAssembly", System.Windows.Input.Key.L);
                    AudioCall("ShuttleAssembly", System.Windows.Input.Key.L);
                    Require((double)assemblyType.GetField("_forwardPreviewRate", flags)!.GetValue(assembly)! == 2, "Repeated L did not increase playback speed");
                    AudioCall("ShuttleAssembly", System.Windows.Input.Key.K);
                    Require(assembly.FindName("LimitSize") is null && assembly.FindName("SizeLimit") is null,
                        "Size limit UI must not exist in VideoAssemblyWindow");
                    Console.WriteLine("PASS: separate audio rows, independent split/delete, undo/redo, preview mute/solo and volume wheel without reloading playback");
                    await IndependentAudioCheck.Run(assembly);
                }
                var timeline = (System.Windows.FrameworkElement)assembly.FindName("Timeline");
                var timelineType = timeline.GetType();
                assemblyType.GetField("_fitTimeline", flags)!.SetValue(assembly, false);
                timelineType.GetProperty("PixelsPerSecond")!.SetValue(timeline, 100.0);
                void DragTimeline(System.Windows.Point from, System.Windows.Point to)
                {
                    timelineType.GetMethod("BeginGesture", flags)!.Invoke(timeline, [from]);
                    timelineType.GetMethod("UpdateGesture", flags)!.Invoke(timeline, [to]);
                    timelineType.GetMethod("EndGesture", flags)!.Invoke(timeline, [to]);
                }
                var firstBeforeTrim = (VideoSegment)list.Items[0];
                DragTimeline(new(2, 60), new(52, 60));
                Require(((VideoSegment)list.Items[0]).Start > firstBeforeTrim.Start + TimeSpan.FromSeconds(0.45),
                    "Dragging the actual clip edge did not trim its source");
                assemblyType.GetMethod("UndoAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                Require(((VideoSegment)list.Items[0]) == firstBeforeTrim, "Undo did not restore a timeline edge drag");
                timelineType.GetMethod("BeginGesture", flags)!.Invoke(timeline, [new System.Windows.Point(2, 60)]);
                timelineType.GetMethod("UpdateGesture", flags)!.Invoke(timeline, [new System.Windows.Point(72, 60)]);
                timelineType.GetMethod("CancelGesture")!.Invoke(timeline, null);
                Require(((VideoSegment)list.Items[0]) == firstBeforeTrim &&
                    !(bool)assemblyType.GetField("_assemblyScrubbing", flags)!.GetValue(assembly)!,
                    "Canceling an edge drag changed export bounds or left playback stuck in scrubbing mode");
                double firstPixels = firstBeforeTrim.Duration.TotalSeconds * 100;
                double totalPixels = list.Items.Cast<VideoSegment>().Sum(s => s.Duration.TotalSeconds) * 100;
                DragTimeline(new(firstPixels / 2, 60), new(totalPixels + 10, 60));
                Require(((VideoSegment)list.Items[list.Items.Count - 1]).Path == firstBeforeTrim.Path,
                    "Dragging the actual clip body did not reorder the montage");
                assemblyType.GetMethod("UndoAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                double cursorSeconds = firstBeforeTrim.Duration.TotalSeconds + 0.25;
                DragTimeline(new(cursorSeconds * 100, 10), new(cursorSeconds * 100, 10));
                Require(list.SelectedIndex == 1 && Math.Abs((double)timelineType.GetProperty("Position")!.GetValue(timeline)! - cursorSeconds) < 0.01,
                    "Ruler click did not position the playhead in the second source");
                int beforeCursorSplit = list.Items.Count;
                assemblyType.GetMethod("SplitAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                Require(list.Items.Count == beforeCursorSplit + 1 && Math.Abs(((VideoSegment)list.Items[1]).End.TotalSeconds - 0.25) < 0.02,
                    "Split ignored the logical playhead while native preview was still seeking");
                assemblyType.GetMethod("UndoAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                assemblyType.GetMethod("InsertProjectVideo", flags)!.Invoke(assembly, [red, 0]);
                Require(((VideoSegment)list.Items[0]).Path == red, "Dropping a project file did not insert at the requested boundary");
                assemblyType.GetMethod("UndoAssembly_Click", flags)!.Invoke(assembly, [null, new System.Windows.RoutedEventArgs()]);
                double beforeZoom = (double)timelineType.GetProperty("PixelsPerSecond")!.GetValue(timeline)!;
                assemblyType.GetMethod("ZoomTimeline", flags)!.Invoke(assembly, [1.25, 100.0]);
                Require((double)timelineType.GetProperty("PixelsPerSecond")!.GetValue(timeline)! > beforeZoom,
                    "Timeline zoom did not increase time resolution");
                assemblyType.GetMethod("FitTimeline", flags)!.Invoke(assembly, null);
                Snapshot(assembly, "assembly.png", 1280, 900);
                Require(((System.Windows.Controls.ScrollViewer)assembly.FindName("TimelineScroll")).ViewportWidth > 1000,
                    "Montage timeline did not span the available editor width");
                Snapshot(assembly, "assembly-compact.png", 960, 740);
                Require(((System.Windows.Controls.TextBlock)assembly.FindName("TimelineHint")).ActualHeight >= 12,
                    "Keyboard hint was clipped at the minimum editor size");
                var saveButton = (System.Windows.FrameworkElement)assembly.FindName("Save");
                var rootContent = (System.Windows.FrameworkElement)assembly.Content;
                Require(saveButton.TranslatePoint(new(0, saveButton.ActualHeight), rootContent).Y <= rootContent.ActualHeight + 1,
                    "Export button was clipped at the minimum editor size");
                var originalTimelineParts = list.Items.Cast<VideoSegment>().ToArray();
                double oldScale = (double)timelineType.GetProperty("PixelsPerSecond")!.GetValue(timeline)!;
                var longTimeline = Enumerable.Range(0, 5000).Select(_ => originalTimelineParts[^1]).ToArray();
                timelineType.GetMethod("SetSegments")!.Invoke(timeline, [longTimeline]);
                timelineType.GetProperty("PixelsPerSecond")!.SetValue(timeline, .03);
                var renderClock = System.Diagnostics.Stopwatch.StartNew();
                Snapshot(assembly, "assembly-stress.png", 1280, 900);
                Require(renderClock.ElapsedMilliseconds < 250, "Dense 5000-clip timeline blocked layout/render");
                Console.WriteLine($"PASS: 5000-clip timeline layout/render {renderClock.ElapsedMilliseconds}ms");
                timelineType.GetMethod("SetSegments")!.Invoke(timeline, [originalTimelineParts]);
                timelineType.GetProperty("PixelsPerSecond")!.SetValue(timeline, oldScale);
                int handlesBeforeSeeks = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
                var seekClock = System.Diagnostics.Stopwatch.StartNew();
                for (int seek = 0; seek < 40; seek++)
                    assemblyType.GetMethod("SeekAssembly", flags)!.Invoke(assembly, [seek % 2 == 0 ? .5 : originalTimelineParts.Take(2).Sum(p => p.Duration.TotalSeconds) + .5]);
                for (int i = 0; i < 120 && ((bool)assemblyType.GetField("_assemblyOpening", flags)!.GetValue(assembly)! ||
                     (bool)assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)!); i++) await Task.Delay(25);
                Require(!(bool)assemblyType.GetField("_assemblyOpening", flags)!.GetValue(assembly)! &&
                    !(bool)assemblyType.GetField("_waitingAssemblyFrame", flags)!.GetValue(assembly)!, "Rapid seeking left preview stuck opening");
                await Task.Delay(250);
                int handleGrowth = System.Diagnostics.Process.GetCurrentProcess().HandleCount - handlesBeforeSeeks;
                Console.WriteLine($"   rapid seek handle growth: {handleGrowth} (LibVLC releases native handles lazily)");
                Require(handleGrowth < 400,
                    $"Rapid seeking leaked media player resources: handle growth {handleGrowth}, elapsed={seekClock.ElapsedMilliseconds}ms");
                Console.WriteLine($"PASS: 40 rapid cross-clip seeks settled in {seekClock.ElapsedMilliseconds}ms; native handle growth bounded");
                assembly.Close();
                var dropWindow = new System.Windows.Window();
                var dropped = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(Aura.Views.ClipEditorWindow).Assembly.GetType("Aura.Views.VideoFileDrop")!.GetMethod("Attach")!
                    .Invoke(null, [dropWindow, (Action<string[]>)(paths => dropped.SetResult(paths))]);
                DropMessages.Send(new System.Windows.Interop.WindowInteropHelper(dropWindow).EnsureHandle(), [red, blue]);
                Require((await dropped.Task.WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(new[] { red, blue }), "Native file-drop delivery failed");
                dropWindow.Close();
                Console.WriteLine("PASS: real WPF editor delete/undo/pick, assembly import/reorder, native WM_DROPFILES delivery, offscreen layout renders");
                uiFinished.SetResult();
            }
            catch (Exception ex) { uiFinished.SetException(ex); }
            finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
        });
        System.Windows.Threading.Dispatcher.Run();
    }
    catch (Exception ex) { uiFinished.TrySetException(ex); }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
await uiFinished.Task.WaitAsync(TimeSpan.FromSeconds(60));
Require(uiThread.Join(5000), "UI selfcheck did not stop");
}

void Snapshot(System.Windows.Window window, string name, int width, int height)
{
    window.Width = width; window.Height = height; window.UpdateLayout();
    var content = (System.Windows.FrameworkElement)window.Content;
    content.Measure(new System.Windows.Size(width, height));
    content.Arrange(new System.Windows.Rect(0, 0, width, height));
    content.UpdateLayout();
    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(content);
    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using var file = File.Create(Path.Combine(root, name));
    png.Save(file);
}

sealed class CancelOnProgress(CancellationTokenSource source) : IProgress<double>
{
    public void Report(double value) { if (value > 0) source.Cancel(); }
}

static class DropMessages
{
    public static void Send(IntPtr window, string[] paths)
    {
        byte[] names = System.Text.Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        byte[] data = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 16);
        names.CopyTo(data, 20);
        IntPtr memory = GlobalAlloc(0x42, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero) throw new Exception("Drop memory allocation failed");
        IntPtr buffer = GlobalLock(memory);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, buffer, data.Length);
        GlobalUnlock(memory);
        SendMessage(window, 0x233, memory, IntPtr.Zero);
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
