using System.Text.Json;

namespace ZdrowieInfo.Server.Sources.Extraction;

/// <summary>
/// Lista w formacie JSON (np. ezdrowie.gov.pl ładuje artykuły przez API). Selektory to nazwy pól:
/// Item – ścieżka do tablicy (kropki), Title/Link/Date/Summary – pola elementu; Link może iść przez LinkTemplate.
/// </summary>
internal static class JsonList
{
    public static ListExtraction Extract(string json, string pageUrl, SourceSelectors selectors)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new SelectorException("Odpowiedź nie jest poprawnym JSON-em – czy adres listy się zmienił?", e);
        }

        using (document)
        {
            var array = document.RootElement;
            foreach (var part in selectors.Item!.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (array.ValueKind != JsonValueKind.Object || !array.TryGetProperty(part, out array))
                    throw new SelectorException($"Brak pola „{part}” w odpowiedzi JSON.");
            }
            if (array.ValueKind != JsonValueKind.Array)
                throw new SelectorException($"Pole „{selectors.Item}” nie jest tablicą.");

            var items = new List<ExtractedItem>();
            var seen = new HashSet<string>();
            var skipped = 0;
            foreach (var element in array.EnumerateArray())
            {
                var link = Text(element, selectors.Link ?? "url");
                var url = link is null ? null : SourceExtractor.ResolveLink(link, pageUrl, selectors.LinkTemplate);
                var title = HtmlText.Normalize(Text(element, selectors.Title ?? "title") ?? "").Replace('\n', ' ');
                if (url is null || title.Length == 0) { skipped++; continue; }

                var key = UrlNormalizer.Key(url);
                if (!seen.Add(key)) continue;
                var summary = selectors.Summary is null ? null : Text(element, selectors.Summary);
                items.Add(new ExtractedItem(key, url, title, PolishDate.Parse(selectors.Date is null ? null : Text(element, selectors.Date)),
                    string.IsNullOrWhiteSpace(summary) ? null : HtmlText.Normalize(summary).Replace('\n', ' ')));
            }
            return new ListExtraction(items, skipped);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
