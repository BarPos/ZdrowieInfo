using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Reports;

/// <summary>Sekcja „Stan źródeł”: które sprawdzono, które z błędem lub ostrzeżeniem, incydenty i przywrócenia od poprzedniego raportu.</summary>
public static class SourcesStateBuilder
{
    public static async Task<List<SourceStateEntry>> BuildAsync(ZdrowieInfoDbContext db, DateTimeOffset since, CancellationToken ct)
    {
        var sources = await db.Sources.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).ToListAsync(ct);
        var snapshots = await db.SourceSnapshots.AsNoTracking().Where(s => s.FetchedAt >= since).ToListAsync(ct);
        var incidents = await db.SourceIncidents.AsNoTracking()
            .Where(i => i.EndedAt == null || i.EndedAt >= since).ToListAsync(ct);

        return sources.Select(source =>
        {
            var mine = snapshots.Where(s => s.SourceId == source.Id).OrderBy(s => s.FetchedAt).ToList();
            var last = mine.LastOrDefault();
            var open = incidents.FirstOrDefault(i => i.SourceId == source.Id && i.EndedAt == null);
            var recoveries = incidents
                .Where(i => i.SourceId == source.Id && i.EndedAt is not null)
                .Select(i => $"Dostęp przywrócony {Common.WarsawClock.ToLocal(i.EndedAt!.Value):dd.MM HH:mm} po przerwie od {Common.WarsawClock.ToLocal(i.StartedAt):dd.MM HH:mm}.")
                .ToList();
            var warning = mine.Where(s => s.Warning is not null).Select(s => s.Warning).LastOrDefault();

            string? problem = null;
            if (open is not null) problem = $"Brak dostępu od {Common.WarsawClock.ToLocal(open.StartedAt):dd.MM HH:mm}: {open.Details}";
            else if (last is { Succeeded: false }) problem = last.ErrorMessage;

            return new SourceStateEntry(source.Id, source.Name, source.Url, mine.Count > 0, problem is null, problem, warning,
                source.LastSuccessAt, open is not null, recoveries);
        }).ToList();
    }
}
