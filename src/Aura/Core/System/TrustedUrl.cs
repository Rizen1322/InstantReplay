namespace Aura.Core.SystemIntegration;

/// <summary>
/// Ворота для ссылок, пришедших из сети.
///
/// ЗАЧЕМ. Поле в JSON — это просто строка. Если её отдать в Process.Start с
/// UseShellExecute, ShellExecute отработает не только https, но и UNC-путь
/// (\\host\share\x.exe), локальный путь и любой зарегистрированный
/// протокол-хендлер. Приложение работает с правами администратора, то есть
/// запущенное так получает высокий уровень целостности без запроса UAC.
///
/// Проверка идёт по <see cref="Uri.Host"/>, а не по подстроке в тексте ссылки:
/// «https://nvidia.com@evil.io/x.exe» содержит nvidia.com, но хост там evil.io.
///
/// Ссылки на обновление проверяет <see cref="UpdateVerification.IsTrustedHost"/> —
/// те же ворота, отдельный список хостов.
/// </summary>
internal static class TrustedUrl
{
    /// <summary>Домены NVIDIA, с которых допустимо открывать загрузку драйвера.</summary>
    private static readonly string[] NvidiaDomains = ["nvidia.com", "geforce.com"];

    /// <summary>
    /// Можно ли отдать ссылку в ShellExecute как загрузку драйвера. Требует https
    /// и хост в домене NVIDIA.
    /// </summary>
    public static bool IsNvidiaDriver(string? url) => IsHttpsWithinAny(url, NvidiaDomains);

    private static bool IsHttpsWithinAny(string? url, string[] domains)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        foreach (string domain in domains)
            if (IsWithinDomain(uri.Host, domain)) return true;

        return false;
    }

    /// <summary>
    /// Хост равен домену или является его поддоменом. Точка перед доменом
    /// обязательна, иначе «evil-nvidia.com» и «nvidia.com.evil.io» прошли бы.
    /// </summary>
    private static bool IsWithinDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
