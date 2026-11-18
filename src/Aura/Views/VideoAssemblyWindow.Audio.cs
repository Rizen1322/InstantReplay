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
    private readonly Dictionary<LibVLCSharp.Shared.MediaPlayer, AssemblySound> _placedAudioPlayers = [];
    private readonly HashSet<LibVLCSharp.Shared.MediaPlayer> _activePlacedAudio = [];
    private readonly HashSet<LibVLCSharp.Shared.MediaPlayer> _continuousPlacedAudio = [];
    private bool _openingPlacedAudio;
    private readonly HashSet<LibVLCSharp.Shared.MediaPlayer> _restartingAudio = [];
    private readonly Dictionary<LibVLCSharp.Shared.MediaPlayer, double> _audioRestartPositions = [];
    private bool _changingAudioHeaders;
    private int _activeAudioTrack;
    private long _lastAudioEdit;
    private (int Clip, int Track, double Event) _lastAudioEditTarget = (-1, -1, -1);

    private bool AnyAudioSolo => _audioTracks.Take(Timeline.AudioTracks).Any(t => t.Solo);
    private IReadOnlyList<AssemblySound> AssemblySounds => AssemblyAudio.Sounds(_segments, SourceAudioTracks);
    private double VideoDuration => _segments.Sum(p => p.Gap.TotalSeconds + p.Duration.TotalSeconds);
    private double LeadingGapEnd => _segments.Count > 0 ? _segments[0].Gap.TotalSeconds : 0;
    private double ProjectDuration => Math.Max(VideoDuration, AssemblySounds.Where(s => !s.Removed).Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max());
    private bool IndependentAudioSelected => Segments.SelectedItem is VideoSegment part && Timeline.SelectedAudioTrack >= 0 &&
        (!part.AudioLinked && (Timeline.SelectedAudioEvent < 0 || Timeline.SelectedAudioEvent < part.AudioEvents.Count &&
            part.AudioEvents[Timeline.SelectedAudioEvent].Track == Timeline.SelectedAudioTrack) ||
         Timeline.SelectedAudioEvent >= 0 && Timeline.SelectedAudioEvent < part.AudioEvents.Count &&
            part.AudioEvents[Timeline.SelectedAudioEvent].SourcePath is string path && path != part.Path);
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
                row.PreviewMouseLeftButtonDown += (_, _) => { _activeAudioTrack = track; Timeline.SelectedAudioTrack = track; Timeline.SelectedAudioEvent = -1; RefreshAudioHeaders(); };
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
            ? $"Звук {Timeline.SelectedAudioTrack + 1} · S — разрезать\n" + (Segments.SelectedItem is VideoSegment { AudioLinked: false } ? "Delete — убрать звук, сохранив видео" : "U — отделить звук от видео")
            : "Края клипа — подрезать. U — отделить звук и двигать его независимо.";
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
        if (Timeline.SelectedAudioEvent >= 0 && index >= 0 && index < _segments.Count && Timeline.SelectedAudioEvent < _segments[index].AudioEvents.Count &&
            _segments[index].AudioEvents[Timeline.SelectedAudioEvent].Track == track)
        {
            int selected = Timeline.SelectedAudioEvent;
            var detached = _segments[index].AudioEvents[selected];
            if (_export is not null || change(detached) == detached) return;
            RememberAudioEdit(index, track, selected, coalesce);
            ReplaceDetachedAudio(index, selected, change(detached));
            SyncPreviewAudio(_assemblyPlayer?.Time / 1000.0 ?? 0); return;
        }
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
        _changingAssembly = true;
        _segments[index] = linked ? part with { AudioLinked = true } : AssemblyAudio.Unlink(part, VideoTimeline.Offset(_segments, index), SourceAudioTracks(part.Path));
        Segments.SelectedIndex = index; _changingAssembly = false;
        if (!linked)
        {
            Timeline.SelectedAudioTrack = Math.Max(0, Timeline.SelectedAudioTrack);
            Timeline.SelectedAudioEvent = _segments[index].AudioEvents.ToList().FindIndex(e => e.Position is not null && e.Track == Timeline.SelectedAudioTrack);
        }
        RefreshTimeline();
        SeekAssembly(Timeline.Position);
    }
    private void ReplaceDetachedAudio(int owner, int index, AudioEvent audio)
    {
        var events = _segments[owner].AudioEvents.ToArray(); events[index] = audio;
        var part = _segments[owner] with { AudioEvents = events }; part.Validate();
        _changingAssembly = true; _segments[owner] = part; Segments.SelectedIndex = owner; _changingAssembly = false;
        Timeline.SelectedAudioEvent = index;
        RefreshTimeline();
    }
    private void EditDetachedAudio(int owner, int index, AudioEvent audio)
    {
        if (_export is not null) return;
        RememberAssembly(); ReplaceDetachedAudio(owner, index, audio);
        SeekAssembly(Timeline.Position);
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
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Info("Editor", $"Волна {Path.GetFileName(path)} недоступна: {ex.Message}"); }
    }

    private async Task StopPreviewAudioAsync(bool keepPlaced = false)
    {
        var players = _audioPlayers.Where(a => !keepPlaced || !_placedAudioPlayers.ContainsKey(a.Player)).ToArray();
        foreach (var audio in players)
        {
            _audioPlayers.Remove(audio); _placedAudioPlayers.Remove(audio.Player); _activePlacedAudio.Remove(audio.Player);
            _continuousPlacedAudio.Remove(audio.Player); _audioRestartPositions.Remove(audio.Player);
        }
        if (keepPlaced) foreach (var player in _placedAudioPlayers.Keys) if (_activePlacedAudio.Contains(player)) _continuousPlacedAudio.Add(player);
        await Task.Run(() =>
        {
            foreach (var audio in players) { audio.Player.Stop(); audio.Player.Dispose(); audio.Media.Dispose(); }
        });
    }

    private void InvalidatePlacedAudio()
    {
        var placed = _audioPlayers.Where(a => _placedAudioPlayers.ContainsKey(a.Player)).ToArray();
        foreach (var audio in placed)
        {
            audio.Player.Mute = true;
            _audioPlayers.Remove(audio);
            _placedAudioPlayers.Remove(audio.Player);
            _activePlacedAudio.Remove(audio.Player);
            _continuousPlacedAudio.Remove(audio.Player);
            _audioRestartPositions.Remove(audio.Player);
        }
        if (placed.Length > 0)
            _ = Task.Run(() => { foreach (var a in placed) { a.Player.Stop(); a.Player.Dispose(); a.Media.Dispose(); } });
    }
    private async Task OpenPreviewAudioAsync(VideoSegment part, double start, int revision, int index)
    {
        for (int track = 0; !part.AudioDetached && track < SourceAudioTracks(part.Path); track++)
        {
            await OpenAudioPlayerAsync(part.Path, track, start, revision, null);
        }
        double global = _tailPosition ?? VideoTimeline.Offset(_segments, index) + start - part.Start.TotalSeconds;
        foreach (var sound in PlacedSoundsNear(global))
            if (!_placedAudioPlayers.Values.Any(p => p.Owner == sound.Owner && p.Event == sound.Event))
                await OpenAudioPlayerAsync(sound.Path, sound.Track, sound.Start.TotalSeconds + Math.Max(0, global - sound.Position.TotalSeconds), revision, sound);
    }
    private IEnumerable<AssemblySound> PlacedSoundsNear(double global) => AssemblySounds.Where(s => s.Detached && !s.Removed &&
        s.Position.TotalSeconds <= global + 1 && s.Finish.TotalSeconds > global);

    private async Task OpenAudioPlayerAsync(string path, int track, double start, int revision, AssemblySound? sound)
    {
            if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision || _assemblyVlc is not { } vlc) return;
            var media = new Media(vlc, new Uri(path));
            media.AddOption(":no-video");
            media.AddOption(":start-paused");
            media.AddOption($":audio-track={track}");
            media.AddOption(":start-time=" + start.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
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
                for (int wait = 0; wait < 10 && tracks.Length == 0; wait++)
                {
                    await Task.Delay(20, _lifetime.Token);
                    if (revision != _assemblyLoadRevision) return;
                    tracks = player.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
                }
                if (track >= tracks.Length) throw new InvalidOperationException("Звуковая дорожка недоступна для предпросмотра.");
                if (player.AudioTrack != tracks[track].Id) player.SetAudioTrack(tracks[track].Id);
                if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision) return;
                _audioPlayers.Add((player, media, track)); keep = true;
                if (sound is not null) _placedAudioPlayers[player] = sound;
                player.SetRate((float)_forwardPreviewRate);
            }
            finally { if (!keep) await Task.Run(() => { player.Stop(); player.Dispose(); media.Dispose(); }); }
    }
    private async Task EnsurePlacedAudioAsync(double global)
    {
        if (_openingPlacedAudio || _lifetime.IsCancellationRequested) return;
        var required = PlacedSoundsNear(global).ToArray();
        if (required.Length == _placedAudioPlayers.Count && required.All(s => _placedAudioPlayers.Values.Any(p => p.Owner == s.Owner && p.Event == s.Event))) return;
        _openingPlacedAudio = true;
        int revision = _assemblyLoadRevision;
        try
        {
            await _assemblyPlayback.WaitAsync(_lifetime.Token);
            try
            {
                if (revision != _assemblyLoadRevision || _lifetime.IsCancellationRequested) return;
                var removed = _audioPlayers.Where(p => _placedAudioPlayers.TryGetValue(p.Player, out var sound) && !required.Any(s => s.Owner == sound.Owner && s.Event == sound.Event)).ToArray();
                foreach (var audio in removed)
                {
                    _audioPlayers.Remove(audio); _placedAudioPlayers.Remove(audio.Player); _activePlacedAudio.Remove(audio.Player);
                    _audioRestartPositions.Remove(audio.Player);
                    await Task.Run(() => { audio.Player.Stop(); audio.Player.Dispose(); audio.Media.Dispose(); });
                }
                foreach (var sound in required)
                    if (!_placedAudioPlayers.Values.Any(p => p.Owner == sound.Owner && p.Event == sound.Event))
                        await OpenAudioPlayerAsync(sound.Path, sound.Track, sound.Start.TotalSeconds + Math.Max(0, global - sound.Position.TotalSeconds), revision, sound);
            }
            finally { _assemblyPlayback.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) Log.Warn("Editor", "Отдельный звук: " + ex.Message); }
        finally { _openingPlacedAudio = false; }
    }
    private void SyncPreviewAudio(double source, bool seek = false)
    {
        if (_lifetime.IsCancellationRequested || _assemblyOpening || _previewIndex < 0 || _previewIndex >= _segments.Count) return;
        var part = _segments[_previewIndex];
        double global = _gapPosition is not null ? Math.Clamp(GapTime, 0, LeadingGapEnd) :
            _tailPosition is not null ? TailTime : VideoTimeline.Offset(_segments, _previewIndex) + source - part.Start.TotalSeconds;
        if (_wantAssemblyPlaying) _ = EnsurePlacedAudioAsync(global);
        bool pause = !_wantAssemblyPlaying || _assemblyScrubbing || _waitingAssemblyFrame || _reversePreviewRate > 0;
        foreach (var audio in _audioPlayers.ToArray())
        {
            if (_lifetime.IsCancellationRequested || !_audioPlayers.Contains(audio)) break;
            try
            {
                double audioSource = source;
                bool audioPause = pause, audioSeek = seek;
                double gain;
                double end = part.End.TotalSeconds;
                if (_placedAudioPlayers.TryGetValue(audio.Player, out var placed))
                {
                    if (placed.Owner >= _segments.Count || placed.Event >= _segments[placed.Owner].AudioEvents.Count) { audio.Player.Mute = true; continue; }
                    var edit = _segments[placed.Owner].AudioEvents[placed.Event];
                    double at = edit.Position!.Value.TotalSeconds;
                    audioSource = edit.Start.TotalSeconds + global - at; end = edit.End.TotalSeconds;
                    bool active = global >= at && audioSource < end;
                    gain = active ? edit.Gain : 0;
                    audioPause |= !active;
                    if (active && _activePlacedAudio.Add(audio.Player)) audioSeek = true;
                    else if (!active) _activePlacedAudio.Remove(audio.Player);
                }
                else
                {
                    bool outsideClip = _tailPosition is not null || _gapPosition is not null;
                    gain = outsideClip || part.AudioDetached ? 0 : AssemblyAudio.GainAt(part, audio.Track, source);
                    audioPause |= outsideClip;
                }
                gain *= AudioTrackState(audio.Track).Gain(AnyAudioSolo);
                if (gain > 0 && audioSource + .1 < end && (audioSeek || _wantAssemblyPlaying) && audio.Player.State == VLCState.Ended &&
                    (!_audioRestartPositions.TryGetValue(audio.Player, out double previousRestart) || audioSource < previousRestart - .1))
                {
                    _audioRestartPositions[audio.Player] = audioSource;
                    _ = RestartPreviewAudioAsync(audio, audioSource);
                    continue;
                }
                // VLC's volume control is cubic; editor/export gains are linear amplitudes.
                audio.Player.Volume = (int)Math.Round(Math.Cbrt(gain) * 100);
                audio.Player.Mute = MutePreview.IsChecked == true || gain <= 0 || _reversePreviewRate > 0;
                // Muted events must keep their clock running; disabling tracks/repeated seeks rebuild audio output.
                if (audioSeek && !_continuousPlacedAudio.Remove(audio.Player))
                    audio.Player.Time = (long)(Math.Max(0, audioSource) * 1000);
                audio.Player.SetPause(audioPause);
            }
            catch (ObjectDisposedException) { }
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
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) Log.Warn("Editor", "Возврат к звуку: " + ex.Message); }
        finally { _restartingAudio.Remove(audio.Player); }
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => Dialogs.Say("Клавиши монтажа",
        "Space / Enter — воспроизведение и пауза\nS — разрезать в позиции курсора\nU / G — отделить / связать видео и звук\nПосле U тяните видео по шкале — звук останется на месте, пустой участок станет чёрным\nZ — mute дорожки, X — solo дорожки\nDelete — удалить выбранное; U отделяет звук для удаления без видео\nCtrl+Z — отменить; Ctrl+Y / Ctrl+Shift+Z — повторить\nJ / K / L — назад / пауза / вперёд\nAlt+← / → — на один кадр; Home / End — в начало / конец\n+ / −, Ctrl+колесо — масштаб; Shift+колесо — прокрутка\nКолесо над звуковым фрагментом — его громкость\nCtrl+O — добавить видео; Ctrl+S — сохранить ролик");
}
