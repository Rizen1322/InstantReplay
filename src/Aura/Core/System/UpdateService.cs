using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aura.Core.Logging;

namespace Aura.Core.SystemIntegration;

/// <summary>
/// Найденное обновление. <see cref="DigestUrl"/> и <see cref="SignatureUrl"/> —
/// сопутствующие файлы релиза (<c>*.sha256</c> и <c>*.sig</c>), по которым установщик
/// проверяется перед запуском; null — такого файла в релизе нет.
/// </summary>
public sealed record UpdateInfo(
    string Version, string DownloadUrl, string ReleaseUrl,
    string? DigestUrl = null, string? SignatureUrl = null);

/// <summary>
/// Автообновление: проверка последнего релиза на GitHub (repo из настроек),
/// сравнение с версией сборки. Установка = открыть страницу релиза / скачать asset.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "InstantReplay-Updater" } }
    };

    /// <summary>
    /// Официальный репозиторий проекта — единственный источник обновлений.
    /// Намеренно константа, а не настройка: обновление подменяет исполняемые файлы,
    /// и путь к ним не должен зависеть от того, что кто-то впишет в настройки.
    /// </summary>
    public const string Repo = "Rizen1322/InstantReplay";

    /// <summary>
    /// Найденное обновление — чтобы страница настроек показала его сразу, без
    /// повторной проверки. Раньше результат проверки терялся: карточка «Есть
    /// обновление» вела на страницу, где было написано «Обновлений не искали»,
    /// а кнопка установки оставалась скрытой — со стороны выглядело так, будто
    /// нажатие ничего не делает.
    /// </summary>
    public UpdateInfo? Available { get; private set; }

    public async Task<UpdateInfo?> CheckAsync(string repo = Repo)
    {
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/')) return null;
        try
        {
            var release = await Http.GetFromJsonAsync<GithubRelease>(
                $"https://api.github.com/repos/{repo}/releases/latest");
            if (release?.TagName is null) return null;

            var remote = ParseVersion(release.TagName);
            var local = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
            if (remote is null || remote <= local) { Available = null; return null; }

            string? asset = AssetUrl(release, n =>
                n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            string? digest = AssetUrl(release, n => n.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
            string? signature = AssetUrl(release, n => n.EndsWith(".sig", StringComparison.OrdinalIgnoreCase));

            Available = new UpdateInfo(remote.ToString(), asset ?? release.HtmlUrl ?? "", release.HtmlUrl ?? "",
                                       digest, signature);
            return Available;
        }
        catch (Exception ex)
        {
            Log.Warn("Update", $"Проверка обновлений не удалась: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Ссылка на файл релиза, подходящий под <paramref name="match"/>.
    ///
    /// Ссылка приходит из ответа GitHub, но это просто строка в JSON — качать по ней
    /// что угодно нельзя. Чужой хост отбрасываем ещё на этапе проверки, чтобы до
    /// закачки дело вообще не дошло.
    /// </summary>
    private static string? AssetUrl(GithubRelease release, Func<string, bool> match)
    {
        string? url = release.Assets?.FirstOrDefault(a => a.Name is not null && match(a.Name))?.BrowserDownloadUrl;
        if (url is null) return null;
        if (UpdateVerification.IsTrustedHost(url)) return url;

        Log.Warn("Update", $"Файл релиза лежит на чужом хосте — игнорирую: {url}");
        return null;
    }

    /// <summary>Версия текущей сборки — её же показываем в UI.</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>
    /// Скачивает установщик новой версии во временную папку и ПРОВЕРЯЕТ его.
    /// progress — доля 0..1 (или -1, если сервер не сообщил размер).
    ///
    /// Возвращает путь только к файлу, который прошёл проверку (см.
    /// <see cref="UpdateVerification.EnsureTrusted"/>). Непрошедший удаляется здесь же:
    /// запускать его будут с правами администратора, и оставлять такой файл на диске
    /// незачем.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!UpdateVerification.IsTrustedHost(update.DownloadUrl))
            throw new InvalidOperationException("Ссылка на обновление ведёт не на GitHub — загрузка отменена.");

        string dir = Path.Combine(Path.GetTempPath(), "AuraUpdate");
        Directory.CreateDirectory(dir);
        // Имя файла прежнее — под ним установщик лежит в релизе, и по нему
        // обновляются установленные версии 1.0.x
        string file = Path.Combine(dir, "InstantReplaySetup.exe");
        // Файл мог остаться от прошлой попытки
        try { if (File.Exists(file)) File.Delete(file); } catch { }

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(file);
            var buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                progress?.Report(total > 0 ? read / (double)total : -1);
            }
        }

        try
        {
            string? digest = await TryGetTextAsync(update.DigestUrl, ct);
            byte[]? signature = DecodeSignature(await TryGetBytesAsync(update.SignatureUrl, ct));
            UpdateVerification.EnsureTrusted(file, digest, signature);
        }
        catch
        {
            try { File.Delete(file); } catch { }
            throw;
        }

        Log.Info("Update", $"Установщик скачан и проверен: {file} ({new FileInfo(file).Length / (1024 * 1024)} МБ)");
        return file;
    }

    /// <summary>Сопутствующий файл релиза; null — его нет или он не читается.</summary>
    private static async Task<string?> TryGetTextAsync(string? url, CancellationToken ct)
    {
        if (!UpdateVerification.IsTrustedHost(url)) return null;
        try { return await Http.GetStringAsync(url!, ct); }
        catch (Exception ex) { Log.Warn("Update", $"Не удалось прочитать {url}: {ex.Message}"); return null; }
    }

    private static async Task<byte[]?> TryGetBytesAsync(string? url, CancellationToken ct)
    {
        if (!UpdateVerification.IsTrustedHost(url)) return null;
        try { return await Http.GetByteArrayAsync(url!, ct); }
        catch (Exception ex) { Log.Warn("Update", $"Не удалось прочитать {url}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Подпись кладём в релиз текстом base64 — так её видно глазами и не портят
    /// инструменты, считающие файл текстовым. Сырые байты тоже принимаем.
    /// </summary>
    private static byte[]? DecodeSignature(byte[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        try { return Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(raw).Trim()); }
        catch (FormatException) { return raw; }
    }

    /// <summary>
    /// Запускает скачанный установщик в тихом режиме обновления и возвращает true,
    /// если он стартовал. Установщик сам закроет приложение, обновит файлы в той же
    /// папке и запустит новую версию — поэтому вызывающий должен просто выйти.
    /// </summary>
    public static bool LaunchInstaller(string installerPath, string installRoot)
    {
        try
        {
            var psi = new ProcessStartInfo(installerPath) { UseShellExecute = true };
            psi.ArgumentList.Add("/update");
            psi.ArgumentList.Add(installRoot);
            Process.Start(psi);
            Log.Info("Update", $"Запущен установщик обновления для {installRoot}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Update", $"Не удалось запустить установщик: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Корень установки: ...\Aura\app\Aura.exe → ...\Aura.
    /// null — приложение запущено не из установленной папки (сборка из dist,
    /// распакованный архив): обновлять на месте нечего.
    ///
    /// Раньше в таком случае возвращалась сама папка exe, и установщик создавал
    /// ВНУТРИ неё ещё одну подпапку app — появлялась вложенная копия, которая
    /// никогда не обновлялась, а запись в реестре начинала указывать на неё.
    /// </summary>
    public static string? InstallRoot
    {
        get
        {
            // Установленная раскладка: <root>\app\Aura.exe
            string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(exeDir), "app", StringComparison.OrdinalIgnoreCase)
                && Directory.GetParent(exeDir) is { } parent)
                return parent.FullName;

            // Запуск не из установленной папки — берём путь установки из реестра,
            // но только если он выглядит настоящим (есть app\Aura.exe).
            // Смотрим и прежний ключ: после переезда с Instant Replay установка
            // может остаться в старой папке, а запись — под старым именем.
            foreach (string name in new[] { "Aura", "InstantReplay" })
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
                    if (key?.GetValue("InstallLocation") is string root && root.Length > 0
                        && File.Exists(Path.Combine(root, "app", "Aura.exe")))
                        return root;
                }
                catch { }

            return null;
        }
    }

    private static Version? ParseVersion(string tag)
    {
        tag = tag.TrimStart('v', 'V');
        return Version.TryParse(tag, out var v) ? v : null;
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GithubAsset>? Assets { get; set; }
    }
    private sealed class GithubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
