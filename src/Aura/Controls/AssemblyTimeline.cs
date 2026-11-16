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
    private int _selected = -1, _dragIndex = -1, _insertion;
    private Point _pressed;
    private VideoSegment? _trim;
    private IReadOnlyList<AssemblySound> _sounds = [];
    private AssemblySound? _audioGesture;
    private AudioEvent? _audioTrim;
    private TimeSpan? _gapTrim;
    private enum Gesture { None, Cursor, Move, Left, Right, AudioSelect, AudioMove, AudioLeft, AudioRight, VideoPlace }
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
    public event Action<int, VideoSegment>? TrimClip;
    public event Action<double, double>? Zoom;
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
        Height = Math.Max(230, LaneBottom + 30);
        Resize();
    }
    private void Resize()
    {
        double length = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            var gap = i == _dragIndex && _gapTrim is TimeSpan dragged ? dragged : _parts[i].Gap;
            length += gap.TotalSeconds + _parts[i].Duration.TotalSeconds;
        }
        if (_sounds.Count > 0) length = Math.Max(length, _sounds.Max(s => s.Finish.TotalSeconds));
        if (_audioTrim is { Position: TimeSpan at }) length = Math.Max(length, (at + _audioTrim.End - _audioTrim.Start).TotalSeconds);
        if (_trim is not null && _dragIndex >= 0 && _gesture == Gesture.Right) length += _trim.Duration.TotalSeconds - _parts[_dragIndex].Duration.TotalSeconds;
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
    private static readonly Pen DropPen = FrozenPen(Color.FromRgb(86, 190, 255), 3);
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
        double gapOffset = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            var gap = _parts[i].Gap;
            gapOffset += gap.TotalSeconds;
            if (gap <= TimeSpan.Zero) { gapOffset += _parts[i].Duration.TotalSeconds; continue; }
            double gapLeft = (gapOffset - gap.TotalSeconds) * _scale;
            double gapWidth = gap.TotalSeconds * _scale;
            gapOffset += _parts[i].Duration.TotalSeconds;
            if (gapLeft + gapWidth < VisibleLeft || gapLeft > endX) continue;
            dc.DrawRectangle(null, GapPen, new Rect(gapLeft + 1, VideoTop, Math.Max(1, gapWidth - 2), 84));
            Text(dc, "Пусто", gapLeft + 8, VideoTop + 34, Math.Max(1, gapWidth - 16), Muted, 10);
        }
        int lastDensePixel = int.MinValue;
        double offset = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            var part = i == _dragIndex && _trim is not null ? _trim : _parts[i];
            double gap = (i == _dragIndex && _gapTrim is TimeSpan dragged ? dragged : _parts[i].Gap).TotalSeconds;
            offset += gap;
            double x = offset * _scale;
            double width = Math.Max(1, part.Duration.TotalSeconds * _scale);
            if (i == _dragIndex && _gesture == Gesture.Left && _trim is not null)
                x += (part.Start - _parts[i].Start).TotalSeconds * _scale;
            offset += i == _dragIndex && _gesture == Gesture.Left && _trim is not null
                ? _parts[i].Duration.TotalSeconds : part.Duration.TotalSeconds;
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
            bool selected = i == _selected;
            bool selectedVideo = selected && (SelectedAudioTrack < 0 || part.AudioLinked);
            var border = selectedVideo ? SelectedBorder : ClipBorder;
            dc.PushOpacity(_gesture == Gesture.Move && _moved && i == _dragIndex ? .4 : 1);
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
                            double amplitude = Math.Min(1, wave.Peak(source, source + 2 / _scale) * Math.Max(.12, gain)) * 17;
                            dc.DrawLine(wavePen, new Point(px, center - amplitude), new Point(px, center + amplitude));
                        }
                    }
                    string label = audio.Removed ? "Удалено" : audio.Muted || state.Muted ? "Без звука" :
                        $"{part.Name}  ·  {(audio.Volume * state.Volume <= .001 ? "−∞" : (20 * Math.Log10(audio.Volume * state.Volume)).ToString("0.#"))} dB";
                    Text(dc, label, ax + 8, row + 5, aw - 16, size: 10);
                    dc.Pop(); dc.Pop();
                }
            }
            if (selectedVideo && width > 12)
            {
                dc.DrawRectangle(Highlight, null, new Rect(x + 1, VideoTop + 1, 4, 82));
                dc.DrawRectangle(Highlight, null, new Rect(x + width - 5, VideoTop + 1, 4, 82));
            }
            dc.Pop();
        }
        foreach (var sound in _sounds.Where(s => s.Detached))
        {
            var audio = _audioGesture == sound && _audioTrim is not null ? _audioTrim : _parts[sound.Owner].AudioEvents[sound.Event];
            double ax = audio.Position!.Value.TotalSeconds * _scale, aw = Math.Max(1, (audio.End - audio.Start).TotalSeconds * _scale);
            if (ax + aw < VisibleLeft || ax > endX) continue;
            var state = TrackState?.Invoke(sound.Track) ?? new AssemblyAudioTrack();
            double gain = audio.Gain * state.Gain(AnySolo), row = AudioTop + sound.Track * AudioLaneHeight;
            bool active = sound.Owner == _selected && sound.Event == SelectedAudioEvent;
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
                    double amplitude = Math.Min(1, wave.Peak(source, source + 2 / _scale) * Math.Max(.12, gain)) * 17;
                    dc.DrawLine(pen, new(px, center - amplitude), new(px, center + amplitude));
                }
            }
            Text(dc, audio.Removed ? "Удалено" : audio.Muted || state.Muted ? "Без звука" : $"{System.IO.Path.GetFileName(sound.Path)} · отдельно", ax + 8, row + 5, aw - 16, size: 10);
            dc.Pop(); dc.Pop();
        }
        if (_gesture == Gesture.Move && _moved)
        {
            double drop = VideoTimeline.Offset(_parts, _insertion) * _scale;
            dc.DrawLine(DropPen, new Point(drop, 27), new Point(drop, LaneBottom));
            Text(dc, "Вставить сюда", drop + 7, LaneBottom + 3, 130, Highlight);
        }
        double cursor = _position * _scale;
        dc.DrawLine(CursorPen, new Point(cursor, 24), new Point(cursor, LaneBottom + 5));
        var marker = new StreamGeometry();
        using (var g = marker.Open()) { g.BeginFigure(new Point(cursor - 6, 22), true, true); g.LineTo(new Point(cursor + 6, 22), true, false); g.LineTo(new Point(cursor, 30), true, false); }
        dc.DrawGeometry(Brushes.White, null, marker);
        if (_parts.Count == 0) Text(dc, "Перетащите сюда видео из медиатеки или Проводника", 24, 75, Math.Max(200, VisibleWidth - 48), Muted, 14);
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
        _audioGesture = null; _audioTrim = null; _gapTrim = null;
        if (HitAudio(point) is { Detached: true } sound && (!_parts[sound.Owner].AudioLinked || sound.Path != _parts[sound.Owner].Path))
        {
            _pressed = point; _moved = false; _selected = _dragIndex = sound.Owner;
            SelectedAudioTrack = sound.Track; SelectedAudioEvent = sound.Event;
            SelectedAudioSeconds = sound.Start.TotalSeconds + point.X / _scale - sound.Position.TotalSeconds;
            _audioGesture = sound; _audioTrim = _parts[sound.Owner].AudioEvents[sound.Event];
            double audioLeft = sound.Position.TotalSeconds * _scale, audioRight = sound.Finish.TotalSeconds * _scale;
            double audioGrip = Math.Min(8, (audioRight - audioLeft) / 3);
            _gesture = point.X - audioLeft <= audioGrip ? Gesture.AudioLeft : audioRight - point.X <= audioGrip ? Gesture.AudioRight : Gesture.AudioMove;
            SelectAudio?.Invoke(sound.Owner, sound.Track, SelectedAudioSeconds); InvalidateVisual(); return;
        }
        _pressed = point; _moved = false; _trim = null;
        if (point.Y >= AudioTop && HitAudio(point) is null)
        {
            _gesture = Gesture.Cursor; Position = Math.Max(0, point.X / _scale);
            Scrub?.Invoke(Position); return;
        }
        _dragIndex = VideoTimeline.ClipAt(_parts, point.X / _scale);
        double total = Math.Max(_parts.Sum(p => p.Gap.TotalSeconds + p.Duration.TotalSeconds), _sounds.Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max());
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
        SelectedAudioSeconds = _parts[_dragIndex].Start.TotalSeconds + point.X / _scale - VideoTimeline.Offset(_parts, _dragIndex);
        SelectClip?.Invoke(_dragIndex);
        SelectAudio?.Invoke(_dragIndex, SelectedAudioTrack, SelectedAudioSeconds);
        double left = VideoTimeline.Offset(_parts, _dragIndex) * _scale;
        double right = left + _parts[_dragIndex].Duration.TotalSeconds * _scale;
        double grip = Math.Min(8, (right - left) / 3);
        _gesture = point.X - left <= grip ? Gesture.Left : right - point.X <= grip ? Gesture.Right : Gesture.Move;
        if (SelectedAudioTrack >= 0 && !_parts[_dragIndex].AudioLinked) _gesture = Gesture.AudioSelect;
        else if (_gesture == Gesture.Move && _parts.Count == 1 && _parts[_dragIndex] is { AudioLinked: false, AudioDetached: true })
        {
            _gesture = Gesture.VideoPlace;
            _gapTrim = _parts[_dragIndex].Gap;
        }
        InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_gesture != Gesture.None) { UpdateGesture(point); e.Handled = true; return; }
        if (HitAudio(point) is { Detached: true } sound)
        {
            Cursor = point.X - sound.Position.TotalSeconds * _scale < 8 || sound.Finish.TotalSeconds * _scale - point.X < 8 ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        int index = VideoTimeline.ClipAt(_parts, point.X / _scale);
        if (point.Y < VideoTop || index < 0) { Cursor = Cursors.IBeam; return; }
        double left = VideoTimeline.Offset(_parts, index) * _scale, right = left + _parts[index].Duration.TotalSeconds * _scale;
        Cursor = point.X < right && (point.X - left < 8 || right - point.X < 8) ? Cursors.SizeWE :
            _parts.Count == 1 && !_parts[index].AudioLinked && _parts[index].AudioDetached ? Cursors.SizeAll : Cursors.Hand;
    }
    internal void UpdateGesture(Point point)
    {
        _moved |= Math.Abs(point.X - _pressed.X) > 3;
        if (_audioGesture is { } sound && _gesture is Gesture.AudioMove or Gesture.AudioLeft or Gesture.AudioRight)
        {
            var original = _parts[sound.Owner].AudioEvents[sound.Event];
            double delta = (point.X - _pressed.X) / _scale;
            var info = SourceInfo?.Invoke(sound.Path) ?? (sound.End.TotalSeconds, 30.0);
            if (SnapToFrames) delta = Math.Round(delta * info.Item2) / info.Item2;
            if (_gesture == Gesture.AudioMove) _audioTrim = original with { Position = TimeSpan.FromSeconds(Math.Max(0, sound.Position.TotalSeconds + delta)) };
            else if (_gesture == Gesture.AudioLeft)
            {
                delta = Math.Clamp(delta, Math.Max(-sound.Position.TotalSeconds, -sound.Start.TotalSeconds), sound.Duration.TotalSeconds - .05);
                _audioTrim = original with { Start = original.Start + TimeSpan.FromSeconds(delta), Position = original.Position + TimeSpan.FromSeconds(delta) };
            }
            else _audioTrim = original with { End = TimeSpan.FromSeconds(Math.Clamp(original.End.TotalSeconds + delta, original.Start.TotalSeconds + .05, info.Item1)) };
            Resize();
        }
        else if (_gesture == Gesture.Cursor)
        {
            Position = Math.Clamp(point.X / _scale, 0, Math.Max(_parts.Sum(p => p.Gap.TotalSeconds + p.Duration.TotalSeconds), _sounds.Select(s => s.Finish.TotalSeconds).DefaultIfEmpty(0).Max()));
            Scrub?.Invoke(Position);
        }
        else if (_gesture == Gesture.VideoPlace && _dragIndex >= 0)
        {
            var original = _parts[_dragIndex];
            var info = SourceInfo?.Invoke(original.Path) ?? (original.End.TotalSeconds, 30.0);
            double delta = (point.X - _pressed.X) / _scale;
            if (SnapToFrames) delta = Math.Round(delta * info.Item2) / info.Item2;
            _gapTrim = TimeSpan.FromSeconds(Math.Max(0, original.Gap.TotalSeconds + delta));
            Resize();
        }
        else if (_gesture == Gesture.Move)
            _insertion = VideoTimeline.Insertion(_parts, point.X / _scale);
        else if (_dragIndex >= 0 && _gesture is Gesture.Left or Gesture.Right)
        {
            var original = _parts[_dragIndex];
            var info = SourceInfo?.Invoke(original.Path) ?? (original.End.TotalSeconds, 30.0);
            _trim = VideoTimeline.Trim(original, _gesture == Gesture.Left, (point.X - _pressed.X) / _scale, info.Item1, info.Item2, SnapToFrames);
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
        var gesture = _gesture; var trim = _trim; int index = _dragIndex, insertion = _insertion;
        var audio = _audioTrim; var sound = _audioGesture; var gap = _gapTrim;
        _gesture = Gesture.None; _trim = null; _dragIndex = -1;
        _audioTrim = null; _audioGesture = null; _gapTrim = null;
        InteractionEnded?.Invoke(true);
        if (sound is not null)
        {
            if (_moved && audio is not null && audio != _parts[sound.Owner].AudioEvents[sound.Event]) EditAudio?.Invoke(sound.Owner, sound.Event, audio);
            else if (!_moved) Seek?.Invoke(Math.Max(0, point.X / _scale));
        }
        else if (gesture == Gesture.Cursor || gesture == Gesture.AudioSelect || (gesture is Gesture.Move or Gesture.VideoPlace) && !_moved) Seek?.Invoke(Math.Max(0, point.X / _scale));
        else if (gesture == Gesture.VideoPlace && _moved && gap is TimeSpan placed && index >= 0 && placed != _parts[index].Gap)
            PlaceClip?.Invoke(index, placed);
        else if (gesture == Gesture.Move && _moved)
        {
            int target = Math.Clamp(insertion > index ? insertion - 1 : insertion, 0, _parts.Count - 1);
            if (target != index) Reorder?.Invoke(index, target);
        }
        else if (_moved && trim is not null && trim != _parts[index]) TrimClip?.Invoke(index, trim);
        Resize();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); CancelGesture(); }
    public void CancelGesture()
    {
        bool active = _gesture != Gesture.None;
        _gesture = Gesture.None; _trim = null; _dragIndex = -1;
        _audioGesture = null; _audioTrim = null; _gapTrim = null;
        if (active) InteractionEnded?.Invoke(false);
        ReleaseMouseCapture(); Resize();
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
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
            SelectClip?.Invoke(index); SelectAudio?.Invoke(index, SelectedAudioTrack, SelectedAudioSeconds); InvalidateVisual();
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
        return track < 0 ? null : _sounds.LastOrDefault(s => s.Track == track && time >= s.Position.TotalSeconds && time < s.Finish.TotalSeconds);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { Zoom?.Invoke(e.Delta > 0 ? 1.25 : .8, e.GetPosition(this).X); e.Handled = true; }
        base.OnMouseWheel(e);
    }
}
