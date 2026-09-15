using Vortice.Direct3D11;

namespace Aura.Core.Capture;

/// <summary>
/// Источник кадров экрана. Две реализации:
/// WGC (Windows 11 — можно отключить жёлтую рамку) и
/// Desktop Duplication (Windows 10 — рамки нет в принципе).
/// Контракт один: текстура в событии валидна ТОЛЬКО внутри обработчика,
/// получатель обязан сделать GPU-копию сразу.
/// </summary>
internal interface IScreenCapture : IDisposable
{
    ID3D11Device D3DDevice { get; }
    ID3D11DeviceContext D3DContext { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>Сколько кадров отдала система (до фильтра по FPS) — диагностика.</summary>
    long FramesReceived { get; }
    /// <summary>Сколько прошло фильтр и ушло в конвейер.</summary>
    long FramesAccepted { get; }
    long InvalidCursorShapes { get; }

    /// <summary>Кадр BGRA в VRAM, валидный только во время обработчика.</summary>
    event Action<CapturedSurface>? FrameArrived;

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

    void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation);
    void Start();
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
    /// Windows 11 стартует с WGC: Windows сама композит курсор и этот
    /// путь лучше переживает обычные fullscreen/borderless-переходы. Windows 10
    /// стартует с DDA, чтобы не было неотключаемой рамки захвата. Движок
    /// может автоматически выбрать второй backend при отказе или доказанном
    /// голодании WGC. INSTANTREPLAY_CAPTURE=wgc|dda оставлен только как diagnostic override.
    /// </summary>
    /// <param name="monitorIndex">
    /// Нужен уже здесь: устройство D3D создаётся на адаптере ЭТОГО монитора,
    /// а не на адаптере по умолчанию (см. <see cref="ScreenCaptureSource"/>).
    /// </param>
    /// <summary>
    /// Можно ли переходить на WGC. Только Windows 11: на Windows 10 право на захват
    /// без жёлтой рамки не выдаётся. Принудительный выбор «wgc» через диагностическую
    /// переменную оставляем — это осознанная проверка, а не автоматика.
    /// </summary>
    internal static bool WgcAllowed =>
        Environment.OSVersion.Version.Build >= 22000 ||
        Selection is { Forced: true, Backend: CaptureBackend.Wgc };

    internal static CaptureBackendSelection Selection => CaptureBackendPolicy.SelectInitial(
        Environment.OSVersion.Version.Build,
        Environment.GetEnvironmentVariable("INSTANTREPLAY_CAPTURE"));

    internal static IScreenCapture Create(CaptureSourceRequest request)
    {
        if (request.Backend == CaptureBackend.Wgc)
        {
            Logging.Log.Info("Capture", "Захват через Windows Graphics Capture");
            return new ScreenCaptureSource(request.MonitorIndex);
        }

        if (request.Backend == CaptureBackend.WgcWindow)
        {
            GameCaptureTarget target = request.Target ??
                throw new ArgumentException("Оконному WGC требуется target", nameof(request));
            Logging.Log.Info("Capture", $"Захват игрового окна через WGC: " +
                                        $"{target.ExecutableName}, revision {target.Revision}");
            return new WindowGraphicsCaptureSource(target);
        }

        if (request.Backend == CaptureBackend.MinecraftOpenGl)
        {
            GameCaptureTarget target = request.Target ??
                throw new ArgumentException("Minecraft OpenGL требуется target", nameof(request));
            Logging.Log.Info(
                "Capture",
                $"Гибридный захват Minecraft: WGC-monitor + OpenGL, PID {target.ProcessId}, " +
                $"revision {target.Revision}");
            return new MinecraftGameCaptureSource(target);
        }

        if (request.Backend == CaptureBackend.DesktopDuplication)
        {
            Logging.Log.Info("Capture", "Захват через Desktop Duplication (рамки записи нет)");
            return new DesktopDuplicationSource();
        }

        throw new ArgumentOutOfRangeException(nameof(request), request.Backend, "Неизвестный backend");
    }

    internal static IScreenCapture Create(CaptureBackend backend, int monitorIndex) =>
        Create(CaptureSourceRequest.Create(backend, monitorIndex, target: null));
}
