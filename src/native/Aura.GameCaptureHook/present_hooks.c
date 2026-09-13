#include "present_hooks.h"

#include <GL/gl.h>
#include <MinHook.h>

typedef BOOL(WINAPI *swap_buffers_fn)(HDC);
typedef BOOL(WINAPI *swap_layer_buffers_fn)(HDC, UINT);

static aura_hook_ipc *g_ipc;
static swap_buffers_fn g_original_gdi_swap_buffers;
static swap_buffers_fn g_original_wgl_swap_buffers;
static swap_layer_buffers_fn g_original_wgl_swap_layer_buffers;
static void *g_gdi_target;
static void *g_wgl_target;
static void *g_layer_target;
static volatile LONG g_active_callbacks;
static __declspec(thread) LONG g_recursion_depth;

static void observe_present(HDC hdc)
{
    aura_hook_ipc *ipc = g_ipc;
    if (ipc == NULL || ipc->header == NULL || hdc == NULL) return;
    if (InterlockedCompareExchange(
            (volatile LONG *)&ipc->header->command,
            0,
            0) != AURA_GAME_HOOK_COMMAND_CAPTURE) {
        return;
    }

    HWND hwnd = WindowFromDC(hdc);
    if (hwnd == NULL) return;
    HWND root = GetAncestor(hwnd, GA_ROOT);
    if (root == NULL) root = hwnd;
    if ((uint64_t)(uintptr_t)root != ipc->header->target_hwnd) return;
    if (wglGetCurrentContext() == NULL || wglGetCurrentDC() != hdc) return;

    InterlockedIncrement64((volatile LONG64 *)&ipc->header->frames_issued);
}

static void enter_present(HDC hdc)
{
    InterlockedIncrement(&g_active_callbacks);
    ++g_recursion_depth;
    if (g_recursion_depth == 1) observe_present(hdc);
}

static void leave_present(void)
{
    --g_recursion_depth;
    InterlockedDecrement(&g_active_callbacks);
}

static BOOL WINAPI detour_gdi_swap_buffers(HDC hdc)
{
    enter_present(hdc);
    BOOL result = g_original_gdi_swap_buffers(hdc);
    leave_present();
    return result;
}

static BOOL WINAPI detour_wgl_swap_buffers(HDC hdc)
{
    enter_present(hdc);
    BOOL result = g_original_wgl_swap_buffers(hdc);
    leave_present();
    return result;
}

static BOOL WINAPI detour_wgl_swap_layer_buffers(HDC hdc, UINT planes)
{
    enter_present(hdc);
    BOOL result = g_original_wgl_swap_layer_buffers(hdc, planes);
    leave_present();
    return result;
}

static bool create_hook(void *target, void *detour, void **original)
{
    if (target == NULL) return true;
    MH_STATUS status = MH_CreateHook(target, detour, original);
    return status == MH_OK;
}

bool aura_present_hooks_install(aura_hook_ipc *ipc)
{
    if (ipc == NULL || ipc->header == NULL) return false;
    if (MH_Initialize() != MH_OK) return false;

    HMODULE gdi32 = GetModuleHandleW(L"gdi32.dll");
    HMODULE opengl32 = GetModuleHandleW(L"opengl32.dll");
    g_gdi_target = gdi32 == NULL ? NULL : (void *)GetProcAddress(gdi32, "SwapBuffers");
    g_wgl_target = opengl32 == NULL ? NULL : (void *)GetProcAddress(opengl32, "wglSwapBuffers");
    g_layer_target = opengl32 == NULL ? NULL : (void *)GetProcAddress(opengl32, "wglSwapLayerBuffers");
    if (g_gdi_target == NULL && g_wgl_target == NULL && g_layer_target == NULL) {
        MH_Uninitialize();
        return false;
    }

    if (!create_hook(
            g_gdi_target,
            (void *)&detour_gdi_swap_buffers,
            (void **)&g_original_gdi_swap_buffers)) {
        aura_present_hooks_remove();
        return false;
    }

    if (g_wgl_target == g_gdi_target) {
        g_wgl_target = NULL;
    } else if (!create_hook(
                   g_wgl_target,
                   (void *)&detour_wgl_swap_buffers,
                   (void **)&g_original_wgl_swap_buffers)) {
        aura_present_hooks_remove();
        return false;
    }

    if (g_layer_target == g_gdi_target || g_layer_target == g_wgl_target) {
        g_layer_target = NULL;
    } else if (!create_hook(
                   g_layer_target,
                   (void *)&detour_wgl_swap_layer_buffers,
                   (void **)&g_original_wgl_swap_layer_buffers)) {
        aura_present_hooks_remove();
        return false;
    }

    g_ipc = ipc;
    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK) {
        aura_present_hooks_remove();
        return false;
    }
    return true;
}

void aura_present_hooks_remove(void)
{
    g_ipc = NULL;
    (void)MH_DisableHook(MH_ALL_HOOKS);
    (void)MH_RemoveHook(MH_ALL_HOOKS);
    (void)MH_Uninitialize();
    g_gdi_target = NULL;
    g_wgl_target = NULL;
    g_layer_target = NULL;
    g_original_gdi_swap_buffers = NULL;
    g_original_wgl_swap_buffers = NULL;
    g_original_wgl_swap_layer_buffers = NULL;
}

LONG aura_present_hooks_active_callbacks(void)
{
    return InterlockedCompareExchange(&g_active_callbacks, 0, 0);
}
