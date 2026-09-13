namespace Aura.Core.Capture;

internal enum CaptureRecoveryAction
{
    Restart,
    HoldForGameWindow
}

[Flags]
internal enum CaptureProviderQuarantine
{
    None = 0,
    WgcMonitor = 1,
    DesktopDuplication = 2,
    WgcWindow = 4,
    MinecraftOpenGl = 8
}

/// <summary>Карантин источников, ограниченный тождеством одного игрового окна.</summary>
internal readonly record struct CaptureEpisode(
    nint TargetHwnd,
    int TargetProcessId,
    long TargetProcessStartTicks,
    long TargetRevision,
    CaptureProviderQuarantine Quarantines)
{
    public static CaptureEpisode Empty => default;

    public static CaptureEpisode ForTarget(in GameCaptureTarget target) => new(
        target.Hwnd,
        target.ProcessId,
        target.ProcessStartTicks,
        target.Revision,
        CaptureProviderQuarantine.None);

    public bool Matches(in GameCaptureTarget target) =>
        TargetHwnd == target.Hwnd &&
        TargetProcessId == target.ProcessId &&
        TargetProcessStartTicks == target.ProcessStartTicks &&
        TargetRevision == target.Revision;

    public CaptureEpisode Align(in GameCaptureTarget target) =>
        Matches(target) ? this : ForTarget(target);

    public CaptureEpisode Quarantine(CaptureBackend backend) =>
        this with { Quarantines = Quarantines | FlagFor(backend) };

    public bool IsQuarantined(CaptureBackend backend) =>
        (Quarantines & FlagFor(backend)) != 0;

    private static CaptureProviderQuarantine FlagFor(CaptureBackend backend) => backend switch
    {
        CaptureBackend.Wgc => CaptureProviderQuarantine.WgcMonitor,
        CaptureBackend.DesktopDuplication => CaptureProviderQuarantine.DesktopDuplication,
        CaptureBackend.WgcWindow => CaptureProviderQuarantine.WgcWindow,
        CaptureBackend.MinecraftOpenGl => CaptureProviderQuarantine.MinecraftOpenGl,
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };
}

internal readonly record struct CaptureRecoveryContext(
    CaptureBackend ActiveBackend,
    CaptureFailureKind FailureKind,
    bool ForcedBackend,
    GameCaptureTarget? Target,
    CaptureEpisode Episode,
    CaptureBackend PreferredMonitorBackend);

internal readonly record struct CaptureRecoveryDecision(
    CaptureRecoveryAction Action,
    CaptureBackend Backend,
    long TargetRevision,
    TimeSpan RetryDelay,
    CaptureEpisode Episode);

/// <summary>Чистая маршрутизация восстановления без WGC/DDA и таймеров.</summary>
internal static class CaptureRecoveryPolicy
{
    private static readonly TimeSpan WindowRetryDelay = TimeSpan.FromMilliseconds(250);

    public static CaptureRecoveryDecision Decide(in CaptureRecoveryContext context)
    {
        CaptureBackend preferred = context.PreferredMonitorBackend is
            CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl
            ? CaptureBackend.Wgc
            : context.PreferredMonitorBackend;

        if (context.ForcedBackend)
            return Restart(context.ActiveBackend, targetRevision: 0, CaptureEpisode.Empty);

        if (context.Target is not GameCaptureTarget target)
        {
            CaptureBackend backend = context.ActiveBackend switch
            {
                CaptureBackend.WgcWindow => preferred,
                CaptureBackend.MinecraftOpenGl => preferred,
                CaptureBackend.Wgc => CaptureBackend.DesktopDuplication,
                CaptureBackend.DesktopDuplication => CaptureBackend.Wgc,
                _ => preferred
            };

            if (context.FailureKind is CaptureFailureKind.DeviceLost or
                CaptureFailureKind.CaptureFormatChanged)
            {
                backend = context.ActiveBackend is
                    CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl
                    ? preferred
                    : context.ActiveBackend;
            }

            return Restart(backend, targetRevision: 0, CaptureEpisode.Empty);
        }

        CaptureEpisode episode = context.Episode.Align(target);

        if (context.FailureKind is CaptureFailureKind.DeviceLost or
            CaptureFailureKind.CaptureFormatChanged)
        {
            return Restart(
                context.ActiveBackend,
                context.ActiveBackend is CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl
                    ? target.Revision
                    : 0,
                episode);
        }

        episode = episode.Quarantine(context.ActiveBackend);
        if (context.ActiveBackend is CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl)
        {
            return new CaptureRecoveryDecision(
                CaptureRecoveryAction.HoldForGameWindow,
                context.ActiveBackend,
                target.Revision,
                WindowRetryDelay,
                episode);
        }

        CaptureBackend targetBackend = CaptureBackendPolicy.IsMinecraftOpenGlTarget(target)
            ? CaptureBackend.MinecraftOpenGl
            : CaptureBackend.WgcWindow;
        return Restart(targetBackend, target.Revision, episode);
    }

    private static CaptureRecoveryDecision Restart(
        CaptureBackend backend,
        long targetRevision,
        CaptureEpisode episode) => new(
            CaptureRecoveryAction.Restart,
            backend,
            targetRevision,
            TimeSpan.Zero,
            episode);
}
