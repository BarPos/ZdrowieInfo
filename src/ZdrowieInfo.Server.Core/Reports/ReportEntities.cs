using NpgsqlTypes;

namespace ZdrowieInfo.Server.Reports;

public enum ReportStatus
{
    /// <summary>Gotowy.</summary>
    Ready = 0,
    /// <summary>Do ręcznej weryfikacji (np. cytat niezgodny z treścią źródła po ponowieniu).</summary>
    NeedsReview = 1,
    /// <summary>Analiza się nie udała – raport zawiera tylko listę wykrytych wpisów.</summary>
    AiError = 2,
}

public enum Importance
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,
}

public enum FindingCategory
{
    Outage = 0,
    P1 = 1,
    CeR = 2,
    Reporting = 3,
    Security = 4,
    Settlements = 5,
    Other = 6,
}

/// <summary>
/// Raport (tabela <c>reports</c>): dzienny – dokładnie jeden na datę, albo okresowy (od <see cref="PeriodStart"/> do <see cref="Date"/>).
/// Unikalny (rodzaj, początek, koniec); wyszukiwanie po tożsamości przez <see cref="ReportQueries.WhereKey"/>.
/// </summary>
public sealed class Report
{
    public long Id { get; set; }
    public ReportKind Kind { get; set; }

    /// <summary>Pierwszy dzień okresu; w raporcie dziennym równy <see cref="Date"/>.</summary>
    public DateOnly PeriodStart { get; set; }

    /// <summary>Dzień raportu dziennego albo ostatni dzień okresu.</summary>
    public DateOnly Date { get; set; }

    public ReportKey Key => new(Kind, PeriodStart, Date);

    /// <summary>
    /// Wpisy źródłowe, z których powstał raport. Raport z harmonogramu i API dodatkowo „zabiera” wpisy (source_items.report_id);
    /// raport wstecz i okresowy tylko je tu zapisuje – wpis może należeć do wielu takich raportów.
    /// </summary>
    public List<long> InputItemIds { get; set; } = [];

    /// <summary>Losowy token publicznego linku do PDF (/udostepnione/{token}); null = nieudostępniony.</summary>
    public string? ShareToken { get; set; }
    public DateTimeOffset? SharedAt { get; set; }
    public bool IsEmpty { get; set; }
    public ReportStatus Status { get; set; }
    public required string Title { get; set; }
    public required string Summary { get; set; }

    /// <summary>Kto przygotował: claude, system (pusty/awaryjny) albo api:&lt;nazwa klucza&gt;.</summary>
    public required string Producer { get; set; }
    public string? Model { get; set; }

    /// <summary>Pełny dokument raportu (jsonb) – także lista pominiętych wpisów z powodami.</summary>
    public ReportDocument Body { get; set; } = new();

    /// <summary>Stan źródeł w dniu raportu (jsonb).</summary>
    public List<SourceStateEntry> SourcesState { get; set; } = [];

    public AiUsage? AiUsage { get; set; }

    /// <summary>Szczegóły podatności sprawdzone w internecie przed analizą (null – nie sprawdzano).</summary>
    public VulnerabilityResearch? Research { get; set; }
    public int GenerationAttempts { get; set; }
    public string? Error { get; set; }
    public List<string> ValidationIssues { get; set; } = [];

    /// <summary>Etap 5: pliki .tex/.pdf. Etap 4: czy wysłano push.</summary>
    public string? TexPath { get; set; }
    public string? PdfPath { get; set; }

    /// <summary>Błąd ostatniej kompilacji PDF (raport HTML jest dostępny niezależnie).</summary>
    public string? PdfError { get; set; }
    public bool PushSent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Kolumna generowana (tytuł A, podsumowanie B) – wyszukiwanie pełnotekstowe.</summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public List<ReportFinding> Findings { get; set; } = [];
}

/// <summary>Punkt raportu (tabela <c>report_findings</c>).</summary>
public sealed class ReportFinding
{
    public long Id { get; set; }
    public long ReportId { get; set; }
    public int Order { get; set; }
    public Importance Importance { get; set; }
    public FindingCategory Category { get; set; }
    public required string Headline { get; set; }
    public required string Body { get; set; }
    public string? Action { get; set; }
    public List<FindingCitation> Citations { get; set; } = [];

    /// <summary>Kolumna generowana (nagłówek A, treść i działanie B).</summary>
    public NpgsqlTsVector? SearchVector { get; set; }
}

/// <summary>Kopia źródła dla punktu raportu (tabela <c>finding_citations</c>) – zostaje, gdy treść wpisu zostanie usunięta.</summary>
public sealed class FindingCitation
{
    public long Id { get; set; }
    public long FindingId { get; set; }
    public long? SourceItemId { get; set; }
    public required string Title { get; set; }
    public required string Url { get; set; }
    public DateOnly? PublishedOn { get; set; }
    public required string Quote { get; set; }

    /// <summary>Cytat sprawdzony jako dosłowny fragment zapisanej treści wpisu.</summary>
    public bool Verified { get; set; }
}

/// <summary>Zużycie Claude API dla raportu.</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int Calls, int WebSearches = 0);

/// <summary>Jedno źródło w sekcji „Stan źródeł”.</summary>
public sealed record SourceStateEntry(
    int SourceId,
    string Name,
    string Url,
    bool CheckedToday,
    bool Ok,
    string? Problem,
    string? Warning,
    DateTimeOffset? LastSuccessAt,
    bool IncidentOpen,
    IReadOnlyList<string> Recoveries);
