using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Checking;
using ZdrowieInfo.Server.Sources.Extraction;
using ZdrowieInfo.Server.Sources.Scheduling;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Sources;

public class SourceCheckingTests(PostgresFixture postgres)
{
    // Wtorek 17.11.2026, 6:00 w Warszawie.
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 5, 0, 0, TimeSpan.Zero);

    private const string NfzNews = "https://www.nfz.gov.pl/dla-swiadczeniodawcy/aktualnosci";
    private const string NfzDi = "https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/komunikaty-departamentu-informatyki/";
    private const string Swiad = "https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-swiad/";
    private const string CzprUrl = "https://www.nfz.gov.pl/aktualnosci/aktualnosci-centrali/komunikat-dla-swiadczeniodawcow-i-tworcow-oprogramowania-format-xml-czpr-w-wersji-1-0-2-aktualizacja-z-dnia-16-11-2026,9034.html";

    private async Task<(AppFactory App, FakeTimeProvider Time)> StartAsync()
    {
        var time = new FakeTimeProvider(Start);
        var app = await AppFactory.CreateAsync(postgres, time: time);
        return (app, time);
    }

    private static async Task<Source> SourceAsync(AppFactory app, string slug)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        return await db.Sources.AsNoTracking().SingleAsync(s => s.Slug == slug);
    }

    private static async Task<List<SourceItem>> ItemsAsync(AppFactory app, string slug)
    {
        var source = await SourceAsync(app, slug);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        return await db.SourceItems.AsNoTracking().Where(i => i.SourceId == source.Id).OrderBy(i => i.Id).ToListAsync();
    }

    private static Task<CheckRunResult> CheckAsync(AppFactory app, params Source[] sources) =>
        app.Services.GetRequiredService<SourceCheckService>().RunAsync(sources.Select(s => s.Id).ToList(), CheckRunKind.Manual);

    private static string WithoutFirstNews(string html)
    {
        var document = SourceExtractor.Parse(html, NfzNews);
        document.QuerySelector(".news-list .news")!.Remove();
        return document.DocumentElement.OuterHtml;
    }

    private const string Csirt = "https://csirt.cez.gov.pl/pl/wszystkie-aktualnosci";
    private const string NfzArchive = "https://www.nfz.gov.pl/aktualnosci/aktualnosci-centrali/";

    private static async Task<SourceSnapshot> LastSnapshotAsync(AppFactory app, int sourceId)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        return await db.SourceSnapshots.AsNoTracking().Where(s => s.SourceId == sourceId).OrderByDescending(s => s.Id).FirstAsync();
    }

    /// <summary>Stan bazowy 1.11, sprawdzenie 17.11 – realny odstęp, żeby granica daty nie zatrzymała przechodzenia po stronach.</summary>
    private async Task<(AppFactory App, FakeTimeProvider Time)> StartOnNovemberFirstAsync()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero));
        return (await AppFactory.CreateAsync(postgres, time: time), time);
    }

    private static string WithOtherUrls(string html) => html.Replace("/pl/aktualnosci/", "/pl/archiwum-testowe/");

    [Fact]
    public async Task Many_new_entries_are_collected_from_following_pages_until_a_known_one()
    {
        var (app, time) = await StartOnNovemberFirstAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "csirt-cez");
        var page1 = Fixtures.Read("csirt-cez", "a.html");
        var page2 = Fixtures.Read("csirt-cez", "page2.html");

        // Stan bazowy = to, co dziś jest na 2. stronie; od tego czasu doszła cała pierwsza strona.
        app.Http.Serve(Csirt, page2);
        await CheckAsync(app, source);
        time.Advance(TimeSpan.FromDays(16));

        app.Http.Serve(Csirt, page1);
        app.Http.Serve(Csirt + "?page=1", page2);
        var result = await CheckAsync(app, source);

        // Nowe = wpisy 1. strony, których nie było na 2. (CSIRT powtarza najnowszy, wyróżniony artykuł na każdej stronie).
        var known = SourceExtractor.ExtractList(page2, Csirt, source.Selectors).Items.Select(i => i.Key).ToHashSet();
        var expected = SourceExtractor.ExtractList(page1, Csirt, source.Selectors).Items.Where(i => !known.Contains(i.Key)).ToList();
        Assert.Equal(5, expected.Count);
        Assert.Equal(expected.Count, result.NewItems);
        Assert.Equal(1, app.Http.CountRequests(Csirt + "?page=1"));
        Assert.Equal(0, app.Http.CountRequests(Csirt + "?page=2")); // na 2. stronie ostatni wpis był znany – koniec
        var snapshot = await LastSnapshotAsync(app, source.Id);
        Assert.Equal(2, snapshot.PagesFetched);
        Assert.Null(snapshot.Warning);
    }

    [Fact]
    public async Task Known_last_entry_means_no_further_pages()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "csirt-cez");
        var page1 = Fixtures.Read("csirt-cez", "a.html");
        var document = SourceExtractor.Parse(page1, Csirt);
        document.QuerySelector(".teaser")!.Remove();

        app.Http.Serve(Csirt, document.DocumentElement.OuterHtml);
        await CheckAsync(app, source);
        app.Http.Serve(Csirt, page1);
        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.NewItems);
        Assert.Equal(0, app.Http.CountRequests(Csirt + "?page=1"));
    }

    [Fact]
    public async Task Removed_entry_does_not_flood_report_with_older_pages()
    {
        var (app, time) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "csirt-cez");
        var page1 = Fixtures.Read("csirt-cez", "a.html");
        app.Http.Serve(Csirt, page1);
        await CheckAsync(app, source);
        time.Advance(TimeSpan.FromDays(1));

        // Strona usunęła jeden wpis z 1. strony, więc na koniec wsunął się starszy wpis z 2. strony.
        var document = SourceExtractor.Parse(page1, Csirt);
        var teasers = document.QuerySelectorAll(".teaser");
        teasers[2].Remove();
        var slid = SourceExtractor.Parse(Fixtures.Read("csirt-cez", "page2.html"), Csirt).QuerySelectorAll(".teaser")[1].OuterHtml;
        teasers[^1].Insert(AngleSharp.Dom.AdjacentPosition.AfterEnd, slid);
        app.Http.Serve(Csirt, document.DocumentElement.OuterHtml);
        app.Http.Serve(Csirt + "?page=1", Fixtures.Read("csirt-cez", "page2.html"));

        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.NewItems); // tylko ten jeden wpis, nie kolejne strony archiwum
        Assert.Equal(0, app.Http.CountRequests(Csirt + "?page=1"));
    }

    [Fact]
    public async Task Failed_next_page_fails_the_check_so_retry_walks_the_list_again()
    {
        var (app, time) = await StartOnNovemberFirstAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "csirt-cez");
        app.Http.Serve(Csirt, WithOtherUrls(Fixtures.Read("csirt-cez", "a.html")));
        await CheckAsync(app, source); // stan bazowy z wpisami, których nie ma na obecnych stronach
        time.Advance(TimeSpan.FromDays(16));
        var before = (await ItemsAsync(app, "csirt-cez")).Count;

        app.Http.Serve(Csirt, Fixtures.Read("csirt-cez", "a.html"));
        app.Http.Serve(Csirt + "?page=1", Fixtures.Read("csirt-cez", "page2.html"));
        // ?page=2 nie jest dostępna (404)
        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.SourcesFailed);
        Assert.Equal(before, (await ItemsAsync(app, "csirt-cez")).Count); // nic nie zapisane – brak trwałej luki
        var s = await SourceAsync(app, "csirt-cez");
        Assert.Equal(SourceStatus.Retrying, s.Status);
        Assert.Contains("3. strony", s.StatusMessage);
    }

    [Fact]
    public async Task List_without_further_pages_gives_warning_not_silence()
    {
        var (app, time) = await StartOnNovemberFirstAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "csirt-cez");
        app.Http.Serve(Csirt, WithOtherUrls(Fixtures.Read("csirt-cez", "a.html")));
        await CheckAsync(app, source);
        time.Advance(TimeSpan.FromDays(16));

        var lastPage = SourceExtractor.Parse(Fixtures.Read("csirt-cez", "page2.html"), Csirt + "?page=1");
        lastPage.QuerySelector(".pager")!.Remove();
        app.Http.Serve(Csirt, Fixtures.Read("csirt-cez", "a.html"));
        app.Http.Serve(Csirt + "?page=1", lastPage.DocumentElement.OuterHtml);
        var result = await CheckAsync(app, source);

        Assert.Equal(0, result.SourcesFailed);
        Assert.True(result.NewItems > 6);
        var snapshot = await LastSnapshotAsync(app, source.Id);
        Assert.Equal(2, snapshot.PagesFetched);
        Assert.Contains("pominięta", snapshot.Warning);
        var s = await SourceAsync(app, "csirt-cez");
        Assert.Equal(SourceStatus.Ok, s.Status);
        Assert.Contains("pominięta", s.StatusMessage);
    }

    [Fact]
    public async Task Overflowing_nfz_block_takes_recent_entries_from_archive()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero));
        await using var app = await AppFactory.CreateAsync(postgres, time: time);
        var source = await SourceAsync(app, "nfz-di-komunikaty");
        // Dzień, w którym w bloku pojawiło się 10 nowych komunikatów (wszystkie z datą 17.11).
        var block = System.Text.RegularExpressions.Regex.Replace(Fixtures.Read("nfz-di-komunikaty", "a.html"),
            @"<div class=""date"">\s*[\d.]+\s*</div>", @"<div class=""date"">17.11.2026</div>");
        var archive = Fixtures.Read("nfz-archiwum", "a.html");

        app.Http.Serve(NfzDi, block.Replace("/aktualnosci/aktualnosci-centrali/", "/archiwum-testowe/"));
        await CheckAsync(app, source); // stan bazowy 16.11 bez żadnego z dzisiejszych wpisów
        time.Advance(TimeSpan.FromDays(1));

        app.Http.Serve(NfzDi, block);
        app.Http.Serve(NfzArchive, archive);
        var result = await CheckAsync(app, source);

        // Z archiwum: wpisy od 15.11 (ostatnie sprawdzenie 16.11 minus dzień zapasu), łącznie z karuzelą „Ważne!”.
        var cutoff = new DateOnly(2026, 11, 15);
        var blockItems = SourceExtractor.ExtractList(block, NfzDi, source.Selectors).Items;
        var archiveItems = SourceExtractor.ExtractList(archive, NfzArchive, source.Selectors).Items;
        var fromArchive = archiveItems.Where(i => (i.PublishedOn ?? cutoff) >= cutoff && blockItems.All(b => b.Key != i.Key)).ToList();
        Assert.Contains(fromArchive, i => i.Url.EndsWith(",9059.html")); // „Ważne!” z karuzeli

        Assert.Equal(blockItems.Count + fromArchive.Count, result.NewItems);
        Assert.Equal(1, app.Http.CountRequests(NfzArchive));
        Assert.Equal(0, app.Http.CountRequests(NfzArchive + "2,strona.html")); // archiwum sięga już przed 15.11
        var snapshot = await LastSnapshotAsync(app, source.Id);
        Assert.Equal(2, snapshot.PagesFetched);
        Assert.Null(snapshot.Warning);
    }

    [Fact]
    public async Task Ezdrowie_json_list_detects_new_article_and_fetches_html_detail()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "ezdrowie-podmioty");
        var json = Fixtures.Read("ezdrowie-podmioty", "a.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["articleDTOList"]!.AsArray().RemoveAt(0);

        app.Http.Serve(source.Url, node.ToJsonString());
        await CheckAsync(app, source);
        app.Http.Serve(source.Url, json);
        const string pui = "https://ezdrowie.gov.pl/portal/artykul/18-listopada-przerwa-w-dostepie-do-platformy-uslug-inteligentnych";
        app.Http.Serve(pui, Fixtures.Read("ezdrowie-podmioty", "detail.html"));
        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.NewItems);
        var item = Assert.Single(await ItemsAsync(app, "ezdrowie-podmioty"), i => !i.IsBaseline);
        Assert.Equal(pui, item.Url);
        Assert.Contains("Platforma Usług Inteligentnych (PUI) będzie niedostępna", item.Content);
        Assert.Empty(app.Http.Unexpected);
    }

    [Fact]
    public async Task Default_sources_are_seeded_once()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        Assert.Equal(0, await app.Services.GetRequiredService<SourceSeeder>().SeedAsync());
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        Assert.Equal(23, await db.Sources.CountAsync());
    }

    [Fact]
    public async Task Baseline_saves_entries_without_reporting_then_new_entry_is_detected_with_content()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-aktualnosci");
        var html = Fixtures.Read("nfz-aktualnosci", "a.html");

        app.Http.Serve(NfzNews, WithoutFirstNews(html));
        var baseline = await CheckAsync(app, source);
        Assert.Equal(0, baseline.NewItems);

        var items = await ItemsAsync(app, "nfz-aktualnosci");
        Assert.Equal(9, items.Count);
        Assert.All(items, i => Assert.True(i.IsBaseline));
        Assert.NotNull((await SourceAsync(app, "nfz-aktualnosci")).BaselineAt);
        Assert.DoesNotContain(app.Http.Requests, r => r.Contains("/aktualnosci-centrali/")); // stan bazowy nie pobiera artykułów

        app.Http.Serve(NfzNews, html);
        app.Http.Serve(CzprUrl, Fixtures.Read("nfz-aktualnosci", "detail.html"));
        var second = await CheckAsync(app, source);

        Assert.Equal(1, second.NewItems);
        var added = Assert.Single(await ItemsAsync(app, "nfz-aktualnosci"), i => !i.IsBaseline);
        Assert.Contains("CZPR", added.Title);
        Assert.Equal(new DateOnly(2026, 11, 16), added.PublishedOn);
        Assert.Contains("zaplanowanymi pracami serwisowymi", added.Content);
        Assert.NotNull(added.DetailFetchedAt);
        Assert.Empty(app.Http.Unexpected);
    }

    [Fact]
    public async Task Same_page_again_gives_no_new_entries()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var list = await SourceAsync(app, "nfz-aktualnosci");
        var page = await SourceAsync(app, "nfz-swiad");
        app.Http.Serve(NfzNews, Fixtures.Read("nfz-aktualnosci", "a.html"));
        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"));

        await CheckAsync(app, list, page);
        var again = await CheckAsync(app, list, page);

        Assert.Equal(0, again.NewItems);
        Assert.Equal(0, again.SourcesFailed);
        Assert.Empty(app.Http.Unexpected);
    }

    [Fact]
    public async Task Bumped_entry_with_new_title_is_reported_as_update()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-aktualnosci");
        var html = Fixtures.Read("nfz-aktualnosci", "a.html");
        const string oldTitle = "Prace serwisowe w dniach 12 i 13 listopada 2026r. w systemie DILO";
        const string dilo = "https://www.nfz.gov.pl/aktualnosci/aktualnosci-centrali/prace-serwisowe-w-dniach-12-i-13-listopada-2026r-w-systemie-dilo,9051.html";

        app.Http.Serve(NfzNews, html);
        await CheckAsync(app, source);

        app.Http.Serve(NfzNews, html.Replace(oldTitle, oldTitle + " [AKTUALIZACJA]"));
        app.Http.Serve(dilo, Fixtures.Read("nfz-aktualnosci", "detail.html"));
        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.NewItems);
        var updated = (await ItemsAsync(app, "nfz-aktualnosci")).Single(i => i.UpdateCount == 1);
        Assert.Equal(oldTitle, updated.PreviousTitle);
        Assert.False(updated.IsBaseline);
        Assert.NotNull(updated.Content);
    }

    [Fact]
    public async Task Whole_page_change_produces_diff()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-swiad");
        var html = Fixtures.Read("nfz-swiad", "a.html");

        app.Http.Serve(Swiad, html);
        await CheckAsync(app, source);
        Assert.Empty(await ItemsAsync(app, "nfz-swiad"));

        app.Http.Serve(Swiad, html.Replace("Komunikat SWIAD (wersja 8.8.3)", "Komunikat SWIAD (wersja 8.8.4)"));
        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.NewItems);
        var change = Assert.Single(await ItemsAsync(app, "nfz-swiad"));
        Assert.Equal(SourceItemKind.PageChange, change.Kind);
        Assert.Contains("+ Komunikat SWIAD (wersja 8.8.4)", change.Content);
        Assert.Contains("- Komunikat SWIAD (wersja 8.8.3)", change.Content);
        Assert.False(change.IsBaseline);
    }

    [Fact]
    public async Task Article_listed_by_two_sources_is_downloaded_once()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var news = await SourceAsync(app, "nfz-aktualnosci");
        var di = await SourceAsync(app, "nfz-di-komunikaty");
        var newsHtml = Fixtures.Read("nfz-aktualnosci", "a.html");
        var diHtml = Fixtures.Read("nfz-di-komunikaty", "a.html");

        app.Http.Serve(NfzNews, WithoutFirstNews(newsHtml));
        app.Http.Serve(NfzDi, WithoutFirstNews(diHtml));
        await CheckAsync(app, news, di);

        app.Http.Serve(NfzNews, newsHtml);
        app.Http.Serve(NfzDi, diHtml);
        app.Http.Serve(CzprUrl, Fixtures.Read("nfz-aktualnosci", "detail.html"));
        var result = await CheckAsync(app, news, di);

        Assert.Equal(2, result.NewItems);
        Assert.Equal(1, app.Http.CountRequests(CzprUrl));
        Assert.NotNull(Assert.Single(await ItemsAsync(app, "nfz-di-komunikaty"), i => !i.IsBaseline).Content);
    }

    [Fact]
    public async Task Not_modified_response_uses_validators_only_after_baseline()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-swiad");
        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"), etag: "\"v1\"");

        await CheckAsync(app, source);
        var result = await CheckAsync(app, source);

        Assert.Equal(0, result.SourcesFailed);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        var snapshots = await db.SourceSnapshots.Where(s => s.SourceId == source.Id).OrderBy(s => s.Id).ToListAsync();
        Assert.False(snapshots[0].NotModified);
        Assert.True(snapshots[1].NotModified);
        Assert.True(snapshots[1].Succeeded);
    }

    [Fact]
    public async Task Three_failures_open_one_incident_hourly_rechecks_and_success_closes_it()
    {
        var (app, time) = await StartAsync();
        await using var _app = app;
        var scheduler = app.Services.GetRequiredService<SourceScheduler>();
        var source = await SourceAsync(app, "nfz-swiad");

        // Stan bazowy, żeby kolejne sprawdzenie było zwykłym sprawdzeniem.
        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"));
        await CheckAsync(app, source);

        app.Http.Fail(Swiad, HttpStatusCode.NotFound);
        var firstFailure = time.GetUtcNow();
        await CheckAsync(app, source);
        var s = await SourceAsync(app, "nfz-swiad");
        Assert.Equal(SourceStatus.Retrying, s.Status);
        Assert.Equal(firstFailure.AddMinutes(5), s.NextRetryAt);
        Assert.Empty(await IncidentsAsync(app, source.Id));

        Assert.Empty((await scheduler.PlanAsync()).Retry); // jeszcze nie czas
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal([source.Id], (await scheduler.PlanAsync()).Retry);
        await scheduler.RunDueAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync();

        var incident = Assert.Single(await IncidentsAsync(app, source.Id));
        Assert.Equal(3, incident.Attempts);
        Assert.Equal(firstFailure, incident.StartedAt);
        Assert.Equal(CheckErrorType.Http, incident.ErrorType);
        Assert.Null(incident.EndedAt);
        s = await SourceAsync(app, "nfz-swiad");
        Assert.Equal(SourceStatus.NeedsAttention, s.Status);
        Assert.Equal(time.GetUtcNow().AddHours(1), s.NextRetryAt);

        time.Advance(TimeSpan.FromHours(1));
        await scheduler.RunDueAsync();
        incident = Assert.Single(await IncidentsAsync(app, source.Id));
        Assert.Equal(4, incident.Attempts);

        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"));
        time.Advance(TimeSpan.FromHours(1));
        await scheduler.RunDueAsync();

        incident = Assert.Single(await IncidentsAsync(app, source.Id));
        Assert.Equal(time.GetUtcNow(), incident.EndedAt);
        s = await SourceAsync(app, "nfz-swiad");
        Assert.Equal(SourceStatus.Ok, s.Status);
        Assert.Null(s.NextRetryAt);
        Assert.Equal(0, s.ConsecutiveFailures);
    }

    [Fact]
    public async Task Single_failure_fixed_by_retry_is_not_an_incident()
    {
        var (app, time) = await StartAsync();
        await using var _app = app;
        var scheduler = app.Services.GetRequiredService<SourceScheduler>();
        var source = await SourceAsync(app, "nfz-swiad");

        app.Http.Fail(Swiad, HttpStatusCode.NotFound);
        await CheckAsync(app, source);
        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"));
        time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync();

        Assert.Empty(await IncidentsAsync(app, source.Id));
        Assert.Equal(SourceStatus.Ok, (await SourceAsync(app, "nfz-swiad")).Status);
        Assert.NotNull((await SourceAsync(app, "nfz-swiad")).BaselineAt); // stan bazowy dopiero przy pierwszym sukcesie
    }

    [Fact]
    public async Task Selector_finding_nothing_is_a_failure()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-aktualnosci");
        app.Http.Serve(NfzNews, "<html><body><p>Nowy układ strony</p></body></html>");

        var result = await CheckAsync(app, source);

        Assert.Equal(1, result.SourcesFailed);
        var s = await SourceAsync(app, "nfz-aktualnosci");
        Assert.Equal(SourceStatus.Retrying, s.Status);
        Assert.Contains("nie znalazł", s.StatusMessage);
        Assert.Null(s.BaselineAt);
    }

    [Fact]
    public async Task Robots_txt_disallow_blocks_fetching()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-swiad");
        app.Http.Serve("https://www.nfz.gov.pl/robots.txt", "User-agent: *\nDisallow: /dla-swiadczeniodawcy/");
        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"));

        await CheckAsync(app, source);

        Assert.Equal(0, app.Http.CountRequests(Swiad));
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SourceId == source.Id);
        Assert.Equal(CheckErrorType.RobotsDisallowed, snapshot.ErrorType);
    }

    [Fact]
    public async Task Scheduler_baselines_new_sources_and_catches_up_missed_groups()
    {
        var (app, time) = await StartAsync();
        await using var _app = app;
        var scheduler = app.Services.GetRequiredService<SourceScheduler>();

        var plan = await scheduler.PlanAsync();
        Assert.Equal(23, plan.Baseline.Count);
        Assert.Empty(plan.Scheduled);

        // Wszystkie źródła mają stan bazowy z 14.10; 17.11 o 6:00 należne są dzienne, tygodniowe (pon. 16.11),
        // miesięczne (1.11) i dwumiesięczne (1.11 – listopad jest nieparzysty).
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync())
        {
            var past = new DateTimeOffset(2026, 10, 14, 4, 0, 0, TimeSpan.Zero);
            await db.Sources.ExecuteUpdateAsync(u => u.SetProperty(s => s.BaselineAt, past).SetProperty(s => s.LastSuccessAt, past));
        }
        plan = await scheduler.PlanAsync();
        Assert.Empty(plan.Baseline);
        Assert.Equal(23, plan.Scheduled.Count);

        // Sprawdzone dziś o 5:10: nic nie jest należne do jutra 5:00.
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync())
            await db.Sources.ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSuccessAt, Start.AddMinutes(-50)));
        Assert.True((await scheduler.PlanAsync()).IsEmpty);

        time.Advance(TimeSpan.FromHours(23)); // 18.11, 5:00 – tylko dzienne
        plan = await scheduler.PlanAsync();
        Assert.Equal(DefaultSource.Load().Count(s => s.Group == SourceGroup.Daily), plan.Scheduled.Count);
    }

    [Fact]
    public async Task Editing_selectors_resets_baseline_and_validators_but_renaming_does_not()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var source = await SourceAsync(app, "nfz-swiad");
        app.Http.Serve(Swiad, Fixtures.Read("nfz-swiad", "a.html"), etag: "\"v1\"");
        await CheckAsync(app, source);
        var admin = app.Services.GetRequiredService<SourceAdminService>();
        source = await SourceAsync(app, "nfz-swiad");
        Assert.NotNull(source.ETag);

        var rename = new SourceAdminService.SourceEdit("Komunikat SWIAD (NFZ)", source.Url, source.Group, source.Method, source.FetchMode, true, null, source.Selectors);
        Assert.False(await admin.UpdateAsync(source.Id, rename));
        Assert.NotNull((await SourceAsync(app, "nfz-swiad")).BaselineAt);

        var selectors = source.Selectors with { Remove = [.. source.Selectors.Remove, ".files"] };
        Assert.True(await admin.UpdateAsync(source.Id, rename with { Selectors = selectors }));
        var edited = await SourceAsync(app, "nfz-swiad");
        Assert.Null(edited.BaselineAt);
        Assert.Null(edited.ETag);
        Assert.Null(edited.PageText);

        // Następne sprawdzenie to nowy stan bazowy – bez „zmiany strony” w wpisach.
        await CheckAsync(app, edited);
        Assert.Empty(await ItemsAsync(app, "nfz-swiad"));
        Assert.NotNull((await SourceAsync(app, "nfz-swiad")).BaselineAt);
    }

    private static async Task<List<SourceIncident>> IncidentsAsync(AppFactory app, int sourceId)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        return await db.SourceIncidents.AsNoTracking().Where(i => i.SourceId == sourceId).ToListAsync();
    }
}
