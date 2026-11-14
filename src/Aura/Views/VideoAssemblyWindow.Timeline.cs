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
        Timeline.SelectClip += index =>
        {
            if (_export is not null) return;
            _wantAssemblyPlaying = false; _assemblyPlayer?.SetPause(true);
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
            PreviewTime.Text = $"{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(AssemblyPosition.Maximum):hh\\:mm\\:ss}";
            FollowTimelinePosition(seconds);
            var (index, source) = VideoTimeline.Locate(_segments, seconds);
            long now = Environment.TickCount64;
            if (index == _previewIndex && !_assemblyOpening && now - _lastTimelineScrub >= 100 && _assemblyPlayer is not null)
            { _lastTimelineScrub = now; _assemblyPlayer.Time = (long)(source * 1000); }
        };
        Timeline.InteractionEnded += committed =>
        {
            _assemblyScrubbing = false; Timeline.Position = AssemblyPosition.Value;
            if (!committed) SeekAssembly(AssemblyPosition.Value);
            else _assemblyPlayer?.SetPause(!_wantAssemblyPlaying);
        };
        Timeline.Reorder += MoveTimelineClip;
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
        return item;
    }

    private void RefreshTimeline()
    {
        foreach (var part in _segments) ProjectItem(part.Path);
        Timeline.SetSegments(_segments);
        Timeline.SelectedIndex = Segments.SelectedIndex;
        Timeline.EditingEnabled = _export is null;
        if (_fitTimeline && IsLoaded) FitTimeline();
    }
    private void FitTimeline()
    {
        _fitTimeline = true;
        double length = _segments.Sum(s => s.Duration.TotalSeconds);
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
        double fit = (Math.Max(100, TimelineScroll.ViewportWidth) - 40) / Math.Max(.05, _segments.Sum(s => s.Duration.TotalSeconds));
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
        _changingAssembly = true; _segments.Move(index, target); Segments.SelectedIndex = target; _changingAssembly = false;
        _ = SelectAssemblyClipAsync();
        RefreshTimeline();
    }
    private void Montage_KeyDown(object sender, KeyEventArgs e)
    {
        if (_export is not null || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or ComboBoxItem || Keyboard.FocusedElement is ComboBox) return;
        if (e.Key == Key.Space) { if (!e.IsRepeat) PlayAssembly_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Key.Escape) Timeline.CancelGesture();
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None) SplitAssembly_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) Save_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Delete) Remove_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) UndoAssembly_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Home) SeekAssembly(0);
        else if (e.Key == Key.End) SeekAssembly(AssemblyPosition.Maximum);
        else if (e.Key is Key.Left or Key.Right)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / 30;
            SeekAssembly(Math.Clamp(Timeline.Position + (e.Key == Key.Left ? -step : step), 0, AssemblyPosition.Maximum));
        }
        else return;
        e.Handled = true;
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
