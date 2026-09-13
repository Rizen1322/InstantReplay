namespace Aura.Core.Capture.GameHook;

internal enum MinecraftHookEligibilityReason
{
    Allowed,
    ForbiddenExecutable,
    ExecutableNotJavaw,
    NotMinecraft,
    TargetSnapshotMismatch,
    ProcessNotAlive,
    StaleProcessIdentity,
    WindowMismatch,
    NotForeground,
    MonitorMismatch,
    ArchitectureMismatch,
    DifferentUser,
    HigherIntegrity,
    ProtectedProcess,
    AntiCheatDetected,
    OpenGlNotLoaded
}

internal readonly record struct MinecraftHookEligibilitySnapshot(
    nint Hwnd,
    nint RootHwnd,
    nint ForegroundHwnd,
    int ProcessId,
    long ProcessStartTicks,
    string ExecutableName,
    string GameName,
    int MonitorIndex,
    bool IsProcessAlive,
    bool Is64BitProcess,
    bool Is64BitController,
    string ProcessUserSid,
    string CurrentUserSid,
    int ProcessIntegrityLevel,
    int CurrentIntegrityLevel,
    bool IsProtectedProcess,
    IReadOnlyList<string> LoadedModuleNames);

internal readonly record struct MinecraftHookTargetIdentity(
    nint Hwnd,
    int ProcessId,
    long ProcessStartTicks,
    int MonitorIndex,
    long TargetRevision);

internal readonly record struct MinecraftHookEligibilityResult(
    bool Allowed,
    MinecraftHookEligibilityReason Reason,
    MinecraftHookTargetIdentity? Identity)
{
    public static MinecraftHookEligibilityResult Denied(MinecraftHookEligibilityReason reason) =>
        new(false, reason, null);
}

/// <summary>
/// Единственная точка допуска native hook. Политика намеренно разрешает только
/// полностью повторно проверенный Minecraft Java и закрыта для остальных процессов.
/// </summary>
internal static class MinecraftHookEligibility
{
    private static readonly HashSet<string> ForbiddenExecutables = new(
        ["cs2.exe"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownAntiCheatModules = new(
        [
            "easyanticheat.dll",
            "easyanticheat_x64.dll",
            "easyanticheat_eos.dll",
            "beclient.dll",
            "beclient_x64.dll",
            "vgk.dll",
            "faceitclient.dll"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static MinecraftHookEligibilityResult Evaluate(
        in GameCaptureTarget target,
        in MinecraftHookEligibilitySnapshot snapshot)
    {
        string targetExecutable = NormalizeBasename(target.ExecutableName);
        string snapshotExecutable = NormalizeBasename(snapshot.ExecutableName);

        if (ForbiddenExecutables.Contains(targetExecutable) ||
            ForbiddenExecutables.Contains(snapshotExecutable))
        {
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.ForbiddenExecutable);
        }

        if (!snapshotExecutable.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.ExecutableNotJavaw);

        if (!snapshot.GameName.Equals("Minecraft", StringComparison.OrdinalIgnoreCase))
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.NotMinecraft);

        if (!targetExecutable.Equals(snapshotExecutable, StringComparison.OrdinalIgnoreCase) ||
            !target.GameName.Equals(snapshot.GameName, StringComparison.OrdinalIgnoreCase))
        {
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.TargetSnapshotMismatch);
        }

        if (!snapshot.IsProcessAlive)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.ProcessNotAlive);

        if (target.ProcessId != snapshot.ProcessId ||
            target.ProcessStartTicks != snapshot.ProcessStartTicks)
        {
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.StaleProcessIdentity);
        }

        if (target.Hwnd == 0 ||
            target.Hwnd != snapshot.Hwnd ||
            target.Hwnd != snapshot.RootHwnd)
        {
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.WindowMismatch);
        }

        if (target.Hwnd != snapshot.ForegroundHwnd)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.NotForeground);

        if (target.MonitorIndex != snapshot.MonitorIndex)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.MonitorMismatch);

        if (!snapshot.Is64BitController || !snapshot.Is64BitProcess)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.ArchitectureMismatch);

        if (string.IsNullOrWhiteSpace(snapshot.CurrentUserSid) ||
            !snapshot.ProcessUserSid.Equals(
                snapshot.CurrentUserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.DifferentUser);
        }

        if (snapshot.ProcessIntegrityLevel > snapshot.CurrentIntegrityLevel)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.HigherIntegrity);

        if (snapshot.IsProtectedProcess)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.ProtectedProcess);

        bool antiCheatLoaded = snapshot.LoadedModuleNames.Any(
            module => KnownAntiCheatModules.Contains(NormalizeBasename(module)));
        if (antiCheatLoaded)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.AntiCheatDetected);

        bool openGlLoaded = snapshot.LoadedModuleNames.Any(
            module => NormalizeBasename(module).Equals(
                "opengl32.dll",
                StringComparison.OrdinalIgnoreCase));
        if (!openGlLoaded)
            return MinecraftHookEligibilityResult.Denied(
                MinecraftHookEligibilityReason.OpenGlNotLoaded);

        return new MinecraftHookEligibilityResult(
            true,
            MinecraftHookEligibilityReason.Allowed,
            new MinecraftHookTargetIdentity(
                target.Hwnd,
                target.ProcessId,
                target.ProcessStartTicks,
                target.MonitorIndex,
                target.Revision));
    }

    private static string NormalizeBasename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string basename = Path.GetFileName(value.Trim());
        return Path.HasExtension(basename) ? basename : basename + ".exe";
    }
}
