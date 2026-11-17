using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Export;

/// <summary>
/// PDF raportu: dla administratora (/eksport/raport-{slug}.pdf) i pod publicznym linkiem (/udostepnione/{token}) dla każdego,
/// kto zna losowy token. Brakujący plik jest generowany na żądanie (kompilacje po kolei).
/// </summary>
public static class ReportFilesEndpoints
{
    public const string SharedPath = "/udostepnione";
    public const string PublicRateLimitPolicy = "public";

    /// <summary>Limit dla publicznych linków: 20 pobrań na minutę z jednego adresu IP.</summary>
    public static IServiceCollection AddPublicRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(o => o.AddPolicy(PublicRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

    public static IEndpointRouteBuilder MapReportFiles(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/eksport/raport-{slug}.pdf", async (string slug, IDbContextFactory<ZdrowieInfoDbContext> dbFactory, ReportExporter exporter,
                HttpContext http, CancellationToken ct) =>
            {
                if (!ReportKey.TryParse(slug, out var key)) return Results.NotFound();
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var report = await db.Reports.AsNoTracking().WhereKey(key).Select(r => new { r.PdfPath }).SingleOrDefaultAsync(ct);
                if (report is null) return Results.NotFound();
                http.Response.Headers.CacheControl = "private, no-cache";
                return await ServeAsync(key, report.PdfPath, exporter, http, ct);
            })
            .RequireAuthorization(AppRoles.AdminPolicy);

        endpoints.MapGet(SharedPath + "/{token}", async (string token, IDbContextFactory<ZdrowieInfoDbContext> dbFactory, ReportExporter exporter,
                HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                http.Response.Headers.CacheControl = "no-store";
                // Ten sam wynik dla nieznanego i wycofanego linku.
                if (token.Length is < 20 or > 64) return Results.NotFound();
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var report = await db.Reports.AsNoTracking().Where(r => r.ShareToken == token)
                    .Select(r => new { r.Kind, r.PeriodStart, r.Date, r.PdfPath }).SingleOrDefaultAsync(ct);
                if (report is null) return Results.NotFound();
                return await ServeAsync(new ReportKey(report.Kind, report.PeriodStart, report.Date), report.PdfPath, exporter, http, ct);
            })
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimitPolicy);
        return endpoints;
    }

    private static async Task<IResult> ServeAsync(ReportKey key, string? path, ReportExporter exporter, HttpContext http, CancellationToken ct)
    {
        if (path is null || !File.Exists(path))
        {
            var result = await exporter.ExportAsync(key, ct);
            path = result.PdfPath;
            if (path is null)
                return Results.Text("Nie udało się wygenerować PDF raportu – spróbuj później.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        http.Response.Headers.ContentDisposition = $"inline; filename=\"ZdrowieInfo-raport-{key.Slug}.pdf\"";
        return Results.File(path, "application/pdf");
    }
}
