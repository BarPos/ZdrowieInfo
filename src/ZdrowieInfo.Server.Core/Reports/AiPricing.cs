namespace ZdrowieInfo.Server.Reports;

/// <summary>Ceny za milion tokenów (USD). Zapis do cache – 5-minutowy (jak cache_control ephemeral w aplikacji).</summary>
public sealed record ModelPrice(decimal Input, decimal CacheWrite, decimal CacheRead, decimal Output);

/// <summary>
/// Cennik Claude API (platform.claude.com/docs/en/about-claude/pricing, listopad 2026) – jedno miejsce dla kosztu
/// wpisu i podsumowania w ustawieniach. Model spoza listy: bez ceny (lepiej „—” niż zgadywanie stawek).
/// </summary>
public static class AiPricing
{
    /// <summary>Web search: 10 USD za 1000 wyszukań.</summary>
    public const decimal PerWebSearch = 0.01m;

    // Najdłuższe identyfikatory najpierw („claude-sonnet-5-5” przed „claude-sonnet-5”).
    private static readonly (string Prefix, ModelPrice Price)[] Prices =
    [
        ("claude-fable-5-2", new(8m, 10m, 0.20m, 40m)),
        ("claude-mythos-5-2", new(8m, 10m, 0.20m, 40m)),
        ("claude-fable-5-1", new(10m, 12.50m, 0.25m, 50m)),
        ("claude-mythos-5-1", new(10m, 12.50m, 0.25m, 50m)),
        ("claude-fable-5", new(10m, 12.50m, 1m, 50m)),
        ("claude-mythos-5", new(10m, 12.50m, 1m, 50m)),
        ("claude-opus-5-5", new(4m, 5m, 0.20m, 20m)),
        ("claude-opus-5", new(5m, 6.25m, 0.50m, 25m)),
        ("claude-opus-4", new(5m, 6.25m, 0.50m, 25m)),
        ("claude-sonnet-5-5", new(2m, 2.50m, 0.10m, 10m)),
        ("claude-sonnet-5", new(2m, 2.50m, 0.20m, 10m)),
        ("claude-sonnet-4", new(3m, 3.75m, 0.30m, 15m)),
        // Haiku 5.5: stawka dla promptów do 100 tys. tokenów (raporty mieszczą się w niej; powyżej – 5× drożej).
        ("claude-haiku-5-5", new(0.10m, 0.125m, 0.01m, 0.50m)),
        ("claude-haiku-4-5", new(1m, 1.25m, 0.10m, 5m)),
    ];

    public static ModelPrice? For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var id = model.Trim().Trim('"').ToLowerInvariant();
        foreach (var (prefix, price) in Prices)
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return price;
        return null;
    }

    /// <summary>Koszt tokenów (bez wyszukiwań); null – nieznany model.</summary>
    public static decimal? TokenCost(AiUsage usage, string? model) =>
        For(model) is { } p
            ? (usage.InputTokens * p.Input + usage.CacheWriteTokens * p.CacheWrite + usage.CacheReadTokens * p.CacheRead + usage.OutputTokens * p.Output) / 1_000_000m
            : null;

    /// <summary>Koszt wywołań raportu: tokeny i wyszukiwania; null – nieznany model.</summary>
    public static decimal? Cost(AiUsage usage, string? model) =>
        TokenCost(usage, model) is { } tokens ? tokens + usage.WebSearches * PerWebSearch : null;
}
