using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Scheduling;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Kiedy przygotować raport: od 5:30 – za poprzedni dzień (minął cały, więc nic z tego dnia nie dojdzie później; wpisy
/// z dzisiejszą datą trafią do jutrzejszego raportu). Do 6:00 czeka, aż sprawdzanie źródeł i ich ponowienia się zakończą;
/// błąd API → ponowienie co 10 minut, a od 6:20 raport awaryjny. Raport za ten dzień przygotowany jeszcze w jego trakcie
/// (ręcznie albo przed zmianą harmonogramu) jest raz zastępowany pełnym. Stan liczony z bazy, więc restart niczego nie gubi.
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
        var target = today.AddDays(-1);
        var clock = TimeOnly.FromDateTime(local);
        if (clock < o.ReportTime) return null;

        // Administrator usunął raport za ten dzień albo nie udało się go zastąpić – harmonogram go nie odtwarza.
        if (await settings.GetSkipDateAsync(ct) == target) return null;

        bool replace;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Pełny = przygotowany po zakończeniu swojego dnia. Wcześniejszy (w trakcie dnia) jest zastępowany.
            var dayEnd = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), WarsawClock.Zone));
            var existing = await db.Reports.WhereKey(ReportKey.Daily(target)).Select(r => (DateTimeOffset?)r.CreatedAt).SingleOrDefaultAsync(ct);
            if (existing >= dayEnd) return null;
            replace = existing is not null;

            if (clock < o.WaitForChecksUntil)
            {
                var plan = await sources.PlanAsync(ct);
                var retrying = await db.Sources.AnyAsync(s => s.IsActive && s.Status == SourceStatus.Retrying, ct);
                if (plan.Baseline.Count > 0 || plan.Scheduled.Count > 0 || retrying) return null;
            }
        }

        var attempts = await settings.GetAttemptsAsync(ct);
        var triedToday = attempts is not null && attempts.Date == target;
        if (triedToday && now < attempts!.LastAttemptAt + o.RetryInterval) return null;

        // Ostatnia próba: po 6:20 i po co najmniej jednej wcześniejszej nieudanej (późny start serwera dostaje jeszcze jedno ponowienie).
        var final = clock >= o.RetryUntil && triedToday;
        var result = await reports.GenerateAsync(target, replace, finalAttempt: final, ct: ct);
        // Nieudane zastąpienie zostawia raport z trakcie dnia; bez tego harmonogram próbowałby co minutę.
        if (result.Outcome == ReportRunOutcome.NotReplaced) await settings.SetSkipDateAsync(target, ct);
        return result;
    }
}
