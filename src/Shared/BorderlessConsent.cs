using Microsoft.Win32;

namespace Aura.Shared;

/// <summary>
/// Разрешение graphicsCaptureWithoutBorder в реестре — то, без чего Windows
/// рисует жёлтую рамку записи вокруг экрана.
///
/// Как устроено согласие (CapabilityAccessManager\ConsentStore):
///   HKLM\...\graphicsCaptureWithoutBorder            Value — общий запрет/разрешение на машину
///   HKCU\...\graphicsCaptureWithoutBorder            Value — «разрешить приложениям»
///   HKCU\...\graphicsCaptureWithoutBorder\NonPackaged Value — «разрешить классическим приложениям»
///   HKCU\...\NonPackaged\C:#Путь#К#Exe               Value — конкретному приложению
/// Отказ на любом уровне даёт GraphicsCaptureAccess.RequestAccessAsync = DeniedByUser.
///
/// Грабли: на чистой Windows 11 машинное значение — Deny (как и у соседнего
/// graphicsCaptureProgrammatic), поэтому у пользователя рамка появляется «на
/// ровном месте» после переустановки системы, хотя приложение не менялось. UI
/// в «Параметрах» для этой возможности нет — правится только реестром.
///
/// HKLM-ветку может писать только процесс с правами администратора: у
/// BUILTIN\Администраторы там FullControl, у пользователей — только чтение.
/// Поэтому вызывается из двух мест: установщик (сработает, если его запустили
/// от админа) и само приложение (оно всегда elevated, см. app.manifest).
///
/// Файл общий для Aura и InstantReplaySetup — подключён ссылкой в оба .csproj,
/// поэтому здесь не должно быть зависимостей ни от одного из проектов.
/// </summary>
public static class BorderlessConsent
{
    private const string CapabilityKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\graphicsCaptureWithoutBorder";

    private const string Allow = "Allow";

    /// <summary>Разрешено ли на уровне машины (HKLM). Отсутствие значения считаем разрешением.</summary>
    public static bool IsMachineWideAllowed()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CapabilityKey);
            string? value = key?.GetValue("Value") as string;
            return value is null || value.Equals(Allow, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Снимает машинный запрет (HKLM). Нужны права администратора; без них
    /// возвращает false с текстом ошибки — вызывающему остаётся только сообщить.
    /// </summary>
    public static bool TryAllowMachineWide(out string detail)
    {
        if (IsMachineWideAllowed()) { detail = "уже разрешено"; return true; }
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(CapabilityKey, writable: true)
                ?? throw new InvalidOperationException("ключ недоступен");
            key.SetValue("Value", Allow, RegistryValueKind.String);
            detail = "разрешено";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Разрешает захват без рамки текущему пользователю: и общий переключатель,
    /// и «классические приложения», и конкретный exe. Прав администратора не требует.
    /// </summary>
    public static bool TryAllowForCurrentUser(string? exePath, out string detail)
    {
        try
        {
            SetAllow(CapabilityKey);
            SetAllow(CapabilityKey + @"\NonPackaged");
            if (!string.IsNullOrWhiteSpace(exePath))
                SetAllow(CapabilityKey + @"\NonPackaged\" + AppKeyName(exePath!));
            detail = "разрешено";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>Убирает запись конкретного exe (при удалении приложения). Общие уровни не трогаем — ими пользуются другие программы.</summary>
    public static void RemoveAppEntry(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        try
        {
            using var nonPackaged = Registry.CurrentUser.OpenSubKey(CapabilityKey + @"\NonPackaged", writable: true);
            nonPackaged?.DeleteSubKeyTree(AppKeyName(exePath!), throwOnMissingSubKey: false);
        }
        catch { /* не повод падать при деинсталляции */ }
    }

    private static void SetAllow(string subKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"не создать ключ {subKey}");
        key.SetValue("Value", Allow, RegistryValueKind.String);
    }

    /// <summary>Windows хранит путь приложения как имя ключа, заменяя «\» на «#»: C:#Program Files#App#App.exe.</summary>
    private static string AppKeyName(string exePath) => exePath.Replace('\\', '#');
}
