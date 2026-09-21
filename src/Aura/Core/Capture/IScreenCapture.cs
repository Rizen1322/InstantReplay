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
/// (буфер выключен) или готовый кадр не годится; вызывающий делает собственную
/// одноразовую сессию.
///
/// <paramref name="allowStale"/> — согласен ли вызывающий на последний готовый кадр
/// ЛЮБОГО возраста. Для миниатюры уведомления это нормально: она иллюстрирует
/// событие, которое уже произошло. Для скриншота — нет: там кадр обязан совпадать
/// с тем, что человек видит на экране прямо сейчас. Запрос с false отдаёт кадр,
/// только если он свежий, и позволяет вызывающему уйти на свою сессию захвата.
/// </summary>
public delegate bool LiveFrameProvider(UseFrame use, bool allowStale);

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

    /// <summary>
    /// Есть ли WGC на этой системе вообще.
    ///
    /// Отличается от <see cref="WgcAllowed"/> намеренно. Там решается, можно ли
    /// ПИСАТЬ через WGC: на Windows 10 нельзя, потому что запись получит жёлтую
    /// рамку. Здесь — можно ли ОДИН РАЗ снять экран, и рамка на снимке не успевает
    /// появиться. Это единственный способ сделать скриншот, пока буфер держит
    /// дупликацию монитора: вторую сессию DDA того же выхода система не создаёт.
    ///
    /// API появился в Windows 10 1903 (сборка 18362).
    /// </summary>
    internal static bool IsWgcAvailable
    {
        get
        {
            if (Environment.OSVersion.Version.Build < 18362) return false;
            try { return Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

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
