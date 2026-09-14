#ifndef AURA_GAME_HOOK_PROTOCOL_H
#define AURA_GAME_HOOK_PROTOCOL_H

#include <stddef.h>
#include <stdint.h>

#define AURA_GAME_HOOK_MAGIC UINT32_C(0x48475541)
#define AURA_GAME_HOOK_BOOTSTRAP_MAGIC UINT32_C(0x42475541)
#define AURA_GAME_HOOK_VERSION UINT16_C(2)
#define AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE 256
#define AURA_GAME_HOOK_HEADER_SIZE 256
#define AURA_GAME_HOOK_SLOT_HEADER_SIZE 64
#define AURA_GAME_HOOK_SLOT_COUNT 3
#define AURA_GAME_HOOK_PIXEL_FORMAT_BGRA8 1
#define AURA_GAME_HOOK_MAX_WIDTH 7680
#define AURA_GAME_HOOK_MAX_HEIGHT 4320

typedef enum aura_game_hook_command {
    AURA_GAME_HOOK_COMMAND_IDLE = 0,
    AURA_GAME_HOOK_COMMAND_CAPTURE = 1,
    AURA_GAME_HOOK_COMMAND_STOP = 2
} aura_game_hook_command;

typedef enum aura_game_hook_state {
    AURA_GAME_HOOK_STATE_EMPTY = 0,
    AURA_GAME_HOOK_STATE_STARTING = 1,
    AURA_GAME_HOOK_STATE_READY = 2,
    AURA_GAME_HOOK_STATE_CAPTURING = 3,
    AURA_GAME_HOOK_STATE_STOPPING = 4,
    AURA_GAME_HOOK_STATE_STOPPED = 5,
    AURA_GAME_HOOK_STATE_FAILED = 6
} aura_game_hook_state;

typedef enum aura_game_hook_error {
    AURA_GAME_HOOK_ERROR_NONE = 0,
    AURA_GAME_HOOK_ERROR_PROTOCOL_MISMATCH = 1,
    AURA_GAME_HOOK_ERROR_TARGET_MISMATCH = 2,
    AURA_GAME_HOOK_ERROR_UNSUPPORTED_OPENGL_READBACK = 3,
    AURA_GAME_HOOK_ERROR_SHARED_MEMORY_UNAVAILABLE = 4,
    AURA_GAME_HOOK_ERROR_HOOK_INSTALLATION_FAILED = 5,
    AURA_GAME_HOOK_ERROR_CAPTURE_FAILED = 6
} aura_game_hook_error;

typedef struct aura_game_hook_bootstrap_header {
    uint32_t magic;
    uint16_t version;
    uint16_t header_size;
    int32_t controller_pid;
    int32_t target_pid;
    int64_t target_process_start_ticks;
    uint64_t target_hwnd;
    int32_t nonce_byte_count;
    int32_t reserved;
    uint8_t nonce[32];
    uint8_t reserved_tail[184];
} aura_game_hook_bootstrap_header;

typedef struct aura_game_hook_header {
    uint32_t magic;
    uint16_t version;
    uint16_t header_size;
    int64_t mapping_size;
    int32_t controller_pid;
    int32_t target_pid;
    int64_t target_process_start_ticks;
    uint64_t target_hwnd;
    int64_t target_revision;
    int64_t capture_generation;
    int64_t route_epoch;
    int32_t command;
    int32_t state;
    int32_t error;
    int32_t target_fps;
    int32_t width;
    int32_t height;
    int32_t stride;
    int32_t pixel_format;
    int32_t slot_count;
    int32_t slot_header_size;
    int64_t slot_stride;
    int64_t newest_sequence;
    int64_t controller_heartbeat_100ns;
    int64_t hook_heartbeat_100ns;
    int64_t frames_issued;
    int64_t frames_published;
    int64_t frames_dropped;
    uint8_t reserved[96];
} aura_game_hook_header;

typedef struct aura_game_hook_frame_slot_header {
    int64_t sequence_lock;
    int64_t frame_sequence;
    int64_t timestamp_100ns;
    int64_t route_epoch;
    int32_t width;
    int32_t height;
    int32_t stride;
    int32_t byte_count;
    /*
     * Виден ли курсор ГЛАЗАМИ САМОЙ ИГРЫ на момент кадра.
     *
     * Снаружи это не выясняется. Счётчик показа курсора у ShowCursor ведётся на
     * очередь ввода потока, поэтому GetCursorInfo из чужого процесса сообщает
     * состояние для СВОЕЙ очереди. Minecraft в полноэкранном режиме прячет курсор
     * у себя, а контроллер видел «показан» и рисовал стрелку посреди записи.
     * Хук живёт внутри игры и на её же потоке, поэтому читает верное значение.
     */
    int32_t cursor_visible;
    uint8_t reserved[12];
} aura_game_hook_frame_slot_header;

_Static_assert(sizeof(aura_game_hook_header) == AURA_GAME_HOOK_HEADER_SIZE,
               "game hook header size changed");
_Static_assert(sizeof(aura_game_hook_frame_slot_header) == AURA_GAME_HOOK_SLOT_HEADER_SIZE,
               "game hook slot header size changed");
_Static_assert(sizeof(aura_game_hook_bootstrap_header) == AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE,
               "game hook bootstrap header size changed");
_Static_assert(_Alignof(aura_game_hook_header) >= 8, "header atomics must be aligned");
_Static_assert(_Alignof(aura_game_hook_frame_slot_header) >= 8, "slot atomics must be aligned");
_Static_assert(_Alignof(aura_game_hook_bootstrap_header) >= 8, "bootstrap atomics must be aligned");

_Static_assert(offsetof(aura_game_hook_bootstrap_header, magic) == 0,
               "bootstrap magic offset changed");
_Static_assert(offsetof(aura_game_hook_bootstrap_header, controller_pid) == 8,
               "bootstrap controller PID offset changed");
_Static_assert(offsetof(aura_game_hook_bootstrap_header, target_pid) == 12,
               "bootstrap target PID offset changed");
_Static_assert(offsetof(aura_game_hook_bootstrap_header, target_process_start_ticks) == 16,
               "bootstrap process identity offset changed");
_Static_assert(offsetof(aura_game_hook_bootstrap_header, target_hwnd) == 24,
               "bootstrap HWND offset changed");
_Static_assert(offsetof(aura_game_hook_bootstrap_header, nonce_byte_count) == 32,
               "bootstrap nonce length offset changed");
_Static_assert(offsetof(aura_game_hook_bootstrap_header, nonce) == 40,
               "bootstrap nonce offset changed");

#define AURA_ASSERT_HEADER_OFFSET(field, expected) \
    _Static_assert(offsetof(aura_game_hook_header, field) == (expected), \
                   "game hook header offset changed: " #field)

AURA_ASSERT_HEADER_OFFSET(magic, 0);
AURA_ASSERT_HEADER_OFFSET(version, 4);
AURA_ASSERT_HEADER_OFFSET(header_size, 6);
AURA_ASSERT_HEADER_OFFSET(mapping_size, 8);
AURA_ASSERT_HEADER_OFFSET(controller_pid, 16);
AURA_ASSERT_HEADER_OFFSET(target_pid, 20);
AURA_ASSERT_HEADER_OFFSET(target_process_start_ticks, 24);
AURA_ASSERT_HEADER_OFFSET(target_hwnd, 32);
AURA_ASSERT_HEADER_OFFSET(target_revision, 40);
AURA_ASSERT_HEADER_OFFSET(capture_generation, 48);
AURA_ASSERT_HEADER_OFFSET(route_epoch, 56);
AURA_ASSERT_HEADER_OFFSET(command, 64);
AURA_ASSERT_HEADER_OFFSET(state, 68);
AURA_ASSERT_HEADER_OFFSET(error, 72);
AURA_ASSERT_HEADER_OFFSET(target_fps, 76);
AURA_ASSERT_HEADER_OFFSET(width, 80);
AURA_ASSERT_HEADER_OFFSET(height, 84);
AURA_ASSERT_HEADER_OFFSET(stride, 88);
AURA_ASSERT_HEADER_OFFSET(pixel_format, 92);
AURA_ASSERT_HEADER_OFFSET(slot_count, 96);
AURA_ASSERT_HEADER_OFFSET(slot_header_size, 100);
AURA_ASSERT_HEADER_OFFSET(slot_stride, 104);
AURA_ASSERT_HEADER_OFFSET(newest_sequence, 112);
AURA_ASSERT_HEADER_OFFSET(controller_heartbeat_100ns, 120);
AURA_ASSERT_HEADER_OFFSET(hook_heartbeat_100ns, 128);
AURA_ASSERT_HEADER_OFFSET(frames_issued, 136);
AURA_ASSERT_HEADER_OFFSET(frames_published, 144);
AURA_ASSERT_HEADER_OFFSET(frames_dropped, 152);

#define AURA_ASSERT_SLOT_OFFSET(field, expected) \
    _Static_assert(offsetof(aura_game_hook_frame_slot_header, field) == (expected), \
                   "game hook slot offset changed: " #field)

AURA_ASSERT_SLOT_OFFSET(sequence_lock, 0);
AURA_ASSERT_SLOT_OFFSET(frame_sequence, 8);
AURA_ASSERT_SLOT_OFFSET(timestamp_100ns, 16);
AURA_ASSERT_SLOT_OFFSET(route_epoch, 24);
AURA_ASSERT_SLOT_OFFSET(width, 32);
AURA_ASSERT_SLOT_OFFSET(height, 36);
AURA_ASSERT_SLOT_OFFSET(stride, 40);
AURA_ASSERT_SLOT_OFFSET(byte_count, 44);
AURA_ASSERT_SLOT_OFFSET(cursor_visible, 48);

#undef AURA_ASSERT_HEADER_OFFSET
#undef AURA_ASSERT_SLOT_OFFSET

#endif
