using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Updates;

/// <summary>
/// Wydania GitHub. Kanał stable: <c>GET /repos/{właściciel}/{repo}/releases/latest</c>; prerelease: najnowsze nie-szkicowe
/// wydanie z <c>/releases</c>. Puste <c>ZdrowieInfo:Updates:GitHubRepository</c> – nie powstaje nawet klient HTTP.
/// </summary>
public sealed class GitHubReleaseSource(IHttpClientFactory httpFactory, IOptions<UpdatesOptions> options,
    ILogger<GitHubReleaseSource> logger) : IReleaseSource
{
    public const string HttpClientName = "ZdrowieInfo.GitHub";
    public const string ApiBase = "https://api.github.com";

    /// <summary>Ogólna nazwa produktu – bez nazwy hosta, użytkownika ani adresu.</summary>
    public static readonly string UserAgent = $"ZdrowieInfo-Server/{AppVersion.Current}";

    private readonly UpdatesOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured && TryGetRepository(out _, out _);

    public async Task<Release?> GetLatestAsync(CancellationToken ct = default)
    {
        if (!TryGetRepository(out var owner, out var repo)) return null;
        if (!string.Equals(_options.Channel?.Trim(), "prerelease", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = await GetJsonAsync($"/repos/{owner}/{repo}/releases/latest", ct);
            return doc is null ? null : Parse(doc.RootElement);
        }

        using var list = await GetJsonAsync($"/repos/{owner}/{repo}/releases?per_page=30", ct);
        if (list is null || list.RootElement.ValueKind != JsonValueKind.Array) return null;
        Release? best = null;
        foreach (var item in list.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var release = Parse(item);
            if (release is null) continue;
            if (best is null || (release.Info.PublishedAt ?? DateTimeOffset.MinValue) > (best.Info.PublishedAt ?? DateTimeOffset.MinValue))
                best = release;
        }
        return best;
    }

    public async Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Aktualizacje nie są skonfigurowane.");
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        // Adres pliku z API wymaga Accept: application/octet-stream (prywatne repozytorium z tokenem).
        // HttpClient nie przekazuje nagłówka Authorization po przekierowaniu na serwer plików.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var response = await CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new ReleaseSourceException($"Pobieranie {asset.Name} nie powiodło się: HTTP {(int)status}.");
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private bool TryGetRepository(out string owner, out string repo)
    {
        owner = repo = "";
        var value = _options.GitHubRepository?.Trim() ?? "";
        if (value.Length == 0) return false;
        var parts = value.Split('/');
        if (parts.Length != 2 || !IsName(parts[0]) || !IsName(parts[1]))
        {
            logger.LogWarning("ZdrowieInfo:Updates:GitHubRepository „{Value}” nie ma postaci właściciel/nazwa – aktualizacje wyłączone.", value);
            return false;
        }
        owner = parts[0];
        repo = parts[1];
        return true;

        static bool IsName(string s) => s.Length is > 0 and <= 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrWhiteSpace(_options.GitHubToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHubToken.Trim());
        return client;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try
        {
            response = await CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"GitHub jest niedostępny: {ex.Message}", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new ReleaseSourceException($"GitHub odpowiedział HTTP {(int)response.StatusCode} na {path}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                throw new ReleaseSourceException("GitHub zwrócił niepoprawny JSON.", ex);
            }
        }
    }

    /// <summary>Jedno wydanie z API GitHub (publiczne dla testów).</summary>
    public Release? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var tag = GetString(e, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var version = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        DateTimeOffset? published = null;
        if (GetString(e, "published_at") is { } p &&
            DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            published = dt.ToUniversalTime();
        var info = new ReleaseInfo(version, tag, GetString(e, "name"), GetString(e, "body"), published,
            e.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True, GetString(e, "html_url"));

        var assets = new List<ReleaseAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var useApiUrl = !string.IsNullOrWhiteSpace(_options.GitHubToken);
            foreach (var a in arr.EnumerateArray())
            {
                var name = GetString(a, "name");
                var url = useApiUrl ? GetString(a, "url") ?? GetString(a, "browser_download_url") : GetString(a, "browser_download_url") ?? GetString(a, "url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : -1;
                assets.Add(new ReleaseAsset(name, size, url));
            }
        }
        return new Release(info, assets);
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
