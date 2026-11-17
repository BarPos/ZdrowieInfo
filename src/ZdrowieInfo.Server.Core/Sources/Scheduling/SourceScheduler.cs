using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources.Checking;

namespace ZdrowieInfo.Server.Sources.Scheduling;

/// <summary>
/// Co jest teraz do zrobienia – liczone z bazy (nie z timerów w pamięci), więc restart serwera niczego nie gubi.
/// </summary>
public sealed class SourceScheduler(
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    SourceCheckService checker,
    TimeProvider time,
    IOptions<SourcesOptions> options,
    ILogger<SourceScheduler> logger)
{
    public sealed record DuePlan(IReadOnlyList<int> Baseline, IReadOnlyList<int> Scheduled, IReadOnlyList<int> Retry)
    {
        public bool IsEmpty => Baseline.Count == 0 && Scheduled.Count == 0 && Retry.Count == 0;
    }

    public async Task<DuePlan> PlanAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var start = options.Value.DailyCheckTime;
        var afterStart = TimeOnly.FromDateTime(WarsawClock.ToLocal(now)) >= start;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.IsActive && s.FetchMode != FetchMode.Manual)
            .Select(s => new { s.Id, s.Group, s.BaselineAt, s.LastSuccessAt, s.NextRetryAt })
            .ToListAsync(ct);

        var baseline = sources.Where(s => s.BaselineAt is null && s.NextRetryAt is null).Select(s => s.Id).ToList();
        var retry = sources.Where(s => s.NextRetryAt is not null && s.NextRetryAt <= now).Select(s => s.Id).ToList();
        var scheduled = afterStart
            ? sources.Where(s => s.BaselineAt is not null && s.NextRetryAt is null
                                 && (s.LastSuccessAt ?? DateTimeOffset.MinValue) < SourceSchedule.LastSlotUtc(s.Group, now, start))
                .Select(s => s.Id).ToList()
            : [];
        return new DuePlan(baseline, scheduled, retry);
    }

    /// <summary>Wykonuje wszystko, co należne. Zwraca liczbę uruchomień.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var plan = await PlanAsync(ct);
        var runs = 0;
        if (plan.Baseline.Count > 0) { await checker.RunAsync(plan.Baseline, CheckRunKind.Baseline, ct); runs++; }
        if (plan.Scheduled.Count > 0) { await checker.RunAsync(plan.Scheduled, CheckRunKind.Scheduled, ct); runs++; }
        if (plan.Retry.Count > 0) { await checker.RunAsync(plan.Retry, CheckRunKind.Retry, ct); runs++; }
        return runs;
    }

    /// <summary>Treść wpisów starszych niż 12 miesięcy jest usuwana (cytaty w raportach mają własną kopię fragmentu).</summary>
    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - options.Value.ItemContentRetention;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cleared = await db.SourceItems
            .Where(i => i.FirstSeenAt < cutoff && i.Content != null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Content, (string?)null), ct);
        if (cleared > 0) logger.LogInformation("Usunięto treść {Count} wpisów starszych niż {Days} dni.", cleared, options.Value.ItemContentRetention.TotalDays);
        return cleared;
    }
}
