#ifndef AURA_GAME_HOOK_IPC_H
#define AURA_GAME_HOOK_IPC_H

#include <stdbool.h>
#include <windows.h>

#include "game_hook_protocol.h"

typedef struct aura_hook_ipc {
    HANDLE bootstrap_mapping;
    aura_game_hook_bootstrap_header *bootstrap;
    HANDLE frame_mapping;
    aura_game_hook_header *header;
    HANDLE frame_ready_event;
    HANDLE control_event;
    HANDLE controller_process;
} aura_hook_ipc;

bool aura_hook_ipc_open(aura_hook_ipc *ipc);
void aura_hook_ipc_close(aura_hook_ipc *ipc);
bool aura_hook_ipc_should_stop(const aura_hook_ipc *ipc);
void aura_hook_ipc_set_state(aura_hook_ipc *ipc, int32_t state, int32_t error);
void aura_hook_ipc_heartbeat(aura_hook_ipc *ipc);

#endif
