using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Scheduling;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Kiedy przygotować raport dnia: od 5:30; do 6:00 czeka, aż sprawdzanie źródeł i ich ponowienia się zakończą;
/// błąd API → ponowienie co 10 minut, a od 6:20 raport awaryjny. Stan liczony z bazy, więc restart niczego nie gubi.
/// </summary>
public sealed class ReportScheduler(
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    DailyReportService reports,
    SourceScheduler sources,
    ReportSettings settings,
    TimeProvider time,
    IOptions<ReportsOptions> options)
{
    public async Task<ReportRunResult?> RunDueAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var local = WarsawClock.ToLocal(now);
        var today = DateOnly.FromDateTime(local);
        var clock = TimeOnly.FromDateTime(local);
        if (clock < o.ReportTime) return null;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Reports.WhereKey(ReportKey.Daily(today)).AnyAsync(ct)) return null;

            if (clock < o.WaitForChecksUntil)
            {
                var plan = await sources.PlanAsync(ct);
                var retrying = await db.Sources.AnyAsync(s => s.IsActive && s.Status == SourceStatus.Retrying, ct);
                if (plan.Baseline.Count > 0 || plan.Scheduled.Count > 0 || retrying) return null;
            }
        }

        // Administrator usunął dzisiejszy raport – harmonogram go nie odtwarza (można przygotować go ręcznie w panelu).
        if (await settings.GetSkipDateAsync(ct) == today) return null;

        var attempts = await settings.GetAttemptsAsync(ct);
        var triedToday = attempts is not null && attempts.Date == today;
        if (triedToday && now < attempts!.LastAttemptAt + o.RetryInterval) return null;

        // Ostatnia próba: po 6:20 i po co najmniej jednej wcześniejszej nieudanej (późny start serwera dostaje jeszcze jedno ponowienie).
        var final = clock >= o.RetryUntil && triedToday;
        return await reports.GenerateAsync(today, finalAttempt: final, ct: ct);
    }
}
