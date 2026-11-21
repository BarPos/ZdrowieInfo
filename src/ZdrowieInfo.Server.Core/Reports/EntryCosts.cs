using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Reports;

/// <param name="InputTokens">Tokeny wejścia razem z odczytem i zapisem cache.</param>
/// <param name="CostUsd">Null – raport z modelem spoza cennika (tokeny są, ceny nie).</param>
/// <param name="Reports">W ilu raportach (z zapisanym zużyciem) był wpis.</param>
public sealed record EntryCost(long InputTokens, long OutputTokens, decimal? CostUsd, int Reports, double WebSearches);

/// <summary>
/// Szacunkowy koszt AI wpisu. Zużycie jest zapisywane na raport (jedno wywołanie obejmuje wszystkie wpisy), więc wpis
/// dostaje udział proporcjonalny do długości swojego tekstu w wejściu (tytuł + treść do limitu wysyłanego modelowi).
/// Sprawdzanie podatności (tokeny i wyszukiwania) dzieli się tylko między sprawdzane wpisy. Wpis z kilku raportów
/// (dzienny, wstecz, okresowy) sumuje udziały. Udziały wpisów raportu sumują się do zużycia raportu.
/// </summary>
public static class EntryCosts
{
    public static async Task<Dictionary<long, EntryCost>> EstimateAsync(ZdrowieInfoDbContext db, IReadOnlyCollection<long> itemIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, EntryCost>();
        if (itemIds.Count == 0) return result;
        var ids = itemIds.ToList();
        var reports = await db.Reports.AsNoTracking()
            .Where(r => r.AiUsage != null && r.InputItemIds.Any(id => ids.Contains(id)))
            .Select(r => new { r.Model, r.AiUsage, r.Research, r.InputItemIds })
            .ToListAsync(ct);
        // Raport bez tokenów (generator przykładowy) to nie koszt – wpis dostaje „—”, a nie „0 / 0”.
        reports = reports.Where(r => r.AiUsage!.InputTokens + r.AiUsage.CacheReadTokens + r.AiUsage.CacheWriteTokens + r.AiUsage.OutputTokens > 0).ToList();
        if (reports.Count == 0) return result;

        // Waga wpisu: długość tekstu, który poszedł do modelu (ta sama dla wszystkich raportów z tym wpisem).
        var all = reports.SelectMany(r => r.InputItemIds).Distinct().ToList();
        var texts = await db.SourceItems.AsNoTracking().Where(i => all.Contains(i.Id))
            .Select(i => new { i.Id, i.Title, Length = (i.Content ?? i.Summary ?? "").Length })
            .ToDictionaryAsync(i => i.Id, ct);
        double Weight(long id) => texts.TryGetValue(id, out var t) ? t.Title.Length + Math.Min(t.Length, ReportComposer.MaxContentChars) + 1 : 1;

        // Raporty sprzed zapisywania listy sprawdzanych wpisów: sprawdzane = pasujące do wzorca podatności.
        var needText = reports.Where(r => r.Research is { ItemIds: null }).SelectMany(r => r.InputItemIds).Distinct().ToList();
        var vulnerable = needText.Count == 0 ? [] : (await db.SourceItems.AsNoTracking().Where(i => needText.Contains(i.Id))
                .Select(i => new { i.Id, i.Title, i.Content, i.Summary }).ToListAsync(ct))
            .Where(i => VulnerabilityCandidates.IsCandidate($"{i.Title}\n{i.Summary}\n{i.Content}")).Select(i => i.Id).ToHashSet();

        var totals = new Dictionary<long, (double In, double Out, decimal Cost, bool Priced, int Reports, double Searches)>();
        foreach (var report in reports)
        {
            var usage = report.AiUsage!;
            var research = report.Research?.Usage;
            var general = research is null ? usage with { WebSearches = 0 } : Minus(usage, research) with { WebSearches = 0 };
            var researchTokens = research is null ? null : research with { WebSearches = 0 };
            var candidates = report.Research is null ? []
                : (report.Research.ItemIds?.ToHashSet() ?? vulnerable).Intersect(report.InputItemIds).ToHashSet();
            var searches = usage.WebSearches;
            if (candidates.Count == 0 && (searches > 0 || researchTokens is not null)) candidates = report.InputItemIds.ToHashSet();

            var weightAll = report.InputItemIds.Sum(Weight);
            var weightCandidates = candidates.Sum(Weight);
            var generalCost = AiPricing.TokenCost(general, report.Model);
            var researchCost = researchTokens is null ? 0m : AiPricing.TokenCost(researchTokens, report.Model);
            var priced = generalCost is not null && researchCost is not null;

            foreach (var id in report.InputItemIds.Where(ids.Contains))
            {
                var share = Weight(id) / weightAll;
                var researchShare = candidates.Contains(id) && weightCandidates > 0 ? Weight(id) / weightCandidates : 0;
                var t = totals.GetValueOrDefault(id, (0, 0, 0m, true, 0, 0));
                t.In += AllInput(general) * share + (researchTokens is null ? 0 : AllInput(researchTokens) * researchShare);
                t.Out += general.OutputTokens * share + (researchTokens?.OutputTokens ?? 0) * researchShare;
                if (priced)
                    t.Cost += generalCost!.Value * (decimal)share + (researchCost!.Value + searches * AiPricing.PerWebSearch) * (decimal)researchShare;
                t.Priced &= priced;
                t.Reports++;
                t.Searches += searches * researchShare;
                totals[id] = t;
            }
        }

        foreach (var (id, t) in totals)
            result[id] = new EntryCost((long)Math.Round(t.In), (long)Math.Round(t.Out), t.Priced ? t.Cost : null, t.Reports, t.Searches);
        return result;
    }

    private static long AllInput(AiUsage u) => u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens;

    private static AiUsage Minus(AiUsage a, AiUsage b) => new(
        Math.Max(0, a.InputTokens - b.InputTokens), Math.Max(0, a.OutputTokens - b.OutputTokens),
        Math.Max(0, a.CacheReadTokens - b.CacheReadTokens), Math.Max(0, a.CacheWriteTokens - b.CacheWriteTokens),
        Math.Max(0, a.Calls - b.Calls), Math.Max(0, a.WebSearches - b.WebSearches));
}
