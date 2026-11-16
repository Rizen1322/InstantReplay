#include "gl_capture.h"

#include <GL/gl.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#define GL_PIXEL_PACK_BUFFER 0x88EB
#define GL_PIXEL_PACK_BUFFER_BINDING 0x88ED
#define GL_STREAM_READ 0x88E1
#define GL_READ_ONLY 0x88B8
#define GL_READ_FRAMEBUFFER_BINDING 0x8CAA
#define GL_READ_FRAMEBUFFER 0x8CA8
#define GL_BGRA 0x80E1
#define GL_SYNC_GPU_COMMANDS_COMPLETE 0x9117
#define GL_ALREADY_SIGNALED 0x911A
#define GL_CONDITION_SATISFIED 0x911C

typedef struct __GLsync *GLsync;
typedef GLsync(APIENTRY *aura_gl_fence_sync_fn)(GLenum, GLbitfield);
typedef GLenum(APIENTRY *aura_gl_client_wait_sync_fn)(GLsync, GLbitfield, uint64_t);
typedef void(APIENTRY *aura_gl_delete_sync_fn)(GLsync);

typedef void(APIENTRY *aura_gl_gen_buffers_fn)(GLsizei, GLuint *);
typedef void(APIENTRY *aura_gl_delete_buffers_fn)(GLsizei, const GLuint *);
typedef void(APIENTRY *aura_gl_bind_buffer_fn)(GLenum, GLuint);
typedef void(APIENTRY *aura_gl_buffer_data_fn)(GLenum, ptrdiff_t, const void *, GLenum);
typedef void *(APIENTRY *aura_gl_map_buffer_fn)(GLenum, GLenum);
typedef GLboolean(APIENTRY *aura_gl_unmap_buffer_fn)(GLenum);
typedef void(APIENTRY *aura_gl_bind_framebuffer_fn)(GLenum, GLuint);

typedef struct aura_gl_capture_state {
    aura_gl_gen_buffers_fn gen_buffers;
    aura_gl_delete_buffers_fn delete_buffers;
    aura_gl_bind_buffer_fn bind_buffer;
    aura_gl_buffer_data_fn buffer_data;
    aura_gl_map_buffer_fn map_buffer;
    aura_gl_unmap_buffer_fn unmap_buffer;
    aura_gl_bind_framebuffer_fn bind_framebuffer;
    aura_gl_fence_sync_fn fence_sync;
    aura_gl_client_wait_sync_fn client_wait_sync;
    aura_gl_delete_sync_fn delete_sync;
    HGLRC owner_context;
    GLuint pbos[3];
    /* Когда в PBO попросили кадр: это и есть время кадра, а не миг чтения.
       Читается PBO через два захвата, и штамп «сейчас» опаздывал на ~33 мс. */
    uint64_t issue_100ns[3];
    /* Готовность чтения кадра в PBO: без неё glMapBuffer на загруженной
       видеокарте останавливал поток отрисовки игры до конца копирования. */
    GLsync fences[3];
    /* Сколько раз подряд кадр пропущен из-за неготовности: при постоянной
       перегрузке пропускать всё подряд нельзя, иначе запись встанет вовсе. */
    int skipped_in_row;
    int width;
    int height;
    int stride;
    int byte_count;
    int64_t route_epoch;
    unsigned write_index;
    unsigned read_index;
    unsigned pending_count;
    uint64_t issued_count;
    uint64_t next_issue_100ns;
    int initialized;
    int unsupported;
} aura_gl_capture_state;

static aura_gl_capture_state g_capture;
static SRWLOCK g_capture_lock = SRWLOCK_INIT;
static volatile LONG g_release_requested;
static volatile LONG g_release_done;
static volatile LONG g_idle_notified;

static uint64_t now_100ns(void)
{
    static LARGE_INTEGER frequency;
    if (frequency.QuadPart == 0 && !QueryPerformanceFrequency(&frequency)) return 0;
    LARGE_INTEGER counter;
    if (!QueryPerformanceCounter(&counter)) return 0;
    return (uint64_t)counter.QuadPart / (uint64_t)frequency.QuadPart * UINT64_C(10000000) +
           ((uint64_t)counter.QuadPart % (uint64_t)frequency.QuadPart) * UINT64_C(10000000) /
               (uint64_t)frequency.QuadPart;
}

static void *load_gl_function(const char *name, const char *arb_name)
{
    PROC result = wglGetProcAddress(name);
    if (result == NULL || result == (PROC)1 || result == (PROC)2 ||
        result == (PROC)3 || result == (PROC)-1) {
        result = arb_name == NULL ? NULL : wglGetProcAddress(arb_name);
    }
    if (result == NULL || result == (PROC)1 || result == (PROC)2 ||
        result == (PROC)3 || result == (PROC)-1) {
        return NULL;
    }
    return (void *)result;
}

static int load_functions(aura_gl_capture_state *state)
{
    state->gen_buffers = (aura_gl_gen_buffers_fn)load_gl_function("glGenBuffers", "glGenBuffersARB");
    state->delete_buffers = (aura_gl_delete_buffers_fn)load_gl_function("glDeleteBuffers", "glDeleteBuffersARB");
    state->bind_buffer = (aura_gl_bind_buffer_fn)load_gl_function("glBindBuffer", "glBindBufferARB");
    state->buffer_data = (aura_gl_buffer_data_fn)load_gl_function("glBufferData", "glBufferDataARB");
    state->map_buffer = (aura_gl_map_buffer_fn)load_gl_function("glMapBuffer", "glMapBufferARB");
    state->unmap_buffer = (aura_gl_unmap_buffer_fn)load_gl_function("glUnmapBuffer", "glUnmapBufferARB");
    state->bind_framebuffer =
        (aura_gl_bind_framebuffer_fn)load_gl_function("glBindFramebuffer", "glBindFramebufferEXT");
    state->fence_sync = (aura_gl_fence_sync_fn)load_gl_function("glFenceSync", NULL);
    state->client_wait_sync = (aura_gl_client_wait_sync_fn)load_gl_function("glClientWaitSync", NULL);
    state->delete_sync = (aura_gl_delete_sync_fn)load_gl_function("glDeleteSync", NULL);
    if (state->fence_sync == NULL || state->client_wait_sync == NULL || state->delete_sync == NULL) {
        state->fence_sync = NULL;
        state->client_wait_sync = NULL;
        state->delete_sync = NULL;
    }
    return state->gen_buffers != NULL && state->delete_buffers != NULL &&
           state->bind_buffer != NULL && state->buffer_data != NULL &&
           state->map_buffer != NULL && state->unmap_buffer != NULL;
}

static void drop_fence(aura_gl_capture_state *state, unsigned index)
{
    if (state->fences[index] != NULL && state->delete_sync != NULL) state->delete_sync(state->fences[index]);
    state->fences[index] = NULL;
}

static void flush_pipeline(aura_gl_capture_state *state)
{
    if (state->initialized && state->owner_context == wglGetCurrentContext()) {
        for (unsigned index = 0; index < 3; ++index) drop_fence(state, index);
    } else {
        memset(state->fences, 0, sizeof(state->fences));
    }
    memset(state->issue_100ns, 0, sizeof(state->issue_100ns));
    state->write_index = 0;
    state->read_index = 0;
    state->pending_count = 0;
    state->skipped_in_row = 0;
    state->issued_count = 0;
    state->next_issue_100ns = 0;
}

static void release_current_context(aura_gl_capture_state *state)
{
    if (state->initialized && state->owner_context == wglGetCurrentContext()) {
        for (unsigned index = 0; index < 3; ++index) drop_fence(state, index);
        if (state->delete_buffers != NULL) state->delete_buffers(3, state->pbos);
    }
    memset(state->fences, 0, sizeof(state->fences));
    memset(state->pbos, 0, sizeof(state->pbos));
    memset(state->issue_100ns, 0, sizeof(state->issue_100ns));
    state->initialized = 0;
    state->write_index = 0;
    state->read_index = 0;
    state->pending_count = 0;
    state->skipped_in_row = 0;
    state->issued_count = 0;
    state->next_issue_100ns = 0;
}

static int initialize_ring(aura_gl_capture_state *state, int width, int height, int64_t route_epoch)
{
    HGLRC current = wglGetCurrentContext();
    if (current == NULL) return 0;
    LONG was_idle = InterlockedExchange(&g_idle_notified, 0);
    if (state->owner_context == current && state->initialized &&
        state->width == width && state->height == height) {
        if (was_idle != 0 || state->route_epoch != route_epoch) {
            flush_pipeline(state);
            state->route_epoch = route_epoch;
        }
        return 1;
    }

    if (state->owner_context == current) release_current_context(state);
    else {
        memset(state->fences, 0, sizeof(state->fences));
        memset(state->pbos, 0, sizeof(state->pbos));
        memset(state->issue_100ns, 0, sizeof(state->issue_100ns));
        state->initialized = 0;
        state->write_index = 0;
        state->read_index = 0;
        state->pending_count = 0;
        state->skipped_in_row = 0;
        state->issued_count = 0;
        state->next_issue_100ns = 0;
    }

    state->owner_context = current;
    state->unsupported = 0;
    if (!load_functions(state)) {
        state->unsupported = 1;
        return 0;
    }

    state->width = width;
    state->height = height;
    state->stride = width * 4;
    state->byte_count = state->stride * height;
    state->route_epoch = route_epoch;

    GLint previous_buffer = 0;
    glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &previous_buffer);
    state->gen_buffers(3, state->pbos);
    for (int index = 0; index < 3; ++index) {
        if (state->pbos[index] == 0) {
            state->bind_buffer(GL_PIXEL_PACK_BUFFER, (GLuint)previous_buffer);
            release_current_context(state);
            return 0;
        }
        state->bind_buffer(GL_PIXEL_PACK_BUFFER, state->pbos[index]);
        state->buffer_data(GL_PIXEL_PACK_BUFFER, (ptrdiff_t)state->byte_count, NULL, GL_STREAM_READ);
    }
    state->bind_buffer(GL_PIXEL_PACK_BUFFER, (GLuint)previous_buffer);
    state->initialized = 1;
    state->next_issue_100ns = 0;
    return 1;
}

static int publish_frame(
    aura_hook_ipc *ipc,
    const aura_gl_capture_state *state,
    const uint8_t *pixels,
    uint64_t timestamp_100ns)
{
    aura_game_hook_header *header = ipc->header;
    int64_t sequence = InterlockedCompareExchange64(
                           (volatile LONG64 *)&header->newest_sequence, 0, 0) + 1;
    int64_t slot_index = (sequence - 1) % AURA_GAME_HOOK_SLOT_COUNT;
    uint8_t *mapping = (uint8_t *)header;
    uint64_t slot_offset = AURA_GAME_HOOK_HEADER_SIZE +
                           (uint64_t)slot_index * (uint64_t)header->slot_stride;
    uint64_t payload_offset = slot_offset + AURA_GAME_HOOK_SLOT_HEADER_SIZE;
    if (header->slot_stride < AURA_GAME_HOOK_SLOT_HEADER_SIZE + state->byte_count ||
        payload_offset + (uint64_t)state->byte_count > (uint64_t)header->mapping_size) return 0;

    aura_game_hook_frame_slot_header *slot =
        (aura_game_hook_frame_slot_header *)(mapping + slot_offset);
    LONG64 lock_value = InterlockedCompareExchange64(
        (volatile LONG64 *)&slot->sequence_lock, 0, 0);
    if ((lock_value & 1) != 0) ++lock_value;
    InterlockedExchange64((volatile LONG64 *)&slot->sequence_lock, lock_value + 1);
    MemoryBarrier();

    slot->frame_sequence = sequence;
    slot->timestamp_100ns = (int64_t)timestamp_100ns;
    slot->route_epoch = state->route_epoch;
    slot->width = state->width;
    slot->height = state->height;
    slot->stride = state->stride;
    slot->byte_count = state->byte_count;
    CURSORINFO cursor = {0};
    cursor.cbSize = sizeof(cursor);
    slot->cursor_visible = GetCursorInfo(&cursor) && (cursor.flags & CURSOR_SHOWING) != 0 ? 1 : 0;
    uint8_t *destination = mapping + payload_offset;
    for (int row = 0; row < state->height; ++row) {
        const uint8_t *source_row = pixels +
            (size_t)(state->height - 1 - row) * (size_t)state->stride;
        memcpy(destination + (size_t)row * (size_t)state->stride,
               source_row,
               (size_t)state->stride);
    }

    InterlockedExchange((volatile LONG *)&header->width, state->width);
    InterlockedExchange((volatile LONG *)&header->height, state->height);
    InterlockedExchange((volatile LONG *)&header->stride, state->stride);
    MemoryBarrier();
    InterlockedExchange64((volatile LONG64 *)&slot->sequence_lock, lock_value + 2);
    InterlockedExchange64((volatile LONG64 *)&header->newest_sequence, sequence);
    InterlockedIncrement64((volatile LONG64 *)&header->frames_published);
    SetEvent(ipc->frame_ready_event);
    return 1;
}

static bool try_drain_oldest_pbo(
    aura_hook_ipc *ipc,
    aura_gl_capture_state *state,
    aura_gl_capture_result *out_result)
{
    unsigned read_index = state->read_index;
    int ready = 1;
    if (state->fences[read_index] != NULL) {
        GLenum wait = state->client_wait_sync(state->fences[read_index], 0, 0);
        ready = wait == GL_ALREADY_SIGNALED || wait == GL_CONDITION_SATISFIED;
        if (!ready && state->skipped_in_row >= 1) ready = 1;
        if (!ready) {
            ++state->skipped_in_row;
            InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_dropped);
            return false;
        }
    }
    state->skipped_in_row = 0;

    drop_fence(state, read_index);
    state->bind_buffer(GL_PIXEL_PACK_BUFFER, state->pbos[read_index]);
    const uint8_t *pixels = (const uint8_t *)state->map_buffer(GL_PIXEL_PACK_BUFFER, GL_READ_ONLY);
    if (pixels != NULL) {
        int published = publish_frame(ipc, state, pixels, state->issue_100ns[read_index]);
        GLboolean unmapped = state->unmap_buffer(GL_PIXEL_PACK_BUFFER);
        if (published && unmapped == GL_TRUE) {
            *out_result = AURA_GL_CAPTURE_PUBLISHED;
        } else {
            InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_dropped);
            *out_result = AURA_GL_CAPTURE_FAILED;
        }
    } else {
        InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_dropped);
    }

    state->read_index = (read_index + 1) % 3;
    --state->pending_count;
    return true;
}

aura_gl_capture_result aura_gl_capture_present(aura_hook_ipc *ipc, HDC dc)
{
    if (ipc == NULL || ipc->header == NULL) return AURA_GL_CAPTURE_FAILED;
    if (!TryAcquireSRWLockExclusive(&g_capture_lock)) {
        InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_dropped);
        return AURA_GL_CAPTURE_SKIPPED;
    }

    aura_gl_capture_result result = AURA_GL_CAPTURE_SKIPPED;
    aura_gl_capture_state *state = &g_capture;

    if (InterlockedCompareExchange(&g_release_requested, 0, 0) != 0) {
        release_current_context(state);
        InterlockedExchange(&g_release_done, 1);
        goto done;
    }

    int width = 0, height = 0;
    HWND window = dc != NULL ? WindowFromDC(dc) : NULL;
    RECT client;
    if (window != NULL && GetClientRect(window, &client)) {
        width = client.right - client.left;
        height = client.bottom - client.top;
    }
    if (width <= 0 || height <= 0) {
        GLint viewport[4] = {0};
        glGetIntegerv(GL_VIEWPORT, viewport);
        width = viewport[2];
        height = viewport[3];
    }
    uint64_t byte_count = width > 0 && height > 0
        ? (uint64_t)width * (uint64_t)height * UINT64_C(4)
        : 0;
    if (width <= 0 || width > AURA_GAME_HOOK_MAX_WIDTH ||
        height <= 0 || height > AURA_GAME_HOOK_MAX_HEIGHT ||
        ipc->header->slot_stride < AURA_GAME_HOOK_SLOT_HEADER_SIZE ||
        byte_count > (uint64_t)ipc->header->slot_stride - AURA_GAME_HOOK_SLOT_HEADER_SIZE) {
        result = AURA_GL_CAPTURE_FAILED;
        goto done;
    }
    int64_t current_epoch = InterlockedCompareExchange64(
        (volatile LONG64 *)&ipc->header->route_epoch, 0, 0);
    if (!initialize_ring(state, width, height, current_epoch)) {
        result = state->unsupported ? AURA_GL_CAPTURE_UNSUPPORTED : AURA_GL_CAPTURE_FAILED;
        goto done;
    }
    if (state->pending_count == 0) {
        InterlockedExchange((volatile LONG *)&ipc->header->width, width);
        InterlockedExchange((volatile LONG *)&ipc->header->height, height);
        InterlockedExchange((volatile LONG *)&ipc->header->stride, width * 4);
    }

    uint64_t now = now_100ns();
    int target_fps = ipc->header->target_fps;
    if (target_fps <= 0 || target_fps > 240) {
        result = AURA_GL_CAPTURE_FAILED;
        goto done;
    }
    uint64_t interval = UINT64_C(10000000) / (uint64_t)target_fps;
    uint64_t slack = interval / 4;
    if (state->next_issue_100ns != 0 && now + slack < state->next_issue_100ns) goto done;
    if (state->next_issue_100ns == 0 || now > state->next_issue_100ns + interval)
        state->next_issue_100ns = now + interval;
    else
        state->next_issue_100ns += interval;

    GLint previous_buffer = 0;
    GLint previous_pack_alignment = 4;
    GLint previous_read_buffer = GL_BACK;
    GLint previous_read_framebuffer = 0;
    glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &previous_buffer);
    glGetIntegerv(GL_PACK_ALIGNMENT, &previous_pack_alignment);
    glGetIntegerv(GL_READ_BUFFER, &previous_read_buffer);
    glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &previous_read_framebuffer);

    if (state->bind_framebuffer != NULL && previous_read_framebuffer != 0)
        state->bind_framebuffer(GL_READ_FRAMEBUFFER, 0);

    // Если все 3 PBO уже в полёте, сначала освобождаем самый старый PBO, чтобы не
    // перезаписать буфер, в который GPU ещё копирует предыдущий кадр.
    if (state->pending_count == 3) {
        if (!try_drain_oldest_pbo(ipc, state, &result)) {
            goto restore_gl;
        }
    }

    unsigned write_index = state->write_index;
    state->bind_buffer(GL_PIXEL_PACK_BUFFER, state->pbos[write_index]);
    glPixelStorei(GL_PACK_ALIGNMENT, 1);
    glReadBuffer(GL_BACK);
    while (glGetError() != GL_NO_ERROR) { }
    glReadPixels(0, 0, width, height, GL_BGRA, GL_UNSIGNED_BYTE, (void *)0);
    GLenum read_error = glGetError();
    if (read_error != GL_NO_ERROR) {
        InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_dropped);
        result = AURA_GL_CAPTURE_FAILED;
        goto restore_gl;
    }
    state->issue_100ns[write_index] = now;
    drop_fence(state, write_index);
    if (state->fence_sync != NULL)
        state->fences[write_index] = state->fence_sync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
    state->write_index = (write_index + 1) % 3;
    ++state->pending_count;
    ++state->issued_count;
    InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_issued);
    if (result != AURA_GL_CAPTURE_PUBLISHED && result != AURA_GL_CAPTURE_FAILED)
        result = AURA_GL_CAPTURE_ISSUED;

    if (state->pending_count == 3) {
        (void)try_drain_oldest_pbo(ipc, state, &result);
    }

restore_gl:
    if (state->bind_framebuffer != NULL && previous_read_framebuffer != 0)
        state->bind_framebuffer(GL_READ_FRAMEBUFFER, (GLuint)previous_read_framebuffer);
    glReadBuffer((GLenum)previous_read_buffer);
    glPixelStorei(GL_PACK_ALIGNMENT, previous_pack_alignment);
    state->bind_buffer(GL_PIXEL_PACK_BUFFER, (GLuint)previous_buffer);

done:
    ReleaseSRWLockExclusive(&g_capture_lock);
    return result;
}

void aura_gl_capture_request_release(void)
{
    InterlockedExchange(&g_release_done, 0);
    InterlockedExchange(&g_release_requested, 1);
}

bool aura_gl_capture_release_done(void)
{
    return InterlockedCompareExchange(&g_release_done, 0, 0) != 0;
}

bool aura_gl_capture_release_pending(void)
{
    return InterlockedCompareExchange(&g_release_requested, 0, 0) != 0;
}

void aura_gl_capture_notify_idle(void)
{
    InterlockedExchange(&g_idle_notified, 1);
}

void aura_gl_capture_abandon(void)
{
    AcquireSRWLockExclusive(&g_capture_lock);
    memset(&g_capture, 0, sizeof(g_capture));
    InterlockedExchange(&g_release_requested, 0);
    InterlockedExchange(&g_release_done, 0);
    InterlockedExchange(&g_idle_notified, 0);
    ReleaseSRWLockExclusive(&g_capture_lock);
}
