namespace Aura.Core.Capture;

/// <summary>Проверенный набор аргументов для создания конкретного источника.</summary>
internal readonly record struct CaptureSourceRequest
{
    private CaptureSourceRequest(
        CaptureBackend backend,
        int monitorIndex,
        GameCaptureTarget? target)
    {
        Backend = backend;
        MonitorIndex = monitorIndex;
        Target = target;
    }

    public CaptureBackend Backend { get; }
    public int MonitorIndex { get; }
    public GameCaptureTarget? Target { get; }
    public long TargetRevision => Target?.Revision ?? 0;

    public static CaptureSourceRequest Create(
        CaptureBackend backend,
        int monitorIndex,
        GameCaptureTarget? target)
    {
        if (monitorIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex));

        if (backend == CaptureBackend.WgcWindow)
        {
            if (target is not GameCaptureTarget windowTarget)
                throw new ArgumentException("Оконному WGC требуется проверенное игровое окно", nameof(target));
            if (windowTarget.MonitorIndex != monitorIndex)
                throw new ArgumentException("Игровое окно находится не на выбранном мониторе", nameof(target));
        }
        else if (target is not null)
        {
            throw new ArgumentException("Мониторный источник не принимает оконный target", nameof(target));
        }

        return new CaptureSourceRequest(backend, monitorIndex, target);
    }
}
