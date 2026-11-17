using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Checking;
using ZdrowieInfo.Server.Sources.Extraction;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Reports;

public class RetrospectiveReportTests(PostgresFixture postgres)
{
    // 20.11.2026, 12:00 czasu warszawskiego.
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly DateOnly Tuesday = new(2026, 11, 17);
    private const string Csirt = "https://csirt.cez.gov.pl/pl/wszystkie-aktualnosci";

    /// <summary>Aktywne tylko wskazane źródła; <paramref name="knownSince"/> – od kiedy aplikacja sprawdza źródła (stan bazowy).</summary>
    private static async Task<AppFactory> StartAsync(PostgresFixture postgres, string activeSlug, DateTimeOffset? knownSince = null,
        Action<IServiceCollection>? services = null)
    {
        var app = await AppFactory.CreateAsync(postgres, time: new FakeTimeProvider(Now), services: services);
        await using var db = await app.DbAsync();
        await db.Sources.Where(s => s.Slug != activeSlug).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, false));
        if (knownSince is { } since)
            await db.Sources.ExecuteUpdateAsync(u => u.SetProperty(s => s.BaselineAt, since).SetProperty(s => s.LastSuccessAt, Now));
        return app;
    }

    private static readonly DateTimeOffset LongAgo = new(2026, 11, 1, 4, 0, 0, TimeSpan.Zero);

    private static DailyReportService Service(AppFactory app) => app.Services.GetRequiredService<DailyReportService>();

    /// <summary>Wpisy z archiwum (jak zastane): znane od dawna, z datą publikacji i treścią.</summary>
    private static async Task SeedAsync(AppFactory app)
    {
        await app.AddItemAsync("csirt-cez", "Podatność w FortiGate", "https://csirt.cez.gov.pl/pl/aktualnosci/forti",
            "Krytyczna podatność w FortiGate pozwala przejąć urządzenie.", published: Monday, baseline: true);
        await app.AddItemAsync("csirt-cez", "Phishing na lekarzy", "https://csirt.cez.gov.pl/pl/aktualnosci/phishing",
            "Trwa kampania phishingowa podszywająca się pod NFZ.", published: Tuesday, baseline: true);
        await app.AddItemAsync("csirt-cez", "Starszy wpis", "https://csirt.cez.gov.pl/pl/aktualnosci/stary",
            "Wpis sprzed okresu raportu.", published: Monday.AddDays(-5), baseline: true);
    }

    [Fact]
    public async Task Backdated_daily_report_uses_entries_published_that_day_without_claiming_them_or_pushing()
    {
        await using var app = await StartAsync(postgres, "csirt-cez", LongAgo);
        await SeedAsync(app);

        var result = await Service(app).GenerateForAsync(Monday, Monday);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        Assert.Empty(app.Http.Requests); // archiwum już sięga przed poniedziałek – nic nie pobierano
        await using var db = await app.DbAsync();
        var report = await db.Reports.Include(r => r.Findings).SingleAsync();
        Assert.Equal((ReportKind.Daily, Monday, Monday), (report.Kind, report.PeriodStart, report.Date));
        Assert.Equal("Podatność w FortiGate", Assert.Single(report.Findings).Headline);
        Assert.True(report.PushSent);
        Assert.Empty(report.SourcesState); // bez dzisiejszego stanu źródeł w raporcie z przeszłości
        Assert.All(await db.SourceItems.ToListAsync(), i => Assert.Null(i.ReportId)); // wpisy nie są „zabierane”

        Assert.Equal(ReportRunOutcome.AlreadyExists, (await Service(app).GenerateForAsync(Monday, Monday)).Outcome);
        Assert.Equal(ReportRunOutcome.Created, (await Service(app).GenerateForAsync(Monday, Monday, replace: true)).Outcome);
        Assert.Equal(1, await db.Reports.CountAsync());
    }

    [Fact]
    public async Task Period_report_covers_all_days_and_has_its_own_page()
    {
        await using var app = await StartAsync(postgres, "csirt-cez", LongAgo);
        await SeedAsync(app);
        await Service(app).GenerateForAsync(Tuesday, Tuesday); // dzienny z tą samą datą końcową nie przeszkadza

        var result = await Service(app).GenerateForAsync(Monday, Tuesday);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        await using var db = await app.DbAsync();
        var period = await db.Reports.Include(r => r.Findings).SingleAsync(r => r.Kind == ReportKind.Period);
        Assert.Equal(2, period.Findings.Count);
        Assert.Equal(2, period.InputItemIds.Count);
        Assert.Equal(2, await db.Reports.CountAsync());

        var browser = await app.SignedInBrowserAsync();
        var page = await browser.TextAsync("/raporty/2026-11-16--2026-11-17");
        Assert.Contains("raport okresowy", page);
        Assert.Contains("od 16 listopada 2026 do 17 listopada 2026", page);
        Assert.Contains("Phishing na lekarzy", page);
        Assert.DoesNotContain("Stan źródeł", page);
        var list = await browser.TextAsync("/");
        Assert.Contains("okresowy", list);
    }

    [Fact]
    public async Task Requests_for_the_future_or_too_long_periods_are_rejected()
    {
        await using var app = await StartAsync(postgres, "csirt-cez", LongAgo);
        Assert.Equal(ReportRunOutcome.Rejected, (await Service(app).GenerateForAsync(Monday, new DateOnly(2026, 11, 21))).Outcome);
        Assert.Equal(ReportRunOutcome.Rejected, (await Service(app).GenerateForAsync(Monday.AddDays(-100), Monday)).Outcome);
        Assert.Equal(ReportRunOutcome.Rejected, (await Service(app).GenerateForAsync(Tuesday, Monday)).Outcome);
    }

    [Fact]
    public async Task Missing_days_are_fetched_from_following_list_pages()
    {
        await using var app = await StartAsync(postgres, "csirt-cez");
        var source = await (await app.DbAsync()).Sources.SingleAsync(s => s.Slug == "csirt-cez");
        var page1 = Fixtures.Read("csirt-cez", "a.html");
        var page2 = Fixtures.Read("csirt-cez", "page2.html");
        app.Http.Serve(Csirt, page1);
        app.Http.Serve(Csirt + "?page=1", page2);
        // Granica w środku drugiej strony: najstarsza data z pierwszej strony.
        var first = SourceExtractor.ExtractList(page1, Csirt, source.Selectors).Items.Where(i => i.PublishedOn is not null).ToList();
        var from = first.Min(i => i.PublishedOn!.Value);
        var second = SourceExtractor.ExtractList(page2, Csirt + "?page=1", source.Selectors).Items.Where(i => i.PublishedOn is not null).ToList();
        Assert.True(second.Min(i => i.PublishedOn!.Value) < from, "fixture: druga strona sięga dalej niż pierwsza");

        var archive = await app.Services.GetRequiredService<SourceCheckService>().EnsureArchiveAsync(from);

        Assert.Equal(1, archive.SourcesFetched);
        Assert.Empty(archive.Warnings);
        Assert.Equal(1, app.Http.CountRequests(Csirt + "?page=1"));
        await using var db = await app.DbAsync();
        var items = await db.SourceItems.Where(i => i.SourceId == source.Id).ToListAsync();
        var expected = first.Concat(second).Where(i => i.PublishedOn >= from).Select(i => i.Key).Distinct().Count();
        Assert.Equal(expected, items.Count);
        Assert.Equal(expected, archive.ItemsAdded);
        Assert.All(items, i => Assert.True(i.IsBaseline && i.PublishedOn >= from)); // nie trafią do raportu dziennego
        var unchanged = await db.Sources.SingleAsync(s => s.Id == source.Id);
        Assert.Null(unchanged.LastSuccessAt); // stan źródła i incydenty bez zmian
        Assert.Equal(from, unchanged.ArchiveFrom);

        // Drugi raz (także od późniejszego dnia, za który nie ma żadnego wpisu): archiwum już sięga tej daty – bez pobierania.
        var before = app.Http.Requests.Count;
        Assert.Equal(0, (await app.Services.GetRequiredService<SourceCheckService>().EnsureArchiveAsync(from)).SourcesFetched);
        Assert.Equal(0, (await app.Services.GetRequiredService<SourceCheckService>().EnsureArchiveAsync(from.AddDays(1))).SourcesFetched);
        Assert.Equal(before, app.Http.Requests.Count);
    }

    [Fact]
    public async Task Archive_limit_is_reported_not_silent()
    {
        await using var app = await StartAsync(postgres, "csirt-cez");
        app.Http.Serve(Csirt, Fixtures.Read("csirt-cez", "a.html"));
        app.Http.Fail(Csirt + "?page=1", HttpStatusCode.InternalServerError);

        var archive = await app.Services.GetRequiredService<SourceCheckService>().EnsureArchiveAsync(new DateOnly(2025, 1, 1));

        Assert.Contains(archive.Warnings, w => w.Contains("CSIRT") && w.Contains("2. strony"));
    }

    [Fact]
    public async Task Deleted_report_keeps_entries_reported_and_today_is_not_recreated_by_scheduler()
    {
        // 18.11.2026 6:10 – po raporcie z harmonogramu.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 5, 10, 0, TimeSpan.Zero));
        await using var app = await AppFactory.CreateAsync(postgres, time: time);
        var item = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "Platforma Usług Inteligentnych (PUI) będzie niedostępna w nocy.");
        var today = new DateOnly(2026, 11, 18);
        Assert.Equal(ReportRunOutcome.Created, (await Service(app).GenerateAsync(today)).Outcome);

        Assert.True(await Service(app).DeleteAsync(ReportKey.Daily(today)));

        await using var db = await app.DbAsync();
        Assert.Equal(0, await db.Reports.CountAsync());
        var stored = await db.SourceItems.SingleAsync(i => i.Id == item.Id);
        Assert.Null(stored.ReportId);
        Assert.NotNull(stored.ReportedAt); // nie wraca do najbliższego raportu dziennego
        Assert.Null(await app.Services.GetRequiredService<ReportScheduler>().RunDueAsync());
        Assert.False(await Service(app).DeleteAsync(ReportKey.Daily(today)));
    }

    [Fact]
    public async Task Public_link_serves_pdf_until_revoked_or_renewed_and_survives_regeneration()
    {
        await using var app = await StartAsync(postgres, "csirt-cez", LongAgo);
        await SeedAsync(app);
        await Service(app).GenerateForAsync(Monday, Monday);
        var key = ReportKey.Daily(Monday);

        var token = await Service(app).ShareAsync(key);
        Assert.NotNull(token);
        Assert.True(token!.Length >= 40);
        Assert.Equal(token, await Service(app).ShareAsync(key)); // ponowne „udostępnij” nie zmienia linku

        // Gotowy plik PDF (bez kompilacji LaTeX-a w tym teście).
        var pdf = Path.Combine(app.DataDirectory, "test.pdf");
        await File.WriteAllBytesAsync(pdf, "%PDF-1.7 test"u8.ToArray());
        await using (var db = await app.DbAsync())
            await db.Reports.ExecuteUpdateAsync(u => u.SetProperty(r => r.PdfPath, pdf));

        var anonymous = app.CreateBrowser();
        var response = await anonymous.GetAsync($"/udostepnione/{token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/udostepnione/" + new string('x', 43))).StatusCode);

        // Ponowne przygotowanie raportu zachowuje link.
        await Service(app).GenerateForAsync(Monday, Monday, replace: true);
        await using (var db = await app.DbAsync())
        {
            Assert.Equal(token, await db.Reports.Select(r => r.ShareToken).SingleAsync());
            await db.Reports.ExecuteUpdateAsync(u => u.SetProperty(r => r.PdfPath, pdf));
        }
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/udostepnione/{token}")).StatusCode);

        var renewed = await Service(app).ShareAsync(key, renew: true);
        Assert.NotEqual(token, renewed);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/udostepnione/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/udostepnione/{renewed}")).StatusCode);

        await Service(app).UnshareAsync(key);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/udostepnione/{renewed}")).StatusCode);
    }

    [Fact]
    public async Task Admin_shares_and_deletes_from_the_report_page_with_forms()
    {
        await using var app = await StartAsync(postgres, "csirt-cez", LongAgo);
        await SeedAsync(app);
        await Service(app).GenerateForAsync(Monday, Monday);
        var admin = await app.SignedInBrowserAsync();

        var shared = await admin.SubmitActionAsync("/raporty/2026-11-16", "raporty/2026-11-16/udostepnij");
        Assert.Equal(HttpStatusCode.Redirect, shared.StatusCode);
        var page = await (await admin.GetAsync("/raporty/2026-11-16?link=udostepniono")).Content.ReadAsStringAsync();
        Assert.Contains("https://localhost/udostepnione/", page);
        Assert.Contains("Wycofaj udostępnienie", page);

        var deleted = await admin.SubmitActionAsync("/raporty/2026-11-16", "raporty/2026-11-16/usun");
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        await using var db = await app.DbAsync();
        Assert.Equal(0, await db.Reports.CountAsync());
    }

    [Fact]
    public async Task Too_many_entries_are_rejected_before_downloading_their_content()
    {
        await using var app = await StartAsync(postgres, "csirt-cez", LongAgo);
        for (var i = 0; i <= DailyReportService.MaxRetrospectiveInputs; i++)
            await app.AddItemAsync("csirt-cez", $"Wpis {i}", $"https://csirt.cez.gov.pl/pl/aktualnosci/wpis-{i}", content: null, published: Monday, baseline: true);

        var result = await Service(app).GenerateForAsync(Monday, Tuesday);

        Assert.Equal(ReportRunOutcome.Rejected, result.Outcome);
        Assert.Contains("krótszy okres", result.Error);
        Assert.Empty(app.Http.Requests);
    }

    [Fact]
    public async Task Regenerating_an_old_daily_report_sends_no_push()
    {
        var push = new Push.FakePushSender();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 5, 40, 0, TimeSpan.Zero));
        await using var app = await AppFactory.CreateAsync(postgres, time: time, services: s => s.AddSingleton<ZdrowieInfo.Server.Push.IPushSender>(push));
        await app.CreateUserAsync("szef", TestData.Password);
        await using (var db = await app.DbAsync())
        {
            db.PushSubscriptions.Add(new ZdrowieInfo.Server.Push.PushSubscriptionRecord { UserId = (await db.Users.FirstAsync()).Id, Endpoint = "https://push.example/a", P256dh = "p", Auth = "a", Origin = "https://localhost", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "Platforma Usług Inteligentnych (PUI) będzie niedostępna w nocy.");
        await Service(app).GenerateAsync(new DateOnly(2026, 11, 18));
        Assert.Single(push.Sent);

        time.SetUtcNow(Now); // dwa dni później
        var result = await Service(app).GenerateForAsync(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18), replace: true);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        Assert.Single(push.Sent);
    }
}
