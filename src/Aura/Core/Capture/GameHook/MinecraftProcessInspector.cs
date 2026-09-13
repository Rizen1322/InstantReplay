using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Aura.Core.Capture.GameHook;

/// <summary>Собирает свежие свойства процесса перед чистой eligibility-проверкой.</summary>
[SupportedOSPlatform("windows")]
internal static class MinecraftProcessInspector
{
    private const int TokenUser = 1;
    private const int TokenIntegrityLevel = 25;
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const int ProcessProtectionLevelInfo = 7;

    public static MinecraftHookEligibilitySnapshot Inspect(in GameCaptureTarget target)
    {
        using Process process = Process.GetProcessById(target.ProcessId);
        nint processHandle = GameHookNativeMethods.OpenProcess(
            GameHookProcessAccess.QueryLimitedInformation | GameHookProcessAccess.VmRead,
            inheritHandle: false,
            target.ProcessId);
        if (processHandle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            string executable = Path.HasExtension(process.ProcessName)
                ? process.ProcessName
                : process.ProcessName + ".exe";
            string[] modules = process.Modules
                .Cast<ProcessModule>()
                .Select(module => module.ModuleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            bool is64Bit = GameHookNativeMethods.IsWow64Process2(
                               processHandle,
                               out ushort processMachine,
                               out ushort nativeMachine) &&
                           nativeMachine == ImageFileMachineAmd64 &&
                           processMachine == ImageFileMachineUnknown;

            ReadTokenIdentity(processHandle, out string processSid, out int processIntegrity);
            string currentSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            int currentIntegrity = ReadCurrentIntegrity();
            bool protectedOrUnknown = !GameHookNativeMethods.GetProcessInformation(
                                          processHandle,
                                          ProcessProtectionLevelInfo,
                                          out GameHookNativeMethods.ProcessProtectionLevelInformation protection,
                                          Marshal.SizeOf<GameHookNativeMethods.ProcessProtectionLevelInformation>()) ||
                                      protection.ProtectionLevel != GameHookNativeMethods.ProtectionLevelNone;

            nint root = GameHookNativeMethods.GetAncestor(target.Hwnd, GameHookNativeMethods.GaRoot);
            if (root == 0) root = target.Hwnd;

            return new MinecraftHookEligibilitySnapshot(
                Hwnd: target.Hwnd,
                RootHwnd: root,
                ForegroundHwnd: GameHookNativeMethods.GetForegroundWindow(),
                ProcessId: process.Id,
                ProcessStartTicks: process.StartTime.ToUniversalTime().Ticks,
                ExecutableName: executable,
                GameName: target.GameName,
                MonitorIndex: target.MonitorIndex,
                IsProcessAlive: !process.HasExited,
                Is64BitProcess: is64Bit,
                Is64BitController: Environment.Is64BitProcess,
                ProcessUserSid: processSid,
                CurrentUserSid: currentSid,
                ProcessIntegrityLevel: processIntegrity,
                CurrentIntegrityLevel: currentIntegrity,
                IsProtectedProcess: protectedOrUnknown,
                LoadedModuleNames: modules);
        }
        finally
        {
            _ = GameHookNativeMethods.CloseHandle(processHandle);
        }
    }

    private static int ReadCurrentIntegrity()
    {
        using Process current = Process.GetCurrentProcess();
        ReadTokenIdentity(current.Handle, out _, out int integrity);
        return integrity;
    }

    private static void ReadTokenIdentity(nint process, out string sid, out int integrity)
    {
        sid = string.Empty;
        integrity = 0;
        if (!GameHookNativeMethods.OpenProcessToken(
                process,
                GameHookNativeMethods.TokenQuery,
                out nint token))
        {
            return;
        }

        try
        {
            sid = ReadSid(token, TokenUser);
            integrity = ReadIntegrity(token);
        }
        finally
        {
            _ = GameHookNativeMethods.CloseHandle(token);
        }
    }

    private static string ReadSid(nint token, int informationClass)
    {
        nint buffer = ReadTokenBuffer(token, informationClass, out _);
        if (buffer == 0) return string.Empty;
        try
        {
            nint sidPointer = Marshal.ReadIntPtr(buffer);
            return new SecurityIdentifier(sidPointer).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadIntegrity(nint token)
    {
        nint buffer = ReadTokenBuffer(token, TokenIntegrityLevel, out _);
        if (buffer == 0) return 0;
        try
        {
            nint sidPointer = Marshal.ReadIntPtr(buffer);
            var sid = new SecurityIdentifier(sidPointer);
            return int.Parse(sid.Value.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint ReadTokenBuffer(nint token, int informationClass, out int length)
    {
        _ = GameHookNativeMethods.GetTokenInformation(token, informationClass, 0, 0, out length);
        if (length <= 0) return 0;
        nint buffer = Marshal.AllocHGlobal(length);
        if (GameHookNativeMethods.GetTokenInformation(
                token,
                informationClass,
                buffer,
                length,
                out _))
        {
            return buffer;
        }

        Marshal.FreeHGlobal(buffer);
        return 0;
    }
}
