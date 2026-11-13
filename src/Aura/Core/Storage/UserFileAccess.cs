using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Aura.Core.Storage;

internal static class UserFileAccess
{
    // Paths from settings must not turn automatic cleanup into an elevated delete operation.
    public static void Run(Action action)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            action();
            return;
        }
        if (!GetTokenInformation(identity.AccessToken, 19, out var token, IntPtr.Size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось понизить права для уборки файлов.");
        using var limited = new SafeAccessTokenHandle(token);
        WindowsIdentity.RunImpersonated(limited, action);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out IntPtr information, int length, out int returnedLength);
}
