using Vortice.Direct3D11;

namespace InstantReplay.Core.Capture;

/// <summary>
/// Источник кадров экрана. Две реализации:
/// WGC (Windows 11 — можно отключить жёлтую рамку) и
/// Desktop Duplication (Windows 10 — рамки нет в принципе).
/// Контракт один: текстура в событии валидна ТОЛЬКО внутри обработчика,
/// получатель обязан сделать GPU-копию сразу.
/// </summary>
public interface IScreenCapture : IDisposable
{
    ID3D11Device D3DDevice { get; }
    ID3D11DeviceContext D3DContext { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>Сколько кадров отдала система (до фильтра по FPS) — диагностика.</summary>
    long FramesReceived { get; }
    /// <summary>Сколько прошло фильтр и ушло в конвейер.</summary>
    long FramesAccepted { get; }

    /// <summary>Кадр: текстура BGRA в VRAM + время кадра (QPC, 100-нс тики).</summary>
    event Action<ID3D11Texture2D, long>? FrameArrived;

    /// <summary>
    /// Дать последний захваченный кадр во временное пользование (для скриншота).
    /// Текстура валидна ТОЛЬКО внутри колбэка. false — источник кадр не хранит.
    ///
    /// Нужно, потому что на Windows 10 DXGI не даёт открыть вторую дупликацию того
    /// же монитора: при включённом буфере скриншот своей сессией захвата падал
    /// с DuplicateOutput → E_INVALIDARG.
    /// </summary>
    bool TryUseLatestFrame(Action<ID3D11Texture2D> use);

    void Start(int monitorIndex, int targetFps, bool captureCursor = true);
    void Stop();
}

/// <summary>Использовать кадр: устройство, контекст и текстура (валидны только в колбэке).</summary>
public delegate void UseFrame(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture);

/// <summary>
/// Поставщик кадра из уже работающего захвата. false — живого захвата нет
/// (буфер выключен), вызывающий делает собственную одноразовую сессию.
/// </summary>
public delegate bool LiveFrameProvider(UseFrame use);

/// <summary>Выбор способа захвата под текущую ОС.</summary>
public static class ScreenCaptureFactory
{
    /// <summary>
    /// Windows 11 — WGC (рамку отключаем через IsBorderRequired, есть аппаратный курсор).
    /// Windows 10 — Desktop Duplication: там WGC всегда рисует жёлтую рамку и убрать её
    /// нечем (свойства IsBorderRequired в системе просто нет).
    /// </summary>
    public static IScreenCapture Create()
    {
        // Принудительный выбор для диагностики: INSTANTREPLAY_CAPTURE=dda | wgc
        string? forced = Environment.GetEnvironmentVariable("INSTANTREPLAY_CAPTURE")?.Trim().ToLowerInvariant();
        if (forced == "dda")
        {
            Logging.Log.Info("Capture", "Принудительно: Desktop Duplication");
            return new DesktopDuplicationSource();
        }
        if (forced == "wgc")
        {
            Logging.Log.Info("Capture", "Принудительно: Windows Graphics Capture");
            return new ScreenCaptureSource();
        }

        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        if (win11 && CaptureAccess.IsBorderControlSupported)
            return new ScreenCaptureSource();

        Logging.Log.Info("Capture", "Windows 10: захват через Desktop Duplication (рамки записи нет)");
        return new DesktopDuplicationSource();
    }
}
