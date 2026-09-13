#include "ipc.h"

#include <stdio.h>
#include <string.h>

static bool build_names(
    const aura_game_hook_bootstrap_header *bootstrap,
    wchar_t *frame_mapping,
    size_t frame_mapping_count,
    wchar_t *frame_event,
    size_t frame_event_count,
    wchar_t *control_event,
    size_t control_event_count)
{
    if (bootstrap->nonce_byte_count != 32) {
        return false;
    }

    char nonce_utf8[33] = {0};
    memcpy(nonce_utf8, bootstrap->nonce, 32);
    wchar_t nonce[33] = {0};
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, nonce_utf8, 32, nonce, 32) != 32) {
        return false;
    }

    int mapping_length = _snwprintf_s(
        frame_mapping,
        frame_mapping_count,
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%ld.%ld.%ls.Frames",
        bootstrap->controller_pid,
        bootstrap->target_pid,
        nonce);
    int frame_length = _snwprintf_s(
        frame_event,
        frame_event_count,
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%ld.%ld.%ls.FrameReady",
        bootstrap->controller_pid,
        bootstrap->target_pid,
        nonce);
    int control_length = _snwprintf_s(
        control_event,
        control_event_count,
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%ld.%ld.%ls.Control",
        bootstrap->controller_pid,
        bootstrap->target_pid,
        nonce);
    return mapping_length > 0 && frame_length > 0 && control_length > 0;
}

bool aura_hook_ipc_open(aura_hook_ipc *ipc)
{
    memset(ipc, 0, sizeof(*ipc));

    wchar_t bootstrap_name[128] = {0};
    if (_snwprintf_s(
            bootstrap_name,
            _countof(bootstrap_name),
            _TRUNCATE,
            L"Local\\Aura.GameCapture.%lu.Bootstrap",
            GetCurrentProcessId()) <= 0) {
        return false;
    }

    ipc->bootstrap_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, bootstrap_name);
    if (ipc->bootstrap_mapping == NULL) {
        return false;
    }
    ipc->bootstrap = (aura_game_hook_bootstrap_header *)MapViewOfFile(
        ipc->bootstrap_mapping,
        FILE_MAP_READ,
        0,
        0,
        AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE);
    if (ipc->bootstrap == NULL ||
        ipc->bootstrap->magic != AURA_GAME_HOOK_BOOTSTRAP_MAGIC ||
        ipc->bootstrap->version != AURA_GAME_HOOK_VERSION ||
        ipc->bootstrap->header_size != AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE ||
        ipc->bootstrap->target_pid != (int32_t)GetCurrentProcessId() ||
        ipc->bootstrap->controller_pid <= 0) {
        aura_hook_ipc_close(ipc);
        return false;
    }

    wchar_t frame_mapping_name[256] = {0};
    wchar_t frame_event_name[256] = {0};
    wchar_t control_event_name[256] = {0};
    if (!build_names(
            ipc->bootstrap,
            frame_mapping_name,
            _countof(frame_mapping_name),
            frame_event_name,
            _countof(frame_event_name),
            control_event_name,
            _countof(control_event_name))) {
        aura_hook_ipc_close(ipc);
        return false;
    }

    ipc->frame_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, frame_mapping_name);
    if (ipc->frame_mapping == NULL) {
        aura_hook_ipc_close(ipc);
        return false;
    }
    ipc->header = (aura_game_hook_header *)MapViewOfFile(
        ipc->frame_mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        0);
    if (ipc->header == NULL ||
        ipc->header->magic != AURA_GAME_HOOK_MAGIC ||
        ipc->header->version != AURA_GAME_HOOK_VERSION ||
        ipc->header->header_size != AURA_GAME_HOOK_HEADER_SIZE ||
        ipc->header->controller_pid != ipc->bootstrap->controller_pid ||
        ipc->header->target_pid != ipc->bootstrap->target_pid ||
        ipc->header->target_hwnd != ipc->bootstrap->target_hwnd) {
        aura_hook_ipc_close(ipc);
        return false;
    }

    ipc->frame_ready_event = OpenEventW(EVENT_MODIFY_STATE, FALSE, frame_event_name);
    ipc->control_event = OpenEventW(SYNCHRONIZE, FALSE, control_event_name);
    ipc->controller_process = OpenProcess(
        SYNCHRONIZE,
        FALSE,
        (DWORD)ipc->bootstrap->controller_pid);
    if (ipc->frame_ready_event == NULL ||
        ipc->control_event == NULL ||
        ipc->controller_process == NULL) {
        aura_hook_ipc_close(ipc);
        return false;
    }

    return true;
}

void aura_hook_ipc_close(aura_hook_ipc *ipc)
{
    if (ipc->controller_process != NULL) CloseHandle(ipc->controller_process);
    if (ipc->control_event != NULL) CloseHandle(ipc->control_event);
    if (ipc->frame_ready_event != NULL) CloseHandle(ipc->frame_ready_event);
    if (ipc->header != NULL) UnmapViewOfFile(ipc->header);
    if (ipc->frame_mapping != NULL) CloseHandle(ipc->frame_mapping);
    if (ipc->bootstrap != NULL) UnmapViewOfFile(ipc->bootstrap);
    if (ipc->bootstrap_mapping != NULL) CloseHandle(ipc->bootstrap_mapping);
    memset(ipc, 0, sizeof(*ipc));
}

bool aura_hook_ipc_should_stop(const aura_hook_ipc *ipc)
{
    if (ipc->header == NULL) return true;
    if (InterlockedCompareExchange(
            (volatile LONG *)&ipc->header->command,
            0,
            0) == AURA_GAME_HOOK_COMMAND_STOP) {
        return true;
    }
    return WaitForSingleObject(ipc->controller_process, 0) != WAIT_TIMEOUT;
}

void aura_hook_ipc_set_state(aura_hook_ipc *ipc, int32_t state, int32_t error)
{
    if (ipc->header == NULL) return;
    InterlockedExchange((volatile LONG *)&ipc->header->error, error);
    InterlockedExchange((volatile LONG *)&ipc->header->state, state);
}

void aura_hook_ipc_heartbeat(aura_hook_ipc *ipc)
{
    if (ipc->header == NULL) return;
    LONG64 now = (LONG64)(GetTickCount64() * UINT64_C(10000));
    InterlockedExchange64((volatile LONG64 *)&ipc->header->hook_heartbeat_100ns, now);
}
