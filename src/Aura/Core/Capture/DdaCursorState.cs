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

/// <summary>
/// Видим ли курсор, если спросить и Desktop Duplication, и систему.
///
/// ЗАЧЕМ. Полноэкранная игра с обзором мышью прячет курсор через ShowCursor(FALSE)
/// и держит его в центре экрана. Desktop Duplication при этом продолжает докладывать
/// указатель видимым, а раз мышь в системных координатах больше не движется, новых
/// обновлений тоже не присылает. Последнее состояние «видим, в центре» так и висело,
/// и в записи посреди игры стоял неподвижный курсор.
///
/// GetCursorInfo отдаёт глобальное состояние курсора, и флаг CURSOR_SHOWING там
/// снят, пока игра его прячет. Так же решает эту задачу OBS при захвате экрана.
/// </summary>
internal static class DdaCursorVisibility
{
    /// <param name="systemShowing">
    /// Флаг CURSOR_SHOWING из GetCursorInfo; null — система не ответила, и тогда
    /// верим одному DDA, как раньше.
    /// </param>
    /// <remarks>
    /// Первый элемент — «DDA действительно прислала обновление указателя», а НЕ
    /// «есть что применить к состоянию курсора». Разница принципиальная: по этому
    /// признаку захват решал, изменилась ли картинка кадра. Когда игра прячет
    /// курсор, DDA о нём молчит, и «спрятан» превращалось в событие указателя на
    /// каждом кадре — включая кадры без единого обновления экрана.
    ///
    /// Скрытость системы до состояния курсора всё равно доводится, но отдельным
    /// признаком <see cref="CaptureCursorUpdate.HasVisibility"/>: его вычисляет
    /// вызывающий (<c>ReadCursorUpdate</c>), он же знает, спрашивал ли систему.
    /// </remarks>
    public static (bool HasPosition, bool Visible) Merge(
        bool ddaHasPosition,
        bool ddaVisible,
        bool? systemShowing)
    {
        // Система говорит «спрятан» — прячем, даже если DDA в этом кадре молчал о
        // позиции. Именно молчание DDA и держало курсор на экране.
        if (systemShowing == false)
            return (ddaHasPosition, false);

        return (ddaHasPosition, ddaHasPosition && ddaVisible);
    }
}

internal enum CaptureCursorMode
{
    SystemComposed,
    Separate
}

/// <summary>
/// Обновление состояния курсора от источника.
/// </summary>
/// <param name="HasPosition">
/// DDA прислала новую позицию указателя. Только это поле означает «курсор
/// сдвинулся»; выставлять его из одного факта «спрятан» нельзя — см.
/// <see cref="DdaCursorVisibility.Merge"/>.
/// </param>
/// <param name="HasVisibility">
/// Видимость известна и её надо применить, даже если позиция не изменилась:
/// игра прячет неподвижный курсор, DDA об этом молчит, а система знает.
/// </param>
internal readonly record struct CaptureCursorUpdate(
    CaptureCursorMode Mode,
    bool HasPosition,
    bool Visible,
    int X,
    int Y,
    DdaCursorShape? Shape,
    bool ResetState = false,
    bool HasVisibility = false)
{
    public static CaptureCursorUpdate SystemComposed =>
        new(CaptureCursorMode.SystemComposed, false, false, 0, 0, null);

    /// <summary>Обновление ничего не меняет: применять нечего.</summary>
    public bool IsEmpty =>
        !HasPosition && !HasVisibility && Shape is null && !ResetState;
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

            // Пустое обновление (кадр пришёл сам по себе, курсор не менялся) не
            // должно двигать ревизию: по ней экран решает, пересобирать ли форму.
            if (update.IsEmpty)
                return true;

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
            else if (update.HasVisibility)
            {
                // Спрятать или показать можно и без новой позиции: игра прячет
                // неподвижный курсор, и координаты обязаны остаться прежними.
                visible = update.Visible;
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

/// <summary>
/// Состояние курсора на стороне ПОТОКА ЗАХВАТА: накапливает обновления DDA и решает,
/// изменилась ли от них картинка кадра.
///
/// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="DdaCursorState"/>. Состояние для дорисовки живёт в
/// GPU-брокере и обновляется только тогда, когда кадр уже опубликован. А решать,
/// публиковать ли кадр, приходится здесь, до копирования. Общего состояния у этих
/// двух мест нет, и это правильно: трекер отвечает ровно на два вопроса.
///
/// 1. МЕНЯЕТСЯ ЛИ КАРТИНКА. Кадр Desktop Duplication приходит и тогда, когда
///    обновился только указатель (AccumulatedFrames == 0). Такой кадр нужен в
///    записи, только если курсор видно: он нарисован поверх картинки. Скрытый
///    курсор не виден нигде, поэтому его перемещения (а игра с обзором мышью
///    двигает его на каждой миллисекунде) не должны становиться кадрами записи.
///    Иначе захват отдаёт энкодеру десятки одинаковых картинок с новыми метками
///    времени, а настоящие кадры игры выбрасывает как «слишком частые» — счётчик
///    показывает 60 fps, а запись дёргается.
///
/// 2. ЧТО НАДО ДОНЕСТИ ДО БРОКЕРА. Форму курсора система отдаёт ровно один раз и
///    второй раз не повторит. Раньше она читалась до решения о захвате, и если
///    кадр отбрасывался, форма терялась навсегда. Здесь всё, что не уехало с
///    кадром, остаётся накопленным (<see cref="Capture"/>) до следующего.
///
/// Поток захвата один, поэтому блокировки не нужны.
/// </summary>
internal sealed class DdaCursorTracker
{
    private bool _visible;
    private int _x, _y;
    private DdaCursorShape? _shape;

    private bool _positionDirty;
    private bool _visibilityDirty;
    private bool _shapeDirty;
    private bool _resetPending;

    public void Reset()
    {
        _visible = false;
        _x = _y = 0;
        _shape = null;
        _positionDirty = _visibilityDirty = _shapeDirty = false;
        _resetPending = false;
    }

    /// <summary>
    /// Учесть обновление и ответить, изменилась ли от него картинка кадра.
    /// Накопленное при этом не теряется — см. <see cref="Capture"/>.
    /// </summary>
    public bool Track(in CaptureCursorUpdate update)
    {
        if (!update.HasPosition && !update.HasVisibility &&
            update.Shape is null && !update.ResetState)
        {
            return false;
        }

        if (update.ResetState)
        {
            _visible = false;
            _x = _y = 0;
            _shape = null;
            _resetPending = true;
        }

        bool knowsVisibility = update.HasPosition || update.HasVisibility;
        bool visible = knowsVisibility ? update.Visible : _visible;
        bool moved = update.HasPosition && (update.X != _x || update.Y != _y);
        bool visibilityChanged = knowsVisibility && visible != _visible;

        if (moved)
        {
            _x = update.X;
            _y = update.Y;
            _positionDirty = true;
        }

        if (visibilityChanged)
        {
            _visible = visible;
            _visibilityDirty = true;
        }

        bool shapeChanged = update.Shape is not null && !ReferenceEquals(update.Shape, _shape);
        if (shapeChanged)
        {
            _shape = update.Shape;
            _shapeDirty = true;
        }

        // Скрытый курсор в кадр не попадает вовсе, поэтому ни его перемещение, ни
        // смена формы картинку не меняют: их можно донести со следующим кадром.
        return update.ResetState || visibilityChanged || (visible && (moved || shapeChanged));
    }

    /// <summary>
    /// Что донести до брокера вместе с кадром, который сейчас уйдёт в запись.
    /// Возвращает только изменения с прошлой доставки — лишних перерисовок нет.
    /// </summary>
    public CaptureCursorUpdate Capture => new(
        CaptureCursorMode.Separate,
        HasPosition: _positionDirty,
        Visible: _visible,
        X: _x,
        Y: _y,
        Shape: _shapeDirty ? _shape : null,
        ResetState: _resetPending,
        HasVisibility: _visibilityDirty && !_positionDirty);

    /// <summary>Кадр опубликован: накопленное больше не висит.</summary>
    public void Delivered()
    {
        _positionDirty = false;
        _visibilityDirty = false;
        _shapeDirty = false;
        _resetPending = false;
    }
}
