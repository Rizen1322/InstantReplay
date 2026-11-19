using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Aura.Core.Tools;
using Aura.Core.Audio;

namespace Aura.Controls;

/// <summary>Связанные видео и звук на одной монтажной шкале.</summary>
public sealed class AssemblyTimeline : FrameworkElement
{
    private IReadOnlyList<VideoSegment> _parts = [];
    private double _scale = 40, _position;
    private int _selected = -1, _dragIndex = -1;
    private Point _pressed;
    private VideoSegment? _trim;
    private IReadOnlyList<AssemblySound> _sounds = [];
    private AssemblySound? _audioGesture;
    private AudioEvent? _audioTrim;
    private VideoTimeline.MovePlan? _dragPlan;
    private IReadOnlyList<AssemblySound>? _dragPlanSounds;
    private double? _snapLine;
    private const double SnapPixels = 8;
    private enum Gesture { None, Cursor, VideoMove, Left, Right, AudioSelect, AudioMove, AudioLeft, AudioRight }
    private Gesture _gesture;
    private bool _moved;
    public const double VideoTop = 36, AudioTop = 130, AudioLaneHeight = 70;
    private double LaneBottom => AudioTop + AudioTracks * AudioLaneHeight;
    public int AudioTracks { get; private set; }
    public int SelectedAudioTrack { get; set; } = -1;
    public double SelectedAudioSeconds { get; set; }
    public int SelectedAudioEvent { get; set; } = -1;
    public event Action<int, int, AudioEvent>? EditAudio;
    public Func<string, int>? AudioTrackCount { get; set; }
    public Func<string, int, AudioWaveform?>? Waveform { get; set; }
    public Func<int, AssemblyAudioTrack>? TrackState { get; set; }
    public bool AnySolo { get; set; }
    public event Action<int, int, double>? SelectAudio;
    public event Action<int, int, double, int>? AdjustAudio;
    public Func<string, ImageSource?>? Thumbnail { get; set; }
    public Func<string, (double Duration, double Fps)>? SourceInfo { get; set; }
    public event Action<int>? SelectClip;
    public event Action<double>? Seek;
    public event Action<double>? Scrub;
    public event Action<int, int>? Reorder;
    public event Action<int, TimeSpan>? PlaceClip;
    public event Action<VideoSegment[], int>? ArrangeClips;
    public event Action<int, VideoSegment>? TrimClip;
    public event Action<bool>? InteractionEnded;
    public double VisibleLeft { get; set; }
    public double VisibleWidth { get; set; } = 1200;
    public double PixelsPerSecond { get => _scale; set { _scale = Math.Clamp(value, .01, 400); Resize(); } }
    public double Position { get => _position; set { _position = Math.Max(0, value); InvalidateVisual(); } }
    public int SelectedIndex { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public bool EditingEnabled { get; set; } = true;
    public bool SnapToFrames { get; set; } = true;
    public bool IsInteracting => _gesture != Gesture.None;

    public AssemblyTimeline()
    {
        Focusable = true; ClipToBounds = true; Height = 230;
        System.Windows.Automation.AutomationProperties.SetName(this, "Монтажная шкала. Видео и связанный звук");
    }

    public void SetSegments(IReadOnlyList<VideoSegment> parts)
    {
        _parts = parts;
        _sounds = AssemblyAudio.Sounds(parts, path => AudioTrackCount?.Invoke(path) ?? 0).Where(s => !s.Removed).ToArray();
        AudioTracks = Math.Max(parts.Count == 0 ? 0 : parts.Max(p => AudioTrackCount?.Invoke(p.Path) ?? 0), _sounds.Count == 0 ? 0 : _sounds.Max(s => s.Track + 1));
        Height = Math.Max(230, LaneBottom + 18);
        Resize();
    }
    private void Resize()
    {
        var parts = _dragPlan?.Parts ?? _parts;
        var sounds = _dragPlanSounds ?? _sounds;
        double length = 0;
        for (int i = 0; i < parts.Count; i++)
            length += parts[i].Gap.TotalSeconds + parts[i].Duration.TotalSeconds;
        if (sounds.Count > 0) length = Math.Max(length, sounds.Max(s => s.Finish.TotalSeconds));
        if (_audioTrim is { Position: TimeSpan at }) length = Math.Max(length, (at + _audioTrim.End - _audioTrim.Start).TotalSeconds);
        if (_trim is not null && _dragIndex >= 0 && _gesture == Gesture.Right && _dragIndex < parts.Count)
            length += _trim.Duration.TotalSeconds - parts[_dragIndex].Duration.TotalSeconds;
        Width = Math.Max(VisibleWidth, length * _scale + 40);
        InvalidateVisual();
    }
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
    // Кисти и перья постоянных цветов создаются и замораживаются один раз: OnRender
    // выполняется на каждый кадр перетаскивания, новых Freezable там быть не должно.
    private static readonly Brush Canvas = FrozenBrush(Color.FromRgb(18, 21, 28));
    private static readonly Brush Video = FrozenBrush(Color.FromRgb(45, 74, 101));
    private static readonly Brush Audio = FrozenBrush(Color.FromRgb(44, 85, 72));
    private static readonly Brush AlternateAudio = FrozenBrush(Color.FromRgb(66, 57, 93));
    private static readonly Brush ClipCaption = FrozenBrush(Color.FromArgb(210, 22, 31, 43));
    private static readonly Brush Highlight = FrozenBrush(Color.FromRgb(86, 190, 255));
    private static readonly Brush TextColor = FrozenBrush(Color.FromRgb(231, 236, 244));
    private static readonly Brush Muted = FrozenBrush(Color.FromRgb(144, 156, 176));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(41, 47, 59), 1);
    private static readonly Pen GapPen = FrozenPen(Color.FromRgb(58, 66, 80), 1, DashStyles.Dash);
    private static readonly Pen ClipBorder = FrozenPen(Color.FromRgb(75, 93, 112), 1);
    private static readonly Pen SelectedBorder = FrozenPen(Color.FromRgb(86, 190, 255), 2);
    private static readonly Pen AudioBorder = FrozenPen(Color.FromRgb(61, 102, 88), 1);
    private static readonly Pen DetachedAudioBorder = FrozenPen(Color.FromRgb(144, 156, 176), 1);
    private static readonly Pen WavePenEven = FrozenPen(Color.FromRgb(134, 214, 180), 1);
    private static readonly Pen WavePenOdd = FrozenPen(Color.FromRgb(184, 163, 239), 1);
    private static readonly Pen DetachedWavePen = FrozenPen(Color.FromRgb(154, 213, 192), 1);
    private static readonly Pen SnapPen = FrozenPen(Color.FromRgb(255, 209, 102), 1.5);
    private static readonly Pen CursorPen = FrozenPen(Colors.White, 1.5);
    private static readonly Typeface Font = new("Segoe UI");

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private static Pen FrozenPen(Color color, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        if (dash is not null) pen.DashStyle = dash;
        pen.Freeze();
        return pen;
    }

    private static string FormatDb(double gain) => gain <= .001 ? "−∞ dB" : $"{20 * Math.Log10(gain):0.#} dB";

    private void Text(DrawingContext dc, string text, double x, double y, double maxWidth, Brush? color = null, double size = 11)
    {
        if (maxWidth < 5) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, color ?? TextColor, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(5, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, new Point(x, y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Canvas, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double step = new[] { .1, .2, .5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600 }.FirstOrDefault(s => s * _scale >= 85);
        if (step <= 0) step = 7200;
        double endX = Math.Min(ActualWidth, VisibleLeft + VisibleWidth + 100);
        for (double second = Math.Floor(VisibleLeft / _scale / step) * step; second * _scale < endX; second += step)
        {
            double x = second * _scale;
            dc.DrawLine(GridPen, new Point(x, 24), new Point(x, LaneBottom));
            Text(dc, TimeSpan.FromSeconds(second).ToString(second < 3600 ? @"mm\:ss\.f" : @"hh\:mm\:ss"), x + 5, 4, 80, Muted);
        }
        for (int track = 0; track <= AudioTracks; track++)
            dc.DrawLine(GridPen, new Point(0, AudioTop + track * AudioLaneHeight - 4), new Point(ActualWidth, AudioTop + track * AudioLaneHeight - 4));
        var parts = _dragPlan?.Parts ?? _parts;
        var sounds = _dragPlanSounds ?? _sounds;
        int selectedIndex = _dragPlan?.TargetIndex ?? _selected;
        double gapOffset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var gap = parts[i].Gap;
            gapOffset += gap.TotalSeconds;
            if (gap <= TimeSpan.Zero) { gapOffset += parts[i].Duration.TotalSeconds; continue; }
            double gapLeft = (gapOffset - gap.TotalSeconds) * _scale;
            double gapWidth = gap.TotalSeconds * _scale;
            gapOffset += parts[i].Duration.TotalSeconds;
            if (gapLeft + gapWidth < VisibleLeft || gapLeft > endX) continue;
            dc.DrawRectangle(null, GapPen, new Rect(gapLeft + 1, VideoTop, Math.Max(1, gapWidth - 2), 84));
            Text(dc, "Пусто", gapLeft + 8, VideoTop + 34, Math.Max(1, gapWidth - 16), Muted, 10);
        }
        int lastDensePixel = int.MinValue;
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = i == _dragIndex && _trim is not null ? _trim : parts[i];
            double gap = parts[i].Gap.TotalSeconds;
            offset += gap;
            double x = offset * _scale;
            double width = Math.Max(1, part.Duration.TotalSeconds * _scale);
            if (i == _dragIndex && _gesture == Gesture.Left && _trim is not null)
                x += (part.Start - parts[i].Start).TotalSeconds * _scale;
            offset += i == _dragIndex && _gesture == Gesture.Left && _trim is not null
                ? parts[i].Duration.TotalSeconds : part.Duration.TotalSeconds;
            if (x + width < VisibleLeft || x > endX) continue;
            if (width < 2)
            {
                int pixel = (int)x;
                if (pixel == lastDensePixel) continue;
                lastDensePixel = pixel;
                dc.DrawRectangle(Video, null, new Rect(x, VideoTop, 1, 84));
                for (int track = 0; track < AudioTracks; track++)
                    dc.DrawRectangle(Audio, null, new Rect(x, AudioTop + track * AudioLaneHeight, 1, AudioLaneHeight - 9));
                continue;
            }
            bool selected = i == selectedIndex;
            bool selectedVideo = selected && (SelectedAudioTrack < 0 || part.AudioLinked);
            var border = selectedVideo ? SelectedBorder : ClipBorder;
            dc.DrawRoundedRectangle(Video, border, new Rect(x + 1, VideoTop, Math.Max(1, width - 2), 84), 3, 3);
            dc.PushClip(new RectangleGeometry(new Rect(x + 3, VideoTop + 2, Math.Max(1, width - 6), 80)));
            if (Thumbnail?.Invoke(part.Path) is { } image)
                for (double tile = x + 3 + Math.Max(0, Math.Floor((VisibleLeft - x) / 96)) * 96; tile < Math.Min(x + width, endX + 96); tile += 96)
                    dc.DrawImage(image, new Rect(tile, VideoTop + 2, 94, 53));
            dc.DrawRectangle(ClipCaption, null, new Rect(x + 3, VideoTop + 56, Math.Max(1, width - 6), 25));
            Text(dc, part.Name, x + 9, VideoTop + 60, width - 18);
            dc.Pop();
            for (int track = 0; !part.AudioDetached && track < (AudioTrackCount?.Invoke(part.Path) ?? 0); track++)
            {
                var state = TrackState?.Invoke(track) ?? new AssemblyAudioTrack();
                double row = AudioTop + track * AudioLaneHeight;
                foreach (var audio in AssemblyAudio.Events(part, track))
                {
                    double ax = x + (audio.Start - part.Start).TotalSeconds * _scale;
                    double aw = Math.Max(1, (audio.End - audio.Start).TotalSeconds * _scale);
                    if (ax + aw < VisibleLeft || ax > endX) continue;
                    bool active = selected && SelectedAudioTrack == track &&
                        SelectedAudioSeconds >= audio.Start.TotalSeconds && SelectedAudioSeconds < audio.End.TotalSeconds;
                    double gain = audio.Gain * state.Gain(AnySolo) * (part.AudioTrack >= 0 && part.AudioTrack != track ? 0 : 1);
                    var audioBorder = active ? SelectedBorder : AudioBorder;
                    dc.PushOpacity(gain <= 0 ? .4 : 1);
                    dc.DrawRoundedRectangle(audio.Removed ? Canvas : track % 2 == 0 ? Audio : AlternateAudio, audioBorder,
                        new Rect(ax + 1, row, Math.Max(1, aw - 2), AudioLaneHeight - 9), 3, 3);
                    dc.PushClip(new RectangleGeometry(new Rect(ax + 3, row + 2, Math.Max(1, aw - 6), AudioLaneHeight - 13)));
                    var wave = Waveform?.Invoke(part.Path, track);
                    double center = row + 39;
                    dc.DrawLine(GridPen, new Point(ax + 3, center), new Point(ax + aw - 3, center));
                    if (wave is not null && !audio.Removed)
                    {
                        var wavePen = track % 2 == 0 ? WavePenEven : WavePenOdd;
                        for (double px = Math.Max(ax + 4, VisibleLeft); px < Math.Min(ax + aw - 3, endX); px += 2)
                        {
                            double source = part.Start.TotalSeconds + (px - x) / _scale;
                            double peak = wave.Peak(source, source + 2 / _scale);
                            double amplitude = peak > 0.0005 ? Math.Max(1.0, Math.Pow(Math.Min(1, peak * Math.Max(.12, gain)), 0.7) * 17) : 0;
                            if (amplitude > 0) dc.DrawLine(wavePen, new Point(px, center - amplitude), new Point(px, center + amplitude));
                        }
                    }
                    bool silent = audio.Muted || state.Muted || AnySolo && !state.Solo || part.AudioTrack >= 0 && part.AudioTrack != track;
                    string label = audio.Removed ? "Удалено" : silent ? "Без звука" :
                        Math.Abs(audio.Volume - 1) >= .01 && Math.Abs(state.Volume - 1) >= .01
                            ? $"{part.Name}  ·  {FormatDb(audio.Volume)} (итог {FormatDb(gain)})"
                            : $"{part.Name}  ·  {FormatDb(gain)}";
                    Text(dc, label, ax + 8, row + 5, aw - 16, size: 10);
                    dc.Pop(); dc.Pop();
                }
            }
            if (selectedVideo && width > 12)
            {
                dc.DrawRectangle(Highlight, null, new Rect(x + 1, VideoTop + 1, 4, 82));
                dc.DrawRectangle(Highlight, null, new Rect(x + width - 5, VideoTop + 1, 4, 82));
            }
        }
        foreach (var sound in sounds.Where(s => s.Detached))
        {
            var audio = _audioGesture == sound && _audioTrim is not null ? _audioTrim : parts[sound.Owner].AudioEvents[sound.Event];
            double ax = audio.Position!.Value.TotalSeconds * _scale, aw = Math.Max(1, (audio.End - audio.Start).TotalSeconds * _scale);
            if (ax + aw < VisibleLeft || ax > endX) continue;
            var state = TrackState?.Invoke(sound.Track) ?? new AssemblyAudioTrack();
            double gain = audio.Gain * state.Gain(AnySolo), row = AudioTop + sound.Track * AudioLaneHeight;
            bool active = sound.Owner == selectedIndex && sound.Event == SelectedAudioEvent;
            dc.PushOpacity(gain <= 0 ? .4 : 1);
            dc.DrawRoundedRectangle(sound.Track % 2 == 0 ? Audio : AlternateAudio, active ? SelectedBorder : DetachedAudioBorder, new Rect(ax + 1, row, Math.Max(1, aw - 2), AudioLaneHeight - 9), 3, 3);
            dc.PushClip(new RectangleGeometry(new Rect(ax + 3, row + 2, Math.Max(1, aw - 6), AudioLaneHeight - 13)));
            var wave = Waveform?.Invoke(sound.Path, sound.Track);
            double center = row + 39;
            dc.DrawLine(GridPen, new(ax + 3, center), new(ax + aw - 3, center));
            if (wave is not null && !audio.Removed)
            {
                var pen = DetachedWavePen;
                for (double px = Math.Max(ax + 4, VisibleLeft); px < Math.Min(ax + aw - 3, endX); px += 2)
                {
                    double source = audio.Start.TotalSeconds + (px - ax) / _scale;
                    double peak = wave.Peak(source, source + 2 / _scale);
                    double amplitude = peak > 0.0005 ? Math.Max(1.0, Math.Pow(Math.Min(1, peak * Math.Max(.12, gain)), 0.7) * 17) : 0;
                    if (amplitude > 0) dc.DrawLine(pen, new(px, center - amplitude), new(px, center + amplitude));
                }
            }
            bool silent = audio.Muted || state.Muted || AnySolo && !state.Solo;
            string label = audio.Removed ? "Удалено" : silent ? "Без звука" :
                $"{System.IO.Path.GetFileName(sound.Path)} · отдельно · {FormatDb(gain)}";
            Text(dc, label, ax + 8, row + 5, aw - 16, size: 10);
            dc.Pop(); dc.Pop();
        }
        if (_snapLine is double snapSec)
            dc.DrawLine(SnapPen, new Point(snapSec * _scale, 24), new Point(snapSec * _scale, LaneBottom + 4));
        double cursor = _position * _scale;
        dc.DrawLine(CursorPen, new Point(cursor, 24), new Point(cursor, LaneBottom + 5));
        var marker = new StreamGeometry();
        using (var g = marker.Open()) { g.BeginFigure(new Point(cursor - 6, 22), true, true); g.LineTo(new Point(cursor + 6, 22), true, false); g.LineTo(new Point(cursor, 30), true, false); }
        dc.DrawGeometry(Brushes.White, null, marker);
        if (parts.Count == 0) Text(dc, "Перетащите сюда видео из медиатеки или Проводника", 24, 75, Math.Max(200, VisibleWidth - 48), Muted, 14);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!EditingEnabled) return;
        Focus();
        BeginGesture(e.GetPosition(this));
        CaptureMouse(); e.Handled = true;
    }
    internal void BeginGesture(Point point)
    {
        _audioGesture = null; _audioTrim = null; _dragPlan = null; _dragPlanSounds = null; _snapLine = null;
        _pressed = point; _moved = false; _trim = null;
        double total = Math.Max(_parts.Sum(p => p.Gap.TotalSeconds + p.Duration.TotalSeconds), _sounds.Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max());
        if (HitAudio(point) is { Detached: true } sound)
        {
            _selected = _dragIndex = sound.Owner;
            SelectedAudioTrack = sound.Track; SelectedAudioEvent = sound.Event;
            SelectedAudioSeconds = Math.Clamp(sound.Start.TotalSeconds + point.X / _scale - sound.Position.TotalSeconds,
                sound.Start.TotalSeconds, sound.End.TotalSeconds);
            if (!_parts[sound.Owner].AudioLinked || sound.Path != _parts[sound.Owner].Path)
            {
                _audioGesture = sound; _audioTrim = _parts[sound.Owner].AudioEvents[sound.Event];
                double audioLeft = sound.Position.TotalSeconds * _scale, audioRight = sound.Finish.TotalSeconds * _scale;
                double audioWidth = audioRight - audioLeft;
                double audioGrip = audioWidth < 18 ? 0 : Math.Min(6, audioWidth / 4);
                _gesture = point.X - audioLeft <= audioGrip ? Gesture.AudioLeft : audioRight - point.X <= audioGrip ? Gesture.AudioRight : Gesture.AudioMove;
                SelectAudio?.Invoke(sound.Owner, sound.Track, SelectedAudioSeconds); InvalidateVisual(); return;
            }
            Position = Math.Clamp(point.X / _scale, 0, total);
            SelectClip?.Invoke(sound.Owner);
            SelectAudio?.Invoke(sound.Owner, sound.Track, SelectedAudioSeconds);
            _gesture = Gesture.AudioSelect;
            InvalidateVisual(); return;
        }
        if (point.Y >= AudioTop && HitAudio(point) is null)
        {
            _gesture = Gesture.Cursor; Position = Math.Max(0, point.X / _scale);
            Scrub?.Invoke(Position); return;
        }
        _dragIndex = VideoTimeline.ClipAt(_parts, point.X / _scale);
        if (point.Y < VideoTop || point.Y > LaneBottom || _dragIndex < 0)
        {
            _gesture = Gesture.Cursor;
            Position = Math.Clamp(point.X / _scale, 0, total);
            Scrub?.Invoke(Position); return;
        }
        _selected = _dragIndex;
        SelectedAudioEvent = -1;
        int audioTrack = (int)Math.Floor((point.Y - AudioTop) / AudioLaneHeight);
        SelectedAudioTrack = point.Y >= AudioTop && audioTrack < (AudioTrackCount?.Invoke(_parts[_dragIndex].Path) ?? 0) ? audioTrack : -1;
        double clickedTime = Math.Clamp(point.X / _scale, 0, total);
        SelectedAudioSeconds = Math.Clamp(_parts[_dragIndex].Start.TotalSeconds + clickedTime - VideoTimeline.Offset(_parts, _dragIndex),
            _parts[_dragIndex].Start.TotalSeconds, _parts[_dragIndex].End.TotalSeconds);
        SelectClip?.Invoke(_dragIndex);
        SelectAudio?.Invoke(_dragIndex, SelectedAudioTrack, SelectedAudioSeconds);
        double left = VideoTimeline.Offset(_parts, _dragIndex) * _scale;
        double right = left + _parts[_dragIndex].Duration.TotalSeconds * _scale;
        double width = right - left;
        double grip = width < 18 ? 0 : Math.Min(6, width / 4);
        if (SelectedAudioTrack >= 0)
        {
            Position = clickedTime;
            _gesture = Gesture.AudioSelect;
        }
        else
            _gesture = point.X - left <= grip ? Gesture.Left : right - point.X <= grip ? Gesture.Right : Gesture.VideoMove;
        InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_gesture != Gesture.None) { UpdateGesture(point); e.Handled = true; return; }
        if (HitAudio(point) is { Detached: true } sound)
        {
            double audioLeft = sound.Position.TotalSeconds * _scale, audioRight = sound.Finish.TotalSeconds * _scale;
            double audioWidth = audioRight - audioLeft;
            double audioGrip = audioWidth < 18 ? 0 : Math.Min(6, audioWidth / 4);
            Cursor = point.X - audioLeft < audioGrip || audioRight - point.X < audioGrip ? Cursors.SizeWE : Cursors.SizeAll;
            return;
        }
        int index = VideoTimeline.ClipAt(_parts, point.X / _scale);
        if (point.Y < VideoTop || index < 0) { Cursor = Cursors.IBeam; return; }
        double left = VideoTimeline.Offset(_parts, index) * _scale, right = left + _parts[index].Duration.TotalSeconds * _scale;
        double width = right - left;
        double grip = width < 18 ? 0 : Math.Min(6, width / 4);
        Cursor = point.Y < AudioTop
            ? grip > 0 && point.X < right && (point.X - left < grip || right - point.X < grip) ? Cursors.SizeWE : Cursors.SizeAll
            : Cursors.Hand;
    }
    private List<double> CollectSnapTargets(int excludeClip = -1, AssemblySound? excludeSound = null, bool excludeRelinkedOfClip = false)
    {
        var targets = new List<double>(2 + _parts.Count * 2 + _sounds.Count * 2) { 0, _position };
        double offset = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            offset += _parts[i].Gap.TotalSeconds;
            double end = offset + _parts[i].Duration.TotalSeconds;
            if (i != excludeClip)
            {
                targets.Add(offset);
                targets.Add(end);
            }
            offset = end;
        }
        for (int i = 0; i < _sounds.Count; i++)
        {
            var s = _sounds[i];
            if (!s.Detached || s == excludeSound) continue;
            if (excludeRelinkedOfClip && excludeClip >= 0 && s.Owner == excludeClip && _parts[excludeClip].AudioLinked &&
                string.Equals(s.Path, _parts[excludeClip].Path, StringComparison.OrdinalIgnoreCase)) continue;
            targets.Add(s.Position.TotalSeconds);
            targets.Add(s.Finish.TotalSeconds);
        }
        return targets;
    }
    internal void UpdateGesture(Point point)
    {
        _moved |= Math.Abs(point.X - _pressed.X) >= 5;
        double snapTol = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0 : SnapPixels / _scale;
        if (_audioGesture is { } sound && _gesture is Gesture.AudioMove or Gesture.AudioLeft or Gesture.AudioRight)
        {
            var original = _parts[sound.Owner].AudioEvents[sound.Event];
            double delta = (point.X - _pressed.X) / _scale;
            var info = SourceInfo?.Invoke(sound.Path) ?? (sound.End.TotalSeconds, 30.0);
            var targets = CollectSnapTargets(excludeSound: sound);
            if (_gesture == Gesture.AudioMove)
            {
                double rawPos = Math.Max(0, sound.Position.TotalSeconds + delta);
                var (snappedPos, guide) = VideoTimeline.SnapRange(rawPos, sound.Duration.TotalSeconds, targets, snapTol, minStart: 0);
                double newPos;
                if (guide is not null) { _snapLine = guide; newPos = snappedPos; }
                else
                {
                    _snapLine = null;
                    double snappedDelta = SnapToFrames ? Math.Round(delta * info.Item2) / info.Item2 : delta;
                    newPos = Math.Max(0, sound.Position.TotalSeconds + snappedDelta);
                }
                _audioTrim = original with { Position = TimeSpan.FromSeconds(newPos) };
            }
            else if (_gesture == Gesture.AudioLeft)
            {
                double minDelta = Math.Max(-sound.Position.TotalSeconds, -sound.Start.TotalSeconds);
                double maxDelta = Math.Max(minDelta, sound.Duration.TotalSeconds - .05);
                double rawEdge = sound.Position.TotalSeconds + delta;
                var (snappedEdge, guide) = VideoTimeline.SnapEdge(rawEdge, targets, snapTol, sound.Position.TotalSeconds + minDelta, sound.Position.TotalSeconds + maxDelta);
                if (guide is not null) { _snapLine = guide; delta = snappedEdge - sound.Position.TotalSeconds; }
                else
                {
                    _snapLine = null;
                    double snappedDelta = SnapToFrames ? Math.Round(delta * info.Item2) / info.Item2 : delta;
                    delta = Math.Clamp(snappedDelta, minDelta, maxDelta);
                }
                _audioTrim = original with { Start = original.Start + TimeSpan.FromSeconds(delta), Position = original.Position + TimeSpan.FromSeconds(delta) };
            }
            else
            {
                double minRight = sound.Position.TotalSeconds + .05;
                double maxRight = Math.Max(minRight, sound.Position.TotalSeconds + (info.Item1 - original.Start.TotalSeconds));
                double rawRight = sound.Position.TotalSeconds + sound.Duration.TotalSeconds + delta;
                var (snappedRight, guide) = VideoTimeline.SnapEdge(rawRight, targets, snapTol, minRight, maxRight);
                double newEnd;
                if (guide is not null) { _snapLine = guide; newEnd = original.Start.TotalSeconds + (snappedRight - sound.Position.TotalSeconds); }
                else
                {
                    _snapLine = null;
                    double snappedDelta = SnapToFrames ? Math.Round(delta * info.Item2) / info.Item2 : delta;
                    newEnd = Math.Clamp(original.End.TotalSeconds + snappedDelta, original.Start.TotalSeconds + .05, info.Item1);
                }
                _audioTrim = original with { End = TimeSpan.FromSeconds(newEnd) };
            }
            Resize();
        }
        else if (_gesture == Gesture.Cursor || _gesture == Gesture.AudioSelect && _moved)
        {
            Position = Math.Clamp(point.X / _scale, 0, Math.Max(_parts.Sum(p => p.Gap.TotalSeconds + p.Duration.TotalSeconds), _sounds.Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max()));
            if (_gesture == Gesture.AudioSelect && _dragIndex >= 0 && _dragIndex < _parts.Count)
                SelectedAudioSeconds = Math.Clamp(_parts[_dragIndex].Start.TotalSeconds + Position - VideoTimeline.Offset(_parts, _dragIndex),
                    _parts[_dragIndex].Start.TotalSeconds, _parts[_dragIndex].End.TotalSeconds);
            Scrub?.Invoke(Position);
        }
        else if (_gesture == Gesture.VideoMove && _dragIndex >= 0 && _moved)
        {
            var original = _parts[_dragIndex];
            var info = SourceInfo?.Invoke(original.Path) ?? (original.End.TotalSeconds, 30.0);
            var targets = CollectSnapTargets(excludeClip: _dragIndex, excludeRelinkedOfClip: true);
            _dragPlan = VideoTimeline.PlanMove(_parts, _dragIndex, (point.X - _pressed.X) / _scale, Math.Max(0, point.X / _scale),
                info.Item2, SnapToFrames, targets, snapTol);
            _snapLine = _dragPlan.SnapGuide;
            _dragPlanSounds = AssemblyAudio.Sounds(_dragPlan.Parts, path => AudioTrackCount?.Invoke(path) ?? 0).Where(s => !s.Removed).ToArray();
            Resize();
        }
        else if (_dragIndex >= 0 && _gesture is Gesture.Left or Gesture.Right && _moved)
        {
            var original = _parts[_dragIndex];
            var info = SourceInfo?.Invoke(original.Path) ?? (original.End.TotalSeconds, 30.0);
            double fps = double.IsFinite(info.Item2) && info.Item2 > 0 ? info.Item2 : 30.0;
            double minimum = Math.Max(0.05, 1 / fps);
            double clipOffset = VideoTimeline.Offset(_parts, _dragIndex);
            double rawDelta = (point.X - _pressed.X) / _scale;
            var targets = CollectSnapTargets(excludeClip: _dragIndex);
            if (_gesture == Gesture.Left)
            {
                double minVisual = clipOffset - original.Start.TotalSeconds;
                double maxVisual = clipOffset + (original.End.TotalSeconds - original.Start.TotalSeconds - minimum);
                var (snappedEdge, guide) = VideoTimeline.SnapEdge(clipOffset + rawDelta, targets, snapTol, minVisual, maxVisual);
                if (guide is not null) { _snapLine = guide; _trim = VideoTimeline.Trim(original, true, snappedEdge - clipOffset, info.Item1, fps, snap: false); }
                else { _snapLine = null; _trim = VideoTimeline.Trim(original, true, rawDelta, info.Item1, fps, SnapToFrames); }
            }
            else
            {
                double maxRightSource = Math.Max(original.Start.TotalSeconds + minimum, Math.Max(info.Item1, original.End.TotalSeconds));
                double minVisual = clipOffset + minimum;
                double maxVisual = clipOffset + (maxRightSource - original.Start.TotalSeconds);
                double origRight = clipOffset + original.Duration.TotalSeconds;
                var (snappedEdge, guide) = VideoTimeline.SnapEdge(origRight + rawDelta, targets, snapTol, minVisual, maxVisual);
                if (guide is not null) { _snapLine = guide; _trim = VideoTimeline.Trim(original, false, snappedEdge - origRight, info.Item1, fps, snap: false); }
                else { _snapLine = null; _trim = VideoTimeline.Trim(original, false, rawDelta, info.Item1, fps, SnapToFrames); }
            }
            Resize();
            Scrub?.Invoke(Math.Max(0, point.X / _scale));
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); EndGesture(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; }
    internal void EndGesture(Point point)
    {
        if (_gesture == Gesture.None) return;
        UpdateGesture(point);
        var gesture = _gesture; var trim = _trim; int index = _dragIndex;
        var audio = _audioTrim; var sound = _audioGesture; var plan = _dragPlan;
        _gesture = Gesture.None; _trim = null; _dragIndex = -1;
        _audioTrim = null; _audioGesture = null; _dragPlan = null; _dragPlanSounds = null; _snapLine = null;
        if (sound is not null)
        {
            if (_moved && audio is not null && audio != _parts[sound.Owner].AudioEvents[sound.Event]) EditAudio?.Invoke(sound.Owner, sound.Event, audio);
            else if (!_moved) Seek?.Invoke(Math.Max(0, point.X / _scale));
        }
        else if (gesture == Gesture.Cursor || gesture == Gesture.AudioSelect || (gesture is Gesture.VideoMove or Gesture.Left or Gesture.Right) && !_moved)
            Seek?.Invoke(Math.Max(0, point.X / _scale));
        else if (gesture == Gesture.VideoMove && _moved && plan is not null)
        {
            if (_parts.Count == 1)
            {
                if (plan.Parts[0].Gap != _parts[0].Gap) PlaceClip?.Invoke(0, plan.Parts[0].Gap);
            }
            else if (ArrangeClips is not null)
            {
                bool changed = plan.Parts.Length != _parts.Count || Enumerable.Range(0, _parts.Count).Any(i => plan.Parts[i] != _parts[i]);
                if (changed) ArrangeClips.Invoke(plan.Parts, plan.TargetIndex);
            }
            else if (plan.TargetIndex != index) Reorder?.Invoke(index, plan.TargetIndex);
            else if (plan.Parts[index].Gap != _parts[index].Gap) PlaceClip?.Invoke(index, plan.Parts[index].Gap);
        }
        else if (_moved && gesture is Gesture.Left or Gesture.Right)
        {
            double fps = (SourceInfo?.Invoke(_parts[index].Path) ?? (0, 30.0)).Fps;
            double minChange = 0.5 / Math.Max(1, fps);
            bool changed = trim is not null && trim != _parts[index] &&
                (Math.Abs((trim.Start - _parts[index].Start).TotalSeconds) >= minChange ||
                 Math.Abs((trim.End - _parts[index].End).TotalSeconds) >= minChange);
            if (changed) TrimClip?.Invoke(index, trim!);
            else Seek?.Invoke(Math.Max(0, point.X / _scale));
        }
        InteractionEnded?.Invoke(true);
        Resize();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); CancelGesture(); }
    public void CancelGesture()
    {
        bool active = _gesture != Gesture.None;
        _gesture = Gesture.None; _trim = null; _dragIndex = -1;
        _audioGesture = null; _audioTrim = null; _dragPlan = null; _dragPlanSounds = null; _snapLine = null;
        if (active) InteractionEnded?.Invoke(false);
        ReleaseMouseCapture(); Resize();
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (EditingEnabled) Focus();
        var point = e.GetPosition(this);
        if (HitAudio(point) is { Detached: true } sound && EditingEnabled)
        {
            _selected = sound.Owner; SelectedAudioTrack = sound.Track; SelectedAudioEvent = sound.Event;
            SelectedAudioSeconds = sound.Start.TotalSeconds + point.X / _scale - sound.Position.TotalSeconds;
            SelectAudio?.Invoke(sound.Owner, sound.Track, SelectedAudioSeconds); InvalidateVisual(); return;
        }
        SelectedAudioEvent = -1;
        int index = VideoTimeline.ClipAt(_parts, point.X / _scale);
        if (index >= 0 && EditingEnabled)
        {
            _selected = index;
            int track = (int)Math.Floor((point.Y - AudioTop) / AudioLaneHeight);
            SelectedAudioTrack = track >= 0 && track < (AudioTrackCount?.Invoke(_parts[index].Path) ?? 0) ? track : -1;
            SelectedAudioSeconds = _parts[index].Start.TotalSeconds + point.X / _scale - VideoTimeline.Offset(_parts, index);
            SelectAudio?.Invoke(index, SelectedAudioTrack, SelectedAudioSeconds); InvalidateVisual();
        }
    }

    public bool AdjustAudioAt(Point point, int delta)
    {
        if (HitAudio(point) is { Detached: true } sound && EditingEnabled)
        {
            _selected = sound.Owner; SelectedAudioTrack = sound.Track; SelectedAudioEvent = sound.Event;
            SelectedAudioSeconds = sound.Start.TotalSeconds + point.X / _scale - sound.Position.TotalSeconds;
            SelectAudio?.Invoke(sound.Owner, sound.Track, SelectedAudioSeconds);
            AdjustAudio?.Invoke(sound.Owner, sound.Track, SelectedAudioSeconds, Math.Sign(delta)); return true;
        }
        SelectedAudioEvent = -1;
        int track = (int)Math.Floor((point.Y - AudioTop) / AudioLaneHeight);
        int index = VideoTimeline.ClipAt(_parts, point.X / _scale);
        if (!EditingEnabled || index < 0 || track < 0 || track >= (AudioTrackCount?.Invoke(_parts[index].Path) ?? 0)) return false;
        double source = _parts[index].Start.TotalSeconds + point.X / _scale - VideoTimeline.Offset(_parts, index);
        _selected = index; SelectedAudioTrack = track; SelectedAudioSeconds = source;
        SelectAudio?.Invoke(index, track, source);
        AdjustAudio?.Invoke(index, track, source, Math.Sign(delta));
        return true;
    }
    private AssemblySound? HitAudio(Point point)
    {
        int track = (int)Math.Floor((point.Y - AudioTop) / AudioLaneHeight);
        double time = point.X / _scale;
        return track < 0 ? null : _sounds
            .Where(s => s.Track == track && time >= s.Position.TotalSeconds && time < s.Finish.TotalSeconds)
            .OrderBy(s => s.Detached ? 1 : 0)
            .LastOrDefault();
    }
}
