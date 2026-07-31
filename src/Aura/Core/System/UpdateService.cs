using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aura.Core.Logging;

namespace Aura.Core.SystemIntegration;

public sealed record UpdateInfo(string Version, string DownloadUrl, string ReleaseUrl);

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
            if (remote is null || remote <= local) return null;

            string? asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ||
                a.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true ||
                a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl;

            return new UpdateInfo(remote.ToString(), asset ?? release.HtmlUrl ?? "", release.HtmlUrl ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("Update", $"Проверка обновлений не удалась: {ex.Message}");
            return null;
        }
    }

    /// <summary>Версия текущей сборки — её же показываем в UI.</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>
    /// Скачивает установщик новой версии во временную папку.
    /// progress — доля 0..1 (или -1, если сервер не сообщил размер).
    /// </summary>
    public async Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AuraUpdate");
        Directory.CreateDirectory(dir);
        // Имя файла прежнее — под ним установщик лежит в релизе, и по нему
        // обновляются установленные версии 1.0.x
        string file = Path.Combine(dir, "InstantReplaySetup.exe");
        // Файл мог остаться от прошлой попытки
        try { if (File.Exists(file)) File.Delete(file); } catch { }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using (var target = File.Create(file))
        {
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

        var info = new FileInfo(file);
        if (info.Length < 1024 * 1024) // установщик весит ~156 МБ; крохотный файл = страница ошибки
            throw new InvalidOperationException(
                $"Скачанный файл подозрительно мал ({info.Length} байт) — ссылка на релиз битая.");

        Log.Info("Update", $"Установщик скачан: {file} ({info.Length / (1024 * 1024)} МБ)");
        return file;
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
