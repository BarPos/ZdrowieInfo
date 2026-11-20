using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Entries;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Entries;

/// <summary>Wpisy własne (panel → Wpisy własne): oczyszczanie HTML, droga do raportu, strona wpisu.</summary>
public class UserEntryTests(PostgresFixture postgres)
{
    private static readonly DateOnly Day = new(2026, 11, 18);

    private const string CsirtHtml = """
        <p class="MsoNormal" style="color:red">CSIRT MON informuje o <strong>krytycznej</strong> podatności.</p>
        <table border="1" style="width:100%"><tr><th>Produkt</th><th>Wersja z poprawką</th></tr>
        <tr><td colspan="1">FortiOS</td><td>7.4.3</td></tr></table>
        <ul><li>Zaktualizuj urządzenia.</li></ul>
        """;

    private static UserEntryService Entries(AppFactory app) => app.Services.GetRequiredService<UserEntryService>();

    [Fact]
    public void Hostile_html_is_rebuilt_from_the_allowlist()
    {
        var html = RichText.Sanitize("""
            <p onclick="x()">Tekst <script>alert(1)</script><img src=x onerror=alert(1)>
            <a href="javascript:alert(1)">zły link</a> <a href="https://cert.pl/a?b=1&amp;c=2" style="x">dobry</a></p>
            <svg><a href="https://zly.pl">svg</a></svg><style>p{}</style><!-- komentarz -->
            <div class="x"><span style="font-weight:bold">w divie</span></div>
            <table><tr><td colspan="2" onclick="x" class="y">komórka</td><td rowspan="999">r</td></tr></table>
            <iframe src="https://zly.pl"></iframe><form><input value="x"></form>
            """);

        foreach (var forbidden in new[] { "script", "onerror", "onclick", "javascript:", "<svg", "zly.pl", "style", "class", "<img", "<iframe", "<form", "<input", "komentarz", "<div", "<span", "999" })
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a href=\"https://cert.pl/a?b=1&amp;c=2\" rel=\"noopener nofollow\" target=\"_blank\">dobry</a>", html);
        Assert.Contains("<td colspan=\"2\">komórka</td>", html);
        Assert.Contains("<p>w divie</p>", html);
        Assert.Contains("<a>zły link</a>", html);
    }

    [Fact]
    public void Text_for_analysis_keeps_table_rows_and_list_items()
    {
        var text = RichText.ToText(RichText.Sanitize(CsirtHtml));

        Assert.Equal("""
            CSIRT MON informuje o krytycznej podatności.
            Produkt | Wersja z poprawką
            FortiOS | 7.4.3
            – Zaktualizuj urządzenia.
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public async Task Entries_go_to_the_report_as_separate_inputs_with_their_own_source_and_link()
    {
        var generator = new ScriptedGenerator();
        await using var app = await AppFactory.CreateAsync(postgres, services: s => s.AddSingleton<IReportGenerator>(generator));
        var first = await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Podatność w FortiOS", CsirtHtml, Day), "admin");
        var second = await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Podatność w VPN", "<p>Druga podatność w VPN.</p>", Day), "admin");
        Assert.True(first.Ok && second.Ok);
        generator.Replies.Enqueue(r =>
        {
            var forti = r.Items.Single(i => i.Title == "Podatność w FortiOS");
            var vpn = r.Items.Single(i => i.Title == "Podatność w VPN");
            return ScriptedGenerator.Result(new ReportDocument
            {
                Title = "Raport 18.11.2026",
                Summary = "Podatności z CSIRT MON.",
                Findings = [new FindingDto { Importance = "wysoka", Category = "bezpieczeństwo", Headline = "FortiOS", Body = "Aktualizacja.", Citations = [new CitationDto { ItemId = forti.Id, Quote = "FortiOS | 7.4.3" }] }],
                Ignored = [new IgnoredDto { ItemId = vpn.Id, Reason = "test" }],
            });
        });

        var result = await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(Day);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        var request = Assert.Single(generator.Requests);
        Assert.Equal(2, request.Items.Count); // ten sam „adres źródła” nie łączy wpisów w jeden
        var forti = request.Items.Single(i => i.Title == "Podatność w FortiOS");
        Assert.Equal(["CSIRT MON (wpis własny)"], forti.SourceNames);
        Assert.Equal($"https://localhost/wpisy-wlasne/{first.Id}", forti.Url);
        Assert.Contains("Produkt | Wersja z poprawką", forti.Content);

        await using (var db = await app.DbAsync())
        {
            var report = await db.Reports.AsNoTracking().Include(r => r.Findings).ThenInclude(f => f.Citations).SingleAsync(r => r.Id == result.ReportId);
            Assert.Equal(ReportStatus.Ready, report.Status);
            Assert.True(report.Findings.Single().Citations.Single().Verified); // cytat z wiersza tabeli
            Assert.Equal(23, report.SourcesState.Count); // źródło systemowe nie jest w stanie źródeł
        }

        // Po raporcie wpisu nie da się zmienić ani usunąć.
        Assert.False((await Entries(app).UpdateAsync(first.Id!.Value, new UserEntryInput("CSIRT MON", "Zmiana", "<p>x</p>", Day))).Ok);
        Assert.False((await Entries(app).DeleteAsync(first.Id!.Value)).Ok);
        Assert.DoesNotContain(app.Http.Requests, r => r.Contains("wpisy-wlasne"));
    }

    [Fact]
    public async Task Unreported_entry_can_be_edited_and_deleted_and_bad_input_is_refused()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        Assert.False((await Entries(app).CreateAsync(new UserEntryInput("", "Tytuł", "<p>x</p>", Day), "admin")).Ok);
        Assert.False((await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Tytuł", "<script>tylko skrypt</script>", Day), "admin")).Ok);
        Assert.False((await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Tytuł", new string('x', RichText.MaxHtmlChars + 1), Day), "admin")).Ok);

        var created = await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Tytuł", "<p>Pierwsza wersja.</p>", Day), "admin");
        Assert.True((await Entries(app).UpdateAsync(created.Id!.Value, new UserEntryInput("CERT Polska", "Nowy tytuł", "<p>Druga wersja.</p>", Day))).Ok);
        await using (var db = await app.DbAsync())
        {
            var item = await UserEntryService.Mine(db).SingleAsync();
            Assert.Equal(("CERT Polska", "Nowy tytuł", "Druga wersja."), (item.Origin, item.Title, item.Content));
        }
        Assert.True((await Entries(app).DeleteAsync(created.Id.Value)).Ok);
        await using (var db = await app.DbAsync())
            Assert.Empty(await UserEntryService.Mine(db).ToListAsync());
    }

    [Fact]
    public async Task Reader_opens_the_entry_page_and_the_system_source_stays_hidden()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var created = await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Podatność w FortiOS", CsirtHtml, Day), "admin");

        Assert.Equal(HttpStatusCode.Redirect, (await app.CreateBrowser().GetAsync($"/wpisy-wlasne/{created.Id}")).StatusCode);
        var reader = await app.SignedInBrowserAsync(UserRole.User, "czytelnik");
        var html = await (await reader.GetAsync($"/wpisy-wlasne/{created.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Podatność w FortiOS", html);
        Assert.Contains("<td>FortiOS</td>", html);
        Assert.Contains("CSIRT MON", html);
        Assert.DoesNotContain("MsoNormal", html);
        Assert.Equal(HttpStatusCode.Redirect, (await reader.GetAsync("/panel/wpisy-wlasne")).StatusCode); // panel tylko dla administratora

        var admin = await app.SignedInBrowserAsync();
        var sources = await (await admin.GetAsync("/panel/zrodla")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Wpisy własne</a>", sources);
    }

    [Fact]
    public async Task Backdated_report_includes_the_entry_without_fetching_it()
    {
        var generator = new ScriptedGenerator();
        await using var app = await AppFactory.CreateAsync(postgres, services: s => s.AddSingleton<IReportGenerator>(generator));
        var day = new DateOnly(2026, 11, 10);
        await Entries(app).CreateAsync(new UserEntryInput("CSIRT MON", "Podatność w FortiOS", CsirtHtml, day), "admin");
        generator.Replies.Enqueue(r => ScriptedGenerator.Result(new ReportDocument
        {
            Title = "Raport 10.11.2026",
            Summary = "Podatność.",
            Ignored = r.Items.Select(i => new IgnoredDto { ItemId = i.Id, Reason = "test" }).ToList(),
        }));

        var result = await app.Services.GetRequiredService<DailyReportService>().GenerateForAsync(day, day);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        Assert.Contains(Assert.Single(generator.Requests).Items, i => i.Title == "Podatność w FortiOS");
        Assert.DoesNotContain(app.Http.Requests, r => r.Contains("wpisy-wlasne") || r.StartsWith("https://localhost"));
    }
}
