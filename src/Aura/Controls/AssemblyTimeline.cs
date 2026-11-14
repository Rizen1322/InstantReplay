using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Aura.Core.Tools;

namespace Aura.Controls;

/// <summary>Связанные видео и звук на одной монтажной шкале.</summary>
public sealed class AssemblyTimeline : FrameworkElement
{
    private IReadOnlyList<VideoSegment> _parts = [];
    private double _scale = 40, _position;
    private int _selected = -1, _dragIndex = -1, _insertion;
    private Point _pressed;
    private VideoSegment? _trim;
    private enum Gesture { None, Cursor, Move, Left, Right }
    private Gesture _gesture;
    private bool _moved;
    private const double VideoTop = 36, AudioTop = 130, LaneBottom = 188;
    public Func<string, ImageSource?>? Thumbnail { get; set; }
    public Func<string, (double Duration, double Fps)>? SourceInfo { get; set; }
    public event Action<int>? SelectClip;
    public event Action<double>? Seek;
    public event Action<double>? Scrub;
    public event Action<int, int>? Reorder;
    public event Action<int, VideoSegment>? TrimClip;
    public event Action<double, double>? Zoom;
    public event Action<bool>? InteractionEnded;
    public double VisibleLeft { get; set; }
    public double VisibleWidth { get; set; } = 1200;
    public double PixelsPerSecond { get => _scale; set { _scale = Math.Clamp(value, .01, 400); Resize(); } }
    public double Position { get => _position; set { _position = Math.Max(0, value); InvalidateVisual(); } }
    public int SelectedIndex { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public bool EditingEnabled { get; set; } = true;
    public bool IsInteracting => _gesture != Gesture.None;

    public AssemblyTimeline()
    {
        Focusable = true; ClipToBounds = true; Height = 210;
        System.Windows.Automation.AutomationProperties.SetName(this, "Монтажная шкала. Видео и связанный звук");
        ToolTip = "Тяните клип — переставить · тяните край — подрезать · щёлкните по линейке — позиция · Ctrl+колесо — масштаб";
    }

    public void SetSegments(IReadOnlyList<VideoSegment> parts)
    {
        _parts = parts;
        Resize();
    }
    private void Resize()
    {
        double length = _parts.Sum(p => p.Duration.TotalSeconds);
        if (_trim is not null && _dragIndex >= 0 && _gesture == Gesture.Right) length += _trim.Duration.TotalSeconds - _parts[_dragIndex].Duration.TotalSeconds;
        Width = Math.Max(VisibleWidth, length * _scale + 40);
        InvalidateVisual();
    }
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
    private static readonly Brush Canvas = new SolidColorBrush(Color.FromRgb(18, 21, 28));
    private static readonly Brush Video = new SolidColorBrush(Color.FromRgb(45, 74, 101));
    private static readonly Brush Audio = new SolidColorBrush(Color.FromRgb(44, 85, 72));
    private static readonly Brush Highlight = new SolidColorBrush(Color.FromRgb(86, 190, 255));
    private static readonly Brush TextColor = new SolidColorBrush(Color.FromRgb(231, 236, 244));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(144, 156, 176));
    private static readonly Typeface Font = new("Segoe UI");

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
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(41, 47, 59)), 1);
        double step = new[] { .1, .2, .5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600 }.FirstOrDefault(s => s * _scale >= 85);
        if (step <= 0) step = 7200;
        double endX = Math.Min(ActualWidth, VisibleLeft + VisibleWidth + 100);
        for (double second = Math.Floor(VisibleLeft / _scale / step) * step; second * _scale < endX; second += step)
        {
            double x = second * _scale;
            dc.DrawLine(grid, new Point(x, 24), new Point(x, LaneBottom));
            Text(dc, TimeSpan.FromSeconds(second).ToString(second < 3600 ? @"mm\:ss\.f" : @"hh\:mm\:ss"), x + 5, 4, 80, Muted);
        }
        dc.DrawLine(grid, new Point(0, AudioTop - 4), new Point(ActualWidth, AudioTop - 4));
        double offset = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            var part = i == _dragIndex && _trim is not null ? _trim : _parts[i];
            double x = offset * _scale, width = Math.Max(1, part.Duration.TotalSeconds * _scale);
            if (i == _dragIndex && _gesture == Gesture.Left && _trim is not null)
            {
                x += (part.Start - _parts[i].Start).TotalSeconds * _scale;
                offset += _parts[i].Duration.TotalSeconds;
            }
            else offset += part.Duration.TotalSeconds;
            if (x + width < VisibleLeft || x > endX) continue;
            bool selected = i == _selected;
            var border = new Pen(selected ? Highlight : new SolidColorBrush(Color.FromRgb(75, 93, 112)), selected ? 2 : 1);
            dc.PushOpacity(_gesture == Gesture.Move && _moved && i == _dragIndex ? .4 : 1);
            dc.DrawRoundedRectangle(Video, border, new Rect(x + 1, VideoTop, Math.Max(1, width - 2), 84), 3, 3);
            dc.DrawRoundedRectangle(Audio, border, new Rect(x + 1, AudioTop, Math.Max(1, width - 2), 52), 3, 3);
            dc.PushClip(new RectangleGeometry(new Rect(x + 3, VideoTop + 2, Math.Max(1, width - 6), 80)));
            if (Thumbnail?.Invoke(part.Path) is { } image)
                for (double tile = x + 3 + Math.Max(0, Math.Floor((VisibleLeft - x) / 96)) * 96; tile < Math.Min(x + width, endX + 96); tile += 96)
                    dc.DrawImage(image, new Rect(tile, VideoTop + 2, 94, 53));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 22, 31, 43)), null, new Rect(x + 3, VideoTop + 56, Math.Max(1, width - 6), 25));
            Text(dc, part.Name, x + 9, VideoTop + 60, width - 18);
            dc.Pop();
            dc.PushClip(new RectangleGeometry(new Rect(x + 3, AudioTop + 2, Math.Max(1, width - 6), 48)));
            Text(dc, part.AudioTrack < 0 ? "Звук · все дорожки" : $"Звук · дорожка {part.AudioTrack + 1}", x + 9, AudioTop + 9, width - 18);
            Text(dc, part.Range, x + 9, AudioTop + 28, width - 18, Muted, 10);
            dc.Pop();
            if (selected && width > 12)
            {
                dc.DrawRectangle(Highlight, null, new Rect(x + 1, VideoTop + 1, 4, 82));
                dc.DrawRectangle(Highlight, null, new Rect(x + width - 5, VideoTop + 1, 4, 82));
            }
            dc.Pop();
        }
        if (_gesture == Gesture.Move && _moved)
        {
            double drop = VideoTimeline.Offset(_parts, _insertion) * _scale;
            dc.DrawLine(new Pen(Highlight, 3), new Point(drop, 27), new Point(drop, LaneBottom));
            Text(dc, "Вставить сюда", drop + 7, LaneBottom + 3, 130, Highlight);
        }
        double cursor = _position * _scale;
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(cursor, 24), new Point(cursor, LaneBottom + 5));
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
        _pressed = point; _moved = false; _trim = null;
        _dragIndex = VideoTimeline.Locate(_parts, point.X / _scale).Index;
        double total = _parts.Sum(p => p.Duration.TotalSeconds);
        if (point.Y < VideoTop || point.Y > LaneBottom || point.X >= total * _scale || _dragIndex < 0)
        {
            _gesture = Gesture.Cursor;
            Position = Math.Clamp(point.X / _scale, 0, total);
            Scrub?.Invoke(Position); return;
        }
        _selected = _dragIndex;
        SelectClip?.Invoke(_dragIndex);
        double left = VideoTimeline.Offset(_parts, _dragIndex) * _scale;
        double right = left + _parts[_dragIndex].Duration.TotalSeconds * _scale;
        double grip = Math.Min(8, (right - left) / 3);
        _gesture = point.X - left <= grip ? Gesture.Left : right - point.X <= grip ? Gesture.Right : Gesture.Move;
        InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_gesture != Gesture.None) { UpdateGesture(point); e.Handled = true; return; }
        int index = VideoTimeline.Locate(_parts, point.X / _scale).Index;
        if (point.Y < VideoTop || index < 0) { Cursor = Cursors.IBeam; return; }
        double left = VideoTimeline.Offset(_parts, index) * _scale, right = left + _parts[index].Duration.TotalSeconds * _scale;
        Cursor = point.X < right && (point.X - left < 8 || right - point.X < 8) ? Cursors.SizeWE : Cursors.Hand;
    }
    internal void UpdateGesture(Point point)
    {
        _moved |= Math.Abs(point.X - _pressed.X) > 3;
        if (_gesture == Gesture.Cursor)
        {
            Position = Math.Clamp(point.X / _scale, 0, _parts.Sum(p => p.Duration.TotalSeconds));
            Scrub?.Invoke(Position);
        }
        else if (_gesture == Gesture.Move)
            _insertion = VideoTimeline.Insertion(_parts, point.X / _scale);
        else if (_dragIndex >= 0 && _gesture is Gesture.Left or Gesture.Right)
        {
            var original = _parts[_dragIndex];
            var info = SourceInfo?.Invoke(original.Path) ?? (original.End.TotalSeconds, 30.0);
            _trim = VideoTimeline.Trim(original, _gesture == Gesture.Left, (point.X - _pressed.X) / _scale, info.Item1, info.Item2);
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
        _gesture = Gesture.None; _trim = null; _dragIndex = -1;
        InteractionEnded?.Invoke(true);
        if (gesture == Gesture.Cursor || gesture == Gesture.Move && !_moved) Seek?.Invoke(Math.Max(0, point.X / _scale));
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
        if (active) InteractionEnded?.Invoke(false);
        ReleaseMouseCapture(); Resize();
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int index = VideoTimeline.Locate(_parts, e.GetPosition(this).X / _scale).Index;
        if (index >= 0 && EditingEnabled) { _selected = index; SelectClip?.Invoke(index); InvalidateVisual(); }
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { Zoom?.Invoke(e.Delta > 0 ? 1.25 : .8, e.GetPosition(this).X); e.Handled = true; }
        base.OnMouseWheel(e);
    }
}
