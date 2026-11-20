using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Raport przygotowany do wyświetlenia – wspólny dla strony i dokumentu LaTeX/PDF, żeby numeracja punktów (2.1, 2.2…)
/// i przypisów ze źródłami była w obu identyczna.
/// </summary>
public sealed record ReportView(
    Report Report,
    string DateLong,
    string ProducerLabel,
    string? StatusNote,
    IReadOnlyList<ReportView.FindingGroup> Groups,
    IReadOnlyList<ReportView.Footnote> Footnotes,
    IReadOnlyList<ReportView.IgnoredEntry> Ignored,
    IReadOnlyList<SourceStateEntry> SourceProblems,
    IReadOnlyList<string> Recoveries,
    int SourcesChecked,
    IReadOnlyList<ReportView.Entry> Entries,
    ReportKey? Previous,
    ReportKey? Next)
{
    public sealed record FindingGroup(Importance Importance, string Heading, IReadOnlyList<FindingEntry> Findings);

    public sealed record FindingEntry(string Number, ReportFinding Finding, string Category, IReadOnlyList<CitationEntry> Citations)
    {
        /// <summary>Ważność punktu („krytyczna”, „wysoka”…) – pokazywana nad kategorią.</summary>
        public string Importance => ReportVocabulary.Label(Finding.Importance);
    }

    /// <summary>Punkty po kolei (od najważniejszych), każdy z własną ważnością – bez nagłówków grup.</summary>
    public IReadOnlyList<FindingEntry> Findings => Groups.SelectMany(g => g.Findings).ToList();

    public sealed record CitationEntry(int Footnote, FindingCitation Citation);

    public sealed record Footnote(int Number, string Title, string Url, DateOnly? PublishedOn, bool Verified);

    public sealed record IgnoredEntry(string Title, string? Url, string Reason);

    public sealed record Entry(long Id, string Title, string Url, string Source, DateOnly? PublishedOn);

    private static readonly System.Globalization.CultureInfo Pl = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");

    public string KindLabel => Report.Kind == ReportKind.Period ? "raport okresowy" : "raport dzienny";

    /// <summary>Raport wstecz i okresowy nie ma stanu źródeł z dnia raportu (dzisiejszy stan byłby mylący).</summary>
    public bool HasSourcesState => Report.SourcesState.Count > 0;

    /// <summary>Szczegóły podatności z internetu – tylko gdy są notatki (sam błąd sprawdzania jest wśród uwag raportu).</summary>
    public VulnerabilityResearch? Research => Report.Research is { HasNotes: true } research ? research : null;

    /// <summary>Numery sekcji: zależą od tego, które sekcje występują.</summary>
    public int SectionFindings => 2;

    public static string Heading(Importance i) => i switch
    {
        Importance.Critical => "Krytyczne",
        Importance.High => "Wysokie",
        Importance.Medium => "Średnie",
        _ => "Niskie",
    };

    public static string Producer(string producer, string? model) => producer switch
    {
        "claude" => model is null ? "Claude" : $"Claude ({model})",
        "sample" => "generator przykładowy (bez Claude)",
        "system" => "system ZdrowieInfo",
        _ when producer.StartsWith("api:", StringComparison.Ordinal) => $"program zewnętrzny ({producer[4..]})",
        _ => producer,
    };

    public static Task<ReportView?> LoadAsync(ZdrowieInfoDbContext db, DateOnly date, CancellationToken ct = default) =>
        LoadAsync(db, ReportKey.Daily(date), ct);

    public static async Task<ReportView?> LoadAsync(ZdrowieInfoDbContext db, ReportKey key, CancellationToken ct = default)
    {
        var report = await db.Reports.AsNoTracking().AsSplitQuery()
            .Include(r => r.Findings).ThenInclude(f => f.Citations)
            .WhereKey(key).SingleOrDefaultAsync(ct);
        if (report is null) return null;

        // Wpisy wejścia: zapisane w raporcie (każdy nowy raport) albo – dla starych raportów – te, które raport „zabrał”.
        var ids = report.InputItemIds;
        var entries = await (from i in db.SourceItems.AsNoTracking()
                             join s in db.Sources on i.SourceId equals s.Id
                             where i.ReportId == report.Id || ids.Contains(i.Id)
                             orderby i.PublishedOn descending, i.Id
                             select new Entry(i.Id, i.Title, i.Url, i.Origin != null ? i.Origin + " (wpis własny)" : s.Name, i.PublishedOn)).ToListAsync(ct);
        // Nawigacja ←/→ między raportami tego samego rodzaju.
        var same = db.Reports.Where(r => r.Kind == key.Kind);
        var previous = await same.Where(r => r.Date < key.End || (r.Date == key.End && r.PeriodStart < key.Start))
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.PeriodStart).Select(r => new { r.Kind, r.PeriodStart, r.Date }).FirstOrDefaultAsync(ct);
        var next = await same.Where(r => r.Date > key.End || (r.Date == key.End && r.PeriodStart > key.Start))
            .OrderBy(r => r.Date).ThenBy(r => r.PeriodStart).Select(r => new { r.Kind, r.PeriodStart, r.Date }).FirstOrDefaultAsync(ct);
        return Build(report, entries,
            previous is null ? null : new ReportKey(previous.Kind, previous.PeriodStart, previous.Date),
            next is null ? null : new ReportKey(next.Kind, next.PeriodStart, next.Date));
    }

    public static ReportView Build(Report report, IReadOnlyList<Entry> entries, ReportKey? previous = null, ReportKey? next = null)
    {
        var footnotes = new List<Footnote>();
        var groups = new List<FindingGroup>();
        var counter = 0;
        foreach (var group in report.Findings.OrderBy(f => f.Order).GroupBy(f => f.Importance).OrderBy(g => g.Key))
        {
            var findings = new List<FindingEntry>();
            foreach (var finding in group)
            {
                counter++;
                var citations = new List<CitationEntry>();
                foreach (var citation in finding.Citations.OrderBy(c => c.Id))
                {
                    footnotes.Add(new Footnote(footnotes.Count + 1, citation.Title, citation.Url, citation.PublishedOn, citation.Verified));
                    citations.Add(new CitationEntry(footnotes.Count, citation));
                }
                findings.Add(new FindingEntry($"2.{counter}", finding, ReportVocabulary.Label(finding.Category), citations));
            }
            groups.Add(new FindingGroup(group.Key, Heading(group.Key), findings));
        }

        var ignored = report.Body.Ignored.Select(g =>
        {
            var entry = entries.FirstOrDefault(e => e.Id.ToString() == g.ItemId);
            return new IgnoredEntry(entry?.Title ?? $"wpis {g.ItemId}", entry?.Url, g.Reason);
        }).ToList();

        string? note = report.Status switch
        {
            ReportStatus.NeedsReview => "Do weryfikacji: automatyczna kontrola znalazła uwagi – sprawdź oznaczone cytaty w źródłach."
                                        + (report.ValidationIssues.Count > 0 ? " " + string.Join(" ", report.ValidationIssues) : ""),
            ReportStatus.AiError => $"Bez analizy: {report.Error}",
            _ => null,
        };

        return new ReportView(
            report,
            report.Kind == ReportKind.Period
                ? $"od {report.PeriodStart.ToString("d MMMM yyyy", Pl)} do {report.Date.ToString("d MMMM yyyy", Pl)}"
                : report.Date.ToString("dddd, d MMMM yyyy", Pl),
            Producer(report.Producer, report.Model == "sample" ? null : report.Model),
            note,
            groups,
            footnotes,
            ignored,
            report.SourcesState.Where(s => !s.Ok || s.Warning is not null).ToList(),
            report.SourcesState.SelectMany(s => s.Recoveries.Select(r => $"{s.Name}: {r}")).ToList(),
            report.SourcesState.Count(s => s.CheckedToday),
            entries,
            previous,
            next);
    }
}
