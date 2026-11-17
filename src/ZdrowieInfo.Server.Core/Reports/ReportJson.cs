using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace ZdrowieInfo.Server.Reports;

public static class ReportJson
{
    /// <summary>snake_case, polskie znaki wprost.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
    };

    public static ReportDocument Parse(string json) =>
        JsonSerializer.Deserialize<ReportDocument>(json, Options) ?? throw new JsonException("Pusty dokument raportu.");

    public static string Serialize(ReportDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>JSON Schema raportu dla structured outputs (additionalProperties: false, enumy po polsku).</summary>
    public static Dictionary<string, JsonElement> Schema()
    {
        var citation = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "item_id", "quote" },
            properties = new
            {
                item_id = new { type = "string", description = "Identyfikator wpisu z wejścia (atrybut id)." },
                quote = new { type = "string", description = "Dosłowny fragment treści tego wpisu (1–2 zdania)." },
            },
        };
        var finding = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "importance", "category", "headline", "body", "action", "citations" },
            properties = new
            {
                importance = new { type = "string", @enum = ReportVocabulary.Importances.Keys.ToArray() },
                category = new { type = "string", @enum = ReportVocabulary.Categories.Keys.ToArray() },
                headline = new { type = "string" },
                body = new { type = "string" },
                action = new { type = new[] { "string", "null" }, description = "Co zrobić i do kiedy; null, gdy nie trzeba nic robić." },
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
