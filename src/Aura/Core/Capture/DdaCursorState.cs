namespace Aura.Core.Capture;

internal enum DdaCursorShapeKind
{
    Monochrome = 1,
    Color = 2,
    MaskedColor = 4
}

internal sealed record DdaCursorShape(
    DdaCursorShapeKind Kind,
    int Width,
    int Height,
    byte[] Pixels)
{
    public const int MaximumDimension = 1024;

    public static bool TryCreate(
        int type,
        int width,
        int reportedHeight,
        int pitch,
        ReadOnlySpan<byte> payload,
        out DdaCursorShape? shape,
        out string reason)
    {
        shape = null;

        if (type is not (1 or 2 or 4))
        {
            reason = $"Неизвестный тип формы курсора: {type}.";
            return false;
        }

        bool monochrome = type == (int)DdaCursorShapeKind.Monochrome;
        if (monochrome && (reportedHeight & 1) != 0)
        {
            reason = "Высота монохромной формы должна содержать две равные маски.";
            return false;
        }

        int visibleHeight = monochrome ? reportedHeight / 2 : reportedHeight;
        if (width is < 1 or > MaximumDimension ||
            visibleHeight is < 1 or > MaximumDimension)
        {
            reason = $"Недопустимые размеры формы курсора: {width}x{visibleHeight}.";
            return false;
        }

        int minimumPitch;
        int requiredBytes;
        try
        {
            minimumPitch = monochrome
                ? checked((width + 7) / 8)
                : checked(width * 4);
            requiredBytes = checked(pitch * reportedHeight);
        }
        catch (OverflowException)
        {
            reason = "Размер буфера формы курсора переполнен.";
            return false;
        }

        if (pitch < minimumPitch)
        {
            reason = $"Шаг строки формы курсора меньше допустимого: {pitch}.";
            return false;
        }

        if (payload.Length < requiredBytes)
        {
            reason = $"Буфер формы курсора обрезан: {payload.Length} из {requiredBytes} байт.";
            return false;
        }

        byte[] source = payload[..requiredBytes].ToArray();
        byte[] pixels = type switch
        {
            (int)DdaCursorShapeKind.Monochrome =>
                CursorShapePixels.ExpandMonochrome(source, width, visibleHeight, pitch),
            (int)DdaCursorShapeKind.MaskedColor =>
                CursorShapePixels.ExpandMaskedColor(source, width, visibleHeight, pitch),
            _ => CursorShapePixels.CopyColor(source, width, visibleHeight, pitch)
        };

        shape = new DdaCursorShape((DdaCursorShapeKind)type, width, visibleHeight, pixels);
        reason = string.Empty;
        return true;
    }
}

internal enum CaptureCursorMode
{
    SystemComposed,
    Separate
}

internal readonly record struct CaptureCursorUpdate(
    CaptureCursorMode Mode,
    bool HasPosition,
    bool Visible,
    int X,
    int Y,
    DdaCursorShape? Shape,
    bool ResetState = false)
{
    public static CaptureCursorUpdate SystemComposed =>
        new(CaptureCursorMode.SystemComposed, false, false, 0, 0, null);
}

internal readonly record struct DdaCursorSnapshot(
    long Generation,
    long Revision,
    bool Visible,
    int X,
    int Y,
    DdaCursorShape? Shape);

internal sealed class DdaCursorState
{
    private readonly object _gate = new();
    private DdaCursorSnapshot _current;

    public DdaCursorSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public void Reset(long generation)
    {
        lock (_gate)
        {
            _current = new DdaCursorSnapshot(
                generation,
                _current.Revision + 1,
                false,
                0,
                0,
                null);
        }
    }

    public bool Apply(long generation, in CaptureCursorUpdate update)
    {
        lock (_gate)
        {
            if (generation != _current.Generation || update.Mode != CaptureCursorMode.Separate)
                return false;

            bool visible = _current.Visible;
            int x = _current.X;
            int y = _current.Y;
            DdaCursorShape? shape = _current.Shape;

            if (update.ResetState)
            {
                visible = false;
                x = 0;
                y = 0;
                shape = null;
            }

            if (update.HasPosition)
            {
                visible = update.Visible;
                x = update.X;
                y = update.Y;
            }

            if (update.Shape is not null)
                shape = update.Shape;

            _current = new DdaCursorSnapshot(
                generation,
                _current.Revision + 1,
                visible,
                x,
                y,
                shape);
            return true;
        }
    }
}
