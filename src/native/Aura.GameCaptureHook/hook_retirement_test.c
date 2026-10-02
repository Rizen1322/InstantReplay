// Deterministic driver-timeout fixture for the real native control-thread logic.
#include <windows.h>
#include <stdio.h>
#include "ipc.h"
#include "gl_capture.h"
#include "present_hooks.h"

static void test_sleep(DWORD milliseconds) { (void)milliseconds; }
static _Noreturn void test_unload(HMODULE module, DWORD exit_code)
{
    (void)module;
    ExitThread(exit_code);
}
#define Sleep test_sleep
#define FreeLibraryAndExitThread test_unload
#include "hook_lifetime.c"
#undef Sleep
#undef FreeLibraryAndExitThread

static aura_game_hook_header test_header;
static int callback_checks;
static bool premature_close;
static bool closed;

bool aura_hook_ipc_open(aura_hook_ipc *ipc)
{
    memset(ipc, 0, sizeof(*ipc));
    ipc->header = &test_header;
    return true;
}
void aura_hook_ipc_close(aura_hook_ipc *ipc)
{
    // An active callback still owns this pointer, even though hooks are disabled.
    premature_close = callback_checks <= 2003;
    closed = true;
    ipc->header = NULL;
}
bool aura_hook_ipc_should_stop(const aura_hook_ipc *ipc) { (void)ipc; return true; }
void aura_hook_ipc_set_state(aura_hook_ipc *ipc, int32_t state, int32_t error)
{
    ipc->header->state = state;
    ipc->header->error = error;
}
void aura_hook_ipc_heartbeat(aura_hook_ipc *ipc) { (void)ipc; }
void aura_gl_capture_request_release(void) { }
bool aura_gl_capture_release_done(void) { return true; }
bool aura_present_hooks_install(aura_hook_ipc *ipc) { return ipc->header != NULL; }
void aura_present_hooks_disable(void) { }
void aura_present_hooks_remove(void) { }
LONG aura_present_hooks_active_callbacks(void)
{
    // Return from the driver only AFTER the initial two-second retirement budget.
    return ++callback_checks <= 2003 ? 1 : 0;
}

int main(void)
{
    HANDLE worker = CreateThread(NULL, 0, aura_hook_control_thread, NULL, 0, NULL);
    if (worker == NULL) return 2;
    if (WaitForSingleObject(worker, 5000) != WAIT_OBJECT_0) return 3;
    CloseHandle(worker);
    if (!closed || premature_close) {
        fprintf(stderr, "FAIL: native IPC closed while driver callback was still active\n");
        return 1;
    }
    puts("Native IPC retirement waits for the late driver callback");
    return 0;
}
