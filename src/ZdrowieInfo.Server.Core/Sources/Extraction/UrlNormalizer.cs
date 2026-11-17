namespace ZdrowieInfo.Server.Sources.Extraction;

public static class UrlNormalizer
{
    private static readonly string[] TrackingPrefixes = ["utm_", "fbclid", "gclid"];

    /// <summary>Adres bezwzględny względem strony; null dla adresów nie-http(s).</summary>
    public static string? Resolve(string? baseUri, string href)
    {
        href = href.Trim();
        if (!Uri.TryCreate(baseUri ?? "", UriKind.Absolute, out var b)) b = null;
        Uri? uri = b is null ? (Uri.TryCreate(href, UriKind.Absolute, out var a) ? a : null)
                             : (Uri.TryCreate(b, href, out var r) ? r : null);
        if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;

        var builder = new UriBuilder(uri) { Fragment = "" };
        if (!string.IsNullOrEmpty(builder.Query))
        {
            var kept = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !TrackingPrefixes.Any(t => p.StartsWith(t, StringComparison.OrdinalIgnoreCase)));
            builder.Query = string.Join('&', kept);
        }
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Klucz wpisu: ten sam artykuł pod http/https i z „www.” albo bez daje ten sam klucz
    /// (strony NFZ linkują raz do nfz.gov.pl, raz do www.nfz.gov.pl).
    /// </summary>
    public static string Key(string absoluteUrl)
    {
        var uri = new Uri(absoluteUrl);
        var host = HostKey(uri.Host);
        return $"{host}{uri.PathAndQuery}".TrimEnd('/');
    }

    /// <summary>Host bez „www.” – do kluczy i do odstępów między żądaniami.</summary>
    public static string HostKey(string host)
    {
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
