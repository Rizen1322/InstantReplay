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

int wmain(int argc, wchar_t **argv)
{
    if (argc != 2) {
        fwprintf(stderr, L"usage: OpenGlCaptureFixture.exe <hook-dll>\n");
        return 2;
    }

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
    header->target_fps = 60;
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

    ULONGLONG deadline = GetTickCount64() + 5000;
    bool observed = false;
    while (GetTickCount64() < deadline) {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        glViewport(0, 0, width, height);
        glClearColor(0.1f, 0.2f, 0.3f, 1.0f);
        glClear(GL_COLOR_BUFFER_BIT);
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
        if (state == AURA_GAME_HOOK_STATE_CAPTURING && issued >= 3 && heartbeat > 0) {
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
