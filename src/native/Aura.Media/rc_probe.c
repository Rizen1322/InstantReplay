// Стенд управления битрейтом: кодирует кадры прослойкой Aura (nvenc_shim.c) и
// печатает фактический средний битрейт, число кадров и ключевых. Сравнивается с
// ffmpeg hevc_nvenc на тех же кадрах. Не входит в поставку.
//
// rc_probe.exe ширина высота fps битрейт кадров десять_бит перенастройка источник
//   источник: noise — шум; stdin — сырые кадры (nv12 или p010le) из ffmpeg
//   перенастройка: 1 — на трети кадров снять AQ, на двух третях вернуть (как подстройка нагрузки)

#define COBJMACROS
#define INITGUID
#include "nvenc_shim.c"
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdlib.h>
#include <io.h>
#include <fcntl.h>

static int read_all(uint8_t* buf, size_t n) {
    size_t done = 0;
    while (done < n) {
        size_t r = fread(buf + done, 1, n - done, stdin);
        if (r == 0) return 0;
        done += r;
    }
    return 1;
}

int main(int argc, char** argv) {
    int w = atoi(argv[1]), h = atoi(argv[2]), fps = atoi(argv[3]), bitrate = atoi(argv[4]);
    int frames = atoi(argv[5]), tenBit = atoi(argv[6]), reconf = atoi(argv[7]);
    int fromStdin = argc > 8 && strcmp(argv[8], "stdin") == 0;
    int warmup = argc > 9 ? atoi(argv[9]) : 0;   // статичных кадров до основных (рабочий стол)
    if (fromStdin) _setmode(_fileno(stdin), _O_BINARY);

    ID3D11Device* dev; ID3D11DeviceContext* ctx;
    D3D_FEATURE_LEVEL lv[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, lv, 2, D3D11_SDK_VERSION, &dev, NULL, &ctx))) { printf("нет устройства\n"); return 1; }

    // Те же параметры, что Aura даёт для 1440p60 (NvencSession.ConfigFor): VBR,
    // потолок и VBV — два средних, GOP 2 с, пресет P4, без B-кадров и второго прохода
    AuraNvencConfig cfg = { 0 };
    cfg.codec = 1; cfg.width = w; cfg.height = h; cfg.fps = fps;
    cfg.bitrate = bitrate; cfg.maxBitrate = bitrate * 2; cfg.vbvBuffer = bitrate * 2;
    cfg.tenBit = tenBit; cfg.gopLength = fps * 2; cfg.preset = 4; cfg.lookahead = 0; cfg.bFrames = 0;
    cfg.spatialAq = 1; cfg.temporalAq = 0; cfg.aqStrength = 8; cfg.multipass = 0; cfg.bufferCount = 0;
    AuraNvencApplied applied; void* s; char err[512];
    if (!aura_nvenc_create(dev, &cfg, &applied, &s, err, sizeof err)) { printf("create: %s\n", err); return 1; }
    // Вариант управления битрейтом для сравнения (argv[10]): vbr2 — как в Aura,
    // vbr15 — потолок и VBV 1,5 среднего, vbrvbv1 — VBV одно среднее, cbr — CBR
    const char* mode = argc > 10 ? argv[10] : "vbr2";
    FILE* out = argc > 11 ? fopen(argv[11], "wb") : NULL;   // поток HEVC для сравнения качества
    {
        Session* ss = (Session*)s;
        NV_ENC_CONFIG c = ss->config;
        NV_ENC_RC_PARAMS* rc = &c.rcParams;
        if (strcmp(mode, "vbr15") == 0) { rc->maxBitRate = bitrate / 2 * 3; rc->vbvBufferSize = rc->vbvInitialDelay = bitrate / 2 * 3; }
        if (strcmp(mode, "vbrvbv1") == 0) { rc->vbvBufferSize = rc->vbvInitialDelay = bitrate; }
        if (strcmp(mode, "cbr2") == 0) { rc->rateControlMode = NV_ENC_PARAMS_RC_CBR; rc->maxBitRate = bitrate; rc->vbvBufferSize = rc->vbvInitialDelay = bitrate * 2; }
        if (strcmp(mode, "cbr") == 0) { rc->rateControlMode = NV_ENC_PARAMS_RC_CBR; rc->maxBitRate = bitrate; rc->vbvBufferSize = rc->vbvInitialDelay = bitrate; }
        if (strcmp(mode, "vbr2") != 0) {
            NV_ENC_RECONFIGURE_PARAMS p = { 0 };
            p.version = NV_ENC_RECONFIGURE_PARAMS_VER;
            p.reInitEncodeParams = ss->init;
            p.reInitEncodeParams.encodeConfig = &c;
            p.resetEncoder = 1;
            p.forceIDR = 1;
            NVENCSTATUS st = ss->api.nvEncReconfigureEncoder(ss->encoder, &p);
            if (st == NV_ENC_SUCCESS) { ss->config = c; describe_rc(ss); }
            else printf("режим %s не применился: %d\n", mode, (int)st);
        }
    }
    char info[600];
    aura_nvenc_rc_info(s, info, sizeof info);
    printf("%s\n", info);

    D3D11_TEXTURE2D_DESC d = { 0 };
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
    d.Format = applied.tenBit ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET;
    ID3D11Texture2D* tex[8];
    for (int i = 0; i < 8; i++) ID3D11Device_CreateTexture2D(dev, &d, NULL, &tex[i]);
    int bpp = applied.tenBit ? 2 : 1;
    size_t total = (size_t)w * h * bpp * 3 / 2;
    uint8_t* buf = (uint8_t*)malloc(total);
    uint32_t seed = 12345;

    long long bytes = 0, part[3] = { 0, 0, 0 };
    int sent = 0, got = 0, keys = 0;
    const uint8_t* data; int size; int64_t opts; int type;
    // Разгон: статичный серый кадр, как рабочий стол перед игрой. В подсчёт не входит.
    if (warmup > 0) {
        memset(buf, 0x80, total);
        ID3D11DeviceContext_UpdateSubresource(ctx, (ID3D11Resource*)tex[7], 0, NULL, buf, (UINT)(w * bpp), 0);
        long long warmBytes = 0;
        for (int f = 0; f < warmup; f++) {
            aura_nvenc_encode(s, tex[7], (int64_t)f * 10000000LL / fps + 123456789LL, 0);
            while (aura_nvenc_free_slots(s) <= 0 && aura_nvenc_get(s, 2000, &data, &size, &opts, &type) == 1) warmBytes += size;
        }
        printf("разгон: %d статичных кадров, %.2f Мбит/с\n", warmup, warmBytes * 8.0 * fps / warmup / 1e6);
    }
    int64_t base = (int64_t)warmup * 10000000LL / fps;
    for (int f = 0; f < frames; f++) {
        if (fromStdin) { if (!read_all(buf, total)) break; }
        else for (size_t i = 0; i < total; i++) { seed = seed * 1664525u + 1013904223u; buf[i] = (uint8_t)(64 + ((seed >> 24) & 127)); }
        // vbrreset: сброс счётчиков rate control на каждом плановом ключевом кадре
        if (strcmp(mode, "vbrreset") == 0 && f > 0 && f % (fps * 2) == 0) {
            Session* ss = (Session*)s;
            NV_ENC_RECONFIGURE_PARAMS p = { 0 };
            p.version = NV_ENC_RECONFIGURE_PARAMS_VER;
            p.reInitEncodeParams = ss->init;
            p.reInitEncodeParams.encodeConfig = &ss->config;
            p.resetEncoder = 1;
            p.forceIDR = 1;
            NVENCSTATUS st = ss->api.nvEncReconfigureEncoder(ss->encoder, &p);
            if (st != NV_ENC_SUCCESS) printf("сброс на кадре %d: %d\n", f, (int)st);
        }
        if (reconf && f == frames / 3) printf("перенастройка (без AQ): %d\n", aura_nvenc_reconfigure(s, 0, 0));
        if (reconf && f == 2 * frames / 3) printf("перенастройка (AQ снова): %d\n", aura_nvenc_reconfigure(s, 0, 1));
        ID3D11Texture2D* t = tex[f % 8];
        ID3D11DeviceContext_UpdateSubresource(ctx, (ID3D11Resource*)t, 0, NULL, buf, (UINT)(w * bpp), 0);
        // Метки как у Aura: 100-нс тики часов, а не номера кадров
        int64_t pts = base + (int64_t)f * 10000000LL / fps + 123456789LL;
        int r = aura_nvenc_encode(s, t, pts, 0);
        if (r <= 0) { printf("encode %d: %d\n", f, r); break; }
        sent++;
        // Как в Aura: подача, пока есть свободный буфер, забор готового
        while (aura_nvenc_free_slots(s) <= 0) {
            int g = aura_nvenc_get(s, 2000, &data, &size, &opts, &type);
            if (g != 1) { printf("get: %d\n", g); break; }
            if (out) fwrite(data, 1, (size_t)size, out); bytes += size; part[got * 3 / frames > 2 ? 2 : got * 3 / frames] += size; got++;
            if (type == NV_ENC_PIC_TYPE_IDR || type == NV_ENC_PIC_TYPE_I) keys++;
        }
    }
    aura_nvenc_end(s);
    while (aura_nvenc_get(s, 2000, &data, &size, &opts, &type) == 1) {
        if (out) fwrite(data, 1, (size_t)size, out); bytes += size; part[2] += size; got++;
        if (type == NV_ENC_PIC_TYPE_IDR || type == NV_ENC_PIC_TYPE_I) keys++;
    }
    double third = frames / 3.0;
    printf("подано %d, получено %d, ключевых %d; средний %.1f Мбит/с; по третям %.1f / %.1f / %.1f\n",
           sent, got, keys, bytes * 8.0 * fps / got / 1e6,
           part[0] * 8.0 * fps / third / 1e6, part[1] * 8.0 * fps / third / 1e6, part[2] * 8.0 * fps / third / 1e6);
    if (out) fclose(out);
    aura_nvenc_destroy(s);
    return 0;
}
