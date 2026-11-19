using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aura.Core.Logging;

namespace Aura.Core.SystemIntegration;

internal sealed record ResolvedRelease(
    string TagName,
    Version Version,
    string DownloadUrl,
    string ReleaseUrl,
    string? DigestUrl,
    string? SignatureUrl,
    string? VersionSignatureUrl);

/// <summary>
/// Поиск последнего релиза на GitHub.
///
/// Основной путь идёт через обычный веб-редирект <c>github.com/.../releases/latest</c>
/// (<c>HEAD</c>, без JSON API): на него не действует лимит 60 неавторизованных запросов
/// в час с одного IP, который часто выбивает <c>403 (rate limit exceeded)</c> на
/// общем или домашнем NAT. Резервный путь <c>api.github.com</c> с <c>ETag</c>
/// (<c>If-None-Match</c>) вызывается только если веб-редирект недоступен.
/// </summary>
internal sealed class UpdateReleaseResolver(HttpClient noRedirectHttp, HttpClient apiHttp)
{
    public const string InstallerName = "InstantReplaySetup.exe";

    private readonly object _cacheSync = new();
    private CachedApiEntry? _cachedApiRelease;

    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string trimmed = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    internal static bool TryExtractTagFromReleaseUrl(Uri uri, string repo, out string tag)
    {
        tag = "";
        if (!uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !UpdateVerification.IsTrustedHost(uri.AbsoluteUri))
            return false;

        string prefix = $"/{repo}/releases/tag/";
        string path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string rawSegment = path[prefix.Length..].TrimEnd('/');
        if (rawSegment.Length == 0 || rawSegment.Contains('/'))
            return false;

        string decoded = Uri.UnescapeDataString(rawSegment);
        if (decoded.Length == 0 || decoded.Contains('/'))
            return false;

        tag = decoded;
        return true;
    }

    internal async Task<ResolvedRelease?> ResolveLatestAsync(string repo, Version currentVersion, CancellationToken ct = default)
    {
        RedirectProbe probe = await TryResolveViaRedirectAsync(repo, currentVersion, ct).ConfigureAwait(false);
        if (probe.Succeeded) return probe.Release;

        return await ResolveViaApiAsync(repo, currentVersion, ct).ConfigureAwait(false);
    }

    private async Task<RedirectProbe> TryResolveViaRedirectAsync(string repo, Version currentVersion, CancellationToken ct)
    {
        try
        {
            Uri latestUri = new($"https://github.com/{repo}/releases/latest");
            using HttpResponseMessage response = await SendHeadOrGetAsync(latestUri, rangeOnGetFallback: false, ct)
                .ConfigureAwait(false);

            if (!IsRedirectStatus(response.StatusCode) || response.Headers.Location is not { } location)
            {
                Log.Warn("Update", $"GitHub releases/latest не вернул редирект (HTTP {(int)response.StatusCode}) — пробую API");
                return RedirectProbe.Fallback;
            }

            Uri targetUri = location.IsAbsoluteUri ? location : new Uri(latestUri, location);
            if (!TryExtractTagFromReleaseUrl(targetUri, repo, out string tag))
            {
                Log.Warn("Update", $"Недоверенный или неожиданный URL релиза «{targetUri}» — пробую API");
                return RedirectProbe.Fallback;
            }

            if (ParseVersion(tag) is not { } remote)
            {
                Log.Warn("Update", $"Не удалось разобрать версию из тега «{tag}» — пробую API");
                return RedirectProbe.Fallback;
            }

            if (remote <= currentVersion)
                return RedirectProbe.UpToDate;

            string encodedTag = Uri.EscapeDataString(tag);
            string baseDownload = $"https://github.com/{repo}/releases/download/{encodedTag}";
            string installerUrl = $"{baseDownload}/{InstallerName}";
            string digestUrl = $"{baseDownload}/{InstallerName}.sha256";
            string signatureUrl = $"{baseDownload}/{InstallerName}.sig";
            string versionSignatureUrl = $"{baseDownload}/{InstallerName}.sig2";

            Uri installerUri = new(installerUrl);
            using HttpResponseMessage assetResponse = await SendHeadOrGetAsync(installerUri, rangeOnGetFallback: true, ct)
                .ConfigureAwait(false);

            if (assetResponse.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException($"В релизе нет установщика {InstallerName}.");

            if (IsRedirectStatus(assetResponse.StatusCode))
            {
                if (assetResponse.Headers.Location is not { } assetLocation)
                {
                    Log.Warn("Update", "Редирект установщика без Location — пробую API");
                    return RedirectProbe.Fallback;
                }

                Uri resolvedAssetUri = assetLocation.IsAbsoluteUri
                    ? assetLocation
                    : new Uri(installerUri, assetLocation);
                if (!UpdateVerification.IsTrustedHost(resolvedAssetUri.AbsoluteUri))
                {
                    Log.Warn("Update", $"Файл релиза перенаправлен на чужой хост ({resolvedAssetUri.Host}) — пробую API");
                    return RedirectProbe.Fallback;
                }
            }
            else if (assetResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                Log.Warn("Update", $"Проверка установщика через редирект вернула HTTP {(int)assetResponse.StatusCode} — пробую API");
                return RedirectProbe.Fallback;
            }

            return RedirectProbe.Found(new ResolvedRelease(
                tag,
                remote,
                installerUrl,
                targetUri.AbsoluteUri,
                digestUrl,
                signatureUrl,
                versionSignatureUrl));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Update", $"Не удалось проверить релиз через redirect ({ex.Message}) — пробую GitHub API");
            return RedirectProbe.Fallback;
        }
    }

    private async Task<ResolvedRelease?> ResolveViaApiAsync(string repo, Version currentVersion, CancellationToken ct)
    {
        CachedApiEntry? cached;
        lock (_cacheSync)
        {
            cached = _cachedApiRelease is { } entry && string.Equals(entry.Repo, repo, StringComparison.OrdinalIgnoreCase)
                ? entry
                : null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        if (cached is { ETag.Length: > 0 })
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        using HttpResponseMessage response = await apiHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        GithubRelease? release;
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            release = cached.Release;
        }
        else
        {
            if (IsRateLimitResponse(response))
            {
                throw new HttpRequestException(
                    "GitHub временно ограничил запросы с этого IP — попробуй позже или скачай обновление вручную.",
                    inner: null,
                    response.StatusCode);
            }

            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync<GithubRelease>(ct).ConfigureAwait(false);

            if (release is not null && response.Headers.ETag?.Tag is { Length: > 0 } etag)
            {
                lock (_cacheSync)
                    _cachedApiRelease = new CachedApiEntry(repo, etag, release);
            }
        }

        if (release?.TagName is null)
            throw new InvalidDataException("GitHub не вернул версию релиза.");

        Version remote = ParseVersion(release.TagName)
            ?? throw new InvalidDataException("Не удалось разобрать версию релиза.");
        if (remote <= currentVersion) return null;

        string? asset = AssetUrl(release, n => n.Equals(InstallerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"В релизе нет установщика {InstallerName}.");
        string? digest = AssetUrl(release, n => n.Equals(InstallerName + ".sha256", StringComparison.OrdinalIgnoreCase));
        string? signature = AssetUrl(release, n => n.Equals(InstallerName + ".sig", StringComparison.OrdinalIgnoreCase));
        string? versionSignature = AssetUrl(release, n => n.Equals(InstallerName + ".sig2", StringComparison.OrdinalIgnoreCase));

        return new ResolvedRelease(
            release.TagName,
            remote,
            asset,
            release.HtmlUrl ?? $"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(release.TagName)}",
            digest,
            signature,
            versionSignature);
    }

    private async Task<HttpResponseMessage> SendHeadOrGetAsync(Uri uri, bool rangeOnGetFallback, CancellationToken ct)
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
        HttpResponseMessage headResponse = await noRedirectHttp
            .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (headResponse.StatusCode != HttpStatusCode.MethodNotAllowed)
            return headResponse;

        headResponse.Dispose();
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        if (rangeOnGetFallback)
            getRequest.Headers.Range = new RangeHeaderValue(0, 0);
        return await noRedirectHttp
            .SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.Any(value => string.Equals(value.Trim(), "0", StringComparison.Ordinal)))
            return true;

        return response.ReasonPhrase?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true;
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

    private readonly record struct RedirectProbe(bool Succeeded, ResolvedRelease? Release)
    {
        public static RedirectProbe Fallback => new(false, null);
        public static RedirectProbe UpToDate => new(true, null);
        public static RedirectProbe Found(ResolvedRelease release) => new(true, release);
    }

    private sealed record CachedApiEntry(string Repo, string ETag, GithubRelease Release);

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
