using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Search;

/// <param name="Offset">Ile trafień pominąć (stronicowanie).</param>
/// <param name="Limit">Ile trafień zwrócić (rozmiar strony).</param>
public sealed record SearchFilters(string? Query, DateOnly? From = null, DateOnly? To = null, Importance? Importance = null,
    FindingCategory? Category = null, int? SourceId = null, int Limit = 100, int Offset = 0);

public sealed record SearchSource(string Title, string Url, DateOnly? PublishedOn);

/// <summary>Trafienie: punkt raportu (FindingId) albo sam raport (tytuł/podsumowanie). Snippet to bezpieczny HTML z &lt;mark&gt;.</summary>
public sealed record SearchHit(
    ReportKey Report,
    string ReportTitle,
    ReportStatus Status,
    long? FindingId,
    Importance? Importance,
    FindingCategory? Category,
    string Headline,
    string Text,
    string SnippetHtml,
    IReadOnlyList<SearchSource> Sources)
{
    public DateOnly Date => Report.End;
}

public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, bool HasMore);

/// <summary>
/// Wyszukiwanie w raportach: pełnotekstowe (tsvector, konfiguracja pl_unaccent, websearch_to_tsquery – cudzysłowy, „or”, „-słowo”)
/// plus dopasowanie fragmentu słowa bez polskich znaków (konfiguracja simple nie odmienia: „przerw” znajduje „przerwa” i „przerwy”).
/// Wspólne dla strony /szukaj i serwera MCP.
/// </summary>
public sealed class ReportSearch(IDbContextFactory<ZdrowieInfoDbContext> dbFactory)
{
    /// <summary>Najdalsze trafienie, do którego da się dojść stronicowaniem.</summary>
    public const int MaxWindow = 1000;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchFilters filters, CancellationToken ct = default) =>
        (await SearchPageAsync(filters, ct)).Hits;

    /// <summary>
    /// Jedna strona wyników. Kolejność jest stała (ranking, potem dopasowania fragmentów, potem same raporty – każda część
    /// z jednoznacznym porządkiem), więc strona N to zawsze ten sam wycinek: liczymy pierwsze Offset+Limit+1 trafień i tniemy.
    /// </summary>
    public async Task<SearchPage> SearchPageAsync(SearchFilters filters, CancellationToken ct = default)
    {
        var query = filters.Query?.Trim() ?? "";
        var terms = Terms(query);
        var offset = Math.Clamp(filters.Offset, 0, MaxWindow);
        var limit = Math.Clamp(filters.Limit, 1, MaxWindow);
        var window = Math.Min(offset + limit + 1, MaxWindow + 1);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var findings = from f in db.ReportFindings.AsNoTracking()
                       join r in db.Reports on f.ReportId equals r.Id
                       select new { f, r };
        if (filters.From is { } from) findings = findings.Where(x => x.r.Date >= from);
        if (filters.To is { } to) findings = findings.Where(x => x.r.PeriodStart <= to);
        if (filters.Importance is { } importance) findings = findings.Where(x => x.f.Importance == importance);
        if (filters.Category is { } category) findings = findings.Where(x => x.f.Category == category);
        if (filters.SourceId is { } sourceId)
        {
            var cited = from c in db.FindingCitations
                        join i in db.SourceItems on c.SourceItemId equals (long?)i.Id
                        where i.SourceId == sourceId
                        select c.FindingId;
            findings = findings.Where(x => cited.Contains(x.f.Id));
        }

        List<Row> rows;
        var partial = findings;
        foreach (var pattern in terms.Select(LikePattern))
            partial = partial.Where(x => EF.Functions.ILike(EF.Functions.Unaccent(x.f.Headline + " " + x.f.Body + " " + (x.f.Action ?? "")), pattern, "\\"));
        var fts = findings.Where(x => x.f.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(ZdrowieInfoDbContext.SearchConfig, query)));

        if (query.Length == 0)
        {
            rows = await findings.OrderByDescending(x => x.r.Date).ThenByDescending(x => x.r.Id).ThenBy(x => x.f.Order).Take(window)
                .Select(x => new Row(x.r.Kind, x.r.PeriodStart, x.r.Date, x.r.Title, x.r.Status, x.f.Id, x.f.Importance, x.f.Category, x.f.Headline, x.f.Body, x.f.Action)).ToListAsync(ct);
        }
        else
        {
            // 1) pełnotekstowo (ranking), 2) fragmenty słów bez polskich znaków – dopisane za trafieniami pełnotekstowymi.
            rows = await fts
                .OrderByDescending(x => x.f.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(ZdrowieInfoDbContext.SearchConfig, query)))
                .ThenByDescending(x => x.r.Date).ThenByDescending(x => x.r.Id).ThenBy(x => x.f.Order).ThenBy(x => x.f.Id).Take(window)
                .Select(x => new Row(x.r.Kind, x.r.PeriodStart, x.r.Date, x.r.Title, x.r.Status, x.f.Id, x.f.Importance, x.f.Category, x.f.Headline, x.f.Body, x.f.Action))
                .ToListAsync(ct);
            if (terms.Count > 0 && rows.Count < window)
            {
                rows.AddRange(await partial.Where(x => !fts.Any(y => y.f.Id == x.f.Id))
                    .OrderByDescending(x => x.r.Date).ThenByDescending(x => x.r.Id).ThenBy(x => x.f.Order).ThenBy(x => x.f.Id).Take(window - rows.Count)
                    .Select(x => new Row(x.r.Kind, x.r.PeriodStart, x.r.Date, x.r.Title, x.r.Status, x.f.Id, x.f.Importance, x.f.Category, x.f.Headline, x.f.Body, x.f.Action))
                    .ToListAsync(ct));
            }
        }

        // Raporty dopasowane tytułem albo podsumowaniem (np. puste „Brak zmian” albo raport bez analizy) – gdy nie filtrujemy po punktach
        // i żaden punkt raportu nie pasuje (wtedy raport jest już na liście przez swoje punkty).
        var reportRows = new List<Row>();
        if (query.Length > 0 && filters.Importance is null && filters.Category is null && filters.SourceId is null && rows.Count < window)
        {
            var withFindings = fts.Select(x => x.r.Id);
            if (terms.Count > 0) withFindings = withFindings.Union(partial.Select(x => x.r.Id));
            var reports = db.Reports.AsNoTracking().Where(r => !withFindings.Contains(r.Id));
            if (filters.From is { } f2) reports = reports.Where(r => r.Date >= f2);
            if (filters.To is { } t2) reports = reports.Where(r => r.PeriodStart <= t2);
            var matching = reports.Where(r => r.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(ZdrowieInfoDbContext.SearchConfig, query)));
            if (terms.Count > 0)
            {
                var partialReports = reports;
                foreach (var pattern in terms.Select(LikePattern))
                    partialReports = partialReports.Where(r => EF.Functions.ILike(EF.Functions.Unaccent(r.Title + " " + r.Summary), pattern, "\\"));
                matching = matching.Union(partialReports);
            }
            reportRows = await matching.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(window - rows.Count)
                .Select(r => new Row(r.Kind, r.PeriodStart, r.Date, r.Title, r.Status, null, null, null, r.Title, r.Summary, null)).ToListAsync(ct);
        }

        var all = rows.Concat(reportRows).ToList();
        var page = all.Skip(offset).Take(limit).ToList();
        var ids = page.Where(r => r.FindingId is not null).Select(r => r.FindingId!.Value).ToList();
        var citations = (await db.FindingCitations.AsNoTracking().Where(c => ids.Contains(c.FindingId)).OrderBy(c => c.Id).ToListAsync(ct))
            .ToLookup(c => c.FindingId);

        var hits = page.Select(r =>
        {
            var text = r.Action is null ? r.Body : $"{r.Body} Do zrobienia: {r.Action}";
            return new SearchHit(new ReportKey(r.Kind, r.PeriodStart, r.Date), r.ReportTitle, r.Status, r.FindingId, r.Importance, r.Category, r.Headline, text,
                Snippet(r.FindingId is null ? r.Body : r.Headline + " — " + text, terms),
                r.FindingId is { } id ? citations[id].Select(c => new SearchSource(c.Title, c.Url, c.PublishedOn)).DistinctBy(c => c.Url).ToList() : []);
        }).ToList();
        return new SearchPage(hits, all.Count > offset + limit && offset + limit < MaxWindow);
    }

    private sealed record Row(ReportKind Kind, DateOnly PeriodStart, DateOnly Date, string ReportTitle, ReportStatus Status, long? FindingId,
        Importance? Importance, FindingCategory? Category, string Headline, string Body, string? Action);

    /// <summary>Słowa zapytania bez operatorów (cudzysłowy, „or”, „-słowo”), bez polskich znaków, małymi literami, co najmniej 3 znaki.</summary>
    public static List<string> Terms(string query) =>
        query.Replace('"', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.StartsWith('-') && !t.Equals("or", StringComparison.OrdinalIgnoreCase))
            .Select(Fold).Where(t => t.Length >= 3).Distinct().Take(8).ToList();

    /// <summary>Wzorzec ILIKE dla fragmentu słowa (znaki specjalne LIKE poprzedzone \\).</summary>
    private static string LikePattern(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static readonly System.Text.Encodings.Web.HtmlEncoder Html =
        System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    /// <summary>Fragment tekstu wokół pierwszego trafienia, zakodowany jako HTML, z &lt;mark&gt; na trafieniach.</summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int radius = 140)
    {
        var folded = Fold(text);
        var first = terms.Select(t => folded.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, first - radius / 2);
        var end = Math.Min(text.Length, start + radius * 2);
        if (start > 0) { var space = text.IndexOf(' ', start); if (space > 0 && space < first) start = space + 1; }

        var marks = new bool[text.Length];
        foreach (var term in terms)
            for (var i = folded.IndexOf(term, StringComparison.Ordinal); i >= 0; i = folded.IndexOf(term, i + term.Length, StringComparison.Ordinal))
                for (var k = i; k < i + term.Length && k < marks.Length; k++) marks[k] = true;

        var sb = new StringBuilder();
        if (start > 0) sb.Append('…');
        var open = false;
        for (var i = start; i < end; i++)
        {
            if (marks[i] && !open) { sb.Append("<mark>"); open = true; }
            if (!marks[i] && open) { sb.Append("</mark>"); open = false; }
            sb.Append(Html.Encode(text[i].ToString()));
        }
        if (open) sb.Append("</mark>");
        if (end < text.Length) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Małe litery bez polskich znaków – ta sama długość co tekst (pozycje się zgadzają).</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'ą' => 'a', 'ć' => 'c', 'ę' => 'e', 'ł' => 'l', 'ń' => 'n', 'ó' => 'o', 'ś' => 's', 'ź' => 'z', 'ż' => 'z',
                _ => ch.ToString().Normalize(NormalizationForm.FormD)[0],
            });
        }
        return sb.ToString();
    }
}
