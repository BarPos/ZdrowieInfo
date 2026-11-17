using System.Reflection;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Pwa;

internal static class PwaEndpoints
{
    private static readonly Lazy<string> ServiceWorker = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZdrowieInfo.Server.Pwa.service-worker.js")
            ?? throw new InvalidOperationException("Brak zasobu service-worker.js.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__APP_VERSION__", AppVersion.Current, StringComparison.Ordinal);
    });

    /// <summary>
    /// /service-worker.js z katalogu głównego (zakres "/"), z wersją cache równą AppVersion.Current.
    /// Bez cache HTTP, żeby przeglądarka szybko zauważyła nową wersję.
    /// </summary>
    public static IEndpointConventionBuilder MapServiceWorker(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/service-worker.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Text(ServiceWorker.Value, "text/javascript; charset=utf-8");
        }).AllowAnonymous();
}
