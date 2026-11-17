using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ZdrowieInfo.Server.Sources.Extraction;

public sealed record ExtractedItem(string Key, string Url, string Title, DateOnly? PublishedOn, string? Summary);

/// <summary>
/// Wynik ekstrakcji listy. <see cref="Skipped"/> – elementy pasujące do selektora wpisu, ale bez linku.
/// <see cref="NextPageUrl"/> – adres następnej strony (gdy selektor NextPage coś znalazł).
/// </summary>
public sealed record ListExtraction(IReadOnlyList<ExtractedItem> Items, int Skipped, string? NextPageUrl = null);

/// <summary>Błędny selektor CSS albo brak wymaganego selektora – traktowane jak „0 wpisów” z czytelnym opisem.</summary>
public sealed class SelectorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Deterministyczna ekstrakcja (bez Claude): wpisy z listy, treść strony, treść wpisu.</summary>
public static class SourceExtractor
{
    private static readonly string[] DefaultDetailContent = ["main", "article", "body"];

    public static IHtmlDocument Parse(string html, string pageUrl)
    {
        var document = new HtmlParser().ParseDocument(html);
        // Base URI do rozwiązywania względnych linków (AngleSharp bierze je z <base> albo z adresu dokumentu).
        if (document.QuerySelector("base[href]") is null)
        {
            var baseElement = document.CreateElement("base");
            baseElement.SetAttribute("href", pageUrl);
            document.Head?.Prepend(baseElement);
        }
        return document;
    }

    public static ListExtraction ExtractList(string html, string pageUrl, SourceSelectors selectors)
    {
        if (string.IsNullOrWhiteSpace(selectors.Item))
            throw new SelectorException("Brak selektora wpisu (item).");
        if (selectors.Format == ListFormat.Json)
            return JsonList.Extract(html, pageUrl, selectors);

        var document = Parse(html, pageUrl);
        var items = new List<ExtractedItem>();
        var seen = new HashSet<string>();
        var skipped = 0;

        foreach (var element in Select(document, selectors.Item))
        {
            var link = selectors.Link is null
                ? element.QuerySelector("a[href]")
                : Select(element, selectors.Link).FirstOrDefault();
            var href = link?.GetAttribute("href");
            var url = href is null ? null : ResolveLink(href, element.BaseUri ?? pageUrl, selectors.LinkTemplate);
            if (url is null) { skipped++; continue; }

            var title = HtmlText.Inline(selectors.Title is null ? link : Select(element, selectors.Title).FirstOrDefault());
            if (title.Length == 0) title = HtmlText.Inline(link);
            if (title.Length == 0) { skipped++; continue; }

            var key = UrlNormalizer.Key(url);
            if (!seen.Add(key)) continue; // ten sam wpis dwa razy na stronie (np. blok „Polecane”)

            DateOnly? date = null;
            if (selectors.Date is not null && Select(element, selectors.Date).FirstOrDefault() is { } dateElement)
                date = PolishDate.Parse(selectors.DateAttribute is null ? HtmlText.Inline(dateElement) : dateElement.GetAttribute(selectors.DateAttribute));

            string? summary = null;
            if (selectors.Summary is not null && Select(element, selectors.Summary).FirstOrDefault() is { } summaryElement)
                summary = HtmlText.Inline(summaryElement) is { Length: > 0 } s ? s : null;

            items.Add(new ExtractedItem(key, url, title, date, summary));
        }

        string? next = null;
        if (selectors.NextPage is not null && Select(document, selectors.NextPage).FirstOrDefault()?.GetAttribute("href") is { } nextHref)
        {
            next = UrlNormalizer.Resolve(pageUrl, nextHref);
            if (next is not null && UrlNormalizer.Key(next) == UrlNormalizer.Key(pageUrl)) next = null; // link do bieżącej strony
        }
        return new ListExtraction(items, skipped, next);
    }

    internal static string? ResolveLink(string value, string pageUrl, string? template)
    {
        if (template is not null && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !value.StartsWith('/'))
            value = template.Replace("{0}", value.Trim(), StringComparison.Ordinal);
        return UrlNormalizer.Resolve(pageUrl, value);
    }

    /// <summary>Metoda „cała strona”: znormalizowany tekst głównej treści albo null, gdy selektor nic nie znalazł.</summary>
    public static string? ExtractPageText(string html, string pageUrl, SourceSelectors selectors)
    {
        if (string.IsNullOrWhiteSpace(selectors.Content))
            throw new SelectorException("Brak selektora treści (content).");
        return ExtractText(Parse(html, pageUrl), [selectors.Content], selectors.Remove);
    }

    /// <summary>Treść strony pojedynczego wpisu (z adresami linków – np. do załączników).</summary>
    public static string? ExtractDetailText(string html, string pageUrl, SourceSelectors selectors)
    {
        var document = Parse(html, pageUrl);
        string[] candidates = selectors.DetailContent is null ? DefaultDetailContent : [selectors.DetailContent, .. DefaultDetailContent];
        return ExtractText(document, candidates, selectors.DetailRemove);
    }

    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? ExtractText(IHtmlDocument document, IEnumerable<string> contentSelectors, IEnumerable<string> remove)
    {
        foreach (var selector in remove)
            foreach (var element in Select(document, selector).ToList())
                element.Remove();

        foreach (var selector in contentSelectors)
        {
            var elements = Select(document, selector).ToList();
            // Pomijamy elementy zagnieżdżone w innych dopasowanych (selektory po przecinku), żeby nie dublować tekstu.
            elements = elements.Where(e => !elements.Any(o => o != e && o.Contains(e))).ToList();
            if (elements.Count == 0) continue;
            var text = HtmlText.FromElements(elements, includeLinkUrls: true);
            if (text.Length > 0) return text;
        }
        return null;
    }

    private static IEnumerable<IElement> Select(IParentNode root, string selector)
    {
        try
        {
            return root.QuerySelectorAll(selector);
        }
        catch (DomException e)
        {
            throw new SelectorException($"Nieprawidłowy selektor CSS: {selector}", e);
        }
    }
}
