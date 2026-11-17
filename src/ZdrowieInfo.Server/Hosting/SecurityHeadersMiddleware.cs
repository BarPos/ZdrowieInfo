namespace ZdrowieInfo.Server.Hosting;

internal static class SecurityHeadersMiddleware
{
    // style-src 'unsafe-inline': MudBlazor ustawia style inline. Skrypty wyłącznie z własnej domeny.
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        // Pliki raportu (PDF, .tex) otwiera wbudowana przeglądarka PDF – CSP stron (object-src 'none', sandbox)
        // i X-Frame-Options ją blokują. To nie są strony HTML, więc tych nagłówków nie potrzebują.
        if (!context.Request.Path.StartsWithSegments("/eksport") && !context.Request.Path.StartsWithSegments(Export.ReportFilesEndpoints.SharedPath))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
        }
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        await next();
    });
}
