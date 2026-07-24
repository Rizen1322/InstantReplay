using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Capture;

/// <summary>
/// Право снимать экран БЕЗ жёлтой рамки записи.
///
/// Жёлтая рамка — системный индикатор Windows Graphics Capture. Отключается
/// свойством GraphicsCaptureSession.IsBorderRequired = false, но само свойство
/// закрыто возможностью graphicsCaptureWithoutBorder: упакованные приложения
/// объявляют её в манифесте, а неупакованным (наш случай) положено запрашивать
/// её через GraphicsCaptureAccess.RequestAccessAsync(Borderless).
///
/// Грабли: на свежих сборках Windows 11 (24H2+) рамка не появляется и без
/// запроса — поэтому баг незаметен у разработчика и вылезает у пользователей
/// на 21H2/22H2/23H2. На Windows 10 API нет вовсе: там рамку убрать нельзя.
/// </summary>
public static class CaptureAccess
{
    private static readonly object Sync = new();
    private static bool _requested;

    /// <summary>Поддерживает ли ОС отключение рамки (Windows 11 21H2+).</summary>
    public static bool IsBorderControlSupported =>
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");

    /// <summary>
    /// Один раз за процесс запрашивает право на захват без рамки.
    /// Синхронно, с таймаутом — вызывается до создания первой сессии захвата.
    /// </summary>
    public static void EnsureBorderlessAccess()
    {
        lock (Sync)
        {
            if (_requested) return;
            _requested = true;

            if (!IsBorderControlSupported)
            {
                Log.Warn("Capture", "ОС не умеет отключать рамку записи (нужна Windows 11) — рамка останется");
                return;
            }
            if (!ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                Log.Info("Capture", "GraphicsCaptureAccess недоступен — полагаемся на IsBorderRequired");
                return;
            }

            try
            {
                // Не на UI-потоке: ожидание WinRT-операции на нём может дать взаимоблокировку
                var task = Task.Run(async () =>
                    await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless));

                if (!task.Wait(5000))
                {
                    Log.Warn("Capture", "Запрос права на захват без рамки: таймаут");
                    return;
                }
                Log.Info("Capture", $"Право на захват без рамки: {task.Result}");
            }
            catch (Exception ex)
            {
                Log.Warn("Capture", $"Запрос права на захват без рамки не удался: {ex.Message}");
            }
        }
    }
}
