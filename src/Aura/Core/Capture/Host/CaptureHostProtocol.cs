namespace Aura.Core.Capture.Host;

/// <summary>
/// Состояние сессии дочернего процесса захвата. Пишется обеими сторонами в
/// управляющий блок общей памяти; сопоставление с кодом — по этим числам.
/// </summary>
internal enum CaptureHostState
{
    Starting = 0,
    Ready = 1,
    Running = 2,
    Stopping = 3,
    Ended = 4,
    Failed = 5
}

/// <summary>
/// Раскладка управляющего блока общей памяти между Aura и дочерним
/// <c>Aura.exe --capture-host</c>. Обе стороны — один и тот же код, поэтому
/// раскладка задаётся константами, а не ручным C-структурным ABI.
///
/// ПОЧЕМУ ОТДЕЛЬНЫЙ БЛОК, А НЕ КАНАЛ НА КАЖДЫЙ КАДР. Данные кадра идут по
/// разделяемым текстурам; управляющий блок несёт только метаданные, счётчики и
/// состояние. Синхронизация готовности текстуры — ID3D11Fence (Signal в
/// производителе, Wait перед чтением у потребителя), без IDXGIKeyedMutex.
/// </summary>
internal static class CaptureHostProtocol
{
    public const int Magic = 0x31484341;      // "ACH1"
    public const int Version = 1;
    public const int SlotCount = 3;

    /// <summary>Максимальный размер формы курсора: 1024x1024 BGRA (DdaCursorShape.MaximumDimension).</summary>
    public const int SlotShapeCapacity = 4 * 1024 * 1024;
    public const int HeaderBytes = 4096;
    public const int SlotMetadataBytes = 64;
    /// <summary>После полей заголовка (они заканчиваются ниже 768), но до конца заголовочной страницы.</summary>
    public const int SlotMetadataBase = 768;

    public static int SlotMetadataOffset(int slot) => SlotMetadataBase + slot * SlotMetadataBytes;
    public static int SlotShapeOffset(int slot) => HeaderBytes + slot * SlotShapeCapacity;
    public static long MappingBytes => HeaderBytes + (long)SlotCount * SlotShapeCapacity;

    // ---- заголовок ----
    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffState = 8;
    public const int OffFailureKind = 12;
    public const int OffWidth = 16;
    public const int OffHeight = 20;
    public const int OffTargetFps = 24;
    public const int OffCaptureCursor = 28;
    public const int OffGeneration = 32;
    public const int OffAdapterLuid = 40;
    public const int OffChildPid = 48;
    public const int OffParentPid = 52;
    public const int OffLoopSequence = 56;          // relay DDA diagnostics
    public const int OffLastLoopProgressQpc = 64;
    public const int OffFramesReceived = 72;
    public const int OffFramesAccepted = 80;
    public const int OffLastHResult = 88;
    public const int OffPhase = 92;
    public const int OffHeartbeatQpc = 96;
    public const int OffDroppedFrames = 104;
    public const int OffMonitorIdentity = 128;      // UTF-16, 512 байт
    public const int MonitorIdentityBytes = 512;
    public const int OffRingHandleBase = 656;       // SlotCount × Int64: NT-хендлы кольца (создаёт воркер)
    public const int OffChildFenceHandle = 680;     // Int64: fence «кадр готов», создаёт воркер
    public const int OffParentFenceChildHandle = 688; // Int64: fence «слот свободен», хендл в процессе воркера
    public const int OffPublishedEpoch = 696;       // Int64: ++ при каждой публикации Ready

    // ---- метаданные слота ----
    public const int SlotOffSequence = 0;           // Int64, глобальный номер кадра
    public const int SlotOffTimestamp = 8;          // Int64, 100 нс (QPC-шкала)
    public const int SlotOffPublishedFence = 16;    // Int64, значение child-fence
    public const int SlotOffReleasedFence = 24;     // Int64, значение parent-fence (пишет родитель)
    public const int SlotOffCursorMode = 32;        // Int32
    public const int SlotOffCursorFlags = 36;       // Int32
    public const int SlotOffCursorX = 40;           // Int32
    public const int SlotOffCursorY = 44;           // Int32
    public const int SlotOffShapeKind = 48;         // Int32
    public const int SlotOffShapeWidth = 52;        // Int32
    public const int SlotOffShapeHeight = 56;       // Int32
    public const int SlotOffShapeLength = 60;       // Int32

    /// <summary>Проверка внутренней согласованности раскладки (вызывается тестами).</summary>
    public static bool ValidateLayout()
    {
        int lastMeta = SlotMetadataOffset(SlotCount - 1) + SlotMetadataBytes;
        return Version > 0
               && SlotCount > 0
               && SlotMetadataBase > OffPublishedEpoch + sizeof(long)
               && lastMeta <= HeaderBytes
               && OffRingHandleBase + SlotCount * sizeof(long) <= OffChildFenceHandle
               && OffChildFenceHandle + sizeof(long) == OffParentFenceChildHandle
               && OffMonitorIdentity + MonitorIdentityBytes <= OffRingHandleBase
               && SlotShapeOffset(0) == HeaderBytes
               && MappingBytes >= SlotShapeOffset(SlotCount - 1) + SlotShapeCapacity;
    }
}

[Flags]
internal enum CaptureCursorWireFlags
{
    None = 0,
    HasPosition = 1,
    Visible = 2,
    HasVisibility = 4,
    ResetState = 8
}

/// <summary>
/// Перенос состояния курсора между воркером и Aura. Форма курсора передаётся
/// отдельным буфером (арены в общей памяти), здесь только её описание.
/// </summary>
internal readonly record struct CaptureCursorWire(
    int Mode,
    CaptureCursorWireFlags Flags,
    int X,
    int Y,
    int ShapeKind,
    int ShapeWidth,
    int ShapeHeight,
    int ShapeLength)
{
    public bool HasPosition => (Flags & CaptureCursorWireFlags.HasPosition) != 0;
    public bool Visible => (Flags & CaptureCursorWireFlags.Visible) != 0;
    public bool HasVisibility => (Flags & CaptureCursorWireFlags.HasVisibility) != 0;
    public bool ResetState => (Flags & CaptureCursorWireFlags.ResetState) != 0;

    public static CaptureCursorWire From(in CaptureCursorUpdate update, int shapeLength)
    {
        var flags = CaptureCursorWireFlags.None;
        if (update.HasPosition) flags |= CaptureCursorWireFlags.HasPosition;
        if (update.Visible) flags |= CaptureCursorWireFlags.Visible;
        if (update.HasVisibility) flags |= CaptureCursorWireFlags.HasVisibility;
        if (update.ResetState) flags |= CaptureCursorWireFlags.ResetState;
        DdaCursorShape? shape = update.Shape;
        return new CaptureCursorWire(
            (int)update.Mode,
            flags,
            update.X,
            update.Y,
            shape is null ? 0 : (int)shape.Kind,
            shape?.Width ?? 0,
            shape?.Height ?? 0,
            shapeLength);
    }

    /// <summary>Собрать обновление для брокера Aura; <paramref name="shape"/> уже скопирован из арены.</summary>
    public CaptureCursorUpdate ToUpdate(DdaCursorShape? shape) =>
        new((CaptureCursorMode)Mode, HasPosition, Visible, X, Y, shape, ResetState, HasVisibility);
}
