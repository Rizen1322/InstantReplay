#ifndef AURA_GAME_CAPTURE_PRESENT_HOOKS_H
#define AURA_GAME_CAPTURE_PRESENT_HOOKS_H

#include <stdbool.h>
#include <windows.h>

#include "ipc.h"

bool aura_present_hooks_install(aura_hook_ipc *ipc);
void aura_present_hooks_remove(void);
LONG aura_present_hooks_active_callbacks(void);

#endif
