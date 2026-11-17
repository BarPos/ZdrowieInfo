using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Reports;

public class DailyReportTests(PostgresFixture postgres)
{
    private static readonly DateOnly Day = new(2026, 11, 18);
    private const string PuiText = "18 listopada 2026 roku w godzinach 20:00–24:00 Platforma Usług Inteligentnych (PUI) będzie niedostępna z powodu prac technicznych.";

    private static async Task<(AppFactory App, ScriptedGenerator Generator)> StartAsync(PostgresFixture postgres, FakeTimeProvider? time = null)
    {
        var generator = new ScriptedGenerator();
        var app = await AppFactory.CreateAsync(postgres, time: time, services: s => s.AddSingleton<IReportGenerator>(generator));
        return (app, generator);
    }

    private static ReportDocument Valid(string itemId, string quote) => new()
    {
        Title = "Przerwa w PUI – 18.11.2026",
        Summary = "Dziś wieczorem przerwa w PUI.",
        Findings = [new FindingDto { Importance = "krytyczna", Category = "awaria", Headline = "Przerwa w PUI dziś 20–24", Body = "PUI niedostępna.", Action = "Uprzedzić rejestrację.", Citations = [new CitationDto { ItemId = itemId, Quote = quote }] }],
    };

    private static DailyReportService Service(AppFactory app) => app.Services.GetRequiredService<DailyReportService>();

    private static async Task<Report> ReportAsync(AppFactory app, DateOnly day)
    {
        await using var db = await app.DbAsync();
        return await db.Reports.AsNoTracking().Include(r => r.Findings).ThenInclude(f => f.Citations).SingleAsync(r => r.Date == day);
    }

    [Fact]
    public async Task No_new_entries_gives_empty_report_without_calling_ai()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("nfz-aktualnosci", "Stary wpis", "https://www.nfz.gov.pl/stary,1.html", "Treść", baseline: true);

        var result = await Service(app).GenerateAsync(Day);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        var report = await ReportAsync(app, Day);
        Assert.True(report.IsEmpty);
        Assert.Equal("Brak zmian – 18.11.2026", report.Title);
        Assert.Equal(23, report.SourcesState.Count);
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task New_entries_give_validated_report_and_are_marked_reported()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        var item = await app.AddItemAsync("ezdrowie-podmioty", "18 listopada przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText, Day.AddDays(-1));
        generator.Replies.Enqueue(r => ScriptedGenerator.Result(Valid(r.Items[0].Id, "Platforma Usług Inteligentnych (PUI) będzie niedostępna")));

        await Service(app).GenerateAsync(Day);

        var report = await ReportAsync(app, Day);
        Assert.Equal(ReportStatus.Ready, report.Status);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(Importance.Critical, finding.Importance);
        var citation = Assert.Single(finding.Citations);
        Assert.True(citation.Verified);
        Assert.Equal(item.Id, citation.SourceItemId);
        Assert.Equal("https://ezdrowie.gov.pl/portal/artykul/pui", citation.Url);
        Assert.Equal(1, report.AiUsage!.Calls);

        await using var db = await app.DbAsync();
        var saved = await db.SourceItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(report.Id, saved.ReportId);
        Assert.NotNull(saved.ReportedAt);

        Assert.Equal(ReportRunOutcome.AlreadyExists, (await Service(app).GenerateAsync(Day)).Outcome);
        Assert.Single(generator.Requests);
    }

    [Fact]
    public async Task Invalid_answer_is_retried_once_with_errors_then_accepted()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        generator.Replies.Enqueue(r => ScriptedGenerator.Result(Valid(r.Items[0].Id, "PUI będzie wyłączona przez cały dzień"))); // zmyślony cytat
        generator.Replies.Enqueue(r => ScriptedGenerator.Result(Valid(r.Items[0].Id, "w godzinach 20:00–24:00 Platforma Usług Inteligentnych")));

        await Service(app).GenerateAsync(Day);

        Assert.Equal(2, generator.Requests.Count);
        Assert.Contains(generator.Requests[1].PreviousErrors!, e => e.Contains("nie jest dosłownym fragmentem"));
        var report = await ReportAsync(app, Day);
        Assert.Equal(ReportStatus.Ready, report.Status);
        Assert.Equal(2, report.AiUsage!.Calls);
    }

    [Fact]
    public async Task Still_invalid_after_retry_is_saved_for_review()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        for (var i = 0; i < 2; i++)
            generator.Replies.Enqueue(r => ScriptedGenerator.Result(Valid(r.Items[0].Id, "PUI będzie wyłączona przez cały dzień")));

        await Service(app).GenerateAsync(Day);

        var report = await ReportAsync(app, Day);
        Assert.Equal(ReportStatus.NeedsReview, report.Status);
        Assert.NotEmpty(report.ValidationIssues);
        Assert.False(report.Findings.Single().Citations.Single().Verified);
    }

    [Fact]
    public async Task Transient_error_waits_for_retry_and_final_attempt_gives_list_without_analysis()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        var item = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        generator.Replies.Enqueue(_ => throw new ReportGenerationException("błąd serwera Claude API", transient: true));
        generator.Replies.Enqueue(_ => throw new ReportGenerationException("błąd serwera Claude API", transient: true));

        var first = await Service(app).GenerateAsync(Day, finalAttempt: false);
        Assert.Equal(ReportRunOutcome.RetryLater, first.Outcome);
        var attempts = await app.Services.GetRequiredService<ReportSettings>().GetAttemptsAsync();
        Assert.Equal(1, attempts!.Attempts);

        var final = await Service(app).GenerateAsync(Day, finalAttempt: true);
        Assert.Equal(ReportRunOutcome.Created, final.Outcome);
        var report = await ReportAsync(app, Day);
        Assert.Equal(ReportStatus.AiError, report.Status);
        Assert.Empty(report.Findings);
        Assert.Contains("błąd serwera", report.Error);
        Assert.Equal(2, report.GenerationAttempts);

        var browser = await app.SignedInBrowserAsync();
        var page = await browser.TextAsync("/raporty/2026-11-18");
        Assert.Contains("Wykryte wpisy (bez analizy)", page);
        Assert.Contains(item.Title, page);
    }

    [Fact]
    public async Task Missing_key_or_credits_gives_list_without_analysis_immediately()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        generator.Replies.Enqueue(_ => throw new ReportGenerationException("brak kredytów", transient: false));

        var result = await Service(app).GenerateAsync(Day, finalAttempt: false);

        Assert.Equal(ReportRunOutcome.Created, result.Outcome);
        Assert.Equal(ReportStatus.AiError, (await ReportAsync(app, Day)).Status);
    }

    [Fact]
    public async Task Replacing_report_reanalyses_its_entries()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        for (var i = 0; i < 2; i++)
            generator.Replies.Enqueue(r => ScriptedGenerator.Result(Valid(r.Items[0].Id, "Platforma Usług Inteligentnych (PUI) będzie niedostępna")));

        await Service(app).GenerateAsync(Day);
        await Service(app).GenerateAsync(Day, replace: true);

        Assert.Equal(2, generator.Requests.Count);
        Assert.Single(generator.Requests[1].Items);
        await using var db = await app.DbAsync();
        Assert.Equal(1, await db.Reports.CountAsync());
    }

    [Fact]
    public async Task Failed_replacement_keeps_previous_report()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        generator.Replies.Enqueue(r => ScriptedGenerator.Result(Valid(r.Items[0].Id, "Platforma Usług Inteligentnych (PUI) będzie niedostępna")));
        generator.Replies.Enqueue(_ => throw new ReportGenerationException("brak kredytów", transient: false));

        await Service(app).GenerateAsync(Day);
        var before = await ReportAsync(app, Day);
        var result = await Service(app).GenerateAsync(Day, replace: true);

        Assert.Equal(ReportRunOutcome.NotReplaced, result.Outcome);
        var after = await ReportAsync(app, Day);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(ReportStatus.Ready, after.Status);
        await using var db = await app.DbAsync();
        Assert.All(await db.SourceItems.Where(i => !i.IsBaseline).ToListAsync(), i => Assert.Equal(before.Id, i.ReportId));
    }

    [Fact]
    public async Task Empty_or_cut_answer_twice_gives_list_without_analysis()
    {
        var (app, generator) = await StartAsync(postgres);
        await using var _ = app;
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        for (var i = 0; i < 2; i++) generator.Replies.Enqueue(_ => ScriptedGenerator.Result(new ReportDocument()));

        await Service(app).GenerateAsync(Day);

        var report = await ReportAsync(app, Day);
        Assert.Equal(ReportStatus.AiError, report.Status);
        Assert.Contains("pusta albo ucięta", report.Error);
        Assert.Equal(2, generator.Requests.Count);
    }

    [Fact]
    public async Task Sample_generator_report_passes_validation()
    {
        await using var app = await AppFactory.CreateAsync(postgres); // generator przykładowy z konfiguracji testów
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", PuiText);
        await app.AddItemAsync("nfz-swiad", "Zmiana strony: Komunikat SWIAD", "https://www.nfz.gov.pl/swiad/", "+ Komunikat SWIAD (wersja 8.8.4)\n- Komunikat SWIAD (wersja 8.8.3)",
            kind: ZdrowieInfo.Server.Sources.SourceItemKind.PageChange);

        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(Day);

        var report = await ReportAsync(app, Day);
        Assert.Equal(ReportStatus.Ready, report.Status);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(Importance.High, report.Findings[0].Importance);
    }
}
