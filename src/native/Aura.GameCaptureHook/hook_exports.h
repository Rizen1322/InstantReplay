#ifndef AURA_GAME_HOOK_EXPORTS_H
#define AURA_GAME_HOOK_EXPORTS_H

#include <stdint.h>

#if defined(_WIN32)
#define AURA_HOOK_EXPORT __declspec(dllexport)
#else
#define AURA_HOOK_EXPORT
#endif

AURA_HOOK_EXPORT uint32_t AuraGameCaptureProtocolMagic(void);
AURA_HOOK_EXPORT uint16_t AuraGameCaptureProtocolVersion(void);
AURA_HOOK_EXPORT uint32_t AuraGameCaptureHeaderSize(void);

#endif
