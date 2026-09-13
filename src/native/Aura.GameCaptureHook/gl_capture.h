#ifndef AURA_GAME_CAPTURE_GL_CAPTURE_H
#define AURA_GAME_CAPTURE_GL_CAPTURE_H

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
void aura_gl_capture_abandon(void);

#endif
