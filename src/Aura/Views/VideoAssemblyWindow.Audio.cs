using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aura.Core.Audio;
using Aura.Core.Logging;
using Aura.Core.Tools;
using LibVLCSharp.Shared;

namespace Aura.Views;

public partial class VideoAssemblyWindow
{
    private readonly List<AssemblyAudioTrack> _audioTracks = [];
    private readonly List<(Border Row, Button Mute, Button Solo, Slider Volume, TextBlock Db)> _audioHeaders = [];
    private readonly Dictionary<string, IReadOnlyList<AudioWaveform>> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _waveformRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _waveformGate = new(1, 1);
    private readonly List<(LibVLCSharp.Shared.MediaPlayer Player, Media Media, int Track)> _audioPlayers = [];
    private readonly HashSet<LibVLCSharp.Shared.MediaPlayer> _restartingAudio = [];
    private readonly Dictionary<LibVLCSharp.Shared.MediaPlayer, double> _audioRestartPositions = [];
    private bool _changingAudioHeaders;
    private int _activeAudioTrack;
    private long _lastAudioEdit;
    private (int Clip, int Track, double Event) _lastAudioEditTarget = (-1, -1, -1);

    private bool AnyAudioSolo => _audioTracks.Take(Timeline.AudioTracks).Any(t => t.Solo);
    private AssemblyAudioTrack AudioTrackState(int track) => track < _audioTracks.Count ? _audioTracks[track] : new();
    private int SourceAudioTracks(string path) => _sourceInfo.TryGetValue(path, out var info) ? info.AudioTracks : 0;
    private static string Decibels(double volume) => volume <= .001 ? "−∞ dB" : $"{20 * Math.Log10(volume):0.#} dB";

    private void RefreshAudioHeaders()
    {
        int count = Timeline.AudioTracks;
        _activeAudioTrack = Math.Clamp(_activeAudioTrack, 0, Math.Max(0, count - 1));
        while (_audioTracks.Count < count) _audioTracks.Add(new());
        if (_audioHeaders.Count != count)
        {
            AudioTrackHeaders.Children.Clear(); _audioHeaders.Clear();
            for (int i = 0; i < count; i++)
            {
                int track = i;
                var body = new StackPanel { Margin = new Thickness(10, 4, 8, 4) };
                var top = new DockPanel();
                Button Control(string content, string tip) => new()
                {
                    Content = content, Width = 26, Height = 23, Padding = new Thickness(0),
                    Margin = new Thickness(3, 0, 0, 0), ToolTip = tip,
                    Style = (Style)FindResource("Btn"), Focusable = false
                };
                var solo = Control("S", "Только эта дорожка · X");
                var mute = Control("M", "Заглушить дорожку · Z");
                DockPanel.SetDock(solo, Dock.Right); DockPanel.SetDock(mute, Dock.Right);
                top.Children.Add(solo); top.Children.Add(mute);
                top.Children.Add(new TextBlock { Text = $"A{i + 1}   Звук {i + 1}", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, FontSize = 11 });
                body.Children.Add(top);
                var volumeRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var db = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 10 };
                DockPanel.SetDock(db, Dock.Right); volumeRow.Children.Add(db);
                var volume = new Slider { Width = double.NaN, Minimum = 0, Maximum = 1, TickFrequency = .05, IsSnapToTickEnabled = true, ToolTip = "Громкость дорожки · колесо над заголовком" };
                volumeRow.Children.Add(volume); body.Children.Add(volumeRow);
                var row = new Border { Height = Controls.AssemblyTimeline.AudioLaneHeight, BorderThickness = new Thickness(3, 0, 1, 1), Child = body };
                row.SetResourceReference(Border.BorderBrushProperty, "SepBrush");
                row.PreviewMouseLeftButtonDown += (_, _) => { _activeAudioTrack = track; Timeline.SelectedAudioTrack = track; RefreshAudioHeaders(); };
                row.PreviewMouseWheel += (_, e) =>
                {
                    if (_export is not null || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
                    SetAudioTrack(track, AudioTrackState(track) with { Volume = Math.Clamp(AudioTrackState(track).Volume + Math.Sign(e.Delta) * .05, 0, 1) }, coalesce: true);
                    e.Handled = true;
                };
                mute.Click += (_, _) => ToggleAudioTrackMute(track);
                solo.Click += (_, _) => ToggleAudioTrackSolo(track);
                volume.ValueChanged += (_, _) =>
                {
                    if (!_changingAudioHeaders) SetAudioTrack(track, AudioTrackState(track) with { Volume = volume.Value }, coalesce: true);
                };
                _audioHeaders.Add((row, mute, solo, volume, db)); AudioTrackHeaders.Children.Add(row);
            }
        }
        _changingAudioHeaders = true;
        for (int track = 0; track < count; track++)
        {
            var state = AudioTrackState(track); var header = _audioHeaders[track];
            header.Mute.Opacity = state.Muted ? 1 : .5;
            header.Solo.Opacity = state.Solo ? 1 : .5;
            header.Mute.Foreground = state.Muted ? Brushes.Salmon : (Brush)FindResource("TxBrush");
            header.Solo.Foreground = state.Solo ? Brushes.Gold : (Brush)FindResource("TxBrush");
            header.Volume.Value = state.Volume; header.Db.Text = Decibels(state.Volume);
            header.Row.Background = track == _activeAudioTrack ? new SolidColorBrush(Color.FromRgb(37, 45, 59)) : Brushes.Transparent;
            header.Row.IsEnabled = _export is null;
        }
        _changingAudioHeaders = false;
        Timeline.AnySolo = AnyAudioSolo; Timeline.InvalidateVisual();
        LinkAudio.Content = Segments.SelectedItem is VideoSegment { AudioLinked: false } ? "Раздельно · G" : "Связаны · U";
        SelectionInfo.Text = Timeline.SelectedAudioTrack >= 0
            ? $"Звук {Timeline.SelectedAudioTrack + 1} · S — разрезать\n" + (Segments.SelectedItem is VideoSegment { AudioLinked: false } ? "Delete — убрать звук, сохранив видео" : "U — отделить разрезы звука от видео")
            : "Края клипа — подрезать. U / G — связь разрезов видео и звука.";
    }

    private void RememberAudioEdit(int clip, int track, double eventStart, bool coalesce)
    {
        long now = Environment.TickCount64;
        var target = (clip, track, eventStart);
        if (!coalesce || target != _lastAudioEditTarget || now - _lastAudioEdit > 400) RememberAssembly();
        _lastAudioEditTarget = target; _lastAudioEdit = now;
    }

    private void SetAudioTrack(int track, AssemblyAudioTrack state, bool coalesce = false)
    {
        if (_export is not null || track < 0 || track >= Timeline.AudioTracks || state == AudioTrackState(track)) return;
        RememberAudioEdit(-1, track, -1, coalesce);
        _activeAudioTrack = track; _audioTracks[track] = state;
        RefreshAudioHeaders(); SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? 0);
    }
    private void ToggleAudioTrackMute(int track) => SetAudioTrack(track, AudioTrackState(track) with { Muted = !AudioTrackState(track).Muted });
    private void ToggleAudioTrackSolo(int track) => SetAudioTrack(track, AudioTrackState(track) with { Solo = !AudioTrackState(track).Solo });

    private void ChangeAudioEvent(int index, int track, double source, Func<AudioEvent, AudioEvent> change, bool coalesce = false)
    {
        if (_export is not null || index < 0 || index >= _segments.Count || track < 0 || track >= SourceAudioTracks(_segments[index].Path)) return;
        var part = _segments[index];
        var audio = AssemblyAudio.Events(part, track).FirstOrDefault(e => source >= e.Start.TotalSeconds && source < e.End.TotalSeconds);
        if (audio is null || change(audio) == audio) return;
        RememberAudioEdit(index, track, audio.Start.TotalSeconds, coalesce);
        _changingAssembly = true;
        _segments[index] = AssemblyAudio.Change(part, track, TimeSpan.FromSeconds(source), change);
        Segments.SelectedIndex = index; _changingAssembly = false;
        RefreshTimeline(); SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? source);
    }
    private void AdjustAudioEvent(int index, int track, double source, int delta) => ChangeAudioEvent(index, track, source,
        e => e with { Volume = Math.Clamp(e.Volume + delta * .05, 0, 1), Muted = false, Removed = false }, coalesce: true);
    private void MuteAudioEvent_Click(object sender, RoutedEventArgs e) => ChangeAudioEvent(Segments.SelectedIndex,
        Timeline.SelectedAudioTrack, Timeline.SelectedAudioSeconds, a => a with { Muted = !a.Muted, Removed = false });

    private void SetAudioLink(bool linked)
    {
        if (_export is not null || Segments.SelectedItem is not VideoSegment part || part.AudioLinked == linked) return;
        RememberAssembly();
        int index = Segments.SelectedIndex;
        _changingAssembly = true; _segments[index] = part with { AudioLinked = linked }; Segments.SelectedIndex = index; _changingAssembly = false;
        RefreshTimeline();
    }
    private void LinkAudio_Click(object sender, RoutedEventArgs e) => SetAudioLink(Segments.SelectedItem is VideoSegment { AudioLinked: false });
    private void UnlinkAudio_Click(object sender, RoutedEventArgs e) => SetAudioLink(false);
    private void RelinkAudio_Click(object sender, RoutedEventArgs e) => SetAudioLink(true);

    private async void RequestWaveform(string path)
    {
        if (!_waveformRequests.Add(path)) return;
        try
        {
            await _waveformGate.WaitAsync(_lifetime.Token);
            try { _waveforms[path] = await Task.Run(() => AudioWaveform.Read(path, _lifetime.Token), _lifetime.Token); }
            finally { _waveformGate.Release(); }
            if (!_lifetime.IsCancellationRequested) Timeline.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Info("Editor", $"Волна {Path.GetFileName(path)} недоступна: {ex.Message}"); }
    }

    private async Task StopPreviewAudioAsync()
    {
        var players = _audioPlayers.ToArray(); _audioPlayers.Clear();
        _audioRestartPositions.Clear();
        await Task.Run(() =>
        {
            foreach (var audio in players) { audio.Player.Stop(); audio.Player.Dispose(); audio.Media.Dispose(); }
        });
    }
    private async Task OpenPreviewAudioAsync(VideoSegment part, double start, int revision)
    {
        for (int track = 0; track < SourceAudioTracks(part.Path); track++)
        {
            if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision) return;
            var media = new Media(_assemblyVlc!, new Uri(part.Path));
            media.AddOption(":no-video"); media.AddOption(":audio-track=" + track);
            media.AddOption(":start-paused");
            media.AddOption(":start-time=" + start.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            var player = new LibVLCSharp.Shared.MediaPlayer(_assemblyVlc!);
            // MMDevice volume/mute controls the shared process session, not an individual track.
            if (!player.SetAudioOutput("directsound")) { player.Dispose(); media.Dispose(); throw new InvalidOperationException("Не удалось открыть независимый вывод звука."); }
            player.Mute = true;
            bool keep = false;
            try
            {
                if (!await Task.Run(() => player.Play(media))) throw new InvalidOperationException("Не удалось открыть звуковую дорожку.");
                for (int attempt = 0; attempt < 100 && player.State is not (VLCState.Paused or VLCState.Error or VLCState.Ended); attempt++)
                {
                    await Task.Delay(20, _lifetime.Token);
                    if (revision != _assemblyLoadRevision) return;
                }
                player.SetPause(true);
                // Resolve the actual stream ID once after VLC has discovered the tracks.
                var tracks = player.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
                if (track >= tracks.Length) throw new InvalidOperationException("Звуковая дорожка недоступна для предпросмотра.");
                if (player.AudioTrack != tracks[track].Id) player.SetAudioTrack(tracks[track].Id);
                if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision) return;
                _audioPlayers.Add((player, media, track)); keep = true;
                player.SetRate((float)_forwardPreviewRate);
            }
            finally { if (!keep) await Task.Run(() => { player.Stop(); player.Dispose(); media.Dispose(); }); }
        }
    }
    private void SyncPreviewAudio(double source, bool seek = false)
    {
        if (_assemblyOpening || _previewIndex < 0 || _previewIndex >= _segments.Count) return;
        var part = _segments[_previewIndex];
        bool pause = !_wantAssemblyPlaying || _assemblyScrubbing || _waitingAssemblyFrame || _reversePreviewRate > 0;
        foreach (var audio in _audioPlayers)
        {
            double gain = AssemblyAudio.GainAt(part, audio.Track, source) * AudioTrackState(audio.Track).Gain(AnyAudioSolo);
            if (gain > 0 && source + .1 < part.End.TotalSeconds && (seek || _wantAssemblyPlaying) && audio.Player.State == VLCState.Ended &&
                (!_audioRestartPositions.TryGetValue(audio.Player, out double previousRestart) || source < previousRestart - .1))
            {
                _audioRestartPositions[audio.Player] = source;
                _ = RestartPreviewAudioAsync(audio, source);
                continue;
            }
            // VLC's volume control is cubic; editor/export gains are linear amplitudes.
            audio.Player.Volume = (int)Math.Round(Math.Cbrt(gain) * 100);
            audio.Player.Mute = MutePreview.IsChecked == true || gain <= 0 || _reversePreviewRate > 0;
            // Muted events must keep their clock running; disabling tracks/repeated seeks rebuild audio output.
            if (seek) audio.Player.Time = (long)(source * 1000);
            audio.Player.SetPause(pause);
        }
    }

    private async Task RestartPreviewAudioAsync((LibVLCSharp.Shared.MediaPlayer Player, Media Media, int Track) audio, double source)
    {
        if (!_restartingAudio.Add(audio.Player)) return;
        try
        {
            await _assemblyPlayback.WaitAsync(_lifetime.Token);
            try
            {
                if (_lifetime.IsCancellationRequested || !_audioPlayers.Contains(audio)) return;
                audio.Player.Mute = true;
                if (!await Task.Run(() => audio.Player.Play(audio.Media))) return;
                audio.Player.Time = (long)(source * 1000);
                audio.Player.SetPause(true);
            }
            finally { _assemblyPlayback.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) Log.Warn("Editor", "Возврат к звуку: " + ex.Message); }
        finally { _restartingAudio.Remove(audio.Player); }
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => Dialogs.Say("Клавиши монтажа",
        "Space / Enter — воспроизведение и пауза\nS — разрезать в позиции курсора\nU / G — отделить / связать разрезы видео и звука\nZ — mute дорожки, X — solo дорожки\nDelete — удалить выбранное; U отделяет звук для удаления без видео\nCtrl+Z — отменить; Ctrl+Y / Ctrl+Shift+Z — повторить\nJ / K / L — назад / пауза / вперёд\nAlt+← / → — на один кадр; Home / End — в начало / конец\n+ / −, Ctrl+колесо — масштаб; Shift+колесо — прокрутка\nКолесо над звуковым фрагментом — его громкость\nCtrl+O — добавить видео; Ctrl+S — сохранить ролик");
}
