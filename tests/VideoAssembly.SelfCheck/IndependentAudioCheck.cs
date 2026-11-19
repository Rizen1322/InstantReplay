using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aura.Core.Tools;

internal static class IndependentAudioCheck
{
    public static async Task Run(Window window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = window.GetType();
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(window, args);
        var list = (ListBox)window.FindName("Segments");
        var timeline = (FrameworkElement)window.FindName("Timeline"); var control = timeline.GetType();
        var original = (VideoSegment)list.Items[2]; list.SelectedIndex = 2;
        await (Task)Call("SelectAssemblyClipAsync", new object?[] { null })!;
        await Ready();
        timeline.Focus();
        Keyboard.Focus(timeline);
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.U)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = timeline };
        Call("Montage_KeyDown", window, key);
        Require(key.Handled && ((VideoSegment)list.Items[2]).AudioDetached,
            $"U did not create independent audio events: handled={key.Handled}, linked={((VideoSegment)list.Items[2]).AudioLinked}, " +
            $"detached={((VideoSegment)list.Items[2]).AudioDetached}, count={list.Items.Count}, selected={list.SelectedIndex}");
        await Ready();
        type.GetField("_fitTimeline", flags)!.SetValue(window, false);
        control.GetProperty("PixelsPerSecond")!.SetValue(timeline, 100.0);
        double offset = list.Items.Cast<VideoSegment>().Take(2).Sum(p => p.Duration.TotalSeconds);
        control.GetProperty("SelectedAudioTrack")!.SetValue(timeline, -1);
        control.GetProperty("SelectedAudioEvent")!.SetValue(timeline, -1);
        Call("SplitAssemblyAt", .8);
        Call("SeekAssembly", offset + .8 + .3); await Ready();
        var placedPlayers = (System.Collections.IDictionary)type.GetField("_placedAudioPlayers", flags)!.GetValue(window)!;
        var playing = placedPlayers.Keys.Cast<LibVLCSharp.Shared.MediaPlayer>().First();
        int paused = 0;
        void Paused(object? sender, EventArgs e) => Interlocked.Increment(ref paused);
        var trackStates = (List<AssemblyAudioTrack>)type.GetField("_audioTracks", flags)!.GetValue(window)!;
        var savedStates = trackStates.ToArray();
        var mute = (CheckBox)window.FindName("MutePreview"); var savedMute = mute.IsChecked;
        using (var volume = new PreviewAudioCheck.TestSessionVolume())
        try
        {
            for (int i = 0; i < trackStates.Count; i++) trackStates[i] = new(.7, Solo: i == 0);
            Call("RefreshAudioHeaders"); mute.IsChecked = false;
            await Task.Delay(300);
            Call("PlayAssembly_Click", null, new RoutedEventArgs());
            await Task.Delay(250);
            playing.Paused += Paused;
            try { await PreviewAudioCheck.ReadLoopback(Path.GetDirectoryName(original.Path)!, "detached-video-cut", 800, .05); }
            finally { playing.Paused -= Paused; }
            volume.AssertUnchanged();
        }
        finally
        {
            Call("ShuttleAssembly", Key.K); mute.IsChecked = savedMute;
            trackStates.Clear(); trackStates.AddRange(savedStates); Call("RefreshAudioHeaders");
        }
        bool kept = placedPlayers.Contains(playing);
        int previewIndex = (int)type.GetField("_previewIndex", flags)!.GetValue(window)!;
        Require(kept && paused == 0 && previewIndex == 3,
            $"Detached audio was stopped/reopened across a video cut: kept={kept}, pauses={paused}, index={previewIndex}, opening={type.GetField("_assemblyOpening", flags)!.GetValue(window)}, " +
            $"items={list.Items.Count}, selected={list.SelectedIndex}, original={original.Start}-{original.End}, timeline={timeline.GetType().GetProperty("Position")!.GetValue(timeline)}, " +
            $"starts={string.Join(",", list.Items.Cast<VideoSegment>().Select(p => p.Start.TotalSeconds + "-" + p.End.TotalSeconds))}");
        Call("UndoAssembly_Click", null, new RoutedEventArgs()); await Ready();
        double left = (offset + .5) * 100;
        control.GetMethod("BeginGesture", flags)!.Invoke(timeline, [new Point(left, 220)]);
        control.GetMethod("EndGesture", flags)!.Invoke(timeline, [new Point(left + 100, 220)]);
        var moved = (VideoSegment)list.Items[2];
        Require(moved.Start == original.Start && moved.End == original.End, "Moving detached audio changed video trim");
        Require(Math.Abs(moved.AudioEvents.Single(e => e.Track == 1).Position!.Value.TotalSeconds - offset - 1) < .04, "Detached audio did not move by one second");
        Require(Math.Abs(moved.AudioEvents.Single(e => e.Track == 0).Position!.Value.TotalSeconds - offset) < .04, "Moving one audio lane moved the other");
        double audioEnd = moved.AudioEvents.Single(e => e.Track == 1).Position!.Value.TotalSeconds + moved.Duration.TotalSeconds;
        control.GetMethod("BeginGesture", flags)!.Invoke(timeline, [new Point(audioEnd * 100 - 3, 220)]);
        control.GetMethod("EndGesture", flags)!.Invoke(timeline, [new Point(audioEnd * 100 - 53, 220)]);
        Require(((VideoSegment)list.Items[2]).AudioEvents.Single(e => e.Track == 1).End < moved.End - TimeSpan.FromSeconds(.45) &&
            ((VideoSegment)list.Items[2]).End == original.End, "Trimming independent audio also trimmed video");
        Call("UndoAssembly_Click", null, new RoutedEventArgs()); await Ready();
        Call("MoveTimelineClip", 2, 0);
        Require(((VideoSegment)list.Items[0]).AudioEvents.SequenceEqual(moved.AudioEvents), "Moving video also moved unlinked audio");
        Call("UndoAssembly_Click", null, new RoutedEventArgs());
        await Ready();
        double selectedStart = ((VideoSegment)list.Items[2]).AudioEvents.Single(e => e.Track == 1).Position!.Value.TotalSeconds;
        control.GetMethod("BeginGesture", flags)!.Invoke(timeline, [new Point((selectedStart + .5) * 100, 220)]);
        control.GetMethod("EndGesture", flags)!.Invoke(timeline, [new Point(.8 * 100, 220)]);
        Call("SeekAssembly", .8); await Ready();
        Require(list.SelectedIndex == 2 && (int)type.GetField("_previewIndex", flags)!.GetValue(window)! == 0,
            "Seeking under a different video lost the independent audio selection");
        Call("SplitAssembly_Click", null, new RoutedEventArgs());
        Require(list.Items.Count == 3 && ((VideoSegment)list.Items[2]).AudioEvents.Count(e => e.Track == 1) == 2,
            "Split cut the video under the cursor instead of selected independent audio");
        Call("Remove_Click", null, new RoutedEventArgs());
        Require(list.Items.Count == 3 && ((VideoSegment)list.Items[2]).AudioEvents.Any(e => e.Track == 1 && e.Removed), "Delete removed the video under the cursor");
        Call("UndoAssembly_Click", null, new RoutedEventArgs());
        Call("UndoAssembly_Click", null, new RoutedEventArgs());
        Call("UndoAssembly_Click", null, new RoutedEventArgs()); await Ready();
        double end = list.Items.Cast<VideoSegment>().Sum(p => p.Duration.TotalSeconds);
        Call("SeekAssembly", end + .5);
        await Ready();
        Require(((FrameworkElement)window.FindName("AssemblyPreview")).Visibility == Visibility.Hidden, "Preview did not show black after video ended while detached audio continues");
        type.GetField("_wantAssemblyPlaying", flags)!.SetValue(window, true);
        type.GetField("_tailTicks", flags)!.SetValue(window, Environment.TickCount64);
        await Task.Delay(180);
        Require(((Slider)window.FindName("AssemblyPosition")).Value > end + .55, "Audio tail playhead stopped when video ended");
        Call("ShuttleAssembly", Key.K);
        Call("UndoAssembly_Click", null, new RoutedEventArgs());
        Call("UndoAssembly_Click", null, new RoutedEventArgs());
        await Ready();
        Require(!((VideoSegment)list.Items[2]).AudioDetached && ((VideoSegment)list.Items[2]).AudioEvents.Count == original.AudioEvents.Count, "Undo did not restore linked audio");
        var media = (ListBox)window.FindName("ProjectMedia");
        media.IsEnabled = false; media.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(media.ActualWidth), (int)Math.Ceiling(media.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(media); var pixel = new byte[4];
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(Path.GetDirectoryName(original.Path)!, "project-media-disabled.png"))) png.Save(file);
        bitmap.CopyPixels(new Int32Rect(Math.Max(0, bitmap.PixelWidth - 10), Math.Max(0, bitmap.PixelHeight - 4), 1, 1), pixel, 4, 0);
        Require(pixel.Take(3).All(c => c < 100), "Disabled project media returned to a white system background");
        media.IsEnabled = true;
        var probe = new Aura.Controls.AssemblyTimeline { PixelsPerSecond = 100, VisibleWidth = 600 };
        var single = new VideoSegment(original.Path, TimeSpan.Zero, TimeSpan.FromSeconds(1))
        {
            AudioLinked = false, AudioDetached = true,
            AudioEvents = [new AudioEvent(0, TimeSpan.Zero, TimeSpan.FromSeconds(1)) { SourcePath = original.Path, Position = TimeSpan.Zero }]
        };
        probe.SetSegments([single]);
        probe.SourceInfo = _ => (4, 30);
        TimeSpan? placedGap = null;
        probe.PlaceClip += (_, gap) => placedGap = gap;
        var probeType = probe.GetType();
        void ProbeGesture(string name, Point at) => probeType.GetMethod(name, flags)!.Invoke(probe, [at]);
        ProbeGesture("BeginGesture", new Point(12, 60));
        ProbeGesture("UpdateGesture", new Point(112, 60));
        ProbeGesture("EndGesture", new Point(112, 60));
        Require(placedGap == TimeSpan.FromSeconds(1), "Dragging independent video did not propose a one-second gap");
        Require(single.AudioEvents[0].Position == TimeSpan.Zero, "Dragging video moved detached audio");
        var shifted = single with { Gap = TimeSpan.FromSeconds(1) };
        probe.SetSegments([shifted]);
        placedGap = null;
        ProbeGesture("BeginGesture", new Point(50, 60));
        ProbeGesture("UpdateGesture", new Point(80, 60));
        ProbeGesture("EndGesture", new Point(80, 60));
        Require(placedGap is null, "Clicking the empty gap moved the video again");

        Call("SeekAssembly", 0.0); await Ready();
        var beforeGap = (VideoSegment)list.Items[0];
        Call("PlaceVideoClip", 0, TimeSpan.FromSeconds(1)); await Ready();
        var gapped = (VideoSegment)list.Items[0];
        Require(gapped.Gap == TimeSpan.FromSeconds(1) && gapped.Start == beforeGap.Start && gapped.End == beforeGap.End,
            "Video gap changed the source trim");
        Call("SeekAssembly", .5); await Ready();
        Require(((FrameworkElement)window.FindName("AssemblyPreview")).Visibility == Visibility.Hidden,
            "Preview did not hide while the playhead was inside the leading gap");
        Require(((Slider)window.FindName("AssemblyPosition")).Value is > .45 and < .6, "Playhead did not stay inside the leading gap");
        type.GetField("_wantAssemblyPlaying", flags)!.SetValue(window, false);
        Call("PlayAssembly_Click", null, new RoutedEventArgs());
        await Task.Delay(1400);
        Require(((Slider)window.FindName("AssemblyPosition")).Value >= .9, "Leading gap did not advance to the video");
        Call("ShuttleAssembly", Key.K);
        Call("SeekAssembly", 2.0); await Ready();
        Require(((FrameworkElement)window.FindName("AssemblyPreview")).Visibility == Visibility.Visible && (int)type.GetField("_previewIndex", flags)!.GetValue(window)! == 0,
            "Video did not reappear after the leading gap");
        Call("UndoAssembly_Click", null, new RoutedEventArgs()); await Ready();
        Require(((VideoSegment)list.Items[0]).Gap == TimeSpan.Zero, "Undo did not remove the video gap");

        var preview = (LibVLCSharp.Shared.MediaPlayer)type.GetField("_assemblyPlayer", flags)!.GetValue(window)!;
        var playhead = (Slider)window.FindName("AssemblyPosition");
        Call("ShuttleAssembly", Key.K);
        type.GetField("_wantAssemblyPlaying", flags)!.SetValue(window, false);
        Call("SeekAssembly", 1.0); await Ready();
        Call("SeekAssembly", 1.2); await Ready();
        Require(!preview.IsPlaying, "Paused preview started playing after a same-clip seek");
        var timeText = (TextBlock)window.FindName("PreviewTime");
        Require(timeText.Text.Contains('.'), "Preview timecode did not show milliseconds: " + timeText.Text);
        Require(timeline.ToolTip is null, "Timeline still shows the large hover tooltip");
        Require(timeline.ContextMenu?.Style is not null, "Timeline context menu has no dark style");
        double beforeStep = playhead.Value;
        var stepKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Left)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = timeline };
        Call("Montage_KeyDown", window, stepKey);
        await Task.Delay(150);
        Require(stepKey.Handled && !preview.IsPlaying && playhead.Value < beforeStep && beforeStep - playhead.Value < .1,
            $"Frame step did not move the paused preview: handled={stepKey.Handled}, playing={preview.IsPlaying}, before={beforeStep}, after={playhead.Value}");
        Call("SeekAssembly", 1.0); await Ready();
        list.SelectedIndex = 1;
        for (int i = 0; i < 100 && (int)type.GetField("_previewIndex", flags)!.GetValue(window)! != 1; i++) await Task.Delay(20);
        await Task.Delay(250);
        Require(!preview.IsPlaying, "Clicking another clip started playback while preview was paused");

        var singleWindow = (Window)Activator.CreateInstance(type, nonPublic: true)!;
        try
        {
            await (Task)type.GetMethod("AddFilesAsync", flags)!.Invoke(singleWindow, [new[] { original.Path }])!;
            var singleList = (ListBox)singleWindow.FindName("Segments");
            singleList.SelectedIndex = 0;
            var singleTimeline = (FrameworkElement)singleWindow.FindName("Timeline");
            type.GetMethod("SetAudioLink", flags)!.Invoke(singleWindow, [false]);
            Require(((VideoSegment)singleList.Items[0]).AudioDetached, "Single-clip U did not detach audio");
            type.GetField("_fitTimeline", flags)!.SetValue(singleWindow, false);
            singleTimeline.GetType().GetProperty("PixelsPerSecond")!.SetValue(singleTimeline, 100.0);
            TimeSpan? singleGap = null;
            ((Aura.Controls.AssemblyTimeline)singleTimeline).PlaceClip += (_, gap) => singleGap = gap;
            void SingleGesture(string name, Point at) => singleTimeline.GetType().GetMethod(name, flags)!.Invoke(singleTimeline, [at]);
            SingleGesture("BeginGesture", new Point(12, 60));
            SingleGesture("UpdateGesture", new Point(112, 60));
            SingleGesture("EndGesture", new Point(112, 60));
            var singleShifted = (VideoSegment)singleList.Items[0];
            Require(Math.Abs(singleShifted.Gap.TotalSeconds - 1) < .02,
                $"Single-clip video drag did not create a gap: gap={singleShifted.Gap}, event={singleGap}, " +
                $"linked={singleShifted.AudioLinked}, detached={singleShifted.AudioDetached}, count={singleList.Items.Count}");
            Require(singleShifted.AudioEvents.All(e => e.Position == TimeSpan.Zero), "Single-clip video drag moved detached audio");
        }
        finally { singleWindow.Close(); }
        await RunSplitAndMidGapCheck(original.Path);
        Console.WriteLine("PASS: independent U key, split clip drag, magnetic snap, mid-timeline gap preview, audio tail preview and undo");

        async Task Ready()
        {
            for (int i = 0; i < 200 && ((bool)type.GetField("_assemblyOpening", flags)!.GetValue(window)! ||
                (bool)type.GetField("_waitingAssemblyFrame", flags)!.GetValue(window)!); i++) await Task.Delay(20);
        }
    }

    public static async Task RunSplitAndMidGapCheck(string clipPath)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(Aura.Views.VideoAssemblyWindow);
        var window = (Window)Activator.CreateInstance(type, nonPublic: true)!;
        try
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000; window.Top = -30000; window.ShowActivated = false; window.ShowInTaskbar = false;
            ((CheckBox)window.FindName("MutePreview")).IsChecked = true;
            window.Show();
            for (int i = 0; i < 150 && type.GetField("_assemblyPlayer", flags)!.GetValue(window) is null; i++) await Task.Delay(50);
            await (Task)type.GetMethod("AddFilesAsync", flags)!.Invoke(window, [new[] { clipPath }])!;
            var list = (ListBox)window.FindName("Segments");
            var timeline = (FrameworkElement)window.FindName("Timeline");
            var tlType = timeline.GetType();
            async Task WaitReady()
            {
                for (int i = 0; i < 200 && ((bool)type.GetField("_assemblyOpening", flags)!.GetValue(window)! ||
                    (bool)type.GetField("_waitingAssemblyFrame", flags)!.GetValue(window)!); i++) await Task.Delay(20);
            }
            void Gesture(string name, Point at) => tlType.GetMethod(name, flags)!.Invoke(timeline, [at]);
            list.SelectedIndex = 0;
            await (Task)type.GetMethod("SelectAssemblyClipAsync", flags)!.Invoke(window, [null])!;
            await WaitReady();
            type.GetField("_fitTimeline", flags)!.SetValue(window, false);
            tlType.GetProperty("PixelsPerSecond")!.SetValue(timeline, 100.0);
            type.GetMethod("SeekAssembly", flags)!.Invoke(window, [1.0]);
            await WaitReady();
            type.GetMethod("SplitAssemblyAt", flags)!.Invoke(window, [1.0]);
            await WaitReady();
            Require(list.Items.Count == 2, "Split did not produce two clips");
            double clip0End = VideoTimeline.Offset(list.Items.Cast<VideoSegment>().ToArray(), 1);
            Gesture("BeginGesture", new Point((clip0End + .5) * 100, 60));
            Gesture("UpdateGesture", new Point((clip0End + 1.5) * 100, 60));
            Gesture("EndGesture", new Point((clip0End + 1.5) * 100, 60));
            await WaitReady();
            double gapSeconds = ((VideoSegment)list.Items[1]).Gap.TotalSeconds;
            double clip1Start = VideoTimeline.Offset(list.Items.Cast<VideoSegment>().ToArray(), 1);
            Require(list.Items.Count == 2 && Math.Abs(gapSeconds - 1.0) < .05,
                $"Split second clip did not slide right freely: gap={gapSeconds}");
            double midGapTime = (clip0End + clip1Start) * 0.5;
            type.GetMethod("SeekAssembly", flags)!.Invoke(window, [midGapTime]);
            await WaitReady();
            var preview = (FrameworkElement)window.FindName("AssemblyPreview");
            var playhead = (Slider)window.FindName("AssemblyPosition");
            Require(preview.Visibility == Visibility.Hidden, "Preview did not hide inside mid-timeline gap");
            Require(Math.Abs(playhead.Value - midGapTime) < .1, $"Playhead did not stay inside mid-timeline gap: {playhead.Value}");
            type.GetMethod("PlayAssembly_Click", flags)!.Invoke(window, [null, new RoutedEventArgs()]);
            for (int i = 0; i < 40 && (playhead.Value < clip1Start || preview.Visibility != Visibility.Visible); i++) await Task.Delay(50);
            await WaitReady();
            Require(playhead.Value >= clip1Start - .02 && preview.Visibility == Visibility.Visible,
                $"Mid-timeline gap did not advance back into clip 1: pos={playhead.Value}, clip1Start={clip1Start}, vis={preview.Visibility}");
            type.GetMethod("ShuttleAssembly", flags)!.Invoke(window, [Key.K]);
            Gesture("BeginGesture", new Point((clip1Start + .5) * 100, 60));
            Gesture("UpdateGesture", new Point((clip0End + .54) * 100, 60));
            Gesture("EndGesture", new Point((clip0End + .54) * 100, 60));
            await WaitReady();
            Require(((VideoSegment)list.Items[1]).Gap == TimeSpan.Zero,
                $"Dragging clip 1 near clip 0 end did not magnetically snap gap to zero: gap={((VideoSegment)list.Items[1]).Gap}");
            type.GetMethod("UndoAssembly_Click", flags)!.Invoke(window, [null, new RoutedEventArgs()]);
            await WaitReady();
            Require(Math.Abs(((VideoSegment)list.Items[1]).Gap.TotalSeconds - 1.0) < .05, "Undo did not restore snapped gap");
            Console.WriteLine("PASS: real WPF split video drag, magnetic snap, mid-timeline gap playback and undo");
        }
        finally { window.Close(); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
