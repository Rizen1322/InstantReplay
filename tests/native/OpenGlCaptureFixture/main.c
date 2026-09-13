#include <windows.h>
#include <GL/gl.h>

#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <string.h>

#include "game_hook_protocol.h"

static LRESULT CALLBACK fixture_window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
{
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

static uint64_t slot_stride(int width, int height)
{
    uint64_t bytes = (uint64_t)width * (uint64_t)height * UINT64_C(4);
    return (AURA_GAME_HOOK_SLOT_HEADER_SIZE + bytes + UINT64_C(63)) & ~UINT64_C(63);
}

static void close_handle(HANDLE *handle)
{
    if (*handle != NULL) CloseHandle(*handle);
    *handle = NULL;
}

static void render_pattern(int width, int height)
{
    glViewport(0, 0, width, height);
    glEnable(GL_SCISSOR_TEST);
    glScissor(0, 0, width, height / 2);
    glClearColor(1.0f, 0.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    glScissor(0, height / 2, width, height - height / 2);
    glClearColor(0.0f, 1.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    glDisable(GL_SCISSOR_TEST);
}

static bool validate_latest_frame(aura_game_hook_header *header)
{
    LONG64 sequence = InterlockedCompareExchange64(
        (volatile LONG64 *)&header->newest_sequence, 0, 0);
    if (sequence <= 0) return false;
    LONG64 index = (sequence - 1) % AURA_GAME_HOOK_SLOT_COUNT;
    uint8_t *base = (uint8_t *)header + AURA_GAME_HOOK_HEADER_SIZE +
                    (uint64_t)index * (uint64_t)header->slot_stride;
    aura_game_hook_frame_slot_header *slot = (aura_game_hook_frame_slot_header *)base;
    LONG64 first_lock = InterlockedCompareExchange64(
        (volatile LONG64 *)&slot->sequence_lock, 0, 0);
    if (first_lock <= 0 || (first_lock & 1) != 0 ||
        slot->frame_sequence != sequence || slot->width != header->width ||
        slot->height != header->height || slot->stride != header->stride ||
        slot->byte_count != header->stride * header->height) return false;

    uint8_t *pixels = base + AURA_GAME_HOOK_SLOT_HEADER_SIZE;
    uint8_t *top = pixels;
    uint8_t *bottom = pixels + (size_t)(slot->height - 1) * (size_t)slot->stride;
    bool colors_ok = top[0] < 32 && top[1] > 220 && top[2] < 32 &&
                     bottom[0] < 32 && bottom[1] < 32 && bottom[2] > 220;
    MemoryBarrier();
    LONG64 second_lock = InterlockedCompareExchange64(
        (volatile LONG64 *)&slot->sequence_lock, 0, 0);
    return colors_ok && first_lock == second_lock;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2 || argc > 3) {
        fwprintf(stderr, L"usage: OpenGlCaptureFixture.exe <hook-dll> [fps]\n");
        return 2;
    }
    int target_fps = argc == 3 ? _wtoi(argv[2]) : 60;
    if (target_fps <= 0 || target_fps > 240) return 2;

    int result = 1;
    HINSTANCE instance = GetModuleHandleW(NULL);
    WNDCLASSW window_class = {0};
    window_class.lpfnWndProc = fixture_window_proc;
    window_class.hInstance = instance;
    window_class.lpszClassName = L"AuraOpenGlCaptureFixture";
    if (RegisterClassW(&window_class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        return 3;
    }

    HWND window = CreateWindowExW(
        0,
        window_class.lpszClassName,
        L"Aura OpenGL Hook Fixture",
        WS_OVERLAPPEDWINDOW,
        0,
        0,
        128,
        128,
        NULL,
        NULL,
        instance,
        NULL);
    if (window == NULL) return 4;

    HDC dc = GetDC(window);
    PIXELFORMATDESCRIPTOR pfd = {0};
    pfd.nSize = sizeof(pfd);
    pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;
    pfd.cAlphaBits = 8;
    pfd.iLayerType = PFD_MAIN_PLANE;
    int pixel_format = ChoosePixelFormat(dc, &pfd);
    if (pixel_format == 0 || !SetPixelFormat(dc, pixel_format, &pfd)) {
        ReleaseDC(window, dc);
        DestroyWindow(window);
        return 5;
    }

    HGLRC context = wglCreateContext(dc);
    if (context == NULL || !wglMakeCurrent(dc, context)) {
        if (context != NULL) wglDeleteContext(context);
        ReleaseDC(window, dc);
        DestroyWindow(window);
        return 6;
    }
    ShowWindow(window, SW_SHOWNA);

    DWORD pid = GetCurrentProcessId();
    const char nonce[] = "0123456789abcdef0123456789abcdef";
    wchar_t bootstrap_name[128];
    wchar_t frame_mapping_name[256];
    wchar_t frame_event_name[256];
    wchar_t control_event_name[256];
    _snwprintf_s(
        bootstrap_name,
        _countof(bootstrap_name),
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%lu.Bootstrap",
        pid);
    _snwprintf_s(
        frame_mapping_name,
        _countof(frame_mapping_name),
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%lu.%lu.%hs.Frames",
        pid,
        pid,
        nonce);
    _snwprintf_s(
        frame_event_name,
        _countof(frame_event_name),
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%lu.%lu.%hs.FrameReady",
        pid,
        pid,
        nonce);
    _snwprintf_s(
        control_event_name,
        _countof(control_event_name),
        _TRUNCATE,
        L"Local\\Aura.GameCapture.%lu.%lu.%hs.Control",
        pid,
        pid,
        nonce);

    const int width = 64;
    const int height = 64;
    uint64_t stride = slot_stride(width, height);
    uint64_t mapping_size = AURA_GAME_HOOK_HEADER_SIZE + AURA_GAME_HOOK_SLOT_COUNT * stride;
    HANDLE bootstrap_mapping = NULL;
    HANDLE frame_mapping = NULL;
    HANDLE frame_event = NULL;
    HANDLE control_event = NULL;
    aura_game_hook_bootstrap_header *bootstrap = NULL;
    aura_game_hook_header *header = NULL;

    bootstrap_mapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        NULL,
        PAGE_READWRITE,
        0,
        AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE,
        bootstrap_name);
    frame_mapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        NULL,
        PAGE_READWRITE,
        (DWORD)(mapping_size >> 32),
        (DWORD)mapping_size,
        frame_mapping_name);
    frame_event = CreateEventW(NULL, FALSE, FALSE, frame_event_name);
    control_event = CreateEventW(NULL, TRUE, FALSE, control_event_name);
    if (bootstrap_mapping == NULL || frame_mapping == NULL ||
        frame_event == NULL || control_event == NULL) {
        goto cleanup;
    }

    bootstrap = (aura_game_hook_bootstrap_header *)MapViewOfFile(
        bootstrap_mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE);
    header = (aura_game_hook_header *)MapViewOfFile(
        frame_mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        (SIZE_T)mapping_size);
    if (bootstrap == NULL || header == NULL) goto cleanup;

    memset(bootstrap, 0, AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE);
    bootstrap->magic = AURA_GAME_HOOK_BOOTSTRAP_MAGIC;
    bootstrap->version = AURA_GAME_HOOK_VERSION;
    bootstrap->header_size = AURA_GAME_HOOK_BOOTSTRAP_HEADER_SIZE;
    bootstrap->controller_pid = (int32_t)pid;
    bootstrap->target_pid = (int32_t)pid;
    bootstrap->target_process_start_ticks = 1;
    bootstrap->target_hwnd = (uint64_t)(uintptr_t)window;
    bootstrap->nonce_byte_count = 32;
    memcpy(bootstrap->nonce, nonce, 32);

    memset(header, 0, (size_t)mapping_size);
    header->magic = AURA_GAME_HOOK_MAGIC;
    header->version = AURA_GAME_HOOK_VERSION;
    header->header_size = AURA_GAME_HOOK_HEADER_SIZE;
    header->mapping_size = (int64_t)mapping_size;
    header->controller_pid = (int32_t)pid;
    header->target_pid = (int32_t)pid;
    header->target_process_start_ticks = 1;
    header->target_hwnd = (uint64_t)(uintptr_t)window;
    header->target_revision = 1;
    header->capture_generation = 1;
    header->route_epoch = 1;
    header->command = AURA_GAME_HOOK_COMMAND_CAPTURE;
    header->target_fps = target_fps;
    header->width = width;
    header->height = height;
    header->stride = width * 4;
    header->pixel_format = AURA_GAME_HOOK_PIXEL_FORMAT_BGRA8;
    header->slot_count = AURA_GAME_HOOK_SLOT_COUNT;
    header->slot_header_size = AURA_GAME_HOOK_SLOT_HEADER_SIZE;
    header->slot_stride = (int64_t)stride;

    HMODULE hook = LoadLibraryW(argv[1]);
    if (hook == NULL) {
        fwprintf(stderr, L"LoadLibraryW failed: %lu\n", GetLastError());
        goto cleanup;
    }

    ULONGLONG capture_started = GetTickCount64();
    ULONGLONG deadline = capture_started + 5000;
    bool observed = false;
    LONG64 last_sequence = 0;
    int sequence_changes = 0;
    while (GetTickCount64() < deadline) {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        render_pattern(header->width, header->height);
        SwapBuffers(dc);
        InterlockedExchange64(
            (volatile LONG64 *)&header->controller_heartbeat_100ns,
            (LONG64)(GetTickCount64() * UINT64_C(10000)));
        LONG state = InterlockedCompareExchange((volatile LONG *)&header->state, 0, 0);
        LONG64 issued = InterlockedCompareExchange64(
            (volatile LONG64 *)&header->frames_issued,
            0,
            0);
        LONG64 heartbeat = InterlockedCompareExchange64(
            (volatile LONG64 *)&header->hook_heartbeat_100ns,
            0,
            0);
        LONG64 sequence = InterlockedCompareExchange64(
            (volatile LONG64 *)&header->newest_sequence, 0, 0);
        if (sequence > last_sequence) {
            last_sequence = sequence;
            ++sequence_changes;
        }
        if (state == AURA_GAME_HOOK_STATE_CAPTURING && issued >= 3 &&
            heartbeat > 0 && sequence_changes >= 2 && validate_latest_frame(header)) {
            observed = true;
            break;
        }
        Sleep(10);
    }

    if (!observed) {
        fwprintf(
            stderr,
            L"hook did not become live: state=%ld error=%ld issued=%lld heartbeat=%lld\n",
            header->state,
            header->error,
            header->frames_issued,
            header->hook_heartbeat_100ns);
        goto cleanup;
    }

    ULONGLONG elapsed = GetTickCount64() - capture_started;
    LONG64 issued_before_pause = InterlockedCompareExchange64(
        (volatile LONG64 *)&header->frames_issued, 0, 0);
    LONG64 maximum_issued = (LONG64)((elapsed * (ULONGLONG)target_fps) / 1000) + 4;
    if (issued_before_pause > maximum_issued) {
        fwprintf(stderr, L"capture throttle exceeded: issued=%lld max=%lld\n",
                 issued_before_pause, maximum_issued);
        goto cleanup;
    }

    InterlockedExchange((volatile LONG *)&header->command, AURA_GAME_HOOK_COMMAND_IDLE);
    for (int frame = 0; frame < 12; ++frame) {
        render_pattern(header->width, header->height);
        SwapBuffers(dc);
        Sleep(10);
    }
    if (InterlockedCompareExchange64(
            (volatile LONG64 *)&header->frames_issued, 0, 0) != issued_before_pause) {
        fwprintf(stderr, L"capture work continued while command was idle\n");
        goto cleanup;
    }

    LONG64 published_before_resize = InterlockedCompareExchange64(
        (volatile LONG64 *)&header->frames_published, 0, 0);
    header->width = 32;
    header->height = 32;
    header->stride = 32 * 4;
    InterlockedExchange((volatile LONG *)&header->command, AURA_GAME_HOOK_COMMAND_CAPTURE);
    deadline = GetTickCount64() + 3000;
    bool resized = false;
    while (GetTickCount64() < deadline) {
        render_pattern(header->width, header->height);
        SwapBuffers(dc);
        LONG64 published = InterlockedCompareExchange64(
            (volatile LONG64 *)&header->frames_published, 0, 0);
        if (published > published_before_resize && validate_latest_frame(header)) {
            resized = true;
            break;
        }
        Sleep(10);
    }
    if (!resized) {
        fwprintf(stderr, L"capture did not resume after idle/resize\n");
        goto cleanup;
    }

    InterlockedExchange((volatile LONG *)&header->command, AURA_GAME_HOOK_COMMAND_STOP);
    SetEvent(control_event);
    deadline = GetTickCount64() + 5000;
    bool stopped = false;
    while (GetTickCount64() < deadline) {
        LONG state = InterlockedCompareExchange((volatile LONG *)&header->state, 0, 0);
        if (state == AURA_GAME_HOOK_STATE_STOPPED) {
            stopped = true;
            break;
        }
        Sleep(5);
    }
    if (!stopped) {
        fwprintf(stderr, L"hook did not stop: state=%ld error=%ld\n", header->state, header->error);
        goto cleanup;
    }

    deadline = GetTickCount64() + 2000;
    while (GetTickCount64() < deadline &&
           GetModuleHandleW(L"Aura.GameCaptureHook64.dll") != NULL) {
        Sleep(5);
    }
    if (GetModuleHandleW(L"Aura.GameCaptureHook64.dll") != NULL) {
        fwprintf(stderr, L"hook module remained loaded\n");
        goto cleanup;
    }

    result = 0;

cleanup:
    if (header != NULL) UnmapViewOfFile(header);
    if (bootstrap != NULL) UnmapViewOfFile(bootstrap);
    close_handle(&control_event);
    close_handle(&frame_event);
    close_handle(&frame_mapping);
    close_handle(&bootstrap_mapping);
    wglMakeCurrent(NULL, NULL);
    wglDeleteContext(context);
    ReleaseDC(window, dc);
    DestroyWindow(window);
    return result;
}
