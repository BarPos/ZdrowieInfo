using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Reports;

public class ReportSchedulerTests(PostgresFixture postgres)
{
    // 18.11.2026, czas warszawski = UTC+1.
    private static DateTimeOffset Warsaw(int h, int m) => new(2026, 11, 18, h - 1, m, 0, TimeSpan.Zero);

    private static async Task MarkSourcesCheckedAsync(AppFactory app)
    {
        await using var db = await app.DbAsync();
        var now = app.Time!.GetUtcNow();
        await db.Sources.ExecuteUpdateAsync(u => u.SetProperty(s => s.BaselineAt, now).SetProperty(s => s.LastSuccessAt, now));
    }

    [Fact]
    public async Task Report_waits_for_530_and_for_source_checks_until_600()
    {
        var time = new FakeTimeProvider(Warsaw(5, 20));
        await using var app = await AppFactory.CreateAsync(postgres, time: time);
        var scheduler = app.Services.GetRequiredService<ReportScheduler>();

        Assert.Null(await scheduler.RunDueAsync()); // przed 5:30

        time.SetUtcNow(Warsaw(5, 35));
        Assert.Null(await scheduler.RunDueAsync()); // źródła jeszcze niesprawdzone (stan bazowy należny)

        time.SetUtcNow(Warsaw(6, 1));
        var result = await scheduler.RunDueAsync(); // po 6:00 nie czeka dłużej
        Assert.Equal(ReportRunOutcome.Created, result!.Outcome);
        Assert.Null(await scheduler.RunDueAsync()); // jeden raport dziennie
    }

    [Fact]
    public async Task Report_starts_at_530_when_checks_are_done()
    {
        var time = new FakeTimeProvider(Warsaw(5, 31));
        await using var app = await AppFactory.CreateAsync(postgres, time: time);
        await MarkSourcesCheckedAsync(app);

        var result = await app.Services.GetRequiredService<ReportScheduler>().RunDueAsync();
        Assert.Equal(ReportRunOutcome.Created, result!.Outcome);
    }

    [Fact]
    public async Task Api_errors_are_retried_every_10_minutes_then_fallback_after_620()
    {
        var time = new FakeTimeProvider(Warsaw(5, 31));
        var generator = new ScriptedGenerator();
        for (var i = 0; i < 5; i++) generator.Replies.Enqueue(_ => throw new ReportGenerationException("503", transient: true));
        await using var app = await AppFactory.CreateAsync(postgres, time: time, services: s => s.AddSingleton<IReportGenerator>(generator));
        await MarkSourcesCheckedAsync(app);
        await app.AddItemAsync("csirt-cez", "Podatność", "https://csirt.cez.gov.pl/pl/aktualnosci/x", "Krytyczna podatność w produkcie.");
        var scheduler = app.Services.GetRequiredService<ReportScheduler>();

        Assert.Equal(ReportRunOutcome.RetryLater, (await scheduler.RunDueAsync())!.Outcome); // 5:31
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await scheduler.RunDueAsync()); // 5:36 – za wcześnie
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ReportRunOutcome.RetryLater, (await scheduler.RunDueAsync())!.Outcome); // 5:41
        time.SetUtcNow(Warsaw(6, 21));
        Assert.Equal(ReportRunOutcome.Created, (await scheduler.RunDueAsync())!.Outcome); // po 6:20 – raport awaryjny

        await using var db = await app.DbAsync();
        var report = await db.Reports.SingleAsync();
        Assert.Equal(ReportStatus.AiError, report.Status);
        Assert.Equal(3, generator.Requests.Count);
    }
}
