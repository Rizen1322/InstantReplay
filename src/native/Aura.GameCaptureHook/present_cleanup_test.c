// Exercise the real present gate and GL resource deletion, not a text assertion.
#include "gl_capture.c"
#include "present_hooks.c"
#include "ipc.c"
#include <stdio.h>
#include <stdlib.h>

typedef GLboolean(APIENTRY *is_buffer_fn)(GLuint);

int main(void)
{
    WNDCLASSW cls = {0};
    cls.lpfnWndProc = DefWindowProcW;
    cls.hInstance = GetModuleHandleW(NULL);
    cls.lpszClassName = L"AuraPresentCleanupTest";
    if (!RegisterClassW(&cls)) return 2;
    HWND window = CreateWindowW(cls.lpszClassName, L"Aura test", WS_OVERLAPPEDWINDOW,
        0, 0, 128, 128, NULL, NULL, cls.hInstance, NULL);
    if (window == NULL) return 3;
    HDC dc = GetDC(window);
    PIXELFORMATDESCRIPTOR pfd = {0};
    pfd.nSize = sizeof(pfd);
    pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;
    int format = ChoosePixelFormat(dc, &pfd);
    if (!format || !SetPixelFormat(dc, format, &pfd)) return 4;
    HGLRC context = wglCreateContext(dc);
    if (context == NULL || !wglMakeCurrent(dc, context)) return 5;

    aura_game_hook_header header = {0};
    header.target_hwnd = (uint64_t)(uintptr_t)window;
    header.command = AURA_GAME_HOOK_COMMAND_CAPTURE;
    header.target_fps = 60;
    header.slot_stride = AURA_GAME_HOOK_SLOT_HEADER_SIZE + 128 * 128 * 4;
    aura_hook_ipc ipc = {0};
    ipc.header = &header;
    g_ipc = &ipc;
    glClearColor(1, 0, 0, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    observe_present(dc); // First frame creates the real PBO ring, no publication yet.
    if (!g_capture.initialized) return 6;
    is_buffer_fn is_buffer = (is_buffer_fn)wglGetProcAddress("glIsBuffer");
    if (is_buffer == NULL) return 7;
    GLuint allocated[3];
    memcpy(allocated, g_capture.pbos, sizeof(allocated));
    for (int i = 0; i < 3; ++i) if (!is_buffer(allocated[i])) return 8;

    // Aura sets STOP before the native control thread asks for GL cleanup.
    header.command = AURA_GAME_HOOK_COMMAND_STOP;
    aura_gl_capture_request_release();
    observe_present(dc);
    if (!aura_gl_capture_release_done()) {
        fprintf(stderr, "FAIL: STOP present gate prevented PBO cleanup\n");
        return 9;
    }
    for (int i = 0; i < 3; ++i) {
        if (is_buffer(allocated[i])) {
            fprintf(stderr, "FAIL: PBO survived capture stop\n");
            return 10;
        }
    }
    g_ipc = NULL;
    wglMakeCurrent(NULL, NULL);
    wglDeleteContext(context);
    ReleaseDC(window, dc);
    DestroyWindow(window);
    puts("STOP present gate releases all three PBOs");
    return 0;
}
