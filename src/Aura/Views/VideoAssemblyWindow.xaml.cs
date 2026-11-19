using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Aura.Core.Logging;
using Aura.Core.Tools;
using Microsoft.Win32;

namespace Aura.Views;

public partial class VideoAssemblyWindow : Window
{
    private readonly ObservableCollection<VideoSegment> _segments = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _export;
    private bool _adding;
    private string? _ffmpeg;

    private VideoAssemblyWindow()
    {
        InitializeComponent();
        Segments.ItemsSource = _segments;
        _segments.CollectionChanged += (_, _) => UpdateSummary();
        _ffmpeg = Ffmpeg.Find(Services.Settings.Current.FfmpegPath, Services.Settings.Current.LosslessCutPath);
        VideoFileDrop.Attach(this, paths => _ = AddFilesAsync(paths));
        InitializeAssemblyPreview();
        InitializeTimeline();
        Closed += (_, _) => { _lifetime.Cancel(); _export?.Cancel(); CloseAssemblyPreview(); };
        UpdateSummary();
    }

    public static void ShowFiles(IEnumerable<string> paths) => ShowSegments([], paths);

    public static void ShowSegments(IEnumerable<VideoSegment> segments, IEnumerable<string>? paths = null)
    {
        var window = new VideoAssemblyWindow();
        foreach (var segment in segments) window._segments.Add(segment);
        if (window._segments.Count > 0) window.Segments.SelectedIndex = 0;
        var owner = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive)
                    ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        if (owner is not null && owner.IsLoaded) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (paths is not null) window.Loaded += async (_, _) => await window.AddFilesAsync(paths.ToArray());
        window.ShowDialog();
    }

    private async Task AddFilesAsync(string[] paths)
    {
        if (_export is not null || _adding || _lifetime.IsCancellationRequested) return;
        if (paths.Length == 0) return;
        if (paths.Any(p => !File.Exists(p) || !VideoFiles.IsVideo(p)))
        { Dialogs.Say("Выберите видео", "Добавляйте видеофайлы, а не папки или изображения."); return; }
        _adding = true;
        Save.IsEnabled = false;
        try
        {
            var added = new List<VideoSegment>();
            foreach (string path in paths)
            {
                Status.Text = "Открываю " + Path.GetFileName(path) + "…";
                var info = await VideoAssembly.ReadInfoAsync(path, _lifetime.Token);
                _sourceInfo[Path.GetFullPath(path)] = info;
                added.Add(new VideoSegment(Path.GetFullPath(path), TimeSpan.Zero, info.Duration));
            }
            if (added.Count > 0) RememberAssembly();
            foreach (var segment in added) _segments.Add(segment);
            if (Segments.SelectedIndex < 0) Segments.SelectedIndex = 0;
            Status.Text = "Исходные файлы сохраняются.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        finally { _adding = false; UpdateSummary(); }
    }

    private void UpdateSummary()
    {
        Summary.Text = $"Фрагментов: {_segments.Count} · Длительность: {TimeSpan.FromSeconds(ProjectDuration):hh\\:mm\\:ss\\.fff}";
        Save.IsEnabled = _segments.Count > 0 && !_adding && _export is null;
        AddVideos.IsEnabled = !_adding && _export is null;
        EmptyPreview.Visibility = _segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AssemblyPosition is not null)
        {
            bool changing = _changingAssembly;
            _changingAssembly = true;
            AssemblyPosition.Maximum = Math.Max(0.05, ProjectDuration);
            _changingAssembly = changing;
        }
        RefreshTimeline();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = VideoFiles.Filter, Multiselect = true, Title = "Добавить видео в ролик" };
        if (picker.ShowDialog(this) == true) await AddFilesAsync(picker.FileNames);
    }

    private void Preview_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => Timeline.Focus();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || Segments.SelectedItem is not VideoSegment segment) return;
        int index = Segments.SelectedIndex;
        var edited = ClipEditorWindow.PickSegments(segment, this);
        if (edited is null || edited.Count == 0) return;
        RememberAssembly();
        InvalidatePlacedAudio();
        var detachedEvents = segment.AudioEvents.Where(a => a.Position is not null).ToArray();
        var linkedEvents = segment.AudioEvents.Where(a => a.Position is null).ToArray();
        var built = new List<VideoSegment>(edited.Count);
        for (int i = 0; i < edited.Count; i++)
        {
            var part = edited[i];
            var clippedLinked = linkedEvents
                .Where(a => a.End > part.Start && a.Start < part.End)
                .Select(a => a with { Start = a.Start > part.Start ? a.Start : part.Start, End = a.End < part.End ? a.End : part.End });
            var events = new List<AudioEvent>(clippedLinked);
            if (i == 0) events.AddRange(detachedEvents);
            built.Add(part with
            {
                Id = i == 0 ? segment.Id : Guid.NewGuid(),
                Gap = i == 0 ? segment.Gap : TimeSpan.Zero,
                AudioLinked = segment.AudioLinked,
                AudioDetached = segment.AudioDetached,
                AudioEvents = events.ToArray()
            });
        }
        _segments.RemoveAt(index);
        for (int i = 0; i < built.Count; i++) _segments.Insert(index + i, built[i]);
        Segments.SelectedIndex = index + built.Count - 1;
    }

    private void Timeline_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool canReorder = _export is null && !IndependentAudioSelected && Segments.SelectedIndex >= 0 && _segments.Count > 1;
        var visibility = canReorder ? Visibility.Visible : Visibility.Collapsed;
        MoveLeftMenuItem.Visibility = MoveRightMenuItem.Visibility = visibility;
        MoveLeftMenuItem.IsEnabled = canReorder && Segments.SelectedIndex > 0;
        MoveRightMenuItem.IsEnabled = canReorder && Segments.SelectedIndex < _segments.Count - 1;
    }

    private void Move(int direction)
    {
        if (IndependentAudioSelected) return;
        int index = Segments.SelectedIndex, target = index + direction;
        if (_export is null && index >= 0 && target >= 0 && target < _segments.Count)
        { MoveTimelineClip(index, target); }
    }
    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || Segments.SelectedIndex < 0) return;
        if (IndependentAudioSelected)
        {
            if (Timeline.SelectedAudioEvent < 0 && Segments.SelectedItem is VideoSegment { AudioDetached: true } detachedPart)
            {
                for (int i = 0; i < detachedPart.AudioEvents.Count; i++)
                {
                    var ev = detachedPart.AudioEvents[i];
                    if (!ev.Removed && ev.Track == Timeline.SelectedAudioTrack && ev.Position is TimeSpan pos &&
                        Timeline.Position >= pos.TotalSeconds && Timeline.Position < (pos + ev.End - ev.Start).TotalSeconds)
                    {
                        Timeline.SelectedAudioEvent = i;
                        break;
                    }
                }
            }
            ChangeAudioEvent(Segments.SelectedIndex, Timeline.SelectedAudioTrack, Timeline.SelectedAudioSeconds, a => a with { Removed = true });
            return;
        }
        var removed = _segments[Segments.SelectedIndex];
        var keepAudio = removed.AudioEvents.Where(a => !a.Removed && a.Position is not null && (!removed.AudioLinked || a.SourcePath is not null && a.SourcePath != removed.Path)).ToArray();
        if (_segments.Count == 1 && keepAudio.Length > 0)
        { Status.Text = "Оставьте хотя бы один видеофрагмент. Отдельный звук можно удалить на его дорожке."; return; }
        RememberAssembly();
        int index = Segments.SelectedIndex;
        _segments.RemoveAt(index);
        InvalidatePlacedAudio();
        if (keepAudio.Length > 0)
        {
            int owner = Math.Min(index, _segments.Count - 1);
            _segments[owner] = _segments[owner] with { AudioEvents = _segments[owner].AudioEvents.Concat(keepAudio).ToArray() };
        }
        Segments.SelectedIndex = Math.Min(index, _segments.Count - 1);
        if (_segments.Count == 0) { _previewIndex = -1; _assemblyPlayer?.Stop(); _ = StopPreviewAudioAsync(); TrimPanel.IsEnabled = false; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || _adding || _segments.Count == 0) return;
        var picker = new SaveFileDialog
        {
            Title = "Сохранить собранный ролик", Filter = "Видео MP4|*.mp4", DefaultExt = ".mp4",
            FileName = _segments.Count == 1 ? Path.GetFileNameWithoutExtension(_segments[0].Path) + " (монтаж).mp4" : "Сборка.mp4",
            InitialDirectory = Path.GetDirectoryName(_segments[0].Path), OverwritePrompt = false
        };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName)) { Status.Text = "Выберите новое имя: существующие файлы не перезаписываются."; return; }
        var parts = _segments.Select(p => AssemblyAudio.ApplyTracks(p, SourceAudioTracks(p.Path), _audioTracks.Take(Timeline.AudioTracks).ToArray())).ToArray();
        _wantAssemblyPlaying = false;
        _assemblyPlayer?.SetPause(true);
        SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? 0);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _export = cancellation;
        Timeline.IsEnabled = ProjectMedia.IsEnabled = false;
        AudioTrackHeaders.IsEnabled = false;
        AddVideos.IsEnabled = false;
        EditingButtons.IsEnabled = Segments.IsEnabled = TrimPanel.IsEnabled = Save.IsEnabled = false;
        CancelExport.Visibility = Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Status.Text = "Собираю ролик…";
        try
        {
            string output = await VideoAssembly.RenderAsync(parts, picker.FileName, _ffmpeg,
                progress: new Progress<double>(p => { if (_export is not null) { Progress.Value = p; Status.Text = $"Сохраняю ролик… {p:P0}"; } }), ct: cancellation.Token);
            ClipCommands.RegisterSavedVideo(output);
            Status.Text = "Сохранено: " + output;
        }
        catch (OperationCanceledException) { Status.Text = "Сохранение отменено."; }
        catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        finally
        {
            _export = null;
            Timeline.IsEnabled = ProjectMedia.IsEnabled = true;
            AudioTrackHeaders.IsEnabled = true;
            EditingButtons.IsEnabled = Segments.IsEnabled = true;
            TrimPanel.IsEnabled = Segments.SelectedItem is VideoSegment;
            CancelExport.Visibility = Progress.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _export?.Cancel();
}
