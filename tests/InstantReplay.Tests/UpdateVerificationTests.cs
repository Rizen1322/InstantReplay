using System.Security.Cryptography;
using Aura.Core.SystemIntegration;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Проверка установщика перед запуском.
///
/// Обновление стартует скачанный exe, а приложение работает с правами администратора,
/// поэтому каждая ветка отказа здесь — это ветка, по которой чужой код НЕ выполнится.
/// Раньше проверка была одна: «файл больше мегабайта».
/// </summary>
public class UpdateVerificationTests
{
    // ---------------- Хосты ----------------

    [Theory]
    [InlineData("https://github.com/Rizen1322/InstantReplay/releases/download/v1/InstantReplaySetup.exe")]
    [InlineData("https://objects.githubusercontent.com/some/blob")]
    [InlineData("https://release-assets.githubusercontent.com/x")]
    [InlineData("https://api.github.com/repos/x/y/releases/latest")]
    public void СвоиХостыРазрешены(string url) => Assert.True(UpdateVerification.IsTrustedHost(url));

    [Theory]
    [InlineData("http://github.com/file.exe")]                       // без TLS
    [InlineData("https://githubusercontent.com.attacker.net/x.exe")]  // похожее имя
    [InlineData("https://evil.example/InstantReplaySetup.exe")]
    [InlineData("ftp://github.com/x.exe")]
    [InlineData("не ссылка")]
    [InlineData(null)]
    public void ЧужиеХостыОтклонены(string? url) => Assert.False(UpdateVerification.IsTrustedHost(url));

    // ---------------- Слепок ----------------

    [Fact]
    public void СлепокЧитаетсяВОбоихФорматах()
    {
        string hex = new('a', 64);
        Assert.Equal(hex, UpdateVerification.ParseDigest(hex));
        Assert.Equal(hex, UpdateVerification.ParseDigest($"{hex} *InstantReplaySetup.exe"));
        Assert.Equal(hex, UpdateVerification.ParseDigest($"  {hex}  \n"));
        Assert.Equal(hex, UpdateVerification.ParseDigest(hex.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("слишком короткий")]
    [InlineData("zzzz567890123456789012345678901234567890123456789012345678901234")] // не hex
    public void МусорВместоСлепкаНеПринимается(string? text) =>
        Assert.Null(UpdateVerification.ParseDigest(text));

    // ---------------- Содержимое файла ----------------

    [Fact]
    public void СтраницаОшибкиВместоУстановщикаОтклонена()
    {
        string file = WriteFile("<!DOCTYPE html><html>404 Not Found</html>", padToMegabyte: true);
        try
        {
            Assert.False(UpdateVerification.LooksLikeWindowsExecutable(file));
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(file, UpdateVerification.Sha256File(file), null, ""));
            Assert.Contains("не является программой Windows", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ОбрезаннаяЗакачкаОтклонена()
    {
        string file = WriteFile("MZ немного байт", padToMegabyte: false);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(file, null, null, ""));
            Assert.Contains("подозрительно мал", ex.Message);
        }
        finally { File.Delete(file); }
    }

    // ---------------- Контрольная сумма ----------------

    [Fact]
    public void НесовпавшийСлепокОстанавливаетОбновление()
    {
        string file = FakeInstaller();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(file, new string('b', 64), null, ""));
            Assert.Contains("Контрольная сумма", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void БезКлючаИБезСлепкаОбновлениеНеПроходит()
    {
        string file = FakeInstaller();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(file, null, null, ""));
            Assert.Contains("ни подписи, ни контрольной суммы", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void СовпавшегоСлепкаДостаточноПокаКлючНеЗаведён()
    {
        string file = FakeInstaller();
        try
        {
            UpdateVerification.EnsureTrusted(file, UpdateVerification.Sha256File(file), null, "");
        }
        finally { File.Delete(file); }
    }

    // ---------------- Подпись ----------------

    [Fact]
    public void ПодписьНашимКлючомПринимается()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        string file = FakeInstaller();
        try
        {
            string digest = UpdateVerification.Sha256File(file);
            byte[] signature = key.SignHash(Convert.FromHexString(digest));

            UpdateVerification.EnsureTrusted(file, digest, signature, publicKey);
            Assert.True(UpdateVerification.VerifySignature(publicKey, digest, signature, out _));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ПодписьЧужимКлючомОтклоняется()
    {
        using var ours = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(ours.ExportSubjectPublicKeyInfo());

        string file = FakeInstaller();
        try
        {
            string digest = UpdateVerification.Sha256File(file);
            byte[] forged = theirs.SignHash(Convert.FromHexString(digest));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(file, digest, forged, publicKey));
            Assert.Contains("Подпись установщика недействительна", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void СКлючомНеподписанныйРелизНеПроходит()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        string file = FakeInstaller();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(file, UpdateVerification.Sha256File(file), null, publicKey));
            Assert.Contains("не подписан ключом проекта", ex.Message);
        }
        finally { File.Delete(file); }
    }

    /// <summary>
    /// Подпись, выпущенную openssl, приложение обязано принимать: именно им подписывает
    /// release.ps1 (openssl приезжает вместе с Git for Windows, а git у релиза и так в
    /// зависимостях; Windows PowerShell 5.1 работать с ключами PKCS#8 не умеет).
    ///
    /// openssl отдаёт подпись DER-последовательностью, .NET по умолчанию ждёт «сырую»
    /// r||s — расхождение форматов проявилось бы только в бою, на первом же обновлении.
    /// Поэтому здесь ЗАФИКСИРОВАНЫ реальные байты, снятые с openssl 3.5:
    ///
    ///   openssl ecparam -name prime256v1 -genkey -noout -out key.pem
    ///   openssl ec -in key.pem -pubout -outform DER | base64
    ///   openssl dgst -sha256 -sign key.pem -out sig.der payload.bin
    ///
    /// Ключ здесь одноразовый, заведён ровно для этого теста и к релизам отношения
    /// не имеет; закрытая часть нигде не сохранялась.
    /// </summary>
    [Fact]
    public void ПодписьВыпущеннаяOpensslПринимается()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESWjcofisOCZ7GIoiVSriS5jK6UEOG8Ydm/2C8NG/fVXe" +
            "8mZ7Vxy4I+XO9tRfCD788Ikxg8xpXpgY2Q6opGrYHw==";
        const string signature =
            "MEUCIDhphtbRrK4cwToO6A7l4Yaegr+Vd9PJV4w9LT5bsMSjAiEAoP58ts3lw3OlhPKZ7JWZVdn1G0kE" +
            "YgZfcOAKoqUw37E=";
        const string payload = "Aura release signing fixture";

        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();

        Assert.Equal("5afacc9731c2864304848b82119ef8348e075196a4f07ae7a25c6892b55eb7ba", digest);
        Assert.True(
            UpdateVerification.VerifySignature(publicKey, digest, Convert.FromBase64String(signature), out string error),
            $"подпись openssl не принята: {error}");
    }

    [Fact]
    public void ПодмененныйФайлНеПроходитДажеСВернойПодписьюСтарого()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        string original = FakeInstaller();
        string tampered = FakeInstaller(marker: 0x42);
        try
        {
            // Подпись выпущена для настоящей сборки, а на диске лежит подменённая
            string realDigest = UpdateVerification.Sha256File(original);
            byte[] signature = key.SignHash(Convert.FromHexString(realDigest));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.EnsureTrusted(tampered, realDigest, signature, publicKey));
            Assert.Contains("Контрольная сумма", ex.Message);
        }
        finally { File.Delete(original); File.Delete(tampered); }
    }

    // ---------------- Удерживаемый хендл (защита от подмены после проверки) ----------------

    [Fact]
    public void ПроверенныйФайлОткрываетсяИОстаётсяПодЗамком()
    {
        string file = FakeInstaller();
        try
        {
            using FileStream held = UpdateVerification.OpenVerified(
                file, UpdateVerification.Sha256File(file), null, "");

            Assert.True(held.CanRead);
            Assert.Equal(file, held.Name);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ПокаХендлУдерживаетсяФайлНельзяПодменить()
    {
        // Это и есть закрытие окна между проверкой и запуском: подменить проверенный
        // установщик можно было бы записью поверх, переименованием или удалением.
        string file = FakeInstaller();
        string decoy = FakeInstaller(marker: 0x42);
        try
        {
            string verified = UpdateVerification.Sha256File(file);
            using FileStream held = UpdateVerification.OpenVerified(file, verified, null, "");

            // Тип исключения у разных операций разный (IOException для записи и
            // удаления, UnauthorizedAccessException для переименования), поэтому
            // проверяем то, ради чего замок и ставится: содержимое не поменялось.
            AssertBlocked(() => File.WriteAllBytes(file, [1, 2, 3]));
            AssertBlocked(() => File.Delete(file));
            AssertBlocked(() => File.Move(decoy, file, overwrite: true));

            Assert.Equal(verified, UpdateVerification.Sha256Stream(held));
        }
        finally { File.Delete(decoy); File.Delete(file); }
    }

    [Fact]
    public void ПослеОсвобожденияХендлаФайлСноваМожноУдалить()
    {
        string file = FakeInstaller();
        using (UpdateVerification.OpenVerified(file, UpdateVerification.Sha256File(file), null, "")) { }

        File.Delete(file);            // не должно бросить
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ПриОтказеПроверкиХендлНеОстаётсяОткрытым()
    {
        // Иначе вызывающий не смог бы удалить забракованный файл
        string file = FakeInstaller();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.OpenVerified(file, new string('a', 64), null, ""));

            File.Delete(file);        // хендл закрыт — удаление проходит
            Assert.False(File.Exists(file));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void ПроверкаЧитаетСодержимоеЧерезТотЖеХендл()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        string original = FakeInstaller();
        string tampered = FakeInstaller(marker: 0x42);
        try
        {
            string realDigest = UpdateVerification.Sha256File(original);
            byte[] signature = key.SignHash(Convert.FromHexString(realDigest));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdateVerification.OpenVerified(tampered, realDigest, signature, publicKey));
            Assert.Contains("Контрольная сумма", ex.Message);
        }
        finally { File.Delete(original); File.Delete(tampered); }
    }

    [Fact]
    public void ЗамокНаЧтениеНеМешаетЗапуститьФайл()
    {
        // Страховка допущения Windows, на котором держится вся схема: если бы
        // FileShare.Read блокировал CreateProcess, удерживаемый хендл сделал бы
        // обновление невозможным. Проверяем на настоящем exe, а не на копии:
        // образу нужен доступ на чтение и выполнение, и share-режим его допускает.
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Assert.True(File.Exists(cmd), "в системе нет cmd.exe — тест неприменим");

        using var held = new FileStream(cmd, FileMode.Open, FileAccess.Read, FileShare.Read);

        var psi = new System.Diagnostics.ProcessStartInfo(cmd) { UseShellExecute = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("exit");

        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(15_000), "процесс не завершился");
        Assert.Equal(0, process.ExitCode);
    }

    // ---------------- Вспомогательное ----------------

    /// <summary>Операция над файлом под замком обязана не пройти.</summary>
    private static void AssertBlocked(Action attempt)
    {
        var ex = Record.Exception(attempt);
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException,
            $"Ожидалась ошибка доступа, получено {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Файл, похожий на установщик: сигнатура PE и размер больше мегабайта.</summary>
    private static string FakeInstaller(byte marker = 0x00)
    {
        string path = Path.Combine(Path.GetTempPath(), "AuraUpdateTests_" + Guid.NewGuid().ToString("N") + ".exe");
        var bytes = new byte[2 * 1024 * 1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[2] = marker;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteFile(string content, bool padToMegabyte)
    {
        string path = Path.Combine(Path.GetTempPath(), "AuraUpdateTests_" + Guid.NewGuid().ToString("N") + ".bin");
        var text = System.Text.Encoding.ASCII.GetBytes(content);
        var bytes = new byte[padToMegabyte ? 2 * 1024 * 1024 : text.Length];
        text.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
