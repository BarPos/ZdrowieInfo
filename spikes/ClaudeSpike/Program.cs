// Spike etapu 0: kształt wywołania Claude dla raportu dziennego ZdrowieInfo.
// Bez ANTHROPIC_API_KEY kończy się po zbudowaniu żądania (sprawdza tylko kompilację i typy SDK).
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

const string Model = "claude-sonnet-5-5";

var systemPrompt = """
    Jesteś analitykiem komunikatów NFZ, CeZ, MZ i CSIRT CeZ dla przychodni.
    Oceniasz nowe wpisy według kryteriów ważności i odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
    Każdy cytat musi być dosłownym fragmentem treści wpisu o podanym item_id.
    """;

var input = """
    <wpisy>
    <wpis item_id="101" zrodlo="NFZ – Aktualności" data="2026-11-17" url="https://www.nfz.gov.pl/przyklad">
    Planowana przerwa techniczna w systemie SZOI w dniu 18 listopada 2026 r. w godzinach 18:00–22:00.
    </wpis>
    </wpisy>
    """;

var parameters = new MessageCreateParams
{
    Model = Model,
    MaxTokens = 16000,
    System = new List<BetaTextBlockParam>
    {
        new() { Text = systemPrompt, CacheControl = new BetaCacheControlEphemeral() },
    },
    Messages = [new BetaUserMessageParam { Content = input }],
    OutputConfig = new BetaOutputConfig
    {
        Effort = Effort.Medium,
        Format = new BetaJsonOutputFormat { Schema = ReportSchema.Build() },
    },
    Betas = ["server-side-fallback-2026-07-01"],
    Fallbacks = new Default(),
};

Console.WriteLine($"Żądanie zbudowane: model={parameters.Model}, schemat={ReportSchema.Build().Count} pól najwyższego poziomu.");

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.WriteLine("Brak ANTHROPIC_API_KEY – pomijam wywołanie API.");
    return;
}

AnthropicClient client = new();
try
{
    var response = await client.Beta.Messages.Create(parameters);

    if (response.StopReason == "refusal")
    {
        Console.WriteLine($"Odmowa: {response.StopDetails}");
        return;
    }
    if (response.StopReason == "max_tokens")
    {
        Console.WriteLine("Odpowiedź ucięta (max_tokens) – traktować jak błąd walidacji.");
        return;
    }

    var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
    Console.WriteLine(json);
    Console.WriteLine($"Tokeny: in={response.Usage.InputTokens} out={response.Usage.OutputTokens} " +
                      $"cache_write={response.Usage.CacheCreationInputTokens} cache_read={response.Usage.CacheReadInputTokens}");
}
catch (AnthropicRateLimitException e) { Console.WriteLine($"429 – ponowić później: {e.Message}"); }
catch (Anthropic5xxException e) { Console.WriteLine($"5xx – ponowić za 10 min: {e.Message}"); }
catch (AnthropicIOException e) { Console.WriteLine($"Sieć – ponowić za 10 min: {e.Message}"); }
catch (Anthropic4xxException e) { Console.WriteLine($"4xx – bez ponawiania (klucz, kredyty, żądanie): {e.Message}"); }
catch (AnthropicApiException e) { Console.WriteLine($"Inny błąd API: {e.Message}"); }

static class ReportSchema
{
    public static Dictionary<string, JsonElement> Build()
    {
        var citation = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "item_id", "quote" },
            properties = new { item_id = new { type = "string" }, quote = new { type = "string" } },
        };
        var finding = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "importance", "category", "headline", "body", "action", "citations" },
            properties = new
            {
                importance = new { type = "string", @enum = new[] { "krytyczna", "wysoka", "średnia", "niska" } },
                category = new { type = "string", @enum = new[] { "awaria", "P1", "CeR", "sprawozdawczość", "bezpieczeństwo", "rozliczenia", "inne" } },
                headline = new { type = "string" },
                body = new { type = "string" },
                action = new { type = new[] { "string", "null" } },
                citations = new { type = "array", items = citation },
            },
        };
        var ignored = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "item_id", "reason" },
            properties = new { item_id = new { type = "string" }, reason = new { type = "string" } },
        };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "title", "summary", "findings", "ignored" }),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                title = new { type = "string" },
                summary = new { type = "string" },
                findings = new { type = "array", items = finding },
                ignored = new { type = "array", items = ignored },
            }),
        };
    }
}
