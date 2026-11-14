using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Aura.Core.Library;
using Aura.Core.Logging;
using Aura.Core.Tools;
using LibVLCSharp.Shared;

namespace Aura.Views;

public partial class VideoAssemblyWindow
{
    private readonly Dictionary<string, VideoFileInfo> _sourceInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClipItem> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _capturedThumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string>> _assemblyMixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _assemblyMixFiles = [];
    private readonly Stack<VideoSegment[]> _assemblyHistory = new();
    private readonly DispatcherTimer _assemblyTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly SemaphoreSlim _assemblyPlayback = new(1, 1);
    private LibVLC? _assemblyVlc;
    private LibVLCSharp.Shared.MediaPlayer? _assemblyPlayer;
    private Media? _assemblyMedia;
    private int _selectionRevision, _previewIndex = -1;
    private bool _changingAssembly, _wantAssemblyPlaying, _waitingAssemblyFrame, _assemblyScrubbing;
    private double _previewStart;
    private bool _trimDragging, _trimRemembered;
    private int _assemblyLoadRevision;
    private bool _assemblyOpening;
    private long _assemblyLoadStarted;

    private void InitializeAssemblyPreview()
    {
        foreach (var slider in new[] { TrimStart, TrimEnd })
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
            { _trimDragging = true; _trimRemembered = false; }));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
            {
                _trimDragging = false;
                if (Segments.SelectedItem is VideoSegment part) LoadAssemblyClip(Segments.SelectedIndex,
                    slider == TrimStart ? part.Start.TotalSeconds : Math.Max(part.Start.TotalSeconds, part.End.TotalSeconds - 0.15));
            }));
        }
        AssemblyPosition.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _assemblyScrubbing = true));
        AssemblyPosition.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        { _assemblyScrubbing = false; SeekAssembly(AssemblyPosition.Value); }));
        _assemblyTimer.Tick += AssemblyPreview_Tick;
        Loaded += async (_, _) =>
        {
            try
            {
                var engine = await Task.Run(() =>
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    return new LibVLC("--quiet", "--no-video-title-show", "--audio-resampler=speex_resampler", "--no-avcodec-hurry-up");
                });
                if (_lifetime.IsCancellationRequested) { engine.Dispose(); return; }
                _assemblyVlc = engine;
                _assemblyPlayer = new LibVLCSharp.Shared.MediaPlayer(engine);
                _assemblyPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
                { if (_assemblyPlayer?.State == VLCState.Ended) AdvanceAssembly(); });
                _assemblyPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
                { _wantAssemblyPlaying = false; Status.Text = "Не удалось показать видео. Выберите другой клип или попробуйте сохранить ролик."; });
                AssemblyPreview.MediaPlayer = _assemblyPlayer;
                _assemblyTimer.Start();
                if (Segments.SelectedIndex < 0 && _segments.Count > 0) Segments.SelectedIndex = 0;
                else await SelectAssemblyClipAsync();
            }
            catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        };
    }

    private async void Segments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingAssembly) await SelectAssemblyClipAsync();
    }

    private async Task SelectAssemblyClipAsync(double? sourceTime = null)
    {
        int revision = ++_selectionRevision;
        if (Segments.SelectedItem is not VideoSegment part) return;
        try
        {
            if (!_sourceInfo.TryGetValue(part.Path, out var info))
                _sourceInfo[part.Path] = info = await VideoAssembly.ReadInfoAsync(part.Path, _lifetime.Token);
            if (revision != _selectionRevision || _lifetime.IsCancellationRequested) return;
            _changingAssembly = true;
            SelectedClipName.Text = part.Name;
            TrimStart.Maximum = TrimEnd.Maximum = info.Duration.TotalSeconds;
            TrimStart.Value = part.Start.TotalSeconds; TrimEnd.Value = part.End.TotalSeconds;
            ClipAudio.Items.Clear();
            ClipAudio.Items.Add(info.AudioTracks == 0 ? "Без звука" : "Все дорожки");
            for (int i = 0; i < info.AudioTracks; i++) ClipAudio.Items.Add("Дорожка " + (i + 1));
            ClipAudio.SelectedIndex = info.AudioTracks == 0 ? 0 : part.AudioTrack + 1;
            ClipAudio.IsEnabled = info.AudioTracks > 1;
            TrimPanel.IsEnabled = _export is null;
            UpdateTrimLabels();
            _changingAssembly = false;
            if (part.AudioTrack < 0 && info.AudioTracks > 1)
            {
                try
                {
                    if (!_assemblyMixes.TryGetValue(part.Path, out var mix))
                    {
                        string output = Path.Combine(Path.GetTempPath(), "aura-assembly-mix-" + Guid.NewGuid().ToString("N") + ".m4a");
                        _assemblyMixFiles.Add(output);
                        async Task<string> Prepare()
                        {
                            await VideoEditor.PrepareMixedAudioAsync(part.Path, output, info.AudioTracks, _ffmpeg, _lifetime.Token);
                            return output;
                        }
                        _assemblyMixes[part.Path] = mix = Prepare();
                    }
                    await mix;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { Status.Text = "Не удалось подготовить общий звук предпросмотра: " + ex.Message; }
            }
            if (revision != _selectionRevision || _lifetime.IsCancellationRequested) return;
            LoadAssemblyClip(Segments.SelectedIndex, sourceTime ?? part.Start.TotalSeconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        finally { _changingAssembly = false; }
    }

    private async void LoadAssemblyClip(int index, double sourceTime)
    {
        if (_assemblyPlayer is null || index < 0 || index >= _segments.Count || _lifetime.IsCancellationRequested) return;
        var part = _segments[index];
        int revision = ++_assemblyLoadRevision;
        double start = Math.Clamp(sourceTime, part.Start.TotalSeconds, Math.Max(part.Start.TotalSeconds, part.End.TotalSeconds - 0.05));
        _waitingAssemblyFrame = true;
        _assemblyOpening = true;
        await _assemblyPlayback.WaitAsync();
        try
        {
            if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision) return;
            var old = _assemblyMedia;
            _assemblyMedia = new Media(_assemblyVlc!, new Uri(part.Path));
            if (part.AudioTrack < 0 && _assemblyMixes.TryGetValue(part.Path, out var mix) && mix.IsCompletedSuccessfully)
                _assemblyMedia.AddSlave(MediaSlaveType.Audio, 4, new Uri(mix.Result).AbsoluteUri);
            _assemblyMedia.AddOption(":start-time=" + start.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            _assemblyPlayer.Mute = MutePreview.IsChecked == true;
            var player = _assemblyPlayer;
            var media = _assemblyMedia;
            // Смена файла останавливает видеовывод: ему может понадобиться свободный UI-поток.
            bool played = await Task.Run(() => player.Play(media));
            old?.Dispose();
            if (!played) throw new InvalidOperationException("Не удалось открыть видео для предпросмотра.");
            if (revision != _assemblyLoadRevision) return;
            _previewIndex = index;
            _previewStart = start;
            _assemblyLoadStarted = Environment.TickCount64;
        }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) { Status.Text = ex.Message; Log.Error("Editor", ex); } }
        finally { if (revision == _assemblyLoadRevision) _assemblyOpening = false; _assemblyPlayback.Release(); }
    }

    private void AssemblyPreview_Tick(object? sender, EventArgs e)
    {
        if (_assemblyOpening || _assemblyPlayer is null || _previewIndex < 0 || _previewIndex >= _segments.Count) return;
        double time = Math.Max(0, _assemblyPlayer.Time / 1000.0);
        if (_waitingAssemblyFrame)
        {
            if (_assemblyPlayer.VoutCount == 0 || time < _previewStart - 0.08)
            {
                if (Environment.TickCount64 - _assemblyLoadStarted > 8000)
                { _waitingAssemblyFrame = _wantAssemblyPlaying = false; _assemblyPlayer.SetPause(true); Status.Text = "Предпросмотр не отдал кадр. Попробуйте выбрать клип ещё раз."; }
                return;
            }
            var part = _segments[_previewIndex];
            var tracks = _assemblyPlayer.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
            bool mixed = part.AudioTrack < 0 && _assemblyMixes.TryGetValue(part.Path, out var prepared) && prepared.IsCompletedSuccessfully;
            int expected = _sourceInfo.TryGetValue(part.Path, out var info) ? info.AudioTracks + (mixed ? 1 : 0) : 0;
            if (tracks.Length < expected)
            {
                if (Environment.TickCount64 - _assemblyLoadStarted > 3000)
                { _waitingAssemblyFrame = _wantAssemblyPlaying = false; _assemblyPlayer.SetPause(true); Status.Text = "Не удалось подключить звук предпросмотра. Выберите дорожку ещё раз."; }
                return;
            }
            _waitingAssemblyFrame = false;
            _ = CaptureAssemblyThumbnailAsync(_segments[_previewIndex].Path);
            int requested = part.AudioTrack;
            int audio = requested >= 0 && requested < tracks.Length ? tracks[requested].Id : mixed && tracks.Length > 0 ? tracks[^1].Id : -1;
            if (audio >= 0 && _assemblyPlayer.AudioTrack != audio)
            {
                _assemblyPlayer.SetAudioTrack(audio);
                if (_assemblyPlayer.IsPlaying) _assemblyPlayer.Time = _assemblyPlayer.Time;
            }
            if (!_wantAssemblyPlaying) _assemblyPlayer.SetPause(true);
        }
        if (_wantAssemblyPlaying && time >= _segments[_previewIndex].End.TotalSeconds - 0.08) { AdvanceAssembly(); return; }
        double global = _segments.Take(_previewIndex).Sum(s => s.Duration.TotalSeconds) +
            Math.Clamp(time - _segments[_previewIndex].Start.TotalSeconds, 0, _segments[_previewIndex].Duration.TotalSeconds);
        if (!_assemblyScrubbing)
        {
            _changingAssembly = true;
            AssemblyPosition.Value = global;
            _changingAssembly = false;
            PreviewTime.Text = $"{TimeSpan.FromSeconds(global):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(AssemblyPosition.Maximum):hh\\:mm\\:ss}";
        }
        PlayAssembly.Content = _wantAssemblyPlaying ? "Ⅱ Пауза" : "▶ Смотреть ролик";
    }

    private void AdvanceAssembly()
    {
        if (_lifetime.IsCancellationRequested || !_wantAssemblyPlaying) return;
        if (_previewIndex + 1 < _segments.Count)
            Segments.SelectedIndex = _previewIndex + 1;
        else
        {
            _wantAssemblyPlaying = false;
            _assemblyPlayer?.SetPause(true);
            PlayAssembly.Content = "▶ Смотреть ролик";
        }
    }

    private void SeekAssembly(double time)
    {
        double offset = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            var part = _segments[i];
            if (time < offset + part.Duration.TotalSeconds || i == _segments.Count - 1)
            {
                _changingAssembly = true; Segments.SelectedIndex = i; _changingAssembly = false;
                _ = SelectAssemblyClipAsync(part.Start.TotalSeconds + Math.Clamp(time - offset, 0, part.Duration.TotalSeconds));
                return;
            }
            offset += part.Duration.TotalSeconds;
        }
    }

    private void AssemblyPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_changingAssembly && !_assemblyScrubbing && IsLoaded) SeekAssembly(e.NewValue); }
    private void PlayAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (_segments.Count == 0 || _assemblyPlayer is null) return;
        _wantAssemblyPlaying = !_wantAssemblyPlaying;
        if (_wantAssemblyPlaying)
        {
            if (_assemblyPlayer.State == VLCState.Ended || AssemblyPosition.Value >= AssemblyPosition.Maximum - 0.1) SeekAssembly(0);
            else _assemblyPlayer.SetPause(false);
        }
        else _assemblyPlayer.SetPause(true);
        PlayAssembly.Content = _wantAssemblyPlaying ? "Ⅱ Пауза" : "▶ Смотреть ролик";
    }
    private void ToStart_Click(object sender, RoutedEventArgs e) => SeekAssembly(0);
    private void Mute_Changed(object sender, RoutedEventArgs e) { if (_assemblyPlayer is not null) _assemblyPlayer.Mute = MutePreview.IsChecked == true; }

    private void RememberAssembly()
    {
        _assemblyHistory.Push(_segments.ToArray());
        UndoAssembly.IsEnabled = true;
    }
    private void SplitAssembly_Click(object sender, RoutedEventArgs e)
    { if (_assemblyPlayer is not null) SplitAssemblyAt(_assemblyPlayer.Time / 1000.0); }
    private void SplitAssemblyAt(double seconds)
    {
        if (_export is not null || Segments.SelectedItem is not VideoSegment part) return;
        var at = TimeSpan.FromSeconds(seconds);
        if (at - part.Start < TimeSpan.FromMilliseconds(50) || part.End - at < TimeSpan.FromMilliseconds(50))
        { Status.Text = "Поставьте позицию просмотра внутри клипа, чтобы разрезать его."; return; }
        RememberAssembly();
        int index = Segments.SelectedIndex;
        _wantAssemblyPlaying = false;
        _changingAssembly = true;
        _segments[index] = part with { End = at };
        _segments.Insert(index + 1, part with { Start = at });
        _changingAssembly = false;
        Segments.SelectedIndex = index + 1;
    }
    private void UndoAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || !_assemblyHistory.TryPop(out var previous)) return;
        _changingAssembly = true;
        _segments.Clear(); foreach (var part in previous) _segments.Add(part);
        _changingAssembly = false;
        UndoAssembly.IsEnabled = _assemblyHistory.Count > 0;
        Segments.SelectedIndex = _segments.Count > 0 ? 0 : -1;
    }
    private void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_changingAssembly || _export is not null || Segments.SelectedItem is not VideoSegment part) return;
        double start = TrimStart.Value, end = TrimEnd.Value;
        if (end - start < 0.05)
        {
            _changingAssembly = true;
            if (sender == TrimStart) TrimStart.Value = part.Start.TotalSeconds; else TrimEnd.Value = part.End.TotalSeconds;
            _changingAssembly = false; return;
        }
        ReplaceAssemblyPart(part with { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) });
        _wantAssemblyPlaying = false;
        if (!_trimDragging) LoadAssemblyClip(Segments.SelectedIndex, sender == TrimStart ? start : Math.Max(start, end - 0.15));
        UpdateTrimLabels();
    }
    private void ReplaceAssemblyPart(VideoSegment part)
    {
        int index = Segments.SelectedIndex;
        if (!_trimDragging || !_trimRemembered) { RememberAssembly(); _trimRemembered = true; }
        _changingAssembly = true; _segments[index] = part; Segments.SelectedIndex = index; _changingAssembly = false;
    }
    private void UpdateTrimLabels()
    {
        TrimStartTime.Text = TimeSpan.FromSeconds(TrimStart.Value).ToString(@"hh\:mm\:ss\.fff");
        TrimEndTime.Text = TimeSpan.FromSeconds(TrimEnd.Value).ToString(@"hh\:mm\:ss\.fff");
    }
    private void MarkStart_Click(object sender, RoutedEventArgs e) { if (_assemblyPlayer is not null) TrimStart.Value = Math.Max(0, _assemblyPlayer.Time / 1000.0); }
    private void MarkEnd_Click(object sender, RoutedEventArgs e) { if (_assemblyPlayer is not null) TrimEnd.Value = Math.Max(0, _assemblyPlayer.Time / 1000.0); }
    private void ClipAudio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingAssembly || _export is not null || Segments.SelectedItem is not VideoSegment part) return;
        ReplaceAssemblyPart(part with { AudioTrack = ClipAudio.SelectedIndex - 1 });
        _ = SelectAssemblyClipAsync();
    }
    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not VideoSegment part) return;
        try
        {
            if (!_thumbs.TryGetValue(part.Path, out var item))
            {
                _thumbs[part.Path] = item = new ClipItem(part.Path, Path.GetDirectoryName(part.Path)!);
                await ClipThumbnails.LoadAsync(item);
            }
            if (!_lifetime.IsCancellationRequested && image.DataContext is VideoSegment current && current.Path == part.Path)
                image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(ClipItem.Thumbnail)) { Source = item });
        }
        catch (Exception ex) { Log.Warn("Editor", "Миниатюра: " + ex.Message); }
    }
    private async Task CaptureAssemblyThumbnailAsync(string path)
    {
        if (_assemblyPlayer is not { } player || !_thumbs.TryGetValue(path, out var item) || item.Thumbnail is not null || !_capturedThumbs.Add(path)) return;
        string temp = Path.Combine(Path.GetTempPath(), "aura-assembly-thumb-" + Guid.NewGuid().ToString("N") + ".png");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Taken(object? sender, MediaPlayerSnapshotTakenEventArgs args) { if (args.Filename == temp) ready.TrySetResult(); }
        player.SnapshotTaken += Taken;
        try
        {
            if (!player.TakeSnapshot(0, temp, 320, 0)) return;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            using var stream = File.OpenRead(temp);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); item.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or NotSupportedException)
        { }
        finally
        {
            try { player.SnapshotTaken -= Taken; } catch (ObjectDisposedException) { }
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private void CloseAssemblyPreview()
    {
        _assemblyTimer.Stop();
        AssemblyPreview.MediaPlayer = null;
        var player = _assemblyPlayer; var media = _assemblyMedia; var engine = _assemblyVlc;
        _assemblyPlayer = null;
        var tasks = _assemblyMixes.Values.ToArray();
        var files = _assemblyMixFiles.ToArray();
        _ = Task.Run(async () =>
        {
            await _assemblyPlayback.WaitAsync();
            try { player?.Stop(); player?.Dispose(); media?.Dispose(); engine?.Dispose(); }
            finally { _assemblyPlayback.Release(); }
            try { await Task.WhenAll(tasks); } catch { }
            foreach (string path in files) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        });
    }
}
