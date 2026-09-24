// Прослойка между Aura (C#) и NVENC API.
//
// ЗАЧЕМ НА C. Структуры NVENC — это сотни полей с битовыми полями, объединениями
// и резервными массивами точного размера. Перенести их в C# вручную значит
// рисковать тихой порчей памяти на любом несовпадении. Здесь раскладку проверяет
// компилятор по официальному заголовку, а наружу торчит десяток простых функций.
//
// Модель работы:
// • у NVENC своё устройство D3D11 (как у OBS), и все вызовы идут из ОДНОГО потока
//   Aura: подача, забор выхода и снятие отображения. Прежде подача и выдача шли из
//   двух потоков на общем с захватом устройстве, и драйвер внутри вызова NVENC
//   держал замок устройства, пока другие наши потоки ждали его же;
// • кадры приходят собственными текстурами энкодера (NV12 или P010); каждая
//   регистрируется в NVENC один раз и дальше только отображается;
// • кодирование асинхронное: у каждого выходного буфера своё событие, кадры
//   забираются строго по порядку отправки, вход освобождается (unmap) вместе с
//   выходом того же порядкового номера;
// • каждый вызов NVENC пишется в кольцо трассировки (aura_nvenc_trace): если
//   конвейер встанет, в логе будет последний вызов, поток и сколько он длится.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include "nvEncodeAPI.h"

#define AURA_EXPORT __declspec(dllexport)

// ---------------------------------------------------------------- параметры

typedef struct AuraNvencConfig {
    int32_t codec;          // 0 — H.264, 1 — HEVC, 2 — AV1
    int32_t width;
    int32_t height;
    int32_t fps;
    int32_t bitrate;        // средний, бит/с
    int32_t maxBitrate;     // потолок VBR, бит/с
    int32_t vbvBuffer;      // бит
    int32_t tenBit;         // вход P010, профиль Main10
    int32_t gopLength;      // кадров между ключевыми
    int32_t preset;         // 1..7 (P1..P7)
    int32_t lookahead;      // кадров, 0 — выключено
    int32_t bFrames;        // 0..4
    int32_t spatialAq;
    int32_t temporalAq;
    int32_t aqStrength;     // 0 — авто, 1..15
    int32_t multipass;      // 0 — нет, 1 — четверть разрешения, 2 — полное
    int32_t bufferCount;    // выходных буферов (глубина конвейера)
} AuraNvencConfig;

// Что реально включилось — для лога и для расчёта глубины очереди в C#.
typedef struct AuraNvencApplied {
    int32_t bFrames;
    int32_t lookahead;
    int32_t temporalAq;
    int32_t spatialAq;
    int32_t multipass;
    int32_t bufferCount;
    int32_t bFrameRef;
    int32_t tenBit;
} AuraNvencApplied;

// ---------------------------------------------------------------- сессия

#define MAX_BUFFERS 64
#define MAX_REGISTERED 256
#define TRACE_SIZE 64

enum {
    API_REGISTER = 1, API_MAP, API_ENCODE, API_LOCK, API_UNLOCK, API_UNMAP, API_EOS, API_DESTROY, API_WAIT
};

typedef struct TraceEntry {
    int api;
    int status;
    int64_t frame;
    DWORD thread;
    int64_t enter;      // QPC
    int64_t exit;       // 0 — вызов ещё не вернулся
} TraceEntry;

typedef struct Slot {
    NV_ENC_OUTPUT_PTR bitstream;
    HANDLE event;
    NV_ENC_INPUT_PTR mapped;   // вход, отображённый для кадра этого порядкового номера
} Slot;

typedef struct Registered {
    void* texture;
    NV_ENC_REGISTERED_PTR handle;
} Registered;

typedef struct Session {
    HMODULE dll;
    NV_ENCODE_API_FUNCTION_LIST api;
    void* encoder;
    NV_ENC_BUFFER_FORMAT format;
    int width, height;
    Slot slots[MAX_BUFFERS];
    int slotCount;
    int64_t sent;              // сколько кадров отправлено (включая ждущие выхода)
    int64_t got;               // сколько выходов забрано
    Registered registered[MAX_REGISTERED];
    int registeredCount;
    CRITICAL_SECTION lock;     // счётчики: отправка и выдача идут из разных потоков
    CRITICAL_SECTION apiLock;  // регистрация, отображение входа и его снятие — строго по одному
    int eosSent;
    TraceEntry trace[TRACE_SIZE];
    volatile LONG traceNext;
    char lastError[256];
    uint8_t* out;              // копия последнего выхода: растёт под самый большой кадр
    int outCapacity;
} Session;

static int64_t qpc_now(void) {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

static int trace_begin(Session* s, int api, int64_t frame) {
    LONG i = InterlockedIncrement(&s->traceNext) - 1;
    TraceEntry* e = &s->trace[i % TRACE_SIZE];
    e->exit = 0;
    e->api = api;
    e->status = 0;
    e->frame = frame;
    e->thread = GetCurrentThreadId();
    e->enter = qpc_now();
    return (int)i;
}

static void trace_end(Session* s, int i, NVENCSTATUS status) {
    TraceEntry* e = &s->trace[i % TRACE_SIZE];
    e->status = (int)status;
    e->exit = qpc_now();
}

static void remember_error(Session* s, const char* call, NVENCSTATUS status) {
    const char* text = s->encoder && s->api.nvEncGetLastErrorString
        ? s->api.nvEncGetLastErrorString(s->encoder) : NULL;
    snprintf(s->lastError, sizeof s->lastError, "%s: NVENCSTATUS %d%s%s", call, (int)status,
             text && *text ? " — " : "", text ? text : "");
}

typedef NVENCSTATUS (NVENCAPI* CreateInstanceFn)(NV_ENCODE_API_FUNCTION_LIST*);
typedef NVENCSTATUS (NVENCAPI* MaxVersionFn)(uint32_t*);

static void set_error(char* err, int cap, const char* text, NVENCSTATUS status) {
    if (!err || cap <= 0) return;
    snprintf(err, (size_t)cap, "%s (NVENCSTATUS %d)", text, (int)status);
}

static GUID codec_guid(int codec) {
    return codec == 1 ? NV_ENC_CODEC_HEVC_GUID : codec == 2 ? NV_ENC_CODEC_AV1_GUID : NV_ENC_CODEC_H264_GUID;
}

static GUID preset_guid(int preset) {
    switch (preset) {
        case 1: return NV_ENC_PRESET_P1_GUID;
        case 2: return NV_ENC_PRESET_P2_GUID;
        case 3: return NV_ENC_PRESET_P3_GUID;
        case 5: return NV_ENC_PRESET_P5_GUID;
        case 6: return NV_ENC_PRESET_P6_GUID;
        case 7: return NV_ENC_PRESET_P7_GUID;
        default: return NV_ENC_PRESET_P4_GUID;
    }
}

static int get_cap(Session* s, GUID codec, NV_ENC_CAPS cap) {
    NV_ENC_CAPS_PARAM param = { 0 };
    param.version = NV_ENC_CAPS_PARAM_VER;
    param.capsToQuery = cap;
    int value = 0;
    if (s->api.nvEncGetEncodeCaps(s->encoder, codec, &param, &value) != NV_ENC_SUCCESS) return 0;
    return value;
}

static int open_api(Session* s, char* err, int cap) {
    // Только из System32: туда её кладёт драйвер. Обычный поиск начинается с папки
    // приложения, и подложенная рядом библиотека загрузилась бы вместо настоящей.
    s->dll = LoadLibraryExW(L"nvEncodeAPI64.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!s->dll) { set_error(err, cap, "nvEncodeAPI64.dll не найден (нет драйвера NVIDIA)", 0); return 0; }

    MaxVersionFn maxVersion = (MaxVersionFn)(void*)GetProcAddress(s->dll, "NvEncodeAPIGetMaxSupportedVersion");
    CreateInstanceFn create = (CreateInstanceFn)(void*)GetProcAddress(s->dll, "NvEncodeAPICreateInstance");
    if (!maxVersion || !create) { set_error(err, cap, "в nvEncodeAPI64.dll нет точек входа", 0); return 0; }

    uint32_t version = 0;
    maxVersion(&version);
    uint32_t wanted = (NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION;
    if (version < wanted) {
        char text[128];
        snprintf(text, sizeof text, "драйвер поддерживает NVENC API %u.%u, нужен 12.1 или новее",
                 version >> 4, version & 0xF);
        set_error(err, cap, text, 0);
        return 0;
    }

    s->api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    NVENCSTATUS st = create(&s->api);
    if (st != NV_ENC_SUCCESS) { set_error(err, cap, "NvEncodeAPICreateInstance", st); return 0; }
    return 1;
}

static void destroy_session(Session* s) {
    if (!s) return;
    if (s->encoder) {
        for (int i = 0; i < s->slotCount; i++) {
            if (s->slots[i].mapped) s->api.nvEncUnmapInputResource(s->encoder, s->slots[i].mapped);
            if (s->slots[i].event) {
                NV_ENC_EVENT_PARAMS ev = { 0 };
                ev.version = NV_ENC_EVENT_PARAMS_VER;
                ev.completionEvent = s->slots[i].event;
                s->api.nvEncUnregisterAsyncEvent(s->encoder, &ev);
                CloseHandle(s->slots[i].event);
            }
            if (s->slots[i].bitstream) s->api.nvEncDestroyBitstreamBuffer(s->encoder, s->slots[i].bitstream);
        }
        for (int i = 0; i < s->registeredCount; i++)
            s->api.nvEncUnregisterResource(s->encoder, s->registered[i].handle);
        s->api.nvEncDestroyEncoder(s->encoder);
    }
    if (s->dll) FreeLibrary(s->dll);
    if (s->out) HeapFree(GetProcessHeap(), 0, s->out);
    DeleteCriticalSection(&s->lock);
    DeleteCriticalSection(&s->apiLock);
    HeapFree(GetProcessHeap(), 0, s);
}

AURA_EXPORT int aura_nvenc_create(void* d3d11Device, const AuraNvencConfig* cfg, AuraNvencApplied* applied,
                                  void** out, char* err, int errCap) {
    *out = NULL;
    Session* s = (Session*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Session));
    if (!s) { set_error(err, errCap, "нет памяти", 0); return 0; }
    InitializeCriticalSection(&s->lock);
    InitializeCriticalSection(&s->apiLock);

    if (!open_api(s, err, errCap)) { destroy_session(s); return 0; }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open = { 0 };
    open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    open.device = d3d11Device;
    open.apiVersion = NVENCAPI_VERSION;
    NVENCSTATUS st = s->api.nvEncOpenEncodeSessionEx(&open, &s->encoder);
    if (st != NV_ENC_SUCCESS) {
        set_error(err, errCap, "сессию NVENC открыть не удалось (другая видеокарта или исчерпан лимит сессий)", st);
        s->encoder = NULL;
        destroy_session(s);
        return 0;
    }

    GUID codec = codec_guid(cfg->codec);
    GUID preset = preset_guid(cfg->preset);

    // Возможности этой видеокарты: всё, чего нет, выключаем, а не падаем.
    int maxB = get_cap(s, codec, NV_ENC_CAPS_NUM_MAX_BFRAMES);
    int canLookahead = get_cap(s, codec, NV_ENC_CAPS_SUPPORT_LOOKAHEAD);
    int canTemporalAq = get_cap(s, codec, NV_ENC_CAPS_SUPPORT_TEMPORAL_AQ);
    int canTenBit = get_cap(s, codec, NV_ENC_CAPS_SUPPORT_10BIT_ENCODE);
    int bRefMode = get_cap(s, codec, NV_ENC_CAPS_SUPPORT_BFRAME_REF_MODE);

    int bFrames = cfg->bFrames < maxB ? cfg->bFrames : maxB;
    if (bFrames < 0) bFrames = 0;
    int lookahead = canLookahead ? cfg->lookahead : 0;
    if (lookahead > 31 - bFrames) lookahead = 31 - bFrames;
    int tenBit = cfg->tenBit && canTenBit && cfg->codec != 0;
    int temporalAq = cfg->temporalAq && canTemporalAq && lookahead > 0;

    NV_ENC_PRESET_CONFIG presetConfig = { 0 };
    presetConfig.version = NV_ENC_PRESET_CONFIG_VER;
    presetConfig.presetCfg.version = NV_ENC_CONFIG_VER;
    st = s->api.nvEncGetEncodePresetConfigEx(s->encoder, codec, preset, NV_ENC_TUNING_INFO_HIGH_QUALITY, &presetConfig);
    if (st != NV_ENC_SUCCESS) { set_error(err, errCap, "nvEncGetEncodePresetConfigEx", st); destroy_session(s); return 0; }

    NV_ENC_CONFIG config = presetConfig.presetCfg;
    config.version = NV_ENC_CONFIG_VER;
    config.gopLength = (uint32_t)cfg->gopLength;
    config.frameIntervalP = bFrames + 1;

    NV_ENC_RC_PARAMS* rc = &config.rcParams;
    rc->rateControlMode = NV_ENC_PARAMS_RC_VBR;
    rc->averageBitRate = (uint32_t)cfg->bitrate;
    rc->maxBitRate = (uint32_t)cfg->maxBitrate;
    rc->vbvBufferSize = (uint32_t)cfg->vbvBuffer;
    rc->vbvInitialDelay = (uint32_t)cfg->vbvBuffer;
    rc->multiPass = cfg->multipass == 2 ? NV_ENC_TWO_PASS_FULL_RESOLUTION
                  : cfg->multipass == 1 ? NV_ENC_TWO_PASS_QUARTER_RESOLUTION
                  : NV_ENC_MULTI_PASS_DISABLED;
    rc->enableAQ = cfg->spatialAq ? 1 : 0;
    rc->aqStrength = (uint32_t)(cfg->aqStrength > 0 && cfg->aqStrength <= 15 ? cfg->aqStrength : 0);
    rc->enableTemporalAQ = temporalAq ? 1 : 0;
    rc->enableLookahead = lookahead > 0 ? 1 : 0;
    rc->lookaheadDepth = (uint16_t)lookahead;

    if (cfg->codec == 0) {
        NV_ENC_CONFIG_H264* h = &config.encodeCodecConfig.h264Config;
        h->idrPeriod = (uint32_t)cfg->gopLength;
        h->repeatSPSPPS = 1;
        h->outputAUD = 0;
        h->chromaFormatIDC = 1;
        h->useBFramesAsRef = bFrames > 1 && bRefMode ? NV_ENC_BFRAME_REF_MODE_MIDDLE : NV_ENC_BFRAME_REF_MODE_DISABLED;
        h->h264VUIParameters.videoSignalTypePresentFlag = 1;
        h->h264VUIParameters.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
        h->h264VUIParameters.videoFullRangeFlag = 0;
        h->h264VUIParameters.colourDescriptionPresentFlag = 1;
        h->h264VUIParameters.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        h->h264VUIParameters.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        h->h264VUIParameters.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
    } else if (cfg->codec == 1) {
        NV_ENC_CONFIG_HEVC* h = &config.encodeCodecConfig.hevcConfig;
        h->idrPeriod = (uint32_t)cfg->gopLength;
        h->repeatSPSPPS = 1;
        h->outputAUD = 0;
        h->chromaFormatIDC = 1;
        h->pixelBitDepthMinus8 = tenBit ? 2 : 0;
        h->useBFramesAsRef = bFrames > 1 && bRefMode ? NV_ENC_BFRAME_REF_MODE_MIDDLE : NV_ENC_BFRAME_REF_MODE_DISABLED;
        h->hevcVUIParameters.videoSignalTypePresentFlag = 1;
        h->hevcVUIParameters.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
        h->hevcVUIParameters.videoFullRangeFlag = 0;
        h->hevcVUIParameters.colourDescriptionPresentFlag = 1;
        h->hevcVUIParameters.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        h->hevcVUIParameters.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        h->hevcVUIParameters.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
        config.profileGUID = tenBit ? NV_ENC_HEVC_PROFILE_MAIN10_GUID : NV_ENC_HEVC_PROFILE_MAIN_GUID;
    } else {
        NV_ENC_CONFIG_AV1* a = &config.encodeCodecConfig.av1Config;
        a->idrPeriod = (uint32_t)cfg->gopLength;
        a->repeatSeqHdr = 1;
        a->chromaFormatIDC = 1;
        a->inputPixelBitDepthMinus8 = tenBit ? 2 : 0;
        a->pixelBitDepthMinus8 = tenBit ? 2 : 0;
        a->useBFramesAsRef = bFrames > 1 && bRefMode ? NV_ENC_BFRAME_REF_MODE_MIDDLE : NV_ENC_BFRAME_REF_MODE_DISABLED;
        a->colorPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        a->transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        a->matrixCoefficients = NV_ENC_VUI_MATRIX_COEFFS_BT709;
        a->colorRange = 0;
    }

    NV_ENC_INITIALIZE_PARAMS init = { 0 };
    init.version = NV_ENC_INITIALIZE_PARAMS_VER;
    init.encodeGUID = codec;
    init.presetGUID = preset;
    init.tuningInfo = NV_ENC_TUNING_INFO_HIGH_QUALITY;
    init.encodeWidth = (uint32_t)cfg->width;
    init.encodeHeight = (uint32_t)cfg->height;
    init.darWidth = (uint32_t)cfg->width;
    init.darHeight = (uint32_t)cfg->height;
    init.maxEncodeWidth = (uint32_t)cfg->width;
    init.maxEncodeHeight = (uint32_t)cfg->height;
    init.frameRateNum = (uint32_t)cfg->fps;
    init.frameRateDen = 1;
    init.enablePTD = 1;
    init.enableEncodeAsync = 1;
    init.encodeConfig = &config;

    st = s->api.nvEncInitializeEncoder(s->encoder, &init);
    if (st != NV_ENC_SUCCESS) {
        // Самые требовательные к железу вещи — многопроходность и просмотр вперёд.
        // Без них NVENC всё ещё лучше MFT (адаптивное квантование, B-кадры).
        rc->multiPass = NV_ENC_MULTI_PASS_DISABLED;
        rc->enableLookahead = 0;
        rc->lookaheadDepth = 0;
        rc->enableTemporalAQ = 0;
        lookahead = 0;
        temporalAq = 0;
        st = s->api.nvEncInitializeEncoder(s->encoder, &init);
        if (st != NV_ENC_SUCCESS) {
            set_error(err, errCap, "nvEncInitializeEncoder", st);
            destroy_session(s);
            return 0;
        }
    }

    s->format = tenBit ? NV_ENC_BUFFER_FORMAT_YUV420_10BIT : NV_ENC_BUFFER_FORMAT_NV12;
    s->width = cfg->width;
    s->height = cfg->height;

    // Глубина конвейера: кадры, которые NVENC держит у себя (просмотр вперёд и
    // B-кадры), плюс запас, чтобы подача не ждала выдачу.
    int count = cfg->bufferCount;
    int minimum = lookahead + bFrames + 4;
    if (count < minimum) count = minimum;
    if (count > MAX_BUFFERS) count = MAX_BUFFERS;
    for (int i = 0; i < count; i++) {
        NV_ENC_CREATE_BITSTREAM_BUFFER buffer = { 0 };
        buffer.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
        st = s->api.nvEncCreateBitstreamBuffer(s->encoder, &buffer);
        if (st != NV_ENC_SUCCESS) { set_error(err, errCap, "nvEncCreateBitstreamBuffer", st); destroy_session(s); return 0; }
        s->slots[i].bitstream = buffer.bitstreamBuffer;

        s->slots[i].event = CreateEventW(NULL, FALSE, FALSE, NULL);
        NV_ENC_EVENT_PARAMS ev = { 0 };
        ev.version = NV_ENC_EVENT_PARAMS_VER;
        ev.completionEvent = s->slots[i].event;
        st = s->api.nvEncRegisterAsyncEvent(s->encoder, &ev);
        if (st != NV_ENC_SUCCESS) { set_error(err, errCap, "nvEncRegisterAsyncEvent", st); destroy_session(s); return 0; }
        s->slotCount = i + 1;
    }

    if (applied) {
        applied->bFrames = bFrames;
        applied->lookahead = lookahead;
        applied->temporalAq = temporalAq;
        applied->spatialAq = cfg->spatialAq ? 1 : 0;
        applied->multipass = rc->multiPass == NV_ENC_MULTI_PASS_DISABLED ? 0
                           : rc->multiPass == NV_ENC_TWO_PASS_QUARTER_RESOLUTION ? 1 : 2;
        applied->bufferCount = s->slotCount;
        applied->bFrameRef = bFrames > 1 && bRefMode ? 1 : 0;
        applied->tenBit = tenBit;
    }
    *out = s;
    return 1;
}

static NV_ENC_REGISTERED_PTR register_texture(Session* s, void* texture) {
    for (int i = 0; i < s->registeredCount; i++)
        if (s->registered[i].texture == texture) return s->registered[i].handle;
    if (s->registeredCount >= MAX_REGISTERED) return NULL;

    NV_ENC_REGISTER_RESOURCE reg = { 0 };
    reg.version = NV_ENC_REGISTER_RESOURCE_VER;
    reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
    reg.width = (uint32_t)s->width;
    reg.height = (uint32_t)s->height;
    reg.pitch = 0;
    reg.resourceToRegister = texture;
    reg.bufferFormat = s->format;
    reg.bufferUsage = NV_ENC_INPUT_IMAGE;
    int t = trace_begin(s, API_REGISTER, s->sent);
    NVENCSTATUS st = s->api.nvEncRegisterResource(s->encoder, &reg);
    trace_end(s, t, st);
    if (st != NV_ENC_SUCCESS) { remember_error(s, "nvEncRegisterResource", st); return NULL; }
    s->registered[s->registeredCount].texture = texture;
    s->registered[s->registeredCount].handle = reg.registeredResource;
    s->registeredCount++;
    return reg.registeredResource;
}

// Сколько выходных буферов свободно: подавать кадр можно, только если > 0.
AURA_EXPORT int aura_nvenc_free_slots(void* handle) {
    Session* s = (Session*)handle;
    EnterCriticalSection(&s->lock);
    int free = s->slotCount - (int)(s->sent - s->got);
    LeaveCriticalSection(&s->lock);
    return free;
}

// Отправить кадр. 1 — принят, 0 — нет свободного буфера, <0 — ошибка NVENC.
AURA_EXPORT int aura_nvenc_encode(void* handle, void* texture, int64_t pts, int forceIdr) {
    Session* s = (Session*)handle;
    EnterCriticalSection(&s->lock);
    if (s->sent - s->got >= s->slotCount || s->eosSent) { LeaveCriticalSection(&s->lock); return 0; }
    Slot* slot = &s->slots[s->sent % s->slotCount];
    LeaveCriticalSection(&s->lock);

    // Поток выдачи снимает отображение входа — отображение и отправка идут под
    // тем же замком: NVENC не обещает, что эти вызовы можно смешивать из двух потоков.
    EnterCriticalSection(&s->apiLock);
    NV_ENC_REGISTERED_PTR reg = register_texture(s, texture);
    if (!reg) { LeaveCriticalSection(&s->apiLock); return -1000; }

    NV_ENC_MAP_INPUT_RESOURCE map = { 0 };
    map.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    map.registeredResource = reg;
    int t = trace_begin(s, API_MAP, s->sent);
    NVENCSTATUS st = s->api.nvEncMapInputResource(s->encoder, &map);
    trace_end(s, t, st);
    if (st != NV_ENC_SUCCESS) { remember_error(s, "nvEncMapInputResource", st); LeaveCriticalSection(&s->apiLock); return -(int)st; }

    NV_ENC_PIC_PARAMS pic = { 0 };
    pic.version = NV_ENC_PIC_PARAMS_VER;
    pic.inputBuffer = map.mappedResource;
    pic.bufferFmt = map.mappedBufferFmt;
    pic.inputWidth = (uint32_t)s->width;
    pic.inputHeight = (uint32_t)s->height;
    pic.outputBitstream = slot->bitstream;
    pic.completionEvent = slot->event;
    pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    pic.inputTimeStamp = (uint64_t)pts;
    if (forceIdr) pic.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;

    slot->mapped = map.mappedResource;
    t = trace_begin(s, API_ENCODE, s->sent);
    st = s->api.nvEncEncodePicture(s->encoder, &pic);
    trace_end(s, t, st);
    if (st != NV_ENC_SUCCESS && st != NV_ENC_ERR_NEED_MORE_INPUT) {
        remember_error(s, "nvEncEncodePicture", st);
        s->api.nvEncUnmapInputResource(s->encoder, map.mappedResource);
        slot->mapped = NULL;
        LeaveCriticalSection(&s->apiLock);
        return -(int)st;
    }
    LeaveCriticalSection(&s->apiLock);

    EnterCriticalSection(&s->lock);
    s->sent++;
    LeaveCriticalSection(&s->lock);
    return 1;
}

// Сообщить о конце потока: NVENC выдаст всё, что держит у себя.
AURA_EXPORT void aura_nvenc_end(void* handle) {
    Session* s = (Session*)handle;
    EnterCriticalSection(&s->lock);
    if (s->eosSent) { LeaveCriticalSection(&s->lock); return; }
    s->eosSent = 1;
    LeaveCriticalSection(&s->lock);

    NV_ENC_PIC_PARAMS pic = { 0 };
    pic.version = NV_ENC_PIC_PARAMS_VER;
    pic.encodePicFlags = NV_ENC_PIC_FLAG_EOS;
    // В асинхронном режиме конец потока сигналит событием последнего буфера
    pic.completionEvent = s->slots[s->sent % s->slotCount].event;
    EnterCriticalSection(&s->apiLock);
    int t = trace_begin(s, API_EOS, s->sent);
    NVENCSTATUS st = s->api.nvEncEncodePicture(s->encoder, &pic);
    trace_end(s, t, st);
    LeaveCriticalSection(&s->apiLock);
}

// Забрать следующий выход по порядку. 1 — кадр в *data (действителен до следующего
// вызова), 0 — ещё не готов за timeoutMs, -2 — отправленных кадров нет,
// <-2 — ошибка NVENC.
//
// Выход блокируется РОВНО ОДИН раз. Прежде кадр, не влезший в буфер вызывающего,
// отпускался, событие взводилось заново, и тот же выход блокировался повторно с
// большим буфером. Повторный nvEncLockBitstream после разблокировки навсегда
// вешается в драйвере: так вставал конвейер на крупных ключевых кадрах (игра,
// 1440p, 10 бит — больше мегабайта). Теперь кадр копируется во внутренний буфер,
// который растёт под размер кадра.
AURA_EXPORT int aura_nvenc_get(void* handle, uint32_t timeoutMs, const uint8_t** data, int* size,
                               int64_t* pts, int* pictureType) {
    Session* s = (Session*)handle;
    EnterCriticalSection(&s->lock);
    int64_t pending = s->sent - s->got;
    LeaveCriticalSection(&s->lock);
    if (pending <= 0) return -2;

    Slot* slot = &s->slots[s->got % s->slotCount];
    if (WaitForSingleObject(slot->event, timeoutMs) != WAIT_OBJECT_0) return 0;

    NV_ENC_LOCK_BITSTREAM lock = { 0 };
    lock.version = NV_ENC_LOCK_BITSTREAM_VER;
    lock.outputBitstream = slot->bitstream;
    int t = trace_begin(s, API_LOCK, s->got);
    NVENCSTATUS st = s->api.nvEncLockBitstream(s->encoder, &lock);
    trace_end(s, t, st);
    if (st != NV_ENC_SUCCESS) {
        // Выход не получить — пропускаем его целиком: снимаем отображение входа и
        // идём к следующему. Иначе следующий вызов ждал бы уже снятое событие этого
        // же выхода, и энкодер встал бы насовсем.
        remember_error(s, "nvEncLockBitstream", st);
        EnterCriticalSection(&s->apiLock);
        if (slot->mapped) {
            s->api.nvEncUnmapInputResource(s->encoder, slot->mapped);
            slot->mapped = NULL;
        }
        LeaveCriticalSection(&s->apiLock);
        EnterCriticalSection(&s->lock);
        s->got++;
        LeaveCriticalSection(&s->lock);
        return -(int)st - 3;
    }

    int bytes = (int)lock.bitstreamSizeInBytes;
    int result = 1;
    if (bytes > s->outCapacity) {
        int grown = bytes * 2 > (1 << 20) ? bytes * 2 : (1 << 20);
        uint8_t* bigger = s->out ? (uint8_t*)HeapReAlloc(GetProcessHeap(), 0, s->out, (SIZE_T)grown)
                                 : (uint8_t*)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)grown);
        if (bigger) { s->out = bigger; s->outCapacity = grown; }
    }
    if (bytes <= s->outCapacity) {
        memcpy(s->out, lock.bitstreamBufferPtr, (size_t)bytes);
    } else {
        // Памяти нет даже под один кадр: кадр теряется, но выход всё равно
        // отпускается и снимается как обычно — повторно его не блокируем.
        bytes = 0;
        result = -1;
    }
    *data = s->out;
    *size = bytes;
    *pts = (int64_t)lock.outputTimeStamp;
    *pictureType = (int)lock.pictureType;
    t = trace_begin(s, API_UNLOCK, s->got);
    st = s->api.nvEncUnlockBitstream(s->encoder, slot->bitstream);
    trace_end(s, t, st);

    // Вход этого порядкового номера NVENC больше не держит
    EnterCriticalSection(&s->apiLock);
    if (slot->mapped) {
        t = trace_begin(s, API_UNMAP, s->got);
        st = s->api.nvEncUnmapInputResource(s->encoder, slot->mapped);
        trace_end(s, t, st);
        slot->mapped = NULL;
    }
    LeaveCriticalSection(&s->apiLock);
    EnterCriticalSection(&s->lock);
    s->got++;
    LeaveCriticalSection(&s->lock);
    return result;
}

// Заголовок последовательности (SPS/PPS/VPS или OBU заголовка AV1).
AURA_EXPORT int aura_nvenc_sequence_header(void* handle, uint8_t* buffer, int capacity, int* size) {
    Session* s = (Session*)handle;
    NV_ENC_SEQUENCE_PARAM_PAYLOAD payload = { 0 };
    uint32_t written = 0;
    payload.version = NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER;
    payload.inBufferSize = (uint32_t)capacity;
    payload.spsppsBuffer = buffer;
    payload.outSPSPPSPayloadSize = &written;
    NVENCSTATUS st = s->api.nvEncGetSequenceParams(s->encoder, &payload);
    if (st != NV_ENC_SUCCESS) return -(int)st;
    *size = (int)written;
    return 1;
}

AURA_EXPORT void aura_nvenc_destroy(void* handle) {
    destroy_session((Session*)handle);
}

static const char* api_name(int api) {
    switch (api) {
        case API_REGISTER: return "nvEncRegisterResource";
        case API_MAP: return "nvEncMapInputResource";
        case API_ENCODE: return "nvEncEncodePicture";
        case API_LOCK: return "nvEncLockBitstream";
        case API_UNLOCK: return "nvEncUnlockBitstream";
        case API_UNMAP: return "nvEncUnmapInputResource";
        case API_EOS: return "nvEncEncodePicture(EOS)";
        case API_DESTROY: return "nvEncDestroyEncoder";
        default: return "?";
    }
}

// Снимок состояния сессии и последних вызовов NVENC — для лога, когда конвейер встал.
// Читается из другого потока без замков: это диагностика, гонка на одном поле
// безвредна, а брать замок, который может держать зависший поток, нельзя.
AURA_EXPORT int aura_nvenc_trace(void* handle, char* buffer, int capacity) {
    Session* s = (Session*)handle;
    if (!s || !buffer || capacity <= 0) return 0;
    LARGE_INTEGER freq;
    QueryPerformanceFrequency(&freq);
    int64_t now = qpc_now();
    int mapped = 0;
    for (int i = 0; i < s->slotCount; i++) if (s->slots[i].mapped) mapped++;
    int n = snprintf(buffer, (size_t)capacity,
                     "отправлено %lld, забрано %lld, в работе %lld/%d, отображено входов %d, зарегистрировано %d%s%s\n",
                     (long long)s->sent, (long long)s->got, (long long)(s->sent - s->got), s->slotCount,
                     mapped, s->registeredCount, s->lastError[0] ? "; последняя ошибка: " : "", s->lastError);
    LONG next = s->traceNext;
    LONG first = next > 16 ? next - 16 : 0;
    for (LONG i = first; i < next && n < capacity - 1; i++) {
        TraceEntry e = s->trace[i % TRACE_SIZE];
        double since = (double)(now - e.enter) * 1000.0 / (double)freq.QuadPart;
        if (e.exit == 0)
            n += snprintf(buffer + n, (size_t)(capacity - n),
                          "  кадр %lld поток %lu %s — ВНУТРИ ВЫЗОВА уже %.0f мс\n",
                          (long long)e.frame, e.thread, api_name(e.api), since);
        else
            n += snprintf(buffer + n, (size_t)(capacity - n),
                          "  кадр %lld поток %lu %s → %d за %.2f мс (%.0f мс назад)\n",
                          (long long)e.frame, e.thread, api_name(e.api), e.status,
                          (double)(e.exit - e.enter) * 1000.0 / (double)freq.QuadPart, since);
    }
    return n;
}
