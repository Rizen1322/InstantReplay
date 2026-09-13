#include <windows.h>

#include "hook_lifetime.h"

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    DisableThreadLibraryCalls(instance);
    HANDLE thread = CreateThread(
        NULL,
        0,
        &aura_hook_control_thread,
        instance,
        0,
        NULL);
    if (thread == NULL) return FALSE;
    CloseHandle(thread);
    return TRUE;
}
