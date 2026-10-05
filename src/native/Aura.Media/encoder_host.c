// Хост NVENC: отдельный процесс, в котором живёт сессия кодировщика.
//
// ЗАЧЕМ ОТДЕЛЬНЫЙ ПРОЦЕСС. Когда драйвер NVENC зависает внутри вызова, в самой Aura
// этот вызов не прервать ничем: поток стоит в драйвере, а освобождать его объекты
// под ним нельзя. Приходилось бросать весь конвейер до конца процесса, и каждый
// такой сбой оставлял за собой видеопамять и висящий поток. Процесс же можно
// убить: Windows сама освобождает всё, что он держал на видеокарте, а Aura
// поднимает новый хост и продолжает запись.
//
// ГРАНИЦА. Хост делает ровно то, что раньше делали прослойка и копия кадра:
// получает номер слота общей текстуры, под ключом (keyed mutex) копирует его в
// собственный вход NVENC, отпускает ключ и отдаёт кадр NVENC. Вся логика потока
// (метки времени, B-кадры, ключевые кадры, подстройка нагрузки) остаётся в Aura.
// Общие текстуры создаёт Aura на своём устройстве: они переживают смерть хоста.
// Копия общей текстуры в свою поверхность перед NVENC — та же граница владения,
// что у OBS (plugins/obs-nvenc/nvenc-d3d11.c).
//
// ОБМЕН. Общая память (управляющий блок + область данных под выход кодировщика)
// и два события: запрос от Aura и ответ хоста. Запрос всегда один: Aura ждёт ответ
// с таймаутом и, если хост молчит, убивает его. Раскладка блока описана ниже и
// повторена в RemoteNvencSession.cs; менять только вместе.

#define COBJMACROS
#define INITGUID
#include "nvenc_shim.c"
#include <d3d11.h>
#include <dxgi1_2.h>
#include <shellapi.h>

// ---------------------------------------------------------------- раскладка блока

#define HOST_MAGIC        0x31484E56   // "VNH1"
#define OFF_MAGIC         0
#define OFF_TYPE          4
#define OFF_RESULT        8
#define OFF_DATA_LENGTH   12
#define OFF_ARGS          16     // int64[16]
#define OFF_OUT           144    // int64[16]
#define OFF_CONFIG        272    // AuraNvencConfig (до 176 байт)
#define OFF_APPLIED       448    // AuraNvencApplied
#define OFF_HANDLES       512    // uint64[64]
#define OFF_ERROR         1024   // char[1024]
#define OFF_DATA          65536
#define MAX_SLOTS         64

enum {
    REQ_OPEN = 1, REQ_ATTACH, REQ_ENCODE, REQ_GET, REQ_FREE_SLOTS, REQ_RECONFIGURE,
    REQ_SEQUENCE_HEADER, REQ_TRACE, REQ_END_OF_STREAM, REQ_DESTROY, REQ_RC_INFO
};

// Коды ответа ENCODE сверх кодов прослойки
#define ENCODE_SLOT_TIMEOUT   -2000   // общий слот не отдан за 200 мс
#define ENCODE_SLOT_ABANDONED -2001   // ключ общей текстуры брошен (WAIT_ABANDONED)
#define ENCODE_BAD_SLOT       -2002

static uint8_t* g_view;
static size_t g_viewSize;

static ID3D11Device* g_device;
static ID3D11DeviceContext* g_context;
static void* g_session;
static AuraNvencApplied g_applied;
static int g_width, g_height, g_tenBit;

static ID3D11Texture2D* g_shared[MAX_SLOTS];
static IDXGIKeyedMutex* g_sharedLock[MAX_SLOTS];
static int g_slotCount;
static ID3D11Texture2D* g_inputs[MAX_BUFFERS];
static int g_inputCount;
static int64_t g_accepted;    // принятых кадров: по нему выбирается вход

static int32_t* i32(int offset) { return (int32_t*)(g_view + offset); }
static int64_t* i64(int offset) { return (int64_t*)(g_view + offset); }
static int64_t arg(int i) { return i64(OFF_ARGS)[i]; }

static void error_text(const char* text) {
    char* e = (char*)(g_view + OFF_ERROR);
    lstrcpynA(e, text, 1024);
}

typedef LONG(WINAPI* SetPriorityClassFn)(HANDLE, int);

// Приоритет очереди видеокарты для процесса хоста, как у Aura (см. App.RaiseGpuPriority):
// без него кадры энкодера под нагрузкой игрой стоят в общей очереди последними.
static void raise_gpu_priority(int priorityClass) {
    if (priorityClass <= 0) return;
    HMODULE gdi = GetModuleHandleA("gdi32.dll");
    if (!gdi) gdi = LoadLibraryA("gdi32.dll");
    if (!gdi) return;
    SetPriorityClassFn set = (SetPriorityClassFn)(void*)GetProcAddress(gdi, "D3DKMTSetProcessSchedulingPriorityClass");
    if (set) set(GetCurrentProcess(), priorityClass);
}

static IDXGIAdapter1* find_adapter(int64_t luid) {
    IDXGIFactory1* factory = NULL;
    if (FAILED(CreateDXGIFactory1(&IID_IDXGIFactory1, (void**)&factory))) return NULL;
    IDXGIAdapter1* found = NULL;
    for (UINT i = 0;; i++) {
        IDXGIAdapter1* adapter = NULL;
        if (IDXGIFactory1_EnumAdapters1(factory, i, &adapter) != S_OK) break;
        DXGI_ADAPTER_DESC1 desc;
        if (SUCCEEDED(IDXGIAdapter1_GetDesc1(adapter, &desc))) {
            int64_t id = (int64_t)(((uint64_t)(uint32_t)desc.AdapterLuid.HighPart << 32) | desc.AdapterLuid.LowPart);
            if (id == luid) { found = adapter; break; }
        }
        IDXGIAdapter1_Release(adapter);
    }
    IDXGIFactory1_Release(factory);
    return found;
}

static int handle_open(void) {
    if (g_session) { error_text("сессия уже открыта"); return 0; }
    raise_gpu_priority((int)arg(1));

    IDXGIAdapter1* adapter = find_adapter(arg(0));
    if (!adapter) { error_text("видеокарта Aura не найдена в хосте (LUID)"); return 0; }
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = D3D11CreateDevice((IDXGIAdapter*)adapter, D3D_DRIVER_TYPE_UNKNOWN, NULL, 0, levels, 2,
                                   D3D11_SDK_VERSION, &g_device, NULL, &g_context);
    IDXGIAdapter1_Release(adapter);
    if (FAILED(hr)) {
        char text[128];
        wsprintfA(text, "устройство D3D11 в хосте не создано (0x%08X)", (unsigned)hr);
        error_text(text);
        return 0;
    }
    IDXGIDevice* dxgi = NULL;
    if (SUCCEEDED(ID3D11Device_QueryInterface(g_device, &IID_IDXGIDevice, (void**)&dxgi))) {
        IDXGIDevice_SetGPUThreadPriority(dxgi, 2);   // как у устройств Aura, см. GpuPriority.cs
        IDXGIDevice_Release(dxgi);
    }

    AuraNvencConfig cfg;
    memcpy(&cfg, g_view + OFF_CONFIG, sizeof cfg);
    char err[512] = { 0 };
    if (!aura_nvenc_create(g_device, &cfg, &g_applied, &g_session, err, sizeof err)) {
        error_text(err[0] ? err : "сессия NVENC не открылась");
        g_session = NULL;
        return 0;
    }
    g_width = cfg.width;
    g_height = cfg.height;
    g_tenBit = g_applied.tenBit;
    memcpy(g_view + OFF_APPLIED, &g_applied, sizeof g_applied);

    // Входы NVENC: по одному на выходной буфер, как в Aura до выноса
    D3D11_TEXTURE2D_DESC desc = { 0 };
    desc.Width = (UINT)g_width;
    desc.Height = (UINT)g_height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = g_tenBit ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    g_inputCount = g_applied.bufferCount < MAX_BUFFERS ? g_applied.bufferCount : MAX_BUFFERS;
    for (int i = 0; i < g_inputCount; i++) {
        hr = ID3D11Device_CreateTexture2D(g_device, &desc, NULL, &g_inputs[i]);
        if (FAILED(hr)) { error_text("вход NVENC в хосте не создан"); return 0; }
    }
    return 1;
}

static int handle_attach(void) {
    int count = (int)arg(0);
    if (!g_device || count <= 0 || count > MAX_SLOTS) { error_text("неверный набор слотов"); return 0; }
    const uint64_t* handles = (const uint64_t*)(g_view + OFF_HANDLES);
    for (int i = 0; i < count; i++) {
        HRESULT hr = ID3D11Device_OpenSharedResource(g_device, (HANDLE)(uintptr_t)handles[i],
                                                     &IID_ID3D11Texture2D, (void**)&g_shared[i]);
        if (FAILED(hr)) {
            char text[128];
            wsprintfA(text, "общая текстура %d не открылась в хосте (0x%08X)", i, (unsigned)hr);
            error_text(text);
            return 0;
        }
        hr = ID3D11Texture2D_QueryInterface(g_shared[i], &IID_IDXGIKeyedMutex, (void**)&g_sharedLock[i]);
        if (FAILED(hr)) { error_text("у общей текстуры нет ключа"); return 0; }
    }
    g_slotCount = count;
    return 1;
}

static int handle_encode(void) {
    int slot = (int)arg(0);
    if (!g_session || g_inputCount <= 0 || slot < 0 || slot >= g_slotCount) return ENCODE_BAD_SLOT;
    ID3D11Texture2D* input = g_inputs[g_accepted % g_inputCount];
    // Повтор после ENCODER_BUSY: кадр уже скопирован в этот вход, слот отдан
    if (!arg(3)) {
        // Захват записал кадр и отпустил ключ 1; берём его, копируем и возвращаем 0.
        // Копия в свой вход — граница владения: слот свободен сразу после неё.
        HRESULT hr = IDXGIKeyedMutex_AcquireSync(g_sharedLock[slot], 1, 200);
        if (hr == (HRESULT)WAIT_ABANDONED) return ENCODE_SLOT_ABANDONED;
        if (hr != S_OK) return ENCODE_SLOT_TIMEOUT;
        ID3D11DeviceContext_CopyResource(g_context, (ID3D11Resource*)input, (ID3D11Resource*)g_shared[slot]);
        IDXGIKeyedMutex_ReleaseSync(g_sharedLock[slot], 0);
    }

    int result = aura_nvenc_encode(g_session, input, arg(1), (int)arg(2));
    if (result > 0) g_accepted++;
    return result;
}

static int handle_get(void) {
    if (!g_session) return 0;
    const uint8_t* data = NULL;
    int size = 0, pictureType = 0;
    int64_t pts = 0;
    int result = aura_nvenc_get(g_session, (uint32_t)arg(0), &data, &size, &pts, &pictureType);
    i64(OFF_OUT)[0] = pts;
    i64(OFF_OUT)[1] = pictureType;
    *i32(OFF_DATA_LENGTH) = 0;
    if ((result == 1 || result == 2) && size > 0) {
        if ((size_t)size > g_viewSize - OFF_DATA) return -1;   // кадр больше области данных
        memcpy(g_view + OFF_DATA, data, (size_t)size);
        *i32(OFF_DATA_LENGTH) = size;
    }
    return result;
}

static void destroy_all(void) {
    if (g_session) { aura_nvenc_destroy(g_session); g_session = NULL; }
    for (int i = 0; i < g_inputCount; i++) if (g_inputs[i]) ID3D11Texture2D_Release(g_inputs[i]);
    for (int i = 0; i < g_slotCount; i++) {
        if (g_sharedLock[i]) IDXGIKeyedMutex_Release(g_sharedLock[i]);
        if (g_shared[i]) ID3D11Texture2D_Release(g_shared[i]);
    }
    g_inputCount = g_slotCount = 0;
    if (g_context) { ID3D11DeviceContext_Release(g_context); g_context = NULL; }
    if (g_device) { ID3D11Device_Release(g_device); g_device = NULL; }
}

static int handle(int type) {
    switch (type) {
    case REQ_OPEN: return handle_open();
    case REQ_ATTACH: return handle_attach();
    case REQ_ENCODE: return handle_encode();
    case REQ_GET: return handle_get();
    case REQ_FREE_SLOTS: return g_session ? aura_nvenc_free_slots(g_session) : 0;
    case REQ_RECONFIGURE: return g_session ? aura_nvenc_reconfigure(g_session, (int)arg(0), (int)arg(1)) : -1;
    case REQ_SEQUENCE_HEADER: {
        int size = 0;
        *i32(OFF_DATA_LENGTH) = 0;
        if (!g_session) return 0;
        int result = aura_nvenc_sequence_header(g_session, g_view + OFF_DATA, (int)(g_viewSize - OFF_DATA), &size);
        if (result == 1) *i32(OFF_DATA_LENGTH) = size;
        return result;
    }
    case REQ_TRACE: {
        int n = g_session ? aura_nvenc_trace(g_session, (char*)(g_view + OFF_DATA), (int)(g_viewSize - OFF_DATA)) : 0;
        *i32(OFF_DATA_LENGTH) = n > 0 ? n : 0;
        return n;
    }
    case REQ_RC_INFO: {
        int n = g_session ? aura_nvenc_rc_info(g_session, (char*)(g_view + OFF_DATA), (int)(g_viewSize - OFF_DATA)) : 0;
        *i32(OFF_DATA_LENGTH) = n;
        return n;
    }
    case REQ_END_OF_STREAM: if (g_session) aura_nvenc_end(g_session); return 0;
    case REQ_DESTROY: destroy_all(); return 0;
    default: return -1;
    }
}

// Командная строка: --host <имя> --parent <pid>
static int parse_args(wchar_t* name, int nameCap, DWORD* parent) {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 0;
    int ok = 0;
    for (int i = 1; i + 1 < argc; i++) {
        if (lstrcmpW(argv[i], L"--host") == 0) { lstrcpynW(name, argv[i + 1], nameCap); ok |= 1; }
        if (lstrcmpW(argv[i], L"--parent") == 0) { *parent = (DWORD)_wtoi(argv[i + 1]); ok |= 2; }
    }
    LocalFree(argv);
    return ok == 3;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show) {
    (void)instance; (void)previous; (void)commandLine; (void)show;
    wchar_t base[200], name[260];
    DWORD parentId = 0;
    if (!parse_args(base, 200, &parentId)) return 2;

    // Aura умерла — хосту жить незачем: держать видеопамять без хозяина нельзя
    HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentId);
    if (!parent) return 3;

    wsprintfW(name, L"%s-map", base);
    HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    wsprintfW(name, L"%s-req", base);
    HANDLE request = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, name);
    wsprintfW(name, L"%s-resp", base);
    HANDLE response = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, name);
    if (!mapping || !request || !response) return 4;
    g_view = (uint8_t*)MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (!g_view) return 5;
    MEMORY_BASIC_INFORMATION info;
    VirtualQuery(g_view, &info, sizeof info);
    g_viewSize = info.RegionSize;

    // Готов: Aura ждёт этот ответ сразу после запуска
    *i32(OFF_MAGIC) = HOST_MAGIC;
    SetEvent(response);

    HANDLE waits[2] = { request, parent };
    for (;;) {
        DWORD w = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (w != WAIT_OBJECT_0) break;
        int type = *i32(OFF_TYPE);
        *i32(OFF_RESULT) = handle(type);
        SetEvent(response);
        if (type == REQ_DESTROY) break;
    }
    destroy_all();
    return 0;
}
