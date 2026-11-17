using System.Text.RegularExpressions;

namespace ZdrowieInfo.Server.Reports;

/// <summary>
/// Raport przykładowy bez Claude: punkt na każdy wpis, cytat = pierwsze zdanie treści (dosłownie, więc przechodzi walidację).
/// Do pracy lokalnej bez klucza API i do testów. Ważność z prostych słów kluczowych – to nie jest analiza.
/// </summary>
public sealed partial class SampleReportGenerator : IReportGenerator
{
    public string Name => "sample";

    public Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
    {
        var findings = request.Items.Select(item =>
        {
            var text = item.Content ?? item.Summary ?? item.Title;
            var outage = OutageWords().IsMatch(item.Title + " " + text);
            return new FindingDto
            {
                Importance = outage ? "wysoka" : "niska",
                Category = outage ? "awaria" : "inne",
                Headline = item.Title,
                Body = $"Nowy wpis w źródle: {string.Join(", ", item.SourceNames)}.",
                Action = null,
                Citations = [new CitationDto { ItemId = item.Id, Quote = FirstSentence(text) }],
            };
        }).ToList();

        var document = new ReportDocument
        {
            Title = $"Raport przykładowy – {request.Key.Label}",
            Summary = $"Raport przygotowany bez Claude (tryb przykładowy): {request.Items.Count} wykrytych wpisów, bez oceny ważności.",
            Findings = findings,
            Ignored = [],
        };
        return Task.FromResult(new GenerationResult(document, "sample", new AiUsage(0, 0, 0, 0, 0)));
    }

    private static string FirstSentence(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length >= 8) ?? text.Trim();
        var end = Sentence().Match(line);
        var sentence = end.Success && end.Index >= 20 ? line[..(end.Index + 1)] : line;
        return sentence.Length > 300 ? sentence[..300] : sentence;
    }

    [GeneratedRegex(@"przerw|awari|niedostęp|prace serwis|utrudnie", RegexOptions.IgnoreCase)]
    private static partial Regex OutageWords();

    [GeneratedRegex(@"[.!?](\s|$)")]
    private static partial Regex Sentence();
}
