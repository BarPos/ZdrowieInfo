using System.Text.Json.Serialization;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Kontrakt raportu – ten sam dla Claude (structured outputs) i dla <c>POST /api/reports</c>.
/// Nazwy pól w JSON: snake_case (item_id), ważność i kategoria po polsku.
/// </summary>
public sealed record ReportDocument
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<FindingDto> Findings { get; init; } = [];
    public List<IgnoredDto> Ignored { get; init; } = [];
}

public sealed record FindingDto
{
    /// <summary>krytyczna | wysoka | średnia | niska</summary>
    public string Importance { get; init; } = "";

    /// <summary>awaria | P1 | CeR | sprawozdawczość | bezpieczeństwo | rozliczenia | inne</summary>
    public string Category { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Body { get; init; } = "";
    public string? Action { get; init; }
    public List<CitationDto> Citations { get; init; } = [];
}

public sealed record CitationDto
{
    /// <summary>Identyfikator wpisu z wejścia. Zewnętrzny producent bez identyfikatorów może podać tylko <see cref="Url"/>.</summary>
    [JsonPropertyName("item_id")]
    public string? ItemId { get; init; }

    public string? Url { get; init; }
    public string Quote { get; init; } = "";
}

public sealed record IgnoredDto
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = "";
    public string Reason { get; init; } = "";
}

public static class ReportVocabulary
{
    public static readonly IReadOnlyDictionary<string, Importance> Importances = new Dictionary<string, Importance>(StringComparer.OrdinalIgnoreCase)
    {
        ["krytyczna"] = Importance.Critical,
        ["wysoka"] = Importance.High,
        ["średnia"] = Importance.Medium,
        ["niska"] = Importance.Low,
    };

    public static readonly IReadOnlyDictionary<string, FindingCategory> Categories = new Dictionary<string, FindingCategory>(StringComparer.OrdinalIgnoreCase)
    {
        ["awaria"] = FindingCategory.Outage,
        ["P1"] = FindingCategory.P1,
        ["CeR"] = FindingCategory.CeR,
        ["sprawozdawczość"] = FindingCategory.Reporting,
        ["bezpieczeństwo"] = FindingCategory.Security,
        ["rozliczenia"] = FindingCategory.Settlements,
        ["inne"] = FindingCategory.Other,
    };

    public static string Label(Importance i) => Importances.First(p => p.Value == i).Key;

    public static string Label(FindingCategory c) => Categories.First(p => p.Value == c).Key;
}
