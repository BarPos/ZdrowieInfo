using System.Globalization;

namespace ZdrowieInfo.Server.Reports;

public enum ReportKind
{
    /// <summary>Raport dzienny (jeden na datę; harmonogram, API, raport wstecz).</summary>
    Daily = 0,
    /// <summary>Raport okresowy – wpisy opublikowane od dnia do dnia, na żądanie administratora.</summary>
    Period = 1,
}

/// <summary>
/// Tożsamość raportu: rodzaj i zakres dat. W adresach jako <see cref="Slug"/>: „2026-11-17” (dzienny)
/// albo „2026-11-01--2026-11-15” (okresowy) – strona /raporty/{slug}, PDF raport-{slug}.pdf, wyszukiwanie i MCP.
/// </summary>
public readonly record struct ReportKey(ReportKind Kind, DateOnly Start, DateOnly End)
{
    private const string Format = "yyyy-MM-dd";
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static ReportKey Daily(DateOnly date) => new(ReportKind.Daily, date, date);

    public static ReportKey Period(DateOnly from, DateOnly to) => new(ReportKind.Period, from, to);

    public string Slug => Kind == ReportKind.Daily
        ? End.ToString(Format, CultureInfo.InvariantCulture)
        : $"{Start.ToString(Format, CultureInfo.InvariantCulture)}--{End.ToString(Format, CultureInfo.InvariantCulture)}";

    /// <summary>„17.11.2026” albo „1.11–15.11.2026”.</summary>
    public string Label => Kind == ReportKind.Daily
        ? End.ToString("dd.MM.yyyy", Polish)
        : Start.Year == End.Year
            ? $"{Start.ToString("d.MM", Polish)}–{End.ToString("d.MM.yyyy", Polish)}"
            : $"{Start.ToString("d.MM.yyyy", Polish)}–{End.ToString("d.MM.yyyy", Polish)}";

    public static bool TryParse(string? slug, out ReportKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(slug)) return false;
        var parts = slug.Split("--");
        if (parts.Length == 1 && DateOnly.TryParseExact(parts[0], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            key = Daily(day);
            return true;
        }
        if (parts.Length == 2
            && DateOnly.TryParseExact(parts[0], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            && DateOnly.TryParseExact(parts[1], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            && from <= to)
        {
            key = Period(from, to);
            return true;
        }
        return false;
    }

    public override string ToString() => Slug;
}

public static class ReportQueries
{
    /// <summary>Jedyne miejsce wyszukiwania raportu po tożsamości (dzienny i okresowy mogą mieć tę samą datę końcową).</summary>
    public static IQueryable<Report> WhereKey(this IQueryable<Report> reports, ReportKey key) =>
        reports.Where(r => r.Kind == key.Kind && r.PeriodStart == key.Start && r.Date == key.End);

    public static IQueryable<Report> Daily(this IQueryable<Report> reports) => reports.Where(r => r.Kind == ReportKind.Daily);
}
