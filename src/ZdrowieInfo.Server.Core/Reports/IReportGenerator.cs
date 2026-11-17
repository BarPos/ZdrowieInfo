namespace ZdrowieInfo.Server.Reports;

/// <param name="Date">Dzień raportu albo ostatni dzień okresu.</param>
/// <param name="PeriodStart">Raport okresowy: pierwszy dzień okresu.</param>
/// <param name="Retrospective">Raport wstecz (wpisy opublikowane w danym dniu/okresie, nie „nowe od ostatniego raportu”).</param>
public sealed record GenerationRequest(DateOnly Date, ClinicProfile Profile, IReadOnlyList<ReportInputItem> Items, IReadOnlyList<string>? PreviousErrors = null,
    DateOnly? PeriodStart = null, bool Retrospective = false)
{
    public ReportKey Key => PeriodStart is { } start ? ReportKey.Period(start, Date) : ReportKey.Daily(Date);
}

public sealed record GenerationResult(ReportDocument Document, string Model, AiUsage Usage);

/// <summary>
/// Błąd przygotowania raportu. <see cref="Transient"/> – warto ponowić za 10 minut (429, 5xx, sieć);
/// inaczej (brak klucza, kredytów, odmowa) od razu raport awaryjny.
/// </summary>
public sealed class ReportGenerationException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>Kto pisze raport: Claude (produkcja) albo generator przykładowy (praca bez klucza API, testy).</summary>
public interface IReportGenerator
{
    string Name { get; }

    Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default);
}
