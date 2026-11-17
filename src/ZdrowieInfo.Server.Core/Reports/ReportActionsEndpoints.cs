using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ZdrowieInfo.Server.Accounts;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Akcje administratora ze strony raportu (zwykłe formularze POST z tokenem antiforgery, bez JavaScriptu):
/// publiczny link do PDF (utwórz, nowy losowy, wycofaj) i usunięcie raportu.
/// </summary>
public static class ReportActionsEndpoints
{
    public static RouteGroupBuilder MapReportActions(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/raporty/{slug}").RequireAuthorization(AppRoles.AdminPolicy);

        group.MapPost("/udostepnij", (string slug, HttpContext http, IAntiforgery af, DailyReportService reports, CancellationToken ct) =>
            RunAsync(slug, http, af, async key => { await reports.ShareAsync(key, renew: false, ct); return Back(key, "udostepniono"); }));
        group.MapPost("/nowy-link", (string slug, HttpContext http, IAntiforgery af, DailyReportService reports, CancellationToken ct) =>
            RunAsync(slug, http, af, async key => { await reports.ShareAsync(key, renew: true, ct); return Back(key, "nowy-link"); }));
        group.MapPost("/wycofaj", (string slug, HttpContext http, IAntiforgery af, DailyReportService reports, CancellationToken ct) =>
            RunAsync(slug, http, af, async key => { await reports.UnshareAsync(key, ct); return Back(key, "wycofano"); }));
        group.MapPost("/usun", (string slug, HttpContext http, IAntiforgery af, DailyReportService reports, CancellationToken ct) =>
            RunAsync(slug, http, af, async key => await reports.DeleteAsync(key, ct)
                ? Results.Redirect($"/?usunieto={Uri.EscapeDataString(key.Label)}")
                : Results.NotFound()));
        return group;
    }

    private static async Task<IResult> RunAsync(string slug, HttpContext http, IAntiforgery antiforgery, Func<ReportKey, Task<IResult>> action)
    {
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest("Formularz wygasł – odśwież stronę i spróbuj ponownie.");
        return ReportKey.TryParse(slug, out var key) ? await action(key) : Results.NotFound();
    }

    private static IResult Back(ReportKey key, string status) => Results.Redirect($"/raporty/{key.Slug}?link={status}#udostepnianie");
}
