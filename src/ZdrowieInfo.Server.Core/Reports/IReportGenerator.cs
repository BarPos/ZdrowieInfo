namespace ZdrowieInfo.Server.Reports;

public sealed record GenerationRequest(DateOnly Date, ClinicProfile Profile, IReadOnlyList<ReportInputItem> Items, IReadOnlyList<string>? PreviousErrors = null);

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
