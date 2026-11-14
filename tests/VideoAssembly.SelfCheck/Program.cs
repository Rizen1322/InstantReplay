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
string root = Path.GetFullPath(Path.Combine("artifacts", "tests", "assembly-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
MediaFactory.MFStartup();
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
    profile.Video.FrameRate.Numerator = 30; profile.Video.FrameRate.Denominator = 1;
    Require(await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile) == TranscodeFailureReason.None, "Fixture render failed");
    return output.Path;
}
string red = await Fixture("красный ' [1].mp4", Color.FromArgb(255, 255, 0, 0), 320, 180, 3, 440);
string blue = await Fixture("синий ; [2].mp4", Color.FromArgb(255, 0, 0, 255), 180, 320, 2, 880);
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
    Require((await VideoAssembly.ReadInfoAsync(silent)).AudioTracks == 0, "Silent track probe failed");
    string twoTrackMp4 = Path.Combine(root, "две дорожки.mp4");
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
    Require(!File.Exists(Path.Combine(root, "cancel-ffmpeg.mp4")) && !Directory.GetFiles(root, "*.part.mp4").Any(), "FFmpeg cancellation leaked partial output");
    Console.WriteLine("PASS: FFmpeg size limit, foreign MKV, multiple audio tracks, together, explicit track, silence, running cancellation");
}
Console.WriteLine("Fixtures: " + root);
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
                using var keySource = new System.Windows.Interop.HwndSource(
                    new System.Windows.Interop.HwndSourceParameters("Aura key selfcheck")
                        { Width = 1, Height = 1, WindowStyle = 0 });
                foreach (var key in new[] { System.Windows.Input.Key.Delete, System.Windows.Input.Key.Left,
                    System.Windows.Input.Key.Right, System.Windows.Input.Key.Home, System.Windows.Input.Key.End,
                    System.Windows.Input.Key.Z, System.Windows.Input.Key.Space })
                {
                    var input = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        keySource, 0, key)
                        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                            Source = editor.FindName("ExportSizeLimit") };
                    Call("Window_KeyDown", editor, input);
                    Require(!input.Handled, "Editor intercepted a key inside the size textbox: " + key);
                }
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
                player.Time = 1000;
                for (int i = 0; i < 30 && (player.Time < 900 || player.Time >= 1500); i++) await Task.Delay(10);
                Require((bool)Call("SkipRemovedSection")!, "Preview did not recognize a removed section");
                for (int i = 0; i < 50 && player.Time < 1490; i++) await Task.Delay(20);
                Require(player.Time >= 1490, "Preview did not seek across the removed section");
                player.SetPause(true);
                Snapshot(editor, "editor-cuts.png", 1180, 760);
                Set("_selectForAssembly", true);
                Call("Export_Click", null, new System.Windows.RoutedEventArgs());
                Console.WriteLine("UI: assembly handlers");
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
                Snapshot(assembly, "assembly.png", 850, 560);
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
await uiFinished.Task.WaitAsync(TimeSpan.FromSeconds(30));
Require(uiThread.Join(5000), "UI selfcheck did not stop");
MediaFactory.MFShutdown();

void Snapshot(System.Windows.Window window, string name, int width, int height)
{
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
