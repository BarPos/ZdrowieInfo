using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Raport z Claude przez oficjalne SDK (Messages API, beta tylko dla serwerowego fallbacku modelu):
/// structured outputs (JSON Schema raportu), stały prompt systemowy z cache, model i effort z panelu, klucz z app_settings.
/// Kształt wywołania ustalony w spike'u etapu 0 (docs/decyzje.md, D5).
/// </summary>
public sealed class ClaudeReportGenerator(ReportSettings settings, IOptions<ReportsOptions> options, ILogger<ClaudeReportGenerator> logger) : IReportGenerator
{
    public string Name => "claude";

    public async Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
    {
        var apiKey = await settings.GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ReportGenerationException("brak klucza Claude API – wpisz go w panelu (Ustawienia)", transient: false);
        var claude = await settings.GetClaudeAsync(ct);

        var parameters = new MessageCreateParams
        {
            Model = claude.Model,
            MaxTokens = options.Value.MaxOutputTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = ReportComposer.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
            },
            Messages = [new BetaUserMessageParam { Content = ReportComposer.BuildUserMessage(request.Date, request.Profile, request.Items, request.PreviousErrors) }],
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(claude.Effort),
                Format = new BetaJsonOutputFormat { Schema = ReportJson.Schema() },
            },
            // Gdy filtr bezpieczeństwa odrzuci żądanie, serwer sam przekaże je modelowi zastępczemu.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };

        AnthropicClient client = new() { ApiKey = apiKey };
        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);
        }
        catch (AnthropicRateLimitException e) { throw new ReportGenerationException($"limit zapytań Claude API (429): {e.Message}", transient: true, e); }
        catch (Anthropic5xxException e) { throw new ReportGenerationException($"błąd serwera Claude API: {e.Message}", transient: true, e); }
        catch (AnthropicIOException e) { throw new ReportGenerationException($"brak połączenia z Claude API: {e.Message}", transient: true, e); }
        catch (Anthropic4xxException e) { throw new ReportGenerationException($"Claude API odrzuciło żądanie (klucz, kredyty albo parametry): {e.Message}", transient: false, e); }
        catch (AnthropicApiException e) { throw new ReportGenerationException($"błąd Claude API: {e.Message}", transient: true, e); }

        var usage = new AiUsage(response.Usage.InputTokens, response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0, 1);
        logger.LogInformation("Claude ({Model}): {In} tokenów wejścia, {Out} wyjścia, cache {Cache}.", response.Model, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens);

        if (response.StopReason == "refusal")
            throw new ReportGenerationException($"Claude odmówił przygotowania raportu ({response.StopDetails})", transient: false);

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        if (response.StopReason == "max_tokens")
            logger.LogWarning("Odpowiedź Claude ucięta na limicie {Max} tokenów.", options.Value.MaxOutputTokens);

        ReportDocument document;
        try
        {
            document = ReportJson.Parse(json);
        }
        catch (System.Text.Json.JsonException e)
        {
            // Ucięta albo niepoprawna odpowiedź – walidacja dostanie pusty raport i poprosi o poprawkę.
            logger.LogWarning(e, "Odpowiedź Claude nie jest poprawnym JSON-em raportu.");
            document = new ReportDocument { Title = "", Summary = "" };
        }
        return new GenerationResult(document, response.Model.ToString().Trim('"'), usage);
    }

    private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
