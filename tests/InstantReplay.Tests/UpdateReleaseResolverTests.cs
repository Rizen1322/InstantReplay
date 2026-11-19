using System.Net;
using System.Net.Http;
using System.Text;
using Aura.Core.SystemIntegration;
using Xunit;

namespace InstantReplay.Tests;

public class UpdateReleaseResolverTests
{
    private const string Repo = "Rizen1322/InstantReplay";

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? IfNoneMatch)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var vals)
                ? string.Join(",", vals)
                : null;
            Calls.Add((request.Method, request.RequestUri!, ifNoneMatch));
            return Task.FromResult(handler(request));
        }
    }

    private static HttpResponseMessage RedirectResponse(Uri location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var resp = new HttpResponseMessage(status);
        resp.Headers.Location = location;
        return resp;
    }

    private static HttpResponseMessage JsonRelease(
        string tag = "v2.0.89",
        string assetHost = "github.com",
        string? etag = "\"etag-2089\"")
    {
        string json = $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/{{Repo}}/releases/tag/{{tag}}",
              "assets": [
                { "name": "InstantReplaySetup.exe", "browser_download_url": "https://{{assetHost}}/{{Repo}}/releases/download/{{tag}}/InstantReplaySetup.exe" },
                { "name": "InstantReplaySetup.exe.sha256", "browser_download_url": "https://{{assetHost}}/{{Repo}}/releases/download/{{tag}}/InstantReplaySetup.exe.sha256" },
                { "name": "InstantReplaySetup.exe.sig", "browser_download_url": "https://{{assetHost}}/{{Repo}}/releases/download/{{tag}}/InstantReplaySetup.exe.sig" },
                { "name": "InstantReplaySetup.exe.sig2", "browser_download_url": "https://{{assetHost}}/{{Repo}}/releases/download/{{tag}}/InstantReplaySetup.exe.sig2" }
              ]
            }
            """;
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (etag is not null)
            resp.Headers.TryAddWithoutValidation("ETag", etag);
        return resp;
    }

    [Theory]
    [InlineData($"https://github.com/{Repo}/releases/tag/v2.0.89", true, "v2.0.89")]
    [InlineData($"https://github.com/{Repo}/releases/tag/v2.0.89/", true, "v2.0.89")]
    [InlineData($"https://github.com/{Repo}/releases/tag/v2.0.89%2Bfix", true, "v2.0.89+fix")]
    [InlineData($"https://github.com/other/repo/releases/tag/v2.0.89", false, "")]
    [InlineData($"https://evil.example/{Repo}/releases/tag/v2.0.89", false, "")]
    [InlineData($"https://github.com/{Repo}/releases", false, "")]
    [InlineData($"https://github.com/{Repo}/releases/tag/v2.0/extra", false, "")]
    public void TryExtractTagFromReleaseUrl_ValidatesAbsoluteUrls(string url, bool expectedOk, string expectedTag)
    {
        bool ok = UpdateReleaseResolver.TryExtractTagFromReleaseUrl(new Uri(url), Repo, out string tag);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedTag, tag);
    }

    [Fact]
    public void TryExtractTagFromReleaseUrl_HandlesRelativeUriSafely()
    {
        var relative = new Uri($"/{Repo}/releases/tag/v2.0.89", UriKind.Relative);
        Assert.False(UpdateReleaseResolver.TryExtractTagFromReleaseUrl(relative, Repo, out string tag));
        Assert.Equal("", tag);

        var resolved = new Uri(new Uri($"https://github.com/{Repo}/releases/latest"), relative);
        Assert.True(UpdateReleaseResolver.TryExtractTagFromReleaseUrl(resolved, Repo, out tag));
        Assert.Equal("v2.0.89", tag);
    }

    [Fact]
    public async Task ResolveLatestAsync_UsesRedirectFirst_WithoutCallingGithubApi()
    {
        using var redirectHandler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
                return RedirectResponse(new Uri($"https://github.com/{Repo}/releases/tag/v2.0.89"));
            if (req.RequestUri.AbsolutePath.EndsWith("/InstantReplaySetup.exe", StringComparison.Ordinal))
                return RedirectResponse(new Uri("https://release-assets.githubusercontent.com/blob/InstantReplaySetup.exe"));
            throw new InvalidOperationException($"Unexpected URI: {req.RequestUri}");
        });
        using var apiHandler = new StubHttpHandler(_ => throw new InvalidOperationException("API must not be called"));
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        ResolvedRelease? release = await resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88));

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 0, 89), release.Version);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v2.0.89/InstantReplaySetup.exe", release.DownloadUrl);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v2.0.89/InstantReplaySetup.exe.sha256", release.DigestUrl);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v2.0.89/InstantReplaySetup.exe.sig", release.SignatureUrl);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v2.0.89/InstantReplaySetup.exe.sig2", release.VersionSignatureUrl);
        Assert.Equal($"https://github.com/{Repo}/releases/tag/v2.0.89", release.ReleaseUrl);
        Assert.Equal(2, redirectHandler.Calls.Count);
        Assert.All(redirectHandler.Calls, c => Assert.Equal(HttpMethod.Head, c.Method));
        Assert.Empty(apiHandler.Calls);
    }

    [Fact]
    public async Task ResolveLatestAsync_UpToDate_MakesSingleHeadRequestOnly()
    {
        using var redirectHandler = new StubHttpHandler(_ =>
            RedirectResponse(new Uri($"/{Repo}/releases/tag/v2.0.89", UriKind.Relative)));
        using var apiHandler = new StubHttpHandler(_ => throw new InvalidOperationException("API must not be called"));
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        ResolvedRelease? release = await resolver.ResolveLatestAsync(Repo, new Version(2, 0, 89));

        Assert.Null(release);
        Assert.Single(redirectHandler.Calls);
        Assert.Equal(HttpMethod.Head, redirectHandler.Calls[0].Method);
        Assert.Empty(apiHandler.Calls);
    }

    [Fact]
    public async Task ResolveLatestAsync_RetriesWithGetWhenHeadReturnsMethodNotAllowed()
    {
        using var redirectHandler = new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
                return RedirectResponse(new Uri($"https://github.com/{Repo}/releases/tag/v2.0.89"));

            return new HttpResponseMessage(HttpStatusCode.PartialContent);
        });
        using var apiHandler = new StubHttpHandler(_ => throw new InvalidOperationException("API must not be called"));
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        ResolvedRelease? release = await resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88));

        Assert.NotNull(release);
        Assert.Equal(4, redirectHandler.Calls.Count);
        Assert.Equal(HttpMethod.Head, redirectHandler.Calls[0].Method);
        Assert.Equal(HttpMethod.Get, redirectHandler.Calls[1].Method);
        Assert.Equal(HttpMethod.Head, redirectHandler.Calls[2].Method);
        Assert.Equal(HttpMethod.Get, redirectHandler.Calls[3].Method);
        Assert.Empty(apiHandler.Calls);
    }

    [Fact]
    public async Task ResolveLatestAsync_RejectsUntrustedRedirectHost_AndFallsBackToApi()
    {
        using var redirectHandler = new StubHttpHandler(_ =>
            RedirectResponse(new Uri($"https://evil.example/{Repo}/releases/tag/v2.0.89")));
        using var apiHandler = new StubHttpHandler(_ => JsonRelease("v2.0.89"));
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        ResolvedRelease? release = await resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88));

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 0, 89), release.Version);
        Assert.Single(apiHandler.Calls);
    }

    [Fact]
    public async Task ResolveLatestAsync_MissingInstaller404_ThrowsInvalidDataException()
    {
        using var redirectHandler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
                return RedirectResponse(new Uri($"https://github.com/{Repo}/releases/tag/v2.0.89"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var apiHandler = new StubHttpHandler(_ => throw new InvalidOperationException("API fallback not expected on confirmed 404"));
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88)));

        Assert.Contains("InstantReplaySetup.exe", ex.Message);
    }

    [Fact]
    public async Task ResolveLatestAsync_ApiFallbackCachesEtagAndUsesNotModified()
    {
        using var redirectHandler = new StubHttpHandler(_ => throw new HttpRequestException("redirect blocked"));
        int apiCallCount = 0;
        using var apiHandler = new StubHttpHandler(req =>
        {
            apiCallCount++;
            if (apiCallCount == 1) return JsonRelease("v2.0.89", etag: "\"abc-89\"");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        ResolvedRelease? first = await resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88));
        ResolvedRelease? second = await resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(2, apiHandler.Calls.Count);
        Assert.Null(apiHandler.Calls[0].IfNoneMatch);
        Assert.Equal("\"abc-89\"", apiHandler.Calls[1].IfNoneMatch);
    }

    [Fact]
    public async Task ResolveLatestAsync_ApiRateLimit403_ThrowsFriendlyRussianMessage()
    {
        using var redirectHandler = new StubHttpHandler(_ => throw new HttpRequestException("offline"));
        using var apiHandler = new StubHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                ReasonPhrase = "rate limit exceeded"
            };
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return resp;
        });
        using var noRedirectClient = new HttpClient(redirectHandler);
        using var apiClient = new HttpClient(apiHandler);

        var resolver = new UpdateReleaseResolver(noRedirectClient, apiClient);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            resolver.ResolveLatestAsync(Repo, new Version(2, 0, 88)));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("ограничил запросы", ex.Message);
        Assert.DoesNotContain("403", ex.Message);
    }
}
