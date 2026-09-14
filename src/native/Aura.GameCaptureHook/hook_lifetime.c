#include "hook_lifetime.h"

#include "ipc.h"
#include "gl_capture.h"
#include "present_hooks.h"

DWORD WINAPI aura_hook_control_thread(void *module_pointer)
{
    HMODULE module = (HMODULE)module_pointer;
    aura_hook_ipc ipc;
    bool opened = false;

    for (int attempt = 0; attempt < 40 && !opened; ++attempt) {
        opened = aura_hook_ipc_open(&ipc);
        if (!opened) Sleep(50);
    }
    if (!opened) {
        FreeLibraryAndExitThread(module, 1);
    }

    aura_hook_ipc_set_state(&ipc, AURA_GAME_HOOK_STATE_STARTING, AURA_GAME_HOOK_ERROR_NONE);
    aura_hook_ipc_heartbeat(&ipc);
    if (!aura_present_hooks_install(&ipc)) {
        aura_hook_ipc_set_state(
            &ipc,
            AURA_GAME_HOOK_STATE_FAILED,
            AURA_GAME_HOOK_ERROR_HOOK_INSTALLATION_FAILED);
        Sleep(100);
        aura_hook_ipc_close(&ipc);
        FreeLibraryAndExitThread(module, 2);
    }

    aura_hook_ipc_set_state(&ipc, AURA_GAME_HOOK_STATE_READY, AURA_GAME_HOOK_ERROR_NONE);
    while (!aura_hook_ipc_should_stop(&ipc)) {
        aura_hook_ipc_heartbeat(&ipc);
        LONG command = InterlockedCompareExchange(
            (volatile LONG *)&ipc.header->command,
            0,
            0);
        LONG error = InterlockedCompareExchange(
            (volatile LONG *)&ipc.header->error,
            0,
            0);
        if (error == AURA_GAME_HOOK_ERROR_NONE) {
            aura_hook_ipc_set_state(
                &ipc,
                command == AURA_GAME_HOOK_COMMAND_CAPTURE
                    ? AURA_GAME_HOOK_STATE_CAPTURING
                    : AURA_GAME_HOOK_STATE_READY,
                AURA_GAME_HOOK_ERROR_NONE);
        }
        Sleep(25);
    }

    aura_hook_ipc_set_state(&ipc, AURA_GAME_HOOK_STATE_STOPPING, AURA_GAME_HOOK_ERROR_NONE);

    // Шаг 1. Пока перехват ещё стоит, просим поток отрисовки игры освободить
    // кольцо PBO: три буфера по размеру кадра живут в её контексте OpenGL, и
    // никто, кроме её собственного потока, удалить их не может. Без этого шага
    // каждый цикл остановки и запуска терял десятки мегабайт видеопамяти игры.
    aura_gl_capture_request_release();
    for (int attempt = 0; attempt < 500 && !aura_gl_capture_release_done(); ++attempt) {
        Sleep(1);
    }

    // Шаг 2. Отключаем перехват и ТОЛЬКО ПОТОМ ждём, пока чужие потоки выйдут из
    // наших обработчиков. Раньше трамплины снимались до ожидания, то есть память
    // под кодом освобождалась, пока по нему ещё могли идти.
    aura_present_hooks_disable();
    for (int attempt = 0; attempt < 200 && aura_present_hooks_active_callbacks() != 0; ++attempt) {
        Sleep(1);
    }

    // Шаг 3. Теперь снятие безопасно.
    aura_present_hooks_remove();
    aura_hook_ipc_set_state(&ipc, AURA_GAME_HOOK_STATE_STOPPED, AURA_GAME_HOOK_ERROR_NONE);
    aura_hook_ipc_heartbeat(&ipc);
    aura_hook_ipc_close(&ipc);
    FreeLibraryAndExitThread(module, 0);
}
