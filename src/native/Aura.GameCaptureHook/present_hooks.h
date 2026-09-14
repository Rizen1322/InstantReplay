#ifndef AURA_GAME_CAPTURE_PRESENT_HOOKS_H
#define AURA_GAME_CAPTURE_PRESENT_HOOKS_H

#include <stdbool.h>
#include <windows.h>

#include "ipc.h"

bool aura_present_hooks_install(aura_hook_ipc *ipc);

/*
 * Отключить перехват, не снимая его.
 *
 * Разделено на два шага намеренно. MH_RemoveHook освобождает трамплины, и делать
 * это, пока чужой поток ещё выполняется внутри нашего обработчика, нельзя. Между
 * этими вызовами поток управления ждёт, пока опустеет счётчик активных вызовов.
 */
void aura_present_hooks_disable(void);

void aura_present_hooks_remove(void);
LONG aura_present_hooks_active_callbacks(void);

#endif
