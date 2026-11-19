using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aura.Core.Library;
using Aura.Core.Tools;
using Microsoft.Win32;

namespace Aura.Views;

public partial class ClipEditorWindow
{
    private IReadOnlyList<VideoSegment> _keptSegments = [];
    private readonly Stack<IReadOnlyList<VideoSegment>> _cutHistory = new();
    private VideoSegment? _initialSegment;
    private IReadOnlyList<VideoSegment>? _pickedSegments;
    private bool _selectForAssembly, _externalVideo;
    private double? _cutSeekTarget;

    public static void ShowExternal(string path)
    {
        var window = new ClipEditorWindow(new ClipItem(path, Path.GetDirectoryName(path)!)) { _externalVideo = true };
        var owner = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    internal static IReadOnlyList<VideoSegment>? PickSegments(VideoSegment segment, Window owner)
    {
        var window = new ClipEditorWindow(new ClipItem(segment.Path, Path.GetDirectoryName(segment.Path)!))
        { Owner = owner, _initialSegment = segment, _selectForAssembly = true, _externalVideo = true, _audioSelection = segment.AudioTrack };
        window.ExportButton.Content = "Готово — вернуть в сборку";
        window.AssemblyButton.Visibility = Visibility.Collapsed;
        window.ShowDialog();
        return window._pickedSegments;
    }

    private void InitializeCuts()
    {
        _startSeconds = _initialSegment?.Start.TotalSeconds ?? 0;
        _endSeconds = Math.Min(_durationSeconds, _initialSegment?.End.TotalSeconds ?? _durationSeconds);
        _keptSegments = [new VideoSegment(_item.FullPath, TimeSpan.FromSeconds(_startSeconds), TimeSpan.FromSeconds(_endSeconds))];
        if (_initialSegment is not null) Seek(_startSeconds);
    }

    private IReadOnlyList<VideoSegment> SelectedSegments() => _keptSegments
        .Select(s => s with
        {
            Start = s.Start > TimeSpan.FromSeconds(_startSeconds) ? s.Start : TimeSpan.FromSeconds(_startSeconds),
            End = s.End < TimeSpan.FromSeconds(_endSeconds) ? s.End : TimeSpan.FromSeconds(_endSeconds),
            AudioTrack = _audioSelection
        }).Where(s => s.Duration >= TimeSpan.FromMilliseconds(50)).ToArray();

    private void ResetSelectionToSource()
    {
        _startSeconds = _initialSegment?.Start.TotalSeconds ?? 0;
        _endSeconds = Math.Min(_durationSeconds, _initialSegment?.End.TotalSeconds ?? _durationSeconds);
    }

    private void RemoveRange_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null || _keptSegments.Count == 0) return;
        try
        {
            var kept = VideoSegment.Remove(_keptSegments, TimeSpan.FromSeconds(_startSeconds), TimeSpan.FromSeconds(_endSeconds));
            if (kept.SequenceEqual(_keptSegments)) return;
            _cutHistory.Push(_keptSegments);
            _keptSegments = kept;
            _player?.SetPause(true);
            ResetSelectionToSource();
            Seek(_keptSegments[0].Start.TotalSeconds);
            UpdateTimelineVisuals();
            ExportHint.Text = DefaultHint();
        }
        catch (InvalidOperationException ex) { Dialogs.Say("Оставьте часть видео", ex.Message); }
    }

    private void UndoCut_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null || !_cutHistory.TryPop(out var previous)) return;
        _keptSegments = previous;
        ResetSelectionToSource();
        UpdateTimelineVisuals();
        ExportHint.Text = DefaultHint();
    }

    private void DrawCuts(double width)
    {
        RemovedRanges.Children.Clear();
        if (_durationSeconds <= 0) return;
        double at = 0;
        foreach (var part in _keptSegments)
        {
            Shade(at, part.Start.TotalSeconds);
            at = part.End.TotalSeconds;
        }
        Shade(at, _durationSeconds);
        UndoCutButton.IsEnabled = _cutHistory.Count > 0 && _exportCancellation is null;
        var selected = SelectedSegments();
        CutSummary.Text = _cutHistory.Count == 0 ? "I / O — границы участка. Delete — убрать его." :
            $"Останется частей: {selected.Count} · {TimeSpan.FromTicks(selected.Sum(s => s.Duration.Ticks)):mm\\:ss\\.fff}";
        void Shade(double start, double end)
        {
            if (end <= start) return;
            var shade = new Border { Width = (end - start) / _durationSeconds * width, Height = 40, Background = new SolidColorBrush(Color.FromArgb(190, 130, 24, 34)) };
            Canvas.SetLeft(shade, start / _durationSeconds * width);
            RemovedRanges.Children.Add(shade);
        }
    }

    private bool SkipRemovedSection()
    {
        if (_cutHistory.Count == 0 || _player?.IsPlaying != true || _scrubbing) return false;
        double now = _cutSeekTarget is null ? LogicalSeconds : CurrentSeconds;
        if (_keptSegments.Any(s => now >= s.Start.TotalSeconds && now < s.End.TotalSeconds))
        { _cutSeekTarget = null; return false; }
        var next = _keptSegments.FirstOrDefault(s => s.Start.TotalSeconds > now);
        if (next is not null)
        {
            // Time обновляется асинхронно: повторные seek не дают декодеру закончить переход.
            double target = next.Start.TotalSeconds;
            if (_cutSeekTarget != target)
            {
                Seek(target);
                _cutSeekTarget = target;
            }
        }
        else _player.SetPause(true);
        return true;
    }

    private void Assembly_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null) return;
        var segments = SelectedSegments();
        _player?.SetPause(true);
        Close();
        VideoAssemblyWindow.ShowSegments(segments);
    }

    private async Task ExportAssemblyAsync()
    {
        var segments = SelectedSegments();
        if (segments.Count == 0) { Dialogs.Say("Нет фрагментов", "Выделите оставшуюся часть видео."); return; }
        var picker = new SaveFileDialog
        {
            Title = "Сохранить результат монтажа", Filter = "Видео MP4|*.mp4", DefaultExt = ".mp4", OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(_item.FullPath) + " (монтаж).mp4", InitialDirectory = Path.GetDirectoryName(_item.FullPath)
        };
        if (picker.ShowDialog(this) != true) return;
        await ExportAsync((_, _, progress, ct) => VideoAssembly.RenderAsync(segments, picker.FileName, _ffmpeg,
            progress: progress, ct: ct), fast: false);
    }
}
