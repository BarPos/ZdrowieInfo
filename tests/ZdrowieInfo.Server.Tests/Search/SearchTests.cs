using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Search;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Search;

public class SearchTests(PostgresFixture postgres)
{
    internal static async Task<AppFactory> StartWithReportsAsync(PostgresFixture postgres)
    {
        var app = await AppFactory.CreateAsync(postgres);
        var item = await app.AddItemAsync("csirt-cez", "Krytyczna podatność FortiGate", "https://csirt.cez.gov.pl/pl/aktualnosci/forti", "Treść");
        await using var db = await app.DbAsync();
        var csirtItemId = item.Id;

        Report Make(DateOnly date, string title, string summary, params ReportFinding[] findings) => new()
        {
            Date = date, Title = title, Summary = summary, Producer = "claude", Status = ReportStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Findings = [.. findings],
        };
        ReportFinding F(Importance i, FindingCategory c, string headline, string body, string? action = null, long? itemId = null) => new()
        {
            Importance = i, Category = c, Headline = headline, Body = body, Action = action,
            Citations = [new FindingCitation { Title = "Źródło " + headline, Url = "https://example.pl/" + headline.Length, Quote = "cytat", Verified = true, SourceItemId = itemId }],
        };

        db.Reports.AddRange(
            Make(new DateOnly(2026, 11, 3), "Raport 3.11", "Przerwa w P1.",
                F(Importance.Critical, FindingCategory.Outage, "Przerwa w systemie DILO 12–13 listopada", "Planowana przerwa techniczna w nocy."),
                F(Importance.Low, FindingCategory.Other, "Nowe źródło danych refundacyjnych", "Aktualizacja <script>alert(1)</script> danych.")),
            Make(new DateOnly(2026, 11, 16), "Raport 16.11", "Podatności.",
                F(Importance.High, FindingCategory.Security, "Krytyczna podatność FortiGate", "Przerwy w działaniu VPN możliwe po aktualizacji.", "Zaktualizować do 7.4.3.", csirtItemId),
                F(Importance.Medium, FindingCategory.CeR, "Centralna e-rejestracja – II etap", "Kolejne poradnie od 1 stycznia.")),
            Make(new DateOnly(2026, 11, 17), "Brak zmian – 17.11.2026", "W monitorowanych źródłach nie pojawiły się nowe wpisy."));
        await db.SaveChangesAsync();
        return app;
    }

    private static Task<IReadOnlyList<SearchHit>> SearchAsync(AppFactory app, SearchFilters filters) =>
        app.Services.GetRequiredService<ReportSearch>().SearchAsync(filters);

    [Fact]
    public async Task Search_ignores_polish_characters_and_matches_word_fragments()
    {
        await using var app = await StartWithReportsAsync(postgres);

        var noDiacritics = await SearchAsync(app, new SearchFilters("zrodlo"));
        Assert.Contains(noDiacritics, h => h.Headline == "Nowe źródło danych refundacyjnych");

        var fragment = await SearchAsync(app, new SearchFilters("przerw"));
        Assert.Contains(fragment, h => h.Headline.StartsWith("Przerwa w systemie DILO"));
        Assert.Contains(fragment, h => h.Headline == "Krytyczna podatność FortiGate"); // „Przerwy” w treści

        var fullWord = await SearchAsync(app, new SearchFilters("fortigate"));
        Assert.Equal("Krytyczna podatność FortiGate", fullWord[0].Headline);
        Assert.Equal("Źródło Krytyczna podatność FortiGate", Assert.Single(fullWord[0].Sources).Title);

        Assert.Empty(await SearchAsync(app, new SearchFilters("xy")));
        Assert.Empty(await SearchAsync(app, new SearchFilters("zupełnie nieistniejące słowo")));
    }

    [Fact]
    public async Task Filters_narrow_results()
    {
        await using var app = await StartWithReportsAsync(postgres);
        await using var db = await app.DbAsync();
        var csirt = await db.Sources.SingleAsync(s => s.Slug == "csirt-cez");

        Assert.All(await SearchAsync(app, new SearchFilters(null, Importance: Importance.Critical)), h => Assert.Equal(Importance.Critical, h.Importance));
        Assert.Equal(["Centralna e-rejestracja – II etap"], (await SearchAsync(app, new SearchFilters(null, Category: FindingCategory.CeR))).Select(h => h.Headline));
        Assert.All(await SearchAsync(app, new SearchFilters("przerw", From: new DateOnly(2026, 11, 10))), h => Assert.Equal(new DateOnly(2026, 11, 16), h.Date));
        Assert.Equal(["Krytyczna podatność FortiGate"], (await SearchAsync(app, new SearchFilters(null, SourceId: csirt.Id))).Select(h => h.Headline));
    }

    [Fact]
    public async Task Report_level_matches_include_reports_without_findings()
    {
        await using var app = await StartWithReportsAsync(postgres);
        var hits = await SearchAsync(app, new SearchFilters("monitorowanych"));
        var hit = Assert.Single(hits);
        Assert.Null(hit.FindingId);
        Assert.Equal(new DateOnly(2026, 11, 17), hit.Date);
    }

    [Fact]
    public async Task Snippet_is_escaped_html_with_highlights()
    {
        await using var app = await StartWithReportsAsync(postgres);
        var hit = Assert.Single(await SearchAsync(app, new SearchFilters("refundacyjnych")), h => h.FindingId is not null);
        Assert.Contains("&lt;script&gt;", hit.SnippetHtml);
        Assert.DoesNotContain("<script>", hit.SnippetHtml);
        Assert.Contains("<mark>refundacyjnych</mark>", hit.SnippetHtml);
    }

    [Fact]
    public void Snippet_marks_terms_regardless_of_diacritics() =>
        Assert.Equal("Nowe <mark>źródło</mark> danych", ReportSearch.Snippet("Nowe źródło danych", ReportSearch.Terms("zrodlo")));

    [Fact]
    public async Task Search_page_works_without_javascript()
    {
        await using var app = await StartWithReportsAsync(postgres);
        var browser = await app.SignedInBrowserAsync();

        var html = await (await browser.GetAsync("/szukaj?q=fortigate")).Content.ReadAsStringAsync();
        Assert.Contains("<mark>FortiGate</mark>", html);
        var text = await browser.TextAsync("/szukaj?q=fortigate&waznosc=wysoka");
        Assert.Contains("Krytyczna podatność FortiGate", text);
        Assert.Contains("Nic nie znaleziono", await browser.TextAsync("/szukaj?q=fortigate&waznosc=niska"));
    }

    [Fact]
    public async Task Pages_are_disjoint_and_cover_all_hits()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await using (var db = await app.DbAsync())
        {
            for (var d = 0; d < 30; d++)
            {
                var date = new DateOnly(2026, 10, 1).AddDays(d);
                db.Reports.Add(new Report
                {
                    Date = date, Title = $"Raport {d}", Summary = "Podsumowanie.", Producer = "claude", Status = ReportStatus.Ready,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    Findings = [new ReportFinding { Importance = Importance.High, Category = FindingCategory.Outage, Headline = $"Przerwa techniczna nr {d}", Body = "Prace serwisowe." }],
                });
            }
            await db.SaveChangesAsync();
        }
        var search = app.Services.GetRequiredService<ReportSearch>();

        var first = await search.SearchPageAsync(new SearchFilters("przerw", Limit: 12));
        var second = await search.SearchPageAsync(new SearchFilters("przerw", Limit: 12, Offset: 12));
        var third = await search.SearchPageAsync(new SearchFilters("przerw", Limit: 12, Offset: 24));

        Assert.Equal((12, true), (first.Hits.Count, first.HasMore));
        Assert.Equal((12, true), (second.Hits.Count, second.HasMore));
        Assert.Equal((6, false), (third.Hits.Count, third.HasMore));
        var all = first.Hits.Concat(second.Hits).Concat(third.Hits).Select(h => h.FindingId).ToList();
        Assert.Equal(30, all.Distinct().Count());

        var browser = await app.SignedInBrowserAsync();
        var page2 = await browser.TextAsync("/szukaj?q=przerw&strona=2");
        Assert.Contains("Wyniki 26–30", page2);
        Assert.Contains("strona 2", page2);
    }
}
