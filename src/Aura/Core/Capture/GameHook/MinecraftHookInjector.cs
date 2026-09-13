using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Aura.Core.Capture.GameHook;

[Flags]
internal enum GameHookProcessAccess : uint
{
    Terminate = 0x0001,
    CreateThread = 0x0002,
    VmOperation = 0x0008,
    VmRead = 0x0010,
    VmWrite = 0x0020,
    SuspendResume = 0x0800,
    QueryLimitedInformation = 0x1000,
    AllAccess = 0x001F0FFF
}

internal enum MinecraftHookInjectionResult
{
    Success,
    Ineligible,
    Quarantined,
    InvalidHookBinary,
    OpenProcessDenied,
    RemoteAllocationFailed,
    RemoteWriteFailed,
    RemoteThreadFailed,
    TimedOut,
    LoadLibraryFailed
}

internal readonly record struct MinecraftHookProcessIdentity(
    int ProcessId,
    long ProcessStartTicks);

internal readonly record struct MinecraftHookInjectionAttempt(
    MinecraftHookInjectionResult Result,
    int Win32Error,
    MinecraftHookEligibilityReason EligibilityReason);

internal static class MinecraftHookInjectionPolicy
{
    public const GameHookProcessAccess RequiredProcessAccess =
        GameHookProcessAccess.CreateThread |
        GameHookProcessAccess.QueryLimitedInformation |
        GameHookProcessAccess.VmOperation |
        GameHookProcessAccess.VmWrite |
        GameHookProcessAccess.VmRead;

    public static bool MustQuarantine(MinecraftHookInjectionResult result) => result is
        MinecraftHookInjectionResult.OpenProcessDenied or
        MinecraftHookInjectionResult.RemoteAllocationFailed or
        MinecraftHookInjectionResult.RemoteWriteFailed or
        MinecraftHookInjectionResult.RemoteThreadFailed or
        MinecraftHookInjectionResult.TimedOut or
        MinecraftHookInjectionResult.LoadLibraryFailed;
}

internal sealed class MinecraftHookInjectionQuarantine
{
    private readonly ConcurrentDictionary<MinecraftHookProcessIdentity, byte> _identities = new();

    public bool Contains(in MinecraftHookProcessIdentity identity) =>
        _identities.ContainsKey(identity);

    public void Add(in MinecraftHookProcessIdentity identity) =>
        _identities.TryAdd(identity, 0);
}

/// <summary>Проверяет DLL до OpenProcess и выполняет один ограниченный LoadLibraryW.</summary>
internal sealed class MinecraftHookInjector
{
    private readonly MinecraftHookInjectionQuarantine _quarantine;

    public MinecraftHookInjector(MinecraftHookInjectionQuarantine quarantine)
    {
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
    }

    public MinecraftHookInjectionAttempt Inject(
        in MinecraftHookEligibilityResult eligibility,
        string hookPath,
        string expectedSha256,
        TimeSpan timeout)
    {
        if (!eligibility.Allowed || eligibility.Identity is not MinecraftHookTargetIdentity target)
            return new(MinecraftHookInjectionResult.Ineligible, 0, eligibility.Reason);

        var processIdentity = new MinecraftHookProcessIdentity(
            target.ProcessId,
            target.ProcessStartTicks);
        if (_quarantine.Contains(processIdentity))
            return new(MinecraftHookInjectionResult.Quarantined, 0, eligibility.Reason);

        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        if (GameHookBinaryValidator.Validate(hookPath, expectedSha256) !=
            GameHookBinaryValidation.Valid)
        {
            return new(MinecraftHookInjectionResult.InvalidHookBinary, 0, eligibility.Reason);
        }

        MinecraftHookInjectionAttempt attempt = InjectVerified(
            processIdentity,
            hookPath,
            checked((uint)timeout.TotalMilliseconds),
            eligibility.Reason);
        if (MinecraftHookInjectionPolicy.MustQuarantine(attempt.Result))
            _quarantine.Add(processIdentity);
        return attempt;
    }

    public MinecraftHookInjectionAttempt InjectUsingEmbeddedHash(
        in MinecraftHookEligibilityResult eligibility,
        string hookPath,
        TimeSpan timeout) =>
        Inject(
            eligibility,
            hookPath,
            GameHookBinaryValidator.LoadEmbeddedExpectedSha256(Assembly.GetExecutingAssembly()),
            timeout);

    private static MinecraftHookInjectionAttempt InjectVerified(
        in MinecraftHookProcessIdentity identity,
        string hookPath,
        uint timeoutMilliseconds,
        MinecraftHookEligibilityReason eligibilityReason)
    {
        nint process = 0;
        nint remotePath = 0;
        nint thread = 0;
        bool deferredCleanup = false;
        try
        {
            process = GameHookNativeMethods.OpenProcess(
                MinecraftHookInjectionPolicy.RequiredProcessAccess,
                inheritHandle: false,
                identity.ProcessId);
            if (process == 0)
                return Failure(MinecraftHookInjectionResult.OpenProcessDenied, eligibilityReason);

            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(
                Path.GetFullPath(hookPath) + '\0');
            remotePath = GameHookNativeMethods.VirtualAllocEx(
                process,
                0,
                (nuint)pathBytes.Length,
                GameHookNativeMethods.MemCommit | GameHookNativeMethods.MemReserve,
                GameHookNativeMethods.PageReadWrite);
            if (remotePath == 0)
                return Failure(MinecraftHookInjectionResult.RemoteAllocationFailed, eligibilityReason);

            if (!GameHookNativeMethods.WriteProcessMemory(
                    process,
                    remotePath,
                    pathBytes,
                    (nuint)pathBytes.Length,
                    out nuint written) ||
                written != (nuint)pathBytes.Length)
            {
                return Failure(MinecraftHookInjectionResult.RemoteWriteFailed, eligibilityReason);
            }

            nint kernel32 = GameHookNativeMethods.GetModuleHandleW("kernel32.dll");
            nint loadLibraryW = kernel32 == 0
                ? 0
                : GameHookNativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibraryW == 0)
                return Failure(MinecraftHookInjectionResult.RemoteThreadFailed, eligibilityReason);

            thread = GameHookNativeMethods.CreateRemoteThread(
                process,
                0,
                0,
                loadLibraryW,
                remotePath,
                0,
                out _);
            if (thread == 0)
                return Failure(MinecraftHookInjectionResult.RemoteThreadFailed, eligibilityReason);

            uint wait = GameHookNativeMethods.WaitForSingleObject(thread, timeoutMilliseconds);
            if (wait == GameHookNativeMethods.WaitTimeout)
            {
                QueueDeferredCleanup(process, thread, remotePath);
                deferredCleanup = true;
                return new(MinecraftHookInjectionResult.TimedOut, 0, eligibilityReason);
            }
            if (wait != GameHookNativeMethods.WaitObject0 ||
                !GameHookNativeMethods.GetExitCodeThread(thread, out uint moduleResult) ||
                moduleResult == 0)
            {
                return Failure(MinecraftHookInjectionResult.LoadLibraryFailed, eligibilityReason);
            }

            return new(MinecraftHookInjectionResult.Success, 0, eligibilityReason);
        }
        finally
        {
            if (!deferredCleanup)
            {
                if (remotePath != 0 && process != 0)
                    _ = GameHookNativeMethods.VirtualFreeEx(
                        process,
                        remotePath,
                        0,
                        GameHookNativeMethods.MemRelease);
                if (thread != 0) _ = GameHookNativeMethods.CloseHandle(thread);
                if (process != 0) _ = GameHookNativeMethods.CloseHandle(process);
            }
        }
    }

    private static MinecraftHookInjectionAttempt Failure(
        MinecraftHookInjectionResult result,
        MinecraftHookEligibilityReason eligibilityReason) =>
        new(result, Marshal.GetLastWin32Error(), eligibilityReason);

    private static void QueueDeferredCleanup(nint process, nint thread, nint remotePath)
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                _ = GameHookNativeMethods.WaitForSingleObject(state.Thread, GameHookNativeMethods.Infinite);
                _ = GameHookNativeMethods.VirtualFreeEx(
                    state.Process,
                    state.RemotePath,
                    0,
                    GameHookNativeMethods.MemRelease);
                _ = GameHookNativeMethods.CloseHandle(state.Thread);
                _ = GameHookNativeMethods.CloseHandle(state.Process);
            },
            (Process: process, Thread: thread, RemotePath: remotePath),
            preferLocal: false);
    }
}
