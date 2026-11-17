using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Sources.Extraction;

namespace ZdrowieInfo.Server.Sources.Fetching;

public sealed record FetchResult(
    bool Success,
    int? Status,
    bool NotModified = false,
    string? Html = null,
    string? ETag = null,
    string? LastModified = null,
    CheckErrorType? Error = null,
    string? Message = null)
{
    public static FetchResult Fail(CheckErrorType error, string message, int? status = null) => new(false, status, Error: error, Message: message);
}

/// <summary>
/// Pobieranie stron źródeł: własny User-Agent, warunkowe GET, odstępy per domena, robots.txt, limit rozmiaru,
/// rozpoznanie blokady. Ponowienia przy 5xx/timeoutach robi handler odporności (Polly).
/// </summary>
public sealed class SourceFetcher(
    IHttpClientFactory httpFactory,
    HostThrottle throttle,
    TimeProvider time,
    IOptions<SourcesOptions> options,
    ILogger<SourceFetcher> logger)
{
    public const string HttpClientName = "ZdrowieInfo.Sources";

    private static readonly string[] BlockSignatures = ["Incapsula incident ID", "Request unsuccessful. Incapsula", "_Incapsula_Resource?SWJIYLWA"];

    private readonly ConcurrentDictionary<string, (RobotsTxt Robots, DateTimeOffset Expires)> _robots = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FetchResult> FetchAsync(string url, string? etag = null, string? lastModified = null, CancellationToken ct = default)
    {
        if (!await IsAllowedByRobotsAsync(url, ct))
            return FetchResult.Fail(CheckErrorType.RobotsDisallowed, "robots.txt strony zabrania pobierania tego adresu.");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (lastModified is not null && DateTimeOffset.TryParse(lastModified, out var since)) request.Headers.IfModifiedSince = since;

        using var lease = await throttle.AcquireAsync(url, ct);
        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new FetchResult(true, status, NotModified: true);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return FetchResult.Fail(CheckErrorType.Blocked, $"Strona odmówiła dostępu (HTTP {status}).", status);
            if (!response.IsSuccessStatusCode)
                return FetchResult.Fail(CheckErrorType.Http, $"HTTP {status} {response.ReasonPhrase}.", status);

            var html = await ReadLimitedAsync(response, ct);
            if (html is null)
                return FetchResult.Fail(CheckErrorType.TooLarge, $"Odpowiedź większa niż {options.Value.MaxResponseBytes / 1024 / 1024} MB.", status);
            if (BlockSignatures.Any(s => html.Contains(s, StringComparison.Ordinal)) && html.Length < 20_000)
                return FetchResult.Fail(CheckErrorType.Blocked, "Strona blokady (Imperva Incapsula) zamiast treści.", status);

            return new FetchResult(true, status, Html: html,
                ETag: response.Headers.ETag?.ToString(),
                LastModified: response.Content.Headers.LastModified?.ToString("R"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FetchResult.Fail(CheckErrorType.Timeout, "Przekroczony czas oczekiwania na odpowiedź.");
        }
        catch (Polly.Timeout.TimeoutRejectedException)
        {
            return FetchResult.Fail(CheckErrorType.Timeout, "Przekroczony czas oczekiwania na odpowiedź.");
        }
        catch (HttpRequestException e)
        {
            return FetchResult.Fail(CheckErrorType.Network, $"Błąd połączenia: {e.Message}");
        }
    }

    public static string BuildUserAgent(SourcesOptions o) => string.IsNullOrWhiteSpace(o.ContactEmail)
        ? $"ZdrowieInfo/{Common.AppVersion.Current}"
        : $"ZdrowieInfo/{Common.AppVersion.Current} (+mailto:{o.ContactEmail})";

    /// <summary>robots.txt pobierany raz na dobę na domenę. Błąd pobrania = brak ograniczeń (i nie jest incydentem).</summary>
    private async Task<bool> IsAllowedByRobotsAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        var key = $"{uri.Scheme}://{uri.Authority}";
        if (!_robots.TryGetValue(key, out var cached) || cached.Expires < time.GetUtcNow())
        {
            var robots = RobotsTxt.AllowAll;
            try
            {
                using var lease = await throttle.AcquireAsync(url, ct);
                using var response = await httpFactory.CreateClient(HttpClientName).GetAsync($"{key}/robots.txt", ct);
                if (response.IsSuccessStatusCode)
                    robots = RobotsTxt.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or Polly.Timeout.TimeoutRejectedException && !ct.IsCancellationRequested)
            {
                logger.LogInformation("Nie udało się pobrać {Url}/robots.txt ({Error}) – brak ograniczeń.", key, e.Message);
            }
            cached = (robots, time.GetUtcNow().AddDays(1));
            _robots[key] = cached;
        }
        return cached.Robots.IsAllowed(uri.PathAndQuery);
    }

    private async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var max = options.Value.MaxResponseBytes;
        if (response.Content.Headers.ContentLength > max) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max) return null;
        }

        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static string HostOf(string url) => UrlNormalizer.HostKey(new Uri(url).Host);
}
