#ifndef AURA_GAME_CAPTURE_GL_CAPTURE_H
#define AURA_GAME_CAPTURE_GL_CAPTURE_H

#include <stdbool.h>
#include <windows.h>

#include "ipc.h"

typedef enum aura_gl_capture_result {
    AURA_GL_CAPTURE_SKIPPED = 0,
    AURA_GL_CAPTURE_ISSUED = 1,
    AURA_GL_CAPTURE_PUBLISHED = 2,
    AURA_GL_CAPTURE_UNSUPPORTED = 3,
    AURA_GL_CAPTURE_FAILED = 4
} aura_gl_capture_result;

aura_gl_capture_result aura_gl_capture_present(aura_hook_ipc *ipc, HDC dc);

/*
 * Попросить поток отрисовки игры освободить кольцо PBO.
 *
 * Буферы живут в контексте OpenGL игры, а останавливает захват поток управления,
 * у которого текущего контекста нет. Удалить их оттуда нельзя. Поэтому здесь
 * поднимается флаг, следующий вызов present освобождает кольцо и подтверждает
 * это, а поток управления ждёт подтверждения перед снятием хуков.
 */
void aura_gl_capture_request_release(void);

/* Освободило ли кольцо PBO после aura_gl_capture_request_release. */
bool aura_gl_capture_release_done(void);

void aura_gl_capture_abandon(void);

#endif
