#include "game_hook_protocol.h"

int main(void)
{
    return sizeof(aura_game_hook_header) == AURA_GAME_HOOK_HEADER_SIZE &&
                   sizeof(aura_game_hook_frame_slot_header) == AURA_GAME_HOOK_SLOT_HEADER_SIZE
               ? 0
               : 1;
}
