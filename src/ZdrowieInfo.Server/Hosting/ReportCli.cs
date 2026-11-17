using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Hosting;

internal static class ReportCli
{
    /// <summary>--generate-report [data] [--replace]</summary>
    public static async Task<int> GenerateAsync(IServiceProvider services, string? date, bool replace)
    {
        var day = date is null ? services.GetRequiredService<WarsawClock>().Today : DateOnly.ParseExact(date, "yyyy-MM-dd");
        var result = await services.GetRequiredService<DailyReportService>().GenerateAsync(day, replace, finalAttempt: true);
        switch (result.Outcome)
        {
            case ReportRunOutcome.AlreadyExists:
                Console.WriteLine($"Raport na {day:dd.MM.yyyy} już istnieje (id {result.ReportId}). Użyj --replace, żeby go zastąpić.");
                return 1;
            case ReportRunOutcome.Created:
                await using (var db = await services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync())
                {
                    var report = await db.Reports.AsNoTracking().Include(r => r.Findings).SingleAsync(r => r.Id == result.ReportId);
                    Console.WriteLine($"Raport {day:dd.MM.yyyy}: {report.Title} ({report.Status}, {report.Findings.Count} punktów).");
                    foreach (var issue in report.ValidationIssues) Console.WriteLine($"  uwaga: {issue}");
                    if (report.Error is not null) Console.WriteLine($"  błąd: {report.Error}");
                }
                return 0;
            case ReportRunOutcome.NotReplaced:
                Console.WriteLine($"Nie udało się przygotować nowego raportu ({result.Error}) – poprzedni raport zostaje bez zmian.");
                return 1;
            default:
                Console.WriteLine($"Nie udało się: {result.Error}");
                return 1;
        }
    }

    /// <summary>
    /// --mark-reported-before: przy włączaniu raportów wpisy wykryte wcześniej (np. w tygodniu obserwacji etapu 2)
    /// nie trafiają hurtem do pierwszego raportu (R9).
    /// </summary>
    public static async Task<int> MarkReportedBeforeAsync(IServiceProvider services, DateOnly before)
    {
        var cutoffLocal = before.ToDateTime(TimeOnly.MinValue);
        var cutoff = new DateTimeOffset(cutoffLocal, WarsawClock.Zone.GetUtcOffset(cutoffLocal));
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await using var db = await services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        var count = await db.SourceItems
            .Where(i => i.ReportedAt == null && !i.IsBaseline && i.LastChangedAt < cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReportedAt, now));
        Console.WriteLine($"Oznaczono jako zaraportowane: {count} wpisów wykrytych przed {before:dd.MM.yyyy}.");
        return 0;
    }
}
