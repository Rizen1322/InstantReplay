using Vortice.Direct3D11;

namespace Aura.Core.Capture;

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
    /// Источник кадров умер безвозвратно — потеряно устройство D3D (TDR, обновление
    /// драйвера, переключение GPU). Все объекты D3D мертвы, конвейер нужно собирать
    /// заново; сам источник из этого состояния не выберется.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНОЕ СОБЫТИЕ. У WGC кадры приходят во FreeThreaded-колбэк WinRT:
    /// выпустить туда исключение — значит уронить процесс, а не сообщить о проблеме.
    /// У DDA свой поток захвата, где исключение просто уходило в лог, и запись
    /// молча не возвращалась. Обоим нужен путь «сказать движку», и он один.
    /// </summary>
    event Action<CaptureFailure>? Failed;

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

/// <summary>Выбор способа захвата экрана.</summary>
public static class ScreenCaptureFactory
{
    /// <summary>
    /// Для записи всего монитора используем Desktop Duplication на любой Windows.
    ///
    /// Причина не теоретическая: на RTX 3070 в borderless-игре с незажатым FPS
    /// WGC отдавал 19–24 новых кадра/с, пока сам NVENC стабильно кодировал 60/с.
    /// Desktop Duplication получает уже представленный рабочий стол напрямую через
    /// DXGI, поддерживает полноэкранный DirectX и не зависит от WGC-сессии DWM.
    /// Курсор эта реализация теперь дорисовывает сама, а рамки захвата у неё нет.
    ///
    /// WGC оставлен для диагностики через INSTANTREPLAY_CAPTURE=wgc.
    /// </summary>
    /// <param name="monitorIndex">
    /// Нужен уже здесь: устройство D3D создаётся на адаптере ЭТОГО монитора,
    /// а не на адаптере по умолчанию (см. <see cref="ScreenCaptureSource"/>).
    /// </param>
    internal static CaptureBackendSelection Selection => CaptureBackendPolicy.SelectInitial(
        Environment.OSVersion.Version.Build,
        Environment.GetEnvironmentVariable("INSTANTREPLAY_CAPTURE"));

    public static IScreenCapture Create(CaptureBackend backend, int monitorIndex)
    {
        if (backend == CaptureBackend.Wgc)
        {
            Logging.Log.Info("Capture", "Захват через Windows Graphics Capture");
            return new ScreenCaptureSource(monitorIndex);
        }

        Logging.Log.Info("Capture", "Захват через Desktop Duplication (рамки записи нет)");
        return new DesktopDuplicationSource();
    }

}
