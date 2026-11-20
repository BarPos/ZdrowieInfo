using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// API dla zewnętrznych producentów raportów (inny program albo agent AI), klucz z zakresem <c>reports:write</c>:
/// <c>GET /api/items/pending</c> – wpisy czekające na raport (z identyfikatorami i treścią do cytowania);
/// <c>POST /api/reports?date=rrrr-mm-dd&amp;replace=true</c> – raport w tym samym formacie co od Claude.
/// </summary>
public static class ReportsApi
{
    public static RouteGroupBuilder MapReportsApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api")
            .RequireAuthorization(ApiScopes.ReportsWrite)
            .RequireRateLimiting(ApiKeysServiceCollectionExtensions.RateLimitPolicy);

        api.MapGet("/items/pending", async (IDbContextFactory<ZdrowieInfoDbContext> dbFactory, CancellationToken ct) =>
        {
            var inputs = await LoadPendingAsync(dbFactory, ct);
            return Results.Json(inputs.Select(i => new
            {
                item_id = i.Id,
                source_item_ids = i.SourceItemIds,
                sources = i.SourceNames,
                kind = i.Kind,
                i.Title,
                i.Url,
                published_on = i.PublishedOn,
                i.Summary,
                i.Content,
            }), ReportJson.Options);
        });

        api.MapPost("/reports", async (HttpContext http, DailyReportService reports, WarsawClock clock, string? date, bool? replace, CancellationToken ct) =>
        {
            DateOnly day;
            if (date is null) day = clock.Today;
            else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out day)) return Results.BadRequest(new { error = "Parametr date: rrrr-mm-dd." });

            ReportDocument document;
            try
            {
                document = await JsonSerializer.DeserializeAsync<ReportDocument>(http.Request.Body, ReportJson.Options, ct)
                           ?? throw new JsonException("Pusta treść.");
            }
            catch (JsonException e)
            {
                return Results.BadRequest(new { error = $"Nieprawidłowy JSON raportu: {e.Message}" });
            }

            var producer = $"api:{http.User.Identity?.Name}";
            var result = await reports.SaveExternalAsync(day, document, producer, replace == true, ct);
            return result.Outcome switch
            {
                ExternalReportOutcome.Conflict => Results.Conflict(new { error = $"Raport na {day:yyyy-MM-dd} już istnieje. Dodaj replace=true, żeby go zastąpić.", report_id = result.ReportId }),
                ExternalReportOutcome.Invalid => Results.UnprocessableEntity(new { errors = result.Errors }),
                _ => Results.Json(new { report_id = result.ReportId, date = day, status = result.Status.ToString(), issues = result.Errors },
                    ReportJson.Options, statusCode: StatusCodes.Status201Created),
            };
        });
        return api;
    }

    internal static async Task<IReadOnlyList<ReportInputItem>> LoadPendingAsync(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await (from i in db.SourceItems.AsNoTracking()
                             join s in db.Sources on i.SourceId equals s.Id
                             where i.ReportedAt == null && !i.IsBaseline
                             select new PendingItem(i, i.Origin != null ? i.Origin + " (wpis własny)" : s.Name)).ToListAsync(ct);
        return ReportComposer.BuildInputs(pending);
    }
}
