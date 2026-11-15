using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Aura.Core.Library;
using Aura.Core.Tools;

namespace Aura.Views;

public partial class VideoAssemblyWindow
{
    private readonly ObservableCollection<ClipItem> _projectMedia = [];
    private Point _mediaPress;
    private ClipItem? _dragMedia;
    private bool _fitTimeline = true;
    private long _lastTimelineScrub;
    private const string ProjectVideoFormat = "Aura.ProjectVideo";

    private void InitializeTimeline()
    {
        ProjectMedia.ItemsSource = _projectMedia;
        Timeline.Thumbnail = path => _thumbs.TryGetValue(path, out var item) ? item.Thumbnail : null;
        Timeline.SourceInfo = path => _sourceInfo.TryGetValue(path, out var info) ? (info.Duration.TotalSeconds, info.Fps) : (0, 30);
        Timeline.AudioTrackCount = SourceAudioTracks;
        Timeline.TrackState = AudioTrackState;
        Timeline.Waveform = (path, track) => _waveforms.TryGetValue(path, out var waves) && track < waves.Count ? waves[track] : null;
        Timeline.SelectAudio += (index, track, source) =>
        {
            if (track >= 0) _activeAudioTrack = track;
            _changingAssembly = true; Segments.SelectedIndex = index; _changingAssembly = false;
            RefreshAudioHeaders();
        };
        Timeline.AdjustAudio += AdjustAudioEvent;
        Timeline.EditAudio += EditDetachedAudio;
        SnapEdits.Checked += (_, _) => Timeline.SnapToFrames = true;
        SnapEdits.Unchecked += (_, _) => Timeline.SnapToFrames = false;
        Timeline.SelectClip += index =>
        {
            if (_export is not null) return;
            _wantAssemblyPlaying = false; _assemblyPlayer?.SetPause(true);
            SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? 0);
            Segments.SelectedIndex = index;
        };
        Timeline.Seek += seconds =>
        {
            _assemblyScrubbing = false;
            _changingAssembly = true; AssemblyPosition.Value = Math.Clamp(seconds, 0, AssemblyPosition.Maximum); _changingAssembly = false;
            SeekAssembly(seconds);
        };
        Timeline.Scrub += seconds =>
        {
            if (!_assemblyScrubbing) _assemblyPlayer?.SetPause(true);
            _assemblyScrubbing = true;
            SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? 0);
            PreviewTime.Text = $"{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(AssemblyPosition.Maximum):hh\\:mm\\:ss}";
            FollowTimelinePosition(seconds);
            if (seconds < LeadingGapEnd) { AssemblyPreview.Visibility = Visibility.Hidden; return; }
            AssemblyPreview.Visibility = Visibility.Visible;
            var (index, source) = VideoTimeline.Locate(_segments, seconds);
            long now = Environment.TickCount64;
            if (index == _previewIndex && !_assemblyOpening && now - _lastTimelineScrub >= 100 && _assemblyPlayer is not null)
            { _lastTimelineScrub = now; _assemblyPlayer.Time = (long)(source * 1000); _assemblyPlayer.NextFrame(); }
        };
        Timeline.InteractionEnded += committed =>
        {
            _assemblyScrubbing = false; Timeline.Position = AssemblyPosition.Value;
            if (!committed) SeekAssembly(AssemblyPosition.Value);
            else if (_gapPosition is null) _assemblyPlayer?.SetPause(!_wantAssemblyPlaying);
        };
        Timeline.Reorder += MoveTimelineClip;
        Timeline.PlaceClip += PlaceVideoClip;
        Timeline.TrimClip += ApplyTimelineTrim;
        Timeline.Zoom += ZoomTimeline;
        Loaded += (_, _) => FitTimeline();
    }

    private ClipItem ProjectItem(string path)
    {
        if (_thumbs.TryGetValue(path, out var existing)) return existing;
        var item = new ClipItem(path, Path.GetDirectoryName(path)!);
        if (_sourceInfo.TryGetValue(path, out var info)) item.Duration = info.Duration;
        _thumbs[path] = item;
        _projectMedia.Add(item);
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ClipItem.Thumbnail)) Timeline.InvalidateVisual(); };
        _ = ClipThumbnails.LoadAsync(item);
        if (SourceAudioTracks(path) > 0) RequestWaveform(path);
        return item;
    }

    private void RefreshTimeline()
    {
        foreach (var part in _segments) ProjectItem(part.Path);
        Timeline.SetSegments(_segments);
        Timeline.SelectedIndex = Segments.SelectedIndex;
        Timeline.EditingEnabled = _export is null;
        RefreshAudioHeaders();
        if (_fitTimeline && IsLoaded) FitTimeline();
    }
    private void FitTimeline()
    {
        _fitTimeline = true;
        double length = ProjectDuration;
        double width = Math.Max(100, TimelineScroll.ViewportWidth);
        Timeline.VisibleWidth = width;
        Timeline.PixelsPerSecond = length > 0 ? (width - 40) / length : 40;
        TimelineScroll.ScrollToHorizontalOffset(0);
        ZoomText.Text = "Весь";
    }
    private void FitTimeline_Click(object sender, RoutedEventArgs e) => FitTimeline();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomTimeline(1.25, Timeline.Position * Timeline.PixelsPerSecond);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomTimeline(.8, Timeline.Position * Timeline.PixelsPerSecond);
    private void ZoomTimeline(double factor, double anchorPixel)
    {
        _fitTimeline = false;
        double time = anchorPixel / Timeline.PixelsPerSecond;
        double relative = anchorPixel - TimelineScroll.HorizontalOffset;
        Timeline.PixelsPerSecond *= factor;
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, time * Timeline.PixelsPerSecond - relative));
        double fit = (Math.Max(100, TimelineScroll.ViewportWidth) - 40) / Math.Max(.05, _segments.Sum(s => s.Gap.TotalSeconds + s.Duration.TotalSeconds));
        ZoomText.Text = $"{Timeline.PixelsPerSecond / fit * 100:0}%";
    }
    private void TimelineScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        Timeline.VisibleLeft = e.HorizontalOffset;
        Timeline.VisibleWidth = Math.Max(100, e.ViewportWidth);
        Timeline.InvalidateVisual();
        if (_fitTimeline && Math.Abs(e.ViewportWidthChange) > .5) FitTimeline();
    }
    private void TimelineScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ZoomTimeline(e.Delta > 0 ? 1.25 : .8, e.GetPosition(Timeline).X);
        else if (Keyboard.Modifiers == ModifierKeys.None && Timeline.AdjustAudioAt(e.GetPosition(Timeline), e.Delta)) { }
        else TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
    private void FollowTimelinePosition(double seconds)
    {
        if (_fitTimeline) return;
        double x = seconds * Timeline.PixelsPerSecond;
        double left = TimelineScroll.HorizontalOffset, width = TimelineScroll.ViewportWidth;
        if (x < left + 10) TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 30));
        else if (x > left + width - 15) TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - width + 35));
    }

    private void ApplyTimelineTrim(int index, VideoSegment part)
    {
        if (_export is not null || index < 0 || index >= _segments.Count) return;
        part.Validate();
        _fitTimeline = false;
        bool leftEdge = part.Start != _segments[index].Start;
        RememberAssembly();
        _wantAssemblyPlaying = false;
        _changingAssembly = true; _segments[index] = part; Segments.SelectedIndex = index; _changingAssembly = false;
        _ = SelectAssemblyClipAsync(leftEdge ? part.Start.TotalSeconds : Math.Max(part.Start.TotalSeconds, part.End.TotalSeconds - 0.1));
        RefreshTimeline();
    }
    private void MoveTimelineClip(int index, int target)
    {
        if (_export is not null || index == target || index < 0 || target < 0 || index >= _segments.Count || target >= _segments.Count) return;
        _fitTimeline = false;
        RememberAssembly();
        double before = VideoTimeline.Offset(_segments, index);
        var moving = _segments[index];
        _changingAssembly = true; _segments.Move(index, target); Segments.SelectedIndex = target; _changingAssembly = false;
        if (moving.AudioLinked && moving.AudioDetached)
        {
            double delta = VideoTimeline.Offset(_segments, target) - before;
            _changingAssembly = true;
            _segments[target] = moving with { AudioEvents = moving.AudioEvents.Select(a => a.Position is TimeSpan at && (a.SourcePath is null || a.SourcePath == moving.Path) ? a with { Position = TimeSpan.FromSeconds(Math.Max(0, at.TotalSeconds + delta)) } : a).ToArray() };
            Segments.SelectedIndex = target; _changingAssembly = false;
        }
        _ = SelectAssemblyClipAsync();
        RefreshTimeline();
    }
    private void PlaceVideoClip(int index, TimeSpan gap)
    {
        if (_export is not null || index < 0 || index >= _segments.Count) return;
        var part = _segments[index];
        if (part.Gap == gap) return;
        var placed = part with { Gap = gap };
        placed.Validate();
        RememberAssembly();
        _fitTimeline = false;
        _wantAssemblyPlaying = false; _assemblyPlayer?.SetPause(true);
        _changingAssembly = true; _segments[index] = placed; Segments.SelectedIndex = index; _changingAssembly = false;
        RefreshTimeline();
        SeekAssembly(Timeline.Position);
    }
    private void Montage_KeyDown(object sender, KeyEventArgs e)
    {
        if (_export is not null || IsAssemblyTextInput(e.OriginalSource as DependencyObject) ||
            IsAssemblyTextInput(Keyboard.FocusedElement as DependencyObject)) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key is Key.Space or Key.Enter && modifiers is ModifierKeys.None or ModifierKeys.Control)
        { if (!e.IsRepeat) PlayAssembly_Click(this, new RoutedEventArgs()); }
        else if (key == Key.Escape)
        {
            Timeline.CancelGesture(); _reversePreviewRate = 0; _wantAssemblyPlaying = false;
            _assemblyPlayer?.SetPause(true); SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? 0);
        }
        else if (key == Key.S && modifiers == ModifierKeys.None) SplitAssembly_Click(this, new RoutedEventArgs());
        else if (key == Key.S && modifiers == ModifierKeys.Control) Save_Click(this, new RoutedEventArgs());
        else if (key == Key.O && modifiers == ModifierKeys.Control) Add_Click(this, new RoutedEventArgs());
        else if (key == Key.Delete && modifiers == ModifierKeys.None) Remove_Click(this, new RoutedEventArgs());
        else if (key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) || key == Key.Y && modifiers == ModifierKeys.Control)
            RedoAssembly_Click(this, new RoutedEventArgs());
        else if (key == Key.Z && modifiers == ModifierKeys.Control || key == Key.Back && modifiers == ModifierKeys.Alt)
            UndoAssembly_Click(this, new RoutedEventArgs());
        else if (key == Key.Z && modifiers == ModifierKeys.None) { if (!e.IsRepeat) ToggleAudioTrackMute(_activeAudioTrack); }
        else if (key == Key.X && modifiers == ModifierKeys.None) { if (!e.IsRepeat) ToggleAudioTrackSolo(_activeAudioTrack); }
        else if (key == Key.U && modifiers == ModifierKeys.None) SetAudioLink(false);
        else if (key == Key.G && modifiers == ModifierKeys.None) SetAudioLink(true);
        else if (key == Key.F8 && modifiers == ModifierKeys.None) SnapEdits.IsChecked = SnapEdits.IsChecked != true;
        else if (key == Key.Home && modifiers is ModifierKeys.None or ModifierKeys.Control) SeekAssembly(0);
        else if (key == Key.End && modifiers is ModifierKeys.None or ModifierKeys.Control) SeekAssembly(AssemblyPosition.Maximum);
        else if (key is Key.OemPlus or Key.Add && modifiers is ModifierKeys.None or ModifierKeys.Shift) ZoomIn_Click(this, new RoutedEventArgs());
        else if (key is Key.OemMinus or Key.Subtract && modifiers == ModifierKeys.None) ZoomOut_Click(this, new RoutedEventArgs());
        else if (key is Key.J or Key.K or Key.L && modifiers == ModifierKeys.None)
        { if (!e.IsRepeat) ShuttleAssembly(key); }
        else if (key is Key.Left or Key.Right && modifiers is ModifierKeys.None or ModifierKeys.Alt or ModifierKeys.Shift)
        {
            double fps = Segments.SelectedItem is VideoSegment part && _sourceInfo.TryGetValue(part.Path, out var info) ? info.Fps : 30;
            double step = modifiers == ModifierKeys.Shift ? 1 : 1 / Math.Max(1, fps);
            _wantAssemblyPlaying = false;
            _assemblyPlayer?.SetPause(true);
            PlayAssembly.Content = "▶ Смотреть ролик";
            SeekAssembly(Math.Clamp(Timeline.Position + (key == Key.Left ? -step : step), 0, AssemblyPosition.Maximum));
        }
        else return;
        e.Handled = true;
    }

    private static bool IsAssemblyTextInput(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is System.Windows.Media.Visual
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or ComboBoxItem or PasswordBox) return true;
        return false;
    }

    private void ShuttleAssembly(Key key)
    {
        if (_assemblyPlayer is null || _segments.Count == 0) return;
        FreezeTail();
        FreezeGap();
        if (key == Key.J)
        {
            _reversePreviewRate = _reversePreviewRate > 0 ? Math.Min(8, _reversePreviewRate * 2) : 1;
            _wantAssemblyPlaying = false; _assemblyPlayer.SetPause(true);
        }
        else if (key == Key.K)
        {
            _reversePreviewRate = 0; _wantAssemblyPlaying = false; _assemblyPlayer.SetPause(true);
        }
        else
        {
            if (_reversePreviewRate > 0) _audioRestartPositions.Clear();
            _forwardPreviewRate = _wantAssemblyPlaying && _reversePreviewRate == 0 ? Math.Min(8, _forwardPreviewRate * 2) : 1;
            _reversePreviewRate = 0; _wantAssemblyPlaying = true;
            if (_gapPosition is null && AssemblyPosition.Value < LeadingGapEnd - .001) SeekAssembly(AssemblyPosition.Value);
            _assemblyPlayer.SetRate((float)_forwardPreviewRate);
            foreach (var audio in _audioPlayers) audio.Player.SetRate((float)_forwardPreviewRate);
            if (_gapPosition is not null) _gapTicks = Environment.TickCount64;
            else if (_tailPosition is null && _assemblyPlayer.State == LibVLCSharp.Shared.VLCState.Ended) SeekAssembly(0);
            else if (_tailPosition is null) _assemblyPlayer.SetPause(false);
        }
        SyncPreviewAudio(_assemblyPlayer.Time / 1000.0);
    }

    private void ProjectMedia_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _mediaPress = e.GetPosition(ProjectMedia);
        _dragMedia = e.OriginalSource is DependencyObject source
            ? (ItemsControl.ContainerFromElement(ProjectMedia, source) as ListBoxItem)?.DataContext as ClipItem : null;
    }
    private void ProjectMedia_MouseMove(object sender, MouseEventArgs e)
    {
        if (_export is not null || e.LeftButton != MouseButtonState.Pressed || _dragMedia is not ClipItem item) return;
        Point at = e.GetPosition(ProjectMedia);
        if (Math.Abs(at.X - _mediaPress.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y - _mediaPress.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(ProjectMedia, new DataObject(ProjectVideoFormat, item.FullPath), DragDropEffects.Copy);
        _dragMedia = null;
    }
    private void ProjectMedia_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ProjectMedia, source) is ListBoxItem { DataContext: ClipItem item })
            InsertProjectVideo(item.FullPath, _segments.Count);
    }
    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _export is null && e.Data.GetDataPresent(ProjectVideoFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Timeline_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ProjectVideoFormat) is string path)
            InsertProjectVideo(path, VideoTimeline.Insertion(_segments, e.GetPosition(Timeline).X / Timeline.PixelsPerSecond));
        e.Handled = true;
    }
    private async void InsertProjectVideo(string path, int insertion)
    {
        if (_export is not null) return;
        try
        {
            if (!_sourceInfo.TryGetValue(path, out var info)) _sourceInfo[path] = info = await VideoAssembly.ReadInfoAsync(path, _lifetime.Token);
            if (_export is not null || _lifetime.IsCancellationRequested) return;
            RememberAssembly();
            insertion = Math.Clamp(insertion, 0, _segments.Count);
            _segments.Insert(insertion, new VideoSegment(path, TimeSpan.Zero, info.Duration));
            Segments.SelectedIndex = insertion;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
