using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// Право снимать экран БЕЗ жёлтой рамки записи.
///
/// Жёлтая рамка — системный индикатор Windows Graphics Capture. Отключается
/// свойством GraphicsCaptureSession.IsBorderRequired = false, но система слушается
/// его только после согласия: GraphicsCaptureAccess.RequestAccessAsync(Borderless).
/// Документация прямо предупреждает: без согласия присвоение false «пройдёт
/// успешно, но значение будет проигнорировано» — то есть обратное чтение свойства
/// НЕ является проверкой, врать оно будет молча.
///
/// Условие для согласия — возможность graphicsCaptureWithoutBorder, объявленная
/// в манифесте ПАКЕТА (uap11:Capability). Процессу без package identity объявить
/// её негде, поэтому неупакованный запуск всегда получает отказ: проверено на
/// 26100.8875 — RequestAccessAsync и AppCapability.CheckAccess дружно отвечали
/// DeniedByUser, а рамка оставалась при IsBorderRequired == false.
///
/// Identity приложение получает от sparse-пакета (packaging with external
/// location): пакет только объявляет identity и возможность, файлы и установщик
/// остаются прежними. Собирается packaging\build_identity_package.ps1,
/// регистрируется установщиком, привязка к exe — элемент msix в app.manifest.
/// </summary>
public static class CaptureAccess
{
    private const int AppModelErrorNoPackage = 15700;

    private static readonly object Sync = new();
    private static bool _requested;

    /// <summary>Поддерживает ли ОС отключение рамки (Windows 10 2104+ / Windows 11).</summary>
    public static bool IsBorderControlSupported =>
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");

    /// <summary>
    /// Выдано ли право на захват без рамки. Пока <see cref="EnsureBorderlessAccess"/>
    /// не вызывали — false: считаем, что права нет, пока не доказано обратное.
    /// </summary>
    public static bool BorderlessGranted { get; private set; }

    /// <summary>Полное имя пакета или null у неупакованного процесса.</summary>
    public static string? PackageFullName
    {
        get
        {
            int length = 0;
            // Первый вызов только измеряет; нет пакета — сразу APPMODEL_ERROR_NO_PACKAGE
            int rc = GetCurrentPackageFullName(ref length, null);
            if (rc == AppModelErrorNoPackage) return null;

            var buffer = new StringBuilder(length);
            rc = GetCurrentPackageFullName(ref length, buffer);
            return rc == 0 ? buffer.ToString() : null;
        }
    }

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

            string? package = PackageFullName;
            Log.Info("Capture", package is null
                ? "Package identity: нет (sparse-пакет не зарегистрирован) — право на захват без рамки не дадут"
                : $"Package identity: {package}");

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
                Report(task.Result);
            }
            catch (Exception ex)
            {
                Log.Warn("Capture", $"Запрос права на захват без рамки не удался: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Разбор ответа системы. Каждый исход разный по смыслу и по тому, что делать
    /// пользователю, поэтому «не Allowed — и ладно» тут не годится.
    /// </summary>
    private static void Report(AppCapabilityAccessStatus status)
    {
        switch (status)
        {
            case AppCapabilityAccessStatus.Allowed:
                BorderlessGranted = true;
                Log.Info("Capture", "Право на захват без рамки: выдано");
                break;

            case AppCapabilityAccessStatus.NotDeclaredByApp:
                Log.Warn("Capture", "Право на захват без рамки: возможность graphicsCaptureWithoutBorder " +
                                    "не объявлена в манифесте пакета — рамка останется");
                break;

            case AppCapabilityAccessStatus.DeniedByUser:
                Log.Warn("Capture", "Право на захват без рамки: отказано. Так же система отвечает процессу " +
                                    "без package identity — проверьте, зарегистрирован ли sparse-пакет");
                break;

            case AppCapabilityAccessStatus.DeniedBySystem:
                Log.Warn("Capture", "Право на захват без рамки: запрещено системной политикой — рамка останется");
                break;

            case AppCapabilityAccessStatus.UserPromptRequired:
                Log.Warn("Capture", "Право на захват без рамки: система ждёт ответа пользователя — рамка останется");
                break;

            default:
                Log.Warn("Capture", $"Право на захват без рамки: неизвестный ответ {status}");
                break;
        }
    }

    /// <summary>
    /// Открывает страницу разрешения в «Параметрах». Диалога согласия у этой
    /// возможности для классических приложений система не показывает: запись
    /// согласия заводится в состоянии Prompt, а решение пользователь принимает
    /// именно здесь. Приложение обязано хотя бы показать, где это делается.
    /// </summary>
    public static void OpenPermissionSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-graphicscapturewithoutborder")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Не удалось открыть страницу разрешения: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
