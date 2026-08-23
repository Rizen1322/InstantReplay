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
    event Action<Exception>? Failed;

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
    /// Windows 11 с выданным правом на захват без рамки — WGC (аппаратный курсор в кадре).
    /// Иначе — Desktop Duplication: рамки там нет в принципе.
    /// </summary>
    /// <param name="monitorIndex">
    /// Нужен уже здесь: устройство D3D создаётся на адаптере ЭТОГО монитора,
    /// а не на адаптере по умолчанию (см. <see cref="ScreenCaptureSource"/>).
    /// </param>
    public static IScreenCapture Create(int monitorIndex)
    {
        if (UsesWgc)
        {
            Logging.Log.Info("Capture", "Захват через Windows Graphics Capture");
            return new ScreenCaptureSource(monitorIndex);
        }

        Logging.Log.Info("Capture", "Захват через Desktop Duplication (рамки записи нет)");
        return new DesktopDuplicationSource();
    }

    /// <summary>
    /// Достанется ли захвату WGC. Вынесено отдельно от <see cref="Create"/>, потому
    /// что от этого зависит не только источник кадров: аппаратный курсор умеет класть
    /// в кадр только WGC, и настройкам нужно знать это ДО запуска движка, чтобы не
    /// обещать курсор там, где его не будет.
    ///
    /// Права на захват без рамки здесь НЕТ в условии намеренно: подменять источник
    /// втихую хуже, чем попросить разрешение. Если права нет, приложение остаётся на
    /// WGC (курсор в кадре) и открывает пользователю страницу разрешения — см.
    /// App.AskBorderlessPermission.
    /// </summary>
    public static bool UsesWgc
    {
        get
        {
            // Принудительный выбор для диагностики: INSTANTREPLAY_CAPTURE=dda | wgc
            string? forced = Environment.GetEnvironmentVariable("INSTANTREPLAY_CAPTURE")?.Trim().ToLowerInvariant();
            if (forced == "dda") return false;
            if (forced == "wgc") return true;

            bool win11 = Environment.OSVersion.Version.Build >= 22000;
            return win11 && CaptureAccess.IsBorderControlSupported;
        }
    }
}
