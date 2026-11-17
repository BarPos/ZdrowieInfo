using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Search;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Mcp;

/// <summary>
/// Narzędzia MCP – wyłącznie do odczytu: wyszukiwanie w raportach, raport dnia, stan źródeł.
/// Wyniki jako JSON (snake_case) z adresami źródeł i adresem raportu w aplikacji.
/// </summary>
[McpServerToolType]
public sealed class ReportTools(
    ReportSearch search,
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    IOptions<ZdrowieInfoOptions> options)
{
    private const int MaxLimit = 50;

    [McpServerTool(Name = "search_reports", Title = "Szukaj w raportach", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Szuka w dziennych raportach ZdrowieInfo (zmiany na stronach NFZ, CeZ, MZ i CSIRT CeZ istotne dla przychodni POZ). " +
                 "Zapytanie jak w wyszukiwarce: słowa (wielkość liter i polskie znaki bez znaczenia, fragment słowa też pasuje), " +
                 "\"dokładna fraza\", or, -wykluczone. Zwraca punkty raportów od najlepiej pasujących, z adresami źródeł.")]
    public async Task<string> SearchReports(
        [Description("Zapytanie, np. \"przerwa P1\", \"e-zwolnienia\", \"FortiGate\". Puste – najnowsze punkty (z filtrami).")] string? query = null,
        [Description("Od dnia (rrrr-mm-dd), włącznie.")] string? from = null,
        [Description("Do dnia (rrrr-mm-dd), włącznie.")] string? to = null,
        [Description("Ważność: krytyczna, wysoka, średnia, niska.")] string? importance = null,
        [Description("Kategoria: awaria, P1, CeR, sprawozdawczość, bezpieczeństwo, rozliczenia, inne.")] string? category = null,
        [Description("Źródło – identyfikator (slug) z list_sources, np. csirt-cez.")] string? source = null,
        [Description("Maksymalna liczba wyników (1–50, domyślnie 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        var fromDate = ParseDate(from, nameof(from));
        var toDate = ParseDate(to, nameof(to));
        Importance? imp = null;
        if (!string.IsNullOrWhiteSpace(importance))
            imp = ReportVocabulary.Importances.TryGetValue(importance.Trim(), out var i) ? i
                : throw new McpException($"Nieznana ważność „{importance}”. Dozwolone: {string.Join(", ", ReportVocabulary.Importances.Keys)}.");
        FindingCategory? cat = null;
        if (!string.IsNullOrWhiteSpace(category))
            cat = ReportVocabulary.Categories.TryGetValue(category.Trim(), out var c) ? c
                : throw new McpException($"Nieznana kategoria „{category}”. Dozwolone: {string.Join(", ", ReportVocabulary.Categories.Keys)}.");
        int? sourceId = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var slug = source.Trim();
            sourceId = await db.Sources.Where(s => s.Slug == slug).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct)
                       ?? throw new McpException($"Nieznane źródło „{source}”. Identyfikatory zwraca list_sources.");
        }

        var hits = await search.SearchAsync(new SearchFilters(query, fromDate, toDate, imp, cat, sourceId, Math.Clamp(limit, 1, MaxLimit)), ct);
        return Json(new
        {
            count = hits.Count,
            results = hits.Select(h => new
            {
                date = h.Date,
                report_title = h.ReportTitle,
                report_url = ReportUrl(h.Date),
                kind = h.FindingId is null ? "raport" : "punkt",
                importance = h.Importance is { } hi ? ReportVocabulary.Label(hi) : null,
                category = h.Category is { } hc ? ReportVocabulary.Label(hc) : null,
                headline = h.Headline,
                text = h.Text,
                sources = h.Sources.Select(s => new { title = s.Title, url = s.Url, published_on = s.PublishedOn }),
            }),
        });
    }

    [McpServerTool(Name = "get_report", Title = "Raport dnia", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Pełny raport dzienny ZdrowieInfo: podsumowanie, punkty (ważność, kategoria, opis, co zrobić) z cytatami i adresami źródeł, " +
                 "pominięte wpisy, problemy ze źródłami. Bez daty – najnowszy raport.")]
    public async Task<string> GetReport(
        [Description("Dzień raportu (rrrr-mm-dd). Puste – najnowszy.")] string? date = null,
        CancellationToken ct = default)
    {
        var day = ParseDate(date, nameof(date));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        day ??= await db.Reports.OrderByDescending(r => r.Date).Select(r => (DateOnly?)r.Date).FirstOrDefaultAsync(ct)
                ?? throw new McpException("Nie ma jeszcze żadnego raportu.");
        var view = await ReportView.LoadAsync(db, day.Value, ct)
                   ?? throw new McpException($"Brak raportu z dnia {day:yyyy-MM-dd}.");

        return Json(new
        {
            date = view.Report.Date,
            title = view.Report.Title,
            summary = view.Report.Summary,
            status = view.Report.Status switch
            {
                ReportStatus.NeedsReview => "do weryfikacji",
                ReportStatus.AiError => "bez analizy",
                _ => "gotowy",
            },
            status_note = view.StatusNote,
            producer = view.ProducerLabel,
            report_url = ReportUrl(view.Report.Date),
            pdf_url = Url($"/eksport/raport-{view.Report.Date:yyyy-MM-dd}.pdf"),
            findings = view.Groups.SelectMany(g => g.Findings).Select(f => new
            {
                number = f.Number,
                importance = ReportVocabulary.Label(f.Finding.Importance),
                category = f.Category,
                headline = f.Finding.Headline,
                body = f.Finding.Body,
                action = f.Finding.Action,
                citations = f.Citations.Select(c => new
                {
                    quote = c.Citation.Quote,
                    verified = c.Citation.Verified,
                    title = c.Citation.Title,
                    url = c.Citation.Url,
                    published_on = c.Citation.PublishedOn,
                }),
            }),
            ignored = view.Ignored.Select(i => new { title = i.Title, url = i.Url, reason = i.Reason }),
            source_problems = view.SourceProblems.Select(s => new { name = s.Name, ok = s.Ok, problem = s.Problem, warning = s.Warning, incident_open = s.IncidentOpen }),
            sources_checked = view.SourcesChecked,
            previous_report = view.PreviousDay,
            next_report = view.NextDay,
        });
    }

    [McpServerTool(Name = "list_sources", Title = "Monitorowane źródła", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Monitorowane strony (NFZ, CeZ, MZ, CSIRT CeZ): identyfikator, nazwa, adres, częstotliwość, stan ostatniego sprawdzenia " +
                 "i otwarty incydent (strona niedostępna albo zmieniona struktura).")]
    public async Task<string> ListSources(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sources = await db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var incidents = (await db.SourceIncidents.AsNoTracking().Where(i => i.EndedAt == null).ToListAsync(ct))
            .ToLookup(i => i.SourceId);

        return Json(new
        {
            count = sources.Count,
            sources = sources.Select(s => new
            {
                id = s.Slug,
                name = s.Name,
                url = s.Url,
                active = s.IsActive,
                schedule = s.Group switch
                {
                    SourceGroup.Daily => "codziennie",
                    SourceGroup.Weekly => "w poniedziałki",
                    SourceGroup.Monthly => "1. dnia miesiąca",
                    _ => "co dwa miesiące",
                },
                status = s.Status switch
                {
                    SourceStatus.New => "jeszcze nie sprawdzone",
                    SourceStatus.Ok => "działa",
                    SourceStatus.Retrying => "ponawianie",
                    SourceStatus.NeedsAttention => "wymaga uwagi",
                    _ => s.Status.ToString(),
                },
                status_message = s.StatusMessage,
                last_checked_at = s.LastCheckedAt,
                last_success_at = s.LastSuccessAt,
                open_incident = incidents[s.Id].Select(i => new
                {
                    since = i.StartedAt,
                    error = Push.NotificationDispatcher.Describe(i.ErrorType),
                    details = i.Details,
                    attempts = i.Attempts,
                }).FirstOrDefault(),
            }),
        });
    }

    private static DateOnly? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", out var d) ? d
            : throw new McpException($"Parametr {name}: oczekiwana data w formacie rrrr-mm-dd, otrzymano „{value}”.");
    }

    private string ReportUrl(DateOnly date) => Url($"/raporty/{date:yyyy-MM-dd}");

    private string Url(string path) => options.Value.PublicUrl.TrimEnd('/') + path;

    private static string Json(object value) => JsonSerializer.Serialize(value, ReportJson.Options);
}
