using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Export;

/// <summary>Pobieranie dokumentu raportu (dla zalogowanego użytkownika). Brakujący plik jest generowany na żądanie.</summary>
public static class ReportFilesEndpoints
{
    public static RouteGroupBuilder MapReportFiles(this IEndpointRouteBuilder endpoints)
    {
        var files = endpoints.MapGroup("/eksport").RequireAuthorization();

        files.MapGet("/raport-{date}.pdf", (string date, IDbContextFactory<ZdrowieInfoDbContext> db, ReportExporter exporter, HttpContext http, CancellationToken ct) =>
            ServeAsync(date, pdf: true, db, exporter, http, ct));
        files.MapGet("/raport-{date}.tex", (string date, IDbContextFactory<ZdrowieInfoDbContext> db, ReportExporter exporter, HttpContext http, CancellationToken ct) =>
            ServeAsync(date, pdf: false, db, exporter, http, ct));
        return files;
    }

    private static async Task<IResult> ServeAsync(string date, bool pdf, IDbContextFactory<ZdrowieInfoDbContext> dbFactory, ReportExporter exporter, HttpContext http, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day)) return Results.NotFound();

        string? path;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var report = await db.Reports.AsNoTracking().Where(r => r.Date == day).Select(r => new { r.PdfPath, r.TexPath }).SingleOrDefaultAsync(ct);
            if (report is null) return Results.NotFound();
            path = pdf ? report.PdfPath : report.TexPath;
        }

        if (path is null || !File.Exists(path))
        {
            var result = await exporter.ExportAsync(day, ct);
            path = pdf ? result.PdfPath : result.TexPath;
            if (path is null)
                return Results.Text($"Nie udało się wygenerować PDF raportu: {result.Error}", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var name = $"ZdrowieInfo-raport-{day:yyyy-MM-dd}.{(pdf ? "pdf" : "tex")}";
        // PDF otwierany w przeglądarce (inline), .tex jako plik do pobrania.
        http.Response.Headers.ContentDisposition = $"{(pdf ? "inline" : "attachment")}; filename=\"{name}\"";
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.File(path, pdf ? "application/pdf" : "application/x-tex; charset=utf-8");
    }
}
