#include <windows.h>

#include "game_hook_protocol.h"
#include "hook_exports.h"

uint32_t AuraGameCaptureProtocolMagic(void)
{
    return AURA_GAME_HOOK_MAGIC;
}

uint16_t AuraGameCaptureProtocolVersion(void)
{
    return AURA_GAME_HOOK_VERSION;
}

uint32_t AuraGameCaptureHeaderSize(void)
{
    return AURA_GAME_HOOK_HEADER_SIZE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reason;
    (void)reserved;
    return TRUE;
}
