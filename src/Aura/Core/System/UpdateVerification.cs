using System.Security.Cryptography;
using Aura.Core.Logging;

namespace Aura.Core.SystemIntegration;

/// <summary>
/// Проверка того, что скачанный установщик — действительно наш.
///
/// ЗАЧЕМ. Обновление запускает скачанный exe, а приложение работает с правами
/// администратора. До этого единственной проверкой было «файл больше мегабайта»:
/// всё, что прилетело по ссылке из релиза, запускалось как есть.
///
/// ТРИ СЛОЯ, от дешёвого к строгому:
///
/// 1. ЭТО ВООБЩЕ ИСПОЛНЯЕМЫЙ ФАЙЛ WINDOWS. Ловит самый частый случай — вместо
///    установщика приехала страница ошибки или обрезанный ответ CDN.
///
/// 2. SHA-256 СОВПАДАЕТ С ОПУБЛИКОВАННЫМ. Слепок лежит отдельным файлом релиза и
///    приезжает по ответу API (api.github.com), а сам установщик — с другого хоста
///    (objects.githubusercontent.com). Подмена на стороне раздачи не проходит.
///
/// 3. ПОДПИСЬ НАШИМ КЛЮЧОМ. Слепок подписан закрытым ключом ECDSA P-256, который
///    НИКОГДА не попадает ни в репозиторий, ни на GitHub. Только этот слой защищает
///    от угона аккаунта: слепок, выложенный злоумышленником, он подписать не сможет.
///
/// Пока <see cref="PublicKeyBase64"/> пуст, третий слой выключен, и об этом честно
/// пишется в лог при каждом обновлении. Как завести ключ — см. README.
/// </summary>
internal static class UpdateVerification
{
    /// <summary>
    /// Открытый ключ ECDSA P-256 в формате SubjectPublicKeyInfo (DER), base64.
    ///
    /// Пустая строка = ключ ещё не заведён. Значение вписывается СЮДА, в исходник:
    /// оно должно ехать вместе со сборкой, а не подтягиваться из сети или настроек —
    /// иначе смысл проверки теряется.
    /// </summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4tsNozJrEr11M9TagqvvkZNFRHUovbwGz0QFMs0wz2Kr" +
        "qMvJKaCRIoBfwxHMtzv1/uV59O7z8u77G60F9BwqSA==";

    /// <summary>
    /// Хосты, с которых вообще позволено что-то скачивать. Ссылка приходит из ответа
    /// GitHub, но доверять ей вслепую нельзя: поле в JSON — это просто строка.
    /// </summary>
    public static bool IsTrustedHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>SHA-256 файла в нижнем регистре hex.</summary>
    public static string Sha256File(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Начинается ли файл с сигнатуры PE («MZ»). Страница ошибки, HTML-заглушка
    /// прокси и обрезанная закачка сюда не проходят.
    /// </summary>
    public static bool LooksLikeWindowsExecutable(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    /// <summary>
    /// Вытащить слепок из текста файла .sha256. Понимает и голый hex, и обычный
    /// формат sha256sum («&lt;hex&gt; *имя-файла»).
    /// </summary>
    public static string? ParseDigest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string first = text.Trim().Split('\n')[0].Trim();
        string hex = first.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (hex.Length != 64) return null;
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return null;
        return hex.ToLowerInvariant();
    }

    /// <summary>
    /// Проверить подпись слепка нашим ключом.
    ///
    /// Принимаем оба принятых формата подписи ECDSA: «сырой» r||s, который отдаёт
    /// .NET, и DER-последовательность, которую отдаёт openssl. Так подписывать можно
    /// чем удобно, не подгоняя инструмент под проверяющего.
    ///
    /// Ключ передаётся параметром, а не читается из константы: так проверку можно
    /// прогнать тестами на сгенерированной паре, не делая саму константу изменяемой.
    /// </summary>
    public static bool VerifySignature(string publicKeyBase64, string digestHex, byte[] signature, out string error)
    {
        error = "";
        if (publicKeyBase64.Length == 0) { error = "открытый ключ не заведён"; return false; }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            byte[] hash = Convert.FromHexString(digestHex);
            if (ecdsa.VerifyHash(hash, signature)) return true;
            if (ecdsa.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence)) return true;

            error = "подпись не соответствует слепку";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Полная проверка скачанного установщика. Бросает, если файл нельзя запускать.
    ///
    /// <paramref name="publishedDigest"/> и <paramref name="signature"/> — содержимое
    /// файлов релиза .sha256 и .sig; null означает «в релизе такого файла нет».
    /// </summary>
    public static void EnsureTrusted(string filePath, string? publishedDigest, byte[]? signature) =>
        EnsureTrusted(filePath, publishedDigest, signature, PublicKeyBase64);

    /// <summary>То же с явным ключом — для тестов, см. <see cref="VerifySignature"/>.</summary>
    public static void EnsureTrusted(string filePath, string? publishedDigest, byte[]? signature, string publicKeyBase64)
    {
        var info = new FileInfo(filePath);
        // Установщик весит ~156 МБ; крохотный файл = страница ошибки, а не сборка
        if (info.Length < 1024 * 1024)
            throw new InvalidOperationException(
                $"Скачанный файл подозрительно мал ({info.Length} байт) — ссылка на релиз битая.");

        if (!LooksLikeWindowsExecutable(filePath))
            throw new InvalidOperationException(
                "Скачанный файл не является программой Windows — обновление отменено.");

        string actual = Sha256File(filePath);
        string? expected = ParseDigest(publishedDigest);

        if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Контрольная сумма установщика не совпадает с опубликованной — обновление отменено.");

        if (publicKeyBase64.Length == 0)
        {
            // Честно и громко: третьего слоя нет, и это видно в логе каждого обновления
            Log.Warn("Update", "Ключ подписи обновлений не заведён — установщик проверен только " +
                               $"по контрольной сумме ({(expected is null ? "и та не опубликована" : "совпала")})");
            if (expected is null)
                throw new InvalidOperationException(
                    "В релизе нет ни подписи, ни контрольной суммы — проверить установщик нечем.");
            return;
        }

        if (signature is null || signature.Length == 0)
            throw new InvalidOperationException(
                "Релиз не подписан ключом проекта — обновление отменено.");

        if (!VerifySignature(publicKeyBase64, actual, signature, out string error))
            throw new InvalidOperationException($"Подпись установщика недействительна ({error}) — обновление отменено.");

        Log.Info("Update", $"Установщик проверен: SHA-256 {actual[..16]}…, подпись ключом проекта верна");
    }
}
