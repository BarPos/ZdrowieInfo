using System.Text;
using System.Text.RegularExpressions;

namespace ZdrowieInfo.Server.Reports;

/// <summary>Wpis podany do raportu. Ten sam artykuł z kilku źródeł (ten sam URL) to jeden wpis z kilkoma SourceItemIds.</summary>
public sealed record ReportInputItem(
    string Id,
    IReadOnlyList<long> SourceItemIds,
    IReadOnlyList<string> SourceNames,
    string Title,
    string Url,
    DateOnly? PublishedOn,
    string Kind,
    string? Summary,
    string? Content,
    bool ContentTruncated)
{
    /// <summary>Tekst, z którego wolno cytować: tytuł, opis i treść.</summary>
    public string QuotableText => string.Join("\n", new[] { Title, Summary, Content }.Where(t => !string.IsNullOrWhiteSpace(t)));
}

public sealed record CitationCheck(int Finding, int Citation, ReportInputItem? Item, bool Verified);

public sealed record ReportValidation(IReadOnlyList<string> Errors, IReadOnlyList<CitationCheck> Citations)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Walidacja odpowiedzi: istniejące identyfikatory, cytaty dosłowne z zapisanej treści wpisu, każdy wpis albo w punkcie,
/// albo na liście pominiętych. Chroni przed zmyślonymi i przekręconymi cytatami oraz po cichu pominiętymi wpisami.
/// </summary>
public static partial class ReportValidator
{
    /// <param name="requireCoverage">Claude musi rozliczyć każdy wpis; zewnętrzny producent – nie.</param>
    /// <param name="allowUrlCitations">Zewnętrzny producent może cytować po samym URL-u (cytat niezweryfikowany, jeśli URL nieznany).</param>
    public static ReportValidation Validate(ReportDocument document, IReadOnlyList<ReportInputItem> inputs, bool requireCoverage, bool allowUrlCitations = false)
    {
        var errors = new List<string>();
        var checks = new List<CitationCheck>();
        var byId = inputs.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(document.Title)) errors.Add("Brak tytułu raportu.");
        if (string.IsNullOrWhiteSpace(document.Summary)) errors.Add("Brak podsumowania raportu.");

        for (var f = 0; f < document.Findings.Count; f++)
        {
            var finding = document.Findings[f];
            var label = $"Punkt {f + 1} („{Short(finding.Headline)}”)";
            if (!ReportVocabulary.Importances.ContainsKey(finding.Importance)) errors.Add($"{label}: nieznana ważność „{finding.Importance}”.");
            if (!ReportVocabulary.Categories.ContainsKey(finding.Category)) errors.Add($"{label}: nieznana kategoria „{finding.Category}”.");
            if (string.IsNullOrWhiteSpace(finding.Headline) || string.IsNullOrWhiteSpace(finding.Body)) errors.Add($"{label}: brak nagłówka albo treści.");
            if (finding.Citations.Count == 0) errors.Add($"{label}: brak cytatu ze źródła.");

            for (var c = 0; c < finding.Citations.Count; c++)
            {
                var citation = finding.Citations[c];
                ReportInputItem? item = null;
                if (!string.IsNullOrEmpty(citation.ItemId))
                {
                    if (!byId.TryGetValue(citation.ItemId, out item))
                    {
                        errors.Add($"{label}: cytat {c + 1} wskazuje nieistniejący wpis „{citation.ItemId}”.");
                        checks.Add(new CitationCheck(f, c, null, false));
                        continue;
                    }
                }
                else if (allowUrlCitations && !string.IsNullOrWhiteSpace(citation.Url))
                {
                    item = inputs.FirstOrDefault(i => string.Equals(i.Url, citation.Url.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        checks.Add(new CitationCheck(f, c, null, false)); // zewnętrzne źródło – nie da się sprawdzić
                        continue;
                    }
                }
                else
                {
                    errors.Add($"{label}: cytat {c + 1} bez identyfikatora wpisu.");
                    checks.Add(new CitationCheck(f, c, null, false));
                    continue;
                }

                covered.Add(item.Id);
                var verified = IsVerbatim(citation.Quote, item.QuotableText);
                if (!verified)
                    errors.Add($"{label}: cytat {c + 1} („{Short(citation.Quote)}”) nie jest dosłownym fragmentem wpisu {item.Id}.");
                checks.Add(new CitationCheck(f, c, item, verified));
            }
        }

        foreach (var ignored in document.Ignored)
        {
            if (!byId.ContainsKey(ignored.ItemId)) errors.Add($"Pominięty wpis „{ignored.ItemId}” nie istnieje.");
            else covered.Add(ignored.ItemId);
        }

        if (requireCoverage)
            foreach (var input in inputs.Where(i => !covered.Contains(i.Id)))
                errors.Add($"Wpis {input.Id} („{Short(input.Title)}”) nie jest ani w żadnym punkcie, ani na liście pominiętych.");

        return new ReportValidation(errors, checks);
    }

    /// <summary>
    /// Cytat jest dosłowny, jeśli po ujednoliceniu białych znaków, cudzysłowów, myślników i wielkości liter jest fragmentem tekstu.
    /// Wielokropek w cytacie dzieli go na części, które muszą wystąpić w tej kolejności.
    /// </summary>
    public static bool IsVerbatim(string quote, string text)
    {
        var parts = Ellipsis().Split(Normalize(quote)).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0 || parts.Sum(p => p.Length) < 8) return false;

        var haystack = Normalize(text);
        var position = 0;
        foreach (var part in parts)
        {
            var found = haystack.IndexOf(part, position, StringComparison.Ordinal);
            if (found < 0) return false;
            position = found + part.Length;
        }
        return true;
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '„' or '”' or '“' or '«' or '»' => '"',
                '‘' or '’' or '‚' => '\'',
                '–' or '—' or '‑' or '−' => '-',
                ' ' or '\t' or '\n' or '\r' => ' ',
                _ => char.ToLowerInvariant(ch),
            });
        }
        return Spaces().Replace(sb.ToString().Replace("­", "").Replace("​", ""), " ").Trim();
    }

    private static string Short(string? text) => text is null ? "" : text.Length <= 60 ? text : text[..57] + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\[\.\.\.\]|\(\.\.\.\)|…|\.\.\.")]
    private static partial Regex Ellipsis();
}
